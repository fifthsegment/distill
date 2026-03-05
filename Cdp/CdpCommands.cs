using System.Text.Json;
using System.Text.Json.Nodes;

namespace Distill.Cdp;

/// <summary>
/// High-level CDP operations: create target → navigate → wait → inject JS → extract HTML → close.
/// </summary>
public sealed class CdpCommands : IAsyncDisposable
{
    private readonly CdpClient _browser;
    private readonly int _debuggingPort;
    private CdpClient? _page;
    private string? _targetId;

    public CdpCommands(CdpClient browserClient, int debuggingPort)
    {
        _browser = browserClient;
        _debuggingPort = debuggingPort;
    }

    /// <param name="preExtractScripts">JS scripts to run after page load but before HTML extraction.</param>
    public async Task<string> GetRenderedHtmlAsync(Uri url, TimeSpan timeout,
        IReadOnlyList<string>? preExtractScripts = null, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var token = cts.Token;

        await OpenPage(token);
        await Navigate(url, token);
        await ScrollAndCapture(token);

        if (preExtractScripts is not null)
        {
            foreach (var script in preExtractScripts)
                await EvalAsync(script, token);
        }

        return await EvalAsync("document.documentElement.outerHTML", token);
    }

    public async Task<string> EvalOnPageAsync(Uri url, string expression, TimeSpan timeout,
        CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var token = cts.Token;

        await OpenPage(token);
        await Navigate(url, token);

        return await EvalAsync(expression, token);
    }

    private async Task OpenPage(CancellationToken ct)
    {
        var createResult = await _browser.SendAsync("Target.createTarget",
            new JsonObject { ["url"] = "about:blank" }, ct);
        _targetId = createResult.GetProperty("targetId").GetString()!;

        var pageWsUrl = await FindTargetWsUrl(_targetId, ct)
            ?? throw new InvalidOperationException($"Could not find WebSocket URL for target {_targetId}");

        _page = new CdpClient();
        await _page.ConnectAsync(new Uri(pageWsUrl), ct);
    }

    private async Task Navigate(Uri url, CancellationToken ct)
    {
        await _page!.SendAsync("Page.enable", null, ct);
        await _page.SendAsync("Network.enable", null, ct);

        await _page.SendAsync("Network.setUserAgentOverride", new JsonObject
        {
            ["userAgent"] = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.3 Safari/605.1.15",
            ["acceptLanguage"] = "en-US,en;q=0.9",
            ["platform"] = "macOS",
        }, ct);

        await _page.SendAsync("Page.addScriptToEvaluateOnNewDocument",
            new JsonObject { ["source"] = StealthScript }, ct);

        var loadDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _page.OnEvent("Page.loadEventFired", _ => loadDone.TrySetResult());

        await _page.SendAsync("Page.navigate",
            new JsonObject { ["url"] = url.ToString() }, ct);

        await using (ct.Register(() => loadDone.TrySetCanceled()))
            await loadDone.Task;

        // After load event, wait for network to go quiet. SPAs fire load early then
        // fetch data via XHR/fetch. This replaces the old fixed-delay approach.
        await WaitForNetworkIdle(ct);
    }

    /// <summary>
    /// Tracks in-flight network requests and waits until none are pending for a sustained
    /// quiet period. Much more reliable than fixed delays for SPAs that fetch data post-load.
    /// </summary>
    private async Task WaitForNetworkIdle(CancellationToken ct, int quietMs = 1500, int maxWaitMs = 15_000)
    {
        int inflight = 0;
        var lastActivity = DateTime.UtcNow;
        var started = DateTime.UtcNow;

        _page!.OnEvent("Network.requestWillBeSent", _ =>
        {
            Interlocked.Increment(ref inflight);
            lastActivity = DateTime.UtcNow;
        });
        _page.OnEvent("Network.loadingFinished", _ =>
        {
            Interlocked.Decrement(ref inflight);
            lastActivity = DateTime.UtcNow;
        });
        _page.OnEvent("Network.loadingFailed", _ =>
        {
            Interlocked.Decrement(ref inflight);
            lastActivity = DateTime.UtcNow;
        });

        while ((DateTime.UtcNow - started).TotalMilliseconds < maxWaitMs)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(200, ct);

            if (inflight <= 0 && (DateTime.UtcNow - lastActivity).TotalMilliseconds >= quietMs)
                return;
        }
    }

    /// <summary>
    /// Scrolls the page in gentle, human-like increments to trigger lazy-loaded and
    /// virtually-rendered content. Does NOT scroll back to top — virtual scroll SPAs
    /// de-render elements when they leave the viewport, so we capture outerHTML with
    /// all elements still materialized.
    /// </summary>
    private async Task ScrollAndCapture(CancellationToken ct)
    {
        const string scrollScript = """
            (async () => {
                const delay = ms => new Promise(r => setTimeout(r, ms + Math.random() * ms * 0.4));
                const vh = window.innerHeight;
                const totalH = () => Math.max(document.body.scrollHeight, document.documentElement.scrollHeight);

                let y = 0;
                const maxScroll = Math.min(totalH(), vh * 6);
                while (y < maxScroll) {
                    const step = vh * (0.6 + Math.random() * 0.4);
                    y = Math.min(y + step, maxScroll);
                    window.scrollTo({ top: y, behavior: 'smooth' });
                    await delay(500);
                }

                // Brief pause at bottom for final lazy loads, then return to top
                // so the extracted HTML has proper document order
                await delay(600);
                window.scrollTo(0, 0);
                await delay(300);
                return totalH().toString();
            })()
            """;

        await EvalAsync(scrollScript, awaitPromise: true, ct);

        // After scroll, wait for any newly-triggered network requests to complete
        await WaitForNetworkIdle(ct, quietMs: 800, maxWaitMs: 5_000);
    }

    private const string StealthScript = """
        Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
        window.chrome = { runtime: {}, loadTimes: function(){}, csi: function(){} };
        const originalQuery = window.navigator.permissions.query;
        window.navigator.permissions.query = (parameters) =>
          parameters.name === 'notifications'
            ? Promise.resolve({ state: Notification.permission })
            : originalQuery(parameters);
        Object.defineProperty(navigator, 'plugins', {
          get: () => [1, 2, 3, 4, 5]
        });
        Object.defineProperty(navigator, 'languages', {
          get: () => ['en-US', 'en']
        });
        """;

    private Task<string> EvalAsync(string expression, CancellationToken ct) =>
        EvalAsync(expression, awaitPromise: false, ct);

    private async Task<string> EvalAsync(string expression, bool awaitPromise, CancellationToken ct)
    {
        var opts = new JsonObject
        {
            ["expression"] = expression,
            ["returnByValue"] = true,
        };
        if (awaitPromise) opts["awaitPromise"] = true;

        var result = await _page!.SendAsync("Runtime.evaluate", opts, ct);

        var inner = result.GetProperty("result");
        if (inner.TryGetProperty("value", out var val) && val.ValueKind == JsonValueKind.String)
            return val.GetString()!;

        return inner.ToString();
    }

    private async Task<string?> FindTargetWsUrl(string targetId, CancellationToken ct)
    {
        using var http = new HttpClient();
        var json = await http.GetStringAsync($"http://localhost:{_debuggingPort}/json/list", ct);
        var targets = JsonDocument.Parse(json).RootElement;

        foreach (var target in targets.EnumerateArray())
        {
            if (target.TryGetProperty("id", out var id) && id.GetString() == targetId &&
                target.TryGetProperty("webSocketDebuggerUrl", out var ws))
            {
                return ws.GetString();
            }
        }
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_page is not null)
            await _page.DisposeAsync();

        if (_targetId is not null)
        {
            try
            {
                await _browser.SendAsync("Target.closeTarget",
                    new JsonObject { ["targetId"] = _targetId });
            }
            catch
            {
                // Best effort cleanup
            }
        }
    }
}
