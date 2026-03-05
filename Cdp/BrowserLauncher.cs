using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Distill.Cdp;

public sealed class BrowserLauncher : IAsyncDisposable
{
    private Process? _process;
    public int Port { get; private set; }

    public static string? FindBrowser(string? explicitPath = null)
    {
        if (!string.IsNullOrEmpty(explicitPath) && File.Exists(explicitPath))
            return explicitPath;

        var envPaths = new[] { "CHROME_PATH", "EDGE_PATH" };
        foreach (var env in envPaths)
        {
            var val = Environment.GetEnvironmentVariable(env);
            if (!string.IsNullOrEmpty(val) && File.Exists(val))
                return val;
        }

        IEnumerable<string> candidates;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            candidates =
            [
                "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
                "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
                "/Applications/Chromium.app/Contents/MacOS/Chromium",
            ];
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            candidates =
            [
                "/usr/bin/google-chrome",
                "/usr/bin/google-chrome-stable",
                "/usr/bin/chromium",
                "/usr/bin/chromium-browser",
                "/usr/bin/microsoft-edge",
                "/usr/bin/microsoft-edge-stable",
            ];
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            candidates =
            [
                Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"),
            ];
        }
        else
        {
            candidates = [];
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <param name="userProfileDir">If set, reuses an existing Chrome profile directory instead of a temp one.</param>
    /// <param name="visible">If true, launches Chrome in visible (non-headless) mode. Avoids headless detection.</param>
    public async Task<Uri> LaunchAsync(string browserPath, string? userProfileDir = null,
        bool visible = false, CancellationToken ct = default)
    {
        Port = GetFreePort();
        var userDataDir = userProfileDir ?? Path.Combine(Path.GetTempPath(), $"distill-chrome-{Port}");

        var args = new List<string>
        {
            $"--remote-debugging-port={Port}",
            $"--user-data-dir={userDataDir}",
            "--no-first-run",
            "--no-default-browser-check",
            "--disable-extensions",
            "--disable-background-networking",
            "--disable-sync",
            "--disable-blink-features=AutomationControlled",
            "--window-size=1920,1080",
            "--lang=en-US",
        };

        if (!visible)
        {
            args.Insert(0, "--headless=new");
            args.Add("--disable-gpu");
        }

        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = browserPath,
                Arguments = string.Join(' ', args),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = !visible,
            },
            EnableRaisingEvents = true,
        };

        _process.Start();

        var wsEndpoint = await WaitForDevToolsEndpoint(ct);
        return new Uri(wsEndpoint);
    }

    private async Task<string> WaitForDevToolsEndpoint(CancellationToken ct)
    {
        using var http = new HttpClient();
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var json = await http.GetStringAsync($"http://localhost:{Port}/json/version", ct);
                var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("webSocketDebuggerUrl", out var ws))
                    return ws.GetString()!;
            }
            catch
            {
                // Browser not ready yet
            }
            await Task.Delay(200, ct);
        }

        throw new TimeoutException($"Chrome DevTools did not become available on port {Port} within 10 seconds.");
    }

    private static int GetFreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is { HasExited: false })
        {
            try
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
            catch
            {
                // Best effort
            }
        }
        _process?.Dispose();
    }
}
