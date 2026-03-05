# Distill

CLI that converts a URL to clean markdown. Outputs to stdout.

## Install

Requires .NET 10 SDK and Chrome or Edge.

```bash
git clone https://github.com/fifthsegment/distill.git
cd distill
dotnet publish -c Release -r osx-arm64    # macOS ARM
dotnet publish -c Release -r osx-x64      # macOS Intel
dotnet publish -c Release -r linux-x64    # Linux
dotnet publish -c Release -r win-x64      # Windows
```

Binary: `bin/Release/net10.0/<rid>/publish/distill`

## MCP configuration

```json
{
  "mcpServers": {
    "distill": {
      "command": "/absolute/path/to/distill",
      "args": ["--stdin"]
    }
  }
}
```

With `--stdin`, send one URL per line on stdin. Output is markdown on stdout. Errors go to stderr.

## Options

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
--version         Print version and exit
```

Without flags, Distill auto-escalates: static fetch first, then headless Chrome if the page needs JS or blocks the request, then visible Chrome with a real profile if headless gets captcha'd.

## License

[CC BY-NC 4.0](https://creativecommons.org/licenses/by-nc/4.0/) — non-commercial use only.
