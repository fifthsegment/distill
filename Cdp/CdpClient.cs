using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Distill.Cdp;

public sealed class CdpClient : IAsyncDisposable
{
    private readonly ClientWebSocket _ws = new();
    private int _nextId;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ConcurrentDictionary<string, Action<JsonElement>> _eventHandlers = new();
    private CancellationTokenSource? _readCts;
    private Task? _readLoop;

    public async Task ConnectAsync(Uri wsUri, CancellationToken ct = default)
    {
        await _ws.ConnectAsync(wsUri, ct);
        _readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _readLoop = Task.Run(() => ReadLoop(_readCts.Token), _readCts.Token);
    }

    public void OnEvent(string method, Action<JsonElement> handler)
    {
        _eventHandlers[method] = handler;
    }

    public async Task<JsonElement> SendAsync(string method, JsonObject? parameters = null,
        CancellationToken ct = default)
    {
        int id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var message = new JsonObject { ["id"] = id, ["method"] = method };
        if (parameters is not null)
            message["params"] = parameters;

        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        await using var _ = cts.Token.Register(() => tcs.TrySetCanceled());

        return await tcs.Task;
    }

    private async Task ReadLoop(CancellationToken ct)
    {
        var buffer = new byte[1024 * 256];
        var messageBuffer = new MemoryStream();

        while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
        {
            try
            {
                var result = await _ws.ReceiveAsync(buffer, ct);
                messageBuffer.Write(buffer, 0, result.Count);

                if (!result.EndOfMessage)
                    continue;

                var json = Encoding.UTF8.GetString(messageBuffer.ToArray());
                messageBuffer.SetLength(0);

                var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("id", out var idProp))
                {
                    int id = idProp.GetInt32();
                    if (_pending.TryRemove(id, out var tcs))
                    {
                        if (root.TryGetProperty("error", out var error))
                            tcs.SetException(new CdpException(error.ToString()));
                        else if (root.TryGetProperty("result", out var resultProp))
                            tcs.SetResult(resultProp.Clone());
                        else
                            tcs.SetResult(default);
                    }
                }
                else if (root.TryGetProperty("method", out var methodProp))
                {
                    var method = methodProp.GetString()!;
                    if (_eventHandlers.TryGetValue(method, out var handler))
                    {
                        root.TryGetProperty("params", out var parms);
                        handler(parms);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (WebSocketException)
            {
                break;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _readCts?.Cancel();

        if (_ws.State == WebSocketState.Open)
        {
            try
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            }
            catch
            {
                // Best effort
            }
        }

        if (_readLoop is not null)
        {
            try { await _readLoop; } catch { /* swallow */ }
        }

        _ws.Dispose();
        _readCts?.Dispose();

        foreach (var tcs in _pending.Values)
            tcs.TrySetCanceled();
        _pending.Clear();
    }
}

public class CdpException(string message) : Exception(message);
