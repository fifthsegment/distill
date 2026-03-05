# Distill — Web Page to Markdown for AI Agents

## Conversation Summary

The goal is to build a lightweight .NET CLI tool that converts JavaScript-heavy web pages (like AliExpress product listings) into clean, LLM-ready markdown. This came from frustration with existing tools:

- **Firecrawl** (88k★) — TypeScript, AGPL-3.0, requires Docker + Redis, designed as a hosted SaaS. Too heavy.
- **Crawl4AI** (61k★) — Python + Playwright. Good but bundles Chromium.
- **Fetcher MCP** (1k★) — TypeScript + Playwright. Lightweight but still Playwright.
- **Jina Reader** (10k★) — Hosted service, not self-hostable as a binary.

All of these depend on Playwright or a bundled Chromium. We want a **single self-contained .NET binary** (~10MB) that talks to the user's already-installed Chrome or Edge via the Chrome DevTools Protocol (CDP) over WebSocket. No Playwright. No bundled browser.

## Target Use Case

**JavaScript-heavy SPAs like AliExpress** — where the raw HTML is just a shell with `<script>` tags, and all product data (titles, prices, images, seller info) is rendered client-side by a JS framework. A simple HTTP fetch returns an empty page. You need a real browser to execute the JS, wait for rendering, then extract the final DOM.

Other target sites: any SPA, React/Vue/Angular apps, dynamically loaded content, infinite scroll pages, pages behind JS-based bot checks.

## Architecture

**Core principle: STATIC-FIRST.** Chrome is expensive, slow, and triggers bot detection. Always try HttpClient first. Chrome is only launched when SPA detection confirms JS rendering is absolutely needed.

```
┌──────────────────────────────────────────────┐
│              Distill (.NET binary)            │
│                                               │
│  1. HttpClient fetches raw HTML               │
│         │                                     │
│         ▼                                     │
│  2. SPA detection (AngleSharp):               │
│     is <body> empty / just <script> tags?     │
│         │                                     │
│     ┌───┴───┐                                 │
│     │ NO    │ YES                             │
│     ▼       ▼                                 │
│  STATIC   CDP PATH (lazy, single instance)    │
│  PATH       │                                 │
│     │       ├─ Stealth: spoof UA, strip       │
│     │       │  webdriver, disable automation  │
│     │       ├─ Page.navigate(url)             │
│     │       ├─ Wait for load + 2s settle      │
│     │       ├─ PageCleaner JS injection:      │
│     │       │  annotate badges, strip chrome  │
│     │       ├─ Extract outerHTML              │
│     │       └─ Reuse tab for next URL         │
│     │       │                                 │
│     ▼       ▼                                 │
│  3. Block detection: captcha? rate-limited?   │
│     → Fail with actionable error message      │
│         │                                     │
│         ▼                                     │
│  4. DOM cleanup                               │
│     Static: HtmlCleaner (AngleSharp)          │
│     CDP: already cleaned by PageCleaner JS    │
│         │                                     │
│         ▼                                     │
│  5. SmartReader extracts main content         │
│     Falls back to cleaned <body> if not       │
│     article-shaped (search results, etc.)     │
│         │                                     │
│         ▼                                     │
│  6. ReverseMarkdown → clean markdown output   │
└──────────────────────────────────────────────┘
```

### Static Path (default, no browser)
For static/server-rendered pages, skip Chrome entirely. Fetch with HttpClient, clean DOM with HtmlCleaner (AngleSharp), extract with SmartReader, convert with ReverseMarkdown. Pure .NET, instant, zero external dependencies. This handles the vast majority of websites.

### CDP Path (SPA rendering, last resort)
For JS-heavy pages (React, Vue, Angular SPAs), connect to Chrome/Edge via CDP:

1. **Lazy launch:** A single headless Chrome/Edge process is launched on first need and reused across all URLs
2. **Stealth:** UA spoofed as Safari, `navigator.webdriver` stripped, `AutomationControlled` blink feature disabled
3. **PageCleaner injection:** JS scripts run post-load to annotate image-only badges with text labels and strip nav/header/footer/cookie banners
4. **Block detection:** If a captcha/rate-limit page is returned, fail immediately with an actionable error instead of passing garbage downstream

The CDP communication is raw WebSocket + JSON — no Playwright, no NuGet browser packages. Just `System.Net.WebSockets.ClientWebSocket` and `System.Text.Json.Nodes`.

### Rate Limiting Protection

Lessons learned from AliExpress: aggressive e-commerce sites will IP-ban headless browsers after rapid-fire requests from fresh profiles.

Mitigations built into the architecture:
- **Static-first:** Most pages don't need Chrome at all
- **Browser reuse:** One Chrome instance across all URLs (not a new launch per request)
- **`--delay`:** Configurable delay between requests when processing multiple URLs
- **`--profile`:** Use real Chrome profile with established cookies/sessions
- **Block detection:** Catches captcha pages early, reports actionable remediation steps
- **Stealth:** UA, webdriver flag, automation feature all spoofed

## .NET Dependencies (NuGet)

| Package | Purpose | License |
|---------|---------|---------|
| **AngleSharp** | HTML5 parser, DOM manipulation, SPA detection | MIT |
| **SmartReader** | Mozilla Readability port — extracts main content | Apache-2.0 |
| **ReverseMarkdown** | HTML → clean Markdown conversion | MIT |

No other dependencies. CDP is implemented directly with `ClientWebSocket` + `System.Text.Json` (both built into .NET).

## CLI Interface

```bash
# Basic usage — auto-detects if SPA rendering is needed
distill https://www.aliexpress.com/item/1005008123522628.html

# Force CDP rendering (skip SPA detection)
distill https://www.aliexpress.com/item/1005008123522628.html --render

# Force static mode (skip CDP, just fetch HTML)
distill https://www.aliexpress.com/item/1005008123522628.html --static

# Output to file
distill https://www.aliexpress.com/item/1005008123522628.html -o output.md

# Specify Chrome/Edge binary path
distill https://example.com --browser /usr/bin/google-chrome

# Specify existing CDP endpoint (reuse running browser)
distill https://example.com --cdp ws://localhost:9222

# Set page load timeout (default 30s)
distill https://example.com --timeout 60000

# Multiple URLs
distill https://example.com https://example.org

# Pipe-friendly: read URLs from stdin
cat urls.txt | distill --stdin
```

## SPA Detection Heuristic

After fetching raw HTML with HttpClient, check:

1. `<body>` has very little text content (< 100 chars after stripping tags)
2. `<body>` contains a single mount point div (`<div id="app">`, `<div id="root">`, `<div id="__next">`, etc.)
3. Page has multiple `<script>` tags with large bundles
4. `<noscript>` tag contains a "enable JavaScript" message

If 2+ of these are true → SPA detected → use CDP path.

## Build & Publish

```bash
# Development
dotnet run -- https://example.com

# Publish as single self-contained binary
dotnet publish -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true -p:PublishTrimmed=true

# Result: a single ~10-15MB binary in bin/Release/net9.0/osx-arm64/publish/distill
```

Target frameworks: `net9.0` (latest LTS).
Runtime identifiers: `osx-arm64`, `osx-x64`, `linux-x64`, `win-x64`.

## Project Structure

```
Distill/
├── Distill.csproj
├── Program.cs                # CLI entry point, argument parsing, pipeline orchestration
├── Cdp/
│   ├── CdpClient.cs          # WebSocket CDP connection, message routing
│   ├── CdpCommands.cs        # High-level CDP ops: navigate, stealth inject, eval, extract
│   └── BrowserLauncher.cs    # Find and launch Chrome/Edge headless, profile support
├── Pipeline/
│   ├── Fetcher.cs             # HttpClient fetch with realistic UA/headers
│   ├── SpaDetector.cs         # Heuristic to decide if CDP is needed
│   ├── BlockDetector.cs       # Detect captcha/rate-limit pages, fail with actionable error
│   ├── PageCleaner.cs         # JS injection scripts for CDP path (badge annotation, chrome strip)
│   ├── HtmlCleaner.cs         # AngleSharp DOM cleanup for static path (same logic, no browser)
│   ├── ContentExtractor.cs    # SmartReader wrapper with fallback for non-article pages
│   └── MarkdownConverter.cs   # ReverseMarkdown wrapper with whitespace cleanup
└── PLAN.md
```

## Key Implementation Notes

- **Browser discovery:** Check common paths for Chrome/Edge on macOS/Linux/Windows. Also check `CHROME_PATH` / `EDGE_PATH` env vars.
- **CDP port management:** Use a random high port to avoid conflicts. Kill the browser process on exit.
- **Stealth (implemented):** UA spoofed as Safari, `navigator.webdriver` stripped via `Page.addScriptToEvaluateOnNewDocument`, `AutomationControlled` blink feature disabled, `chrome` object faked, plugins/languages spoofed.
- **Timeouts:** Default 30s page load timeout. Configurable via `--timeout`.
- **Error handling:** If no Chrome/Edge is found, clear error. If captcha detected, actionable error with remediation steps.
- **Output encoding:** UTF-8 to stdout. Handle emoji, CJK, RTL text correctly.
- **Profile support (implemented):** `--profile` flag reuses existing Chrome profile directory (cookies, session, history) to reduce bot detection.
- **Cookie/session support (future):** `--cookie` flag or `--cookie-file` for authenticated pages.

## Non-Goals (for v1)

- Not an MCP server (can be wrapped as one later)
- Not a crawler (single URL → markdown, not recursive)
- No structured data extraction (just markdown)
- No screenshot support
- No proxy support (add later)
