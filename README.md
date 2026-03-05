# Distill

A single-binary CLI tool that converts any web page into clean, LLM-ready markdown. Built for AI agents and MCP tool servers.

Distill handles the hard parts of web scraping — JavaScript rendering, bot detection, virtual scroll, lazy loading — and outputs clean markdown that fits in a context window.

## Quick start

```bash
# Simple page
distill https://example.com

# Search results from a JS-heavy site
distill https://www.dhgate.com/wholesale/search.do?searchkey=ssd

# Anti-bot site with your real Chrome profile
distill https://www.aliexpress.com/w/wholesale-ssd.html --render --visible
```

## How it works

```
URL
 │
 ▼
┌─────────────┐   blocked/SPA    ┌──────────────┐   captcha    ┌─────────────────────┐
│ Static fetch │ ──────────────►  │ Headless CDP │ ──────────► │ Visible + Profile   │
│ (HttpClient) │                  │ (Chrome)     │             │ (your real browser) │
└──────┬──────┘                  └──────┬───────┘             └──────────┬──────────┘
       │                                │                                │
       ▼                                ▼                                ▼
   HtmlCleaner                    PageCleaner (JS)               PageCleaner (JS)
   (AngleSharp)                   + Network idle                 + Network idle
       │                          + Scroll-to-load               + Scroll-to-load
       ▼                                │                                │
       └────────────────┬───────────────┘────────────────────────────────┘
                        ▼
                ContentExtractor (SmartReader / body fallback)
                        ▼
                MarkdownConverter (ReverseMarkdown)
                        ▼
                   Clean Markdown
```

**Auto-escalation**: Distill tries the cheapest approach first (static HTTP), then automatically escalates to headless Chrome if the page is blocked or needs JS, and further to visible Chrome with your real browser profile if it hits a captcha.

**Network idle detection**: Instead of fixed delays, Distill tracks in-flight network requests via CDP and waits until the page is truly done loading. Works with any SPA framework.

**Scroll-to-load**: Automatically scrolls the page to trigger lazy-loaded and virtually-rendered content (infinite scroll, intersection observer patterns).

## Installation

Download the single-file binary for your platform from [Releases](https://github.com/abdullah-cognite/distill/releases), or build from source:

```bash
# Requires .NET 10 SDK
dotnet publish -c Release -r osx-arm64    # macOS Apple Silicon
dotnet publish -c Release -r osx-x64      # macOS Intel
dotnet publish -c Release -r linux-x64    # Linux
dotnet publish -c Release -r win-x64      # Windows
```

The binary is at `bin/Release/net10.0/<rid>/publish/distill`.

### Runtime dependency

Distill uses Chrome or Edge (already installed on most machines) for JS rendering. No Playwright, no Puppeteer, no npm — just a direct WebSocket connection to the browser's DevTools protocol.

## Usage

```
distill <url> [url2 ...] [options]

Options:
  --render          Force browser rendering (skip SPA detection)
  --static          Force static fetch (skip browser entirely)
  --visible         Launch Chrome visibly (non-headless), best for anti-bot sites
  --profile <dir>   Use existing Chrome profile (helps avoid bot detection)
  -o, --output      Output to file instead of stdout
  --browser <path>  Path to Chrome/Edge binary
  --cdp <ws-url>    Connect to existing CDP endpoint
  --timeout <ms>    Override timeout (auto-scales: static 30s, render 60s, visible 90s)
  --delay <ms>      Delay between requests when processing multiple URLs
  --stdin           Read URLs from stdin (one per line)
```

### Examples

```bash
# Pipe to an LLM
distill https://docs.example.com/api | llm "summarize the auth section"

# Multiple URLs with throttling
distill https://a.com https://b.com https://c.com --delay 2000

# URLs from a file
cat urls.txt | distill --stdin -o results.md

# Connect to already-running Chrome
distill https://example.com --cdp ws://localhost:9222/devtools/browser/...
```

## MCP tool server

Distill is designed to be called by AI agents via MCP (Model Context Protocol). Add it as a tool in your MCP config:

```json
{
  "mcpServers": {
    "distill": {
      "command": "/path/to/distill",
      "args": ["--stdin"],
      "description": "Convert web pages to clean markdown for analysis"
    }
  }
}
```

Or wrap it as a stdio-based tool that accepts a URL and returns markdown:

```json
{
  "mcpServers": {
    "web": {
      "command": "bash",
      "args": ["-c", "/path/to/distill \"$1\""],
      "description": "Fetch and distill a web page to markdown"
    }
  }
}
```

### What agents get

Distill strips navigation chrome, cookie banners, ads, and boilerplate. An agent asking "find me the cheapest M.2 SSD on AliExpress" gets:

```markdown
### PHONEPACE SSD M2 NVME 128GB 256GB 512GB 1TB
NOK 223.15
NOK 613.88 -63%
4.9 ★ · 5,000+ sold

### Kingston A400-NGFF SSD M.2 2280 Internal Solid State Drives
NOK 286.22
NOK 572.44 -50%
4.6 ★ · 500+ sold
```

Instead of 700KB of HTML with scripts, styles, and tracking pixels.

## Anti-bot handling

Many e-commerce sites aggressively block automated access. Distill handles this with a layered approach:

| Technique | What it does |
|-----------|-------------|
| Static-first | Avoids launching a browser at all when possible |
| UA spoofing | Identifies as Safari, not HeadlessChrome |
| Stealth scripts | Removes `navigator.webdriver`, fakes plugins/languages |
| `--visible` | Launches real Chrome window (not headless) |
| `--profile` | Uses your existing cookies and login sessions |
| Auto-escalation | Automatically retries with stronger measures on block |
| Block detection | Catches captchas, JS challenges, rate limits early |
| Network idle | Waits for real page load, not arbitrary timeouts |
| Human-like scroll | Randomized timing and step sizes |

### Site compatibility

| Site | Mode | Works? |
|------|------|--------|
| Amazon, eBay | Static | Yes |
| Banggood | Static (search), Render (product pages) | Yes |
| DHgate | Auto-escalate (403 → render) | Yes |
| Wish | Render | Yes |
| React/Vue/Angular/Svelte docs | Auto (SPA detected) | Yes |
| AliExpress | `--render --visible` | Yes |
| Shein | `--render --visible --profile` | Yes |
| Temu | `--render --visible --profile` | Partial (needs login) |

## Project structure

```
Program.cs                  CLI entry, arg parsing, orchestration, auto-escalation
Cdp/
  BrowserLauncher.cs        Find/launch Chrome with stealth flags
  CdpClient.cs              Raw WebSocket CDP transport
  CdpCommands.cs            Navigate, network idle, scroll, extract
Pipeline/
  Fetcher.cs                HttpClient with realistic headers
  SpaDetector.cs            Heuristic: does this page need JS?
  BlockDetector.cs          Detect captchas, JS challenges, rate limits
  PageCleaner.cs            DOM cleanup via JS injection (browser path)
  HtmlCleaner.cs            DOM cleanup via AngleSharp (static path)
  ContentExtractor.cs       SmartReader + body fallback
  MarkdownConverter.cs      HTML → Markdown via ReverseMarkdown
```

## License

[CC BY-NC 4.0](https://creativecommons.org/licenses/by-nc/4.0/) — free for non-commercial use.
