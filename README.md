# Distill

CLI that converts a URL to clean markdown. Outputs to stdout. Runs as an MCP server with `--mcp`.

## Install

```bash
curl -sfL https://raw.githubusercontent.com/fifthsegment/distill/main/install.sh | bash
```

Installs to `~/.local/bin`. Set `DISTILL_INSTALL_DIR` to change. Requires Chrome or Edge installed.

## MCP configuration

```json
{
  "mcpServers": {
    "distill": {
      "command": "~/.local/bin/distill",
      "args": ["--mcp"]
    }
  }
}
```

This starts Distill as a JSON-RPC 2.0 MCP server over stdio. It exposes one tool:

**`distill`** — Convert a web page URL to clean, LLM-ready markdown.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `url` | string | yes | The URL to convert |
| `render` | boolean | no | Force browser rendering for JS-heavy pages |
| `visible` | boolean | no | Launch Chrome visibly to bypass headless detection |
| `profile` | string | no | Path to Chrome profile directory |

## CLI usage

```
distill <url> [options]

--render          Force browser rendering (for JS-heavy / SPA pages)
--static          Force static HTTP fetch (skip browser entirely)
--visible         Launch Chrome visibly (bypasses headless detection)
--profile <dir>   Reuse existing Chrome profile (cookies, login sessions)
--timeout <ms>    Override timeout (default: 30s static, 60s render, 90s visible)
--delay <ms>      Delay between requests when processing multiple URLs
-o <file>         Write output to file instead of stdout
--browser <path>  Path to Chrome/Edge binary
--cdp <ws-url>    Connect to an already-running Chrome DevTools endpoint
--stdin           Read URLs from stdin, one per line
--mcp             Run as MCP server (JSON-RPC over stdio)
--version         Print version and exit
```

Without flags, Distill auto-escalates: static fetch first, then headless Chrome if the page needs JS or blocks the request, then visible Chrome with a real profile if headless gets captcha'd.

## License

[CC BY-NC 4.0](https://creativecommons.org/licenses/by-nc/4.0/) — non-commercial use only.
