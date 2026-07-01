using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LGforWin.Services;

/// <summary>
/// Low-level webOS SSAP client over a WebSocket. Handles the registration/pairing
/// handshake and correlates request ids to responses. One instance == one TV.
/// </summary>
public sealed class WebOsClient : IDisposable
{
    private readonly string _host;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;
    private int _nextId;

    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private TaskCompletionSource<string>? _registerTcs;

    public WebOsClient(string host) => _host = host;

    public bool IsConnected => _ws?.State == WebSocketState.Open;

    /// <summary>Raised on the receive loop when the socket drops, so callers can reconnect.</summary>
    public event Action? Disconnected;

    /// <summary>
    /// Opens the WebSocket and starts the background receive loop. When <paramref name="secure"/>
    /// is set, connects with wss:// and accepts the TV's self-signed certificate.
    /// </summary>
    public async Task ConnectAsync(int port, bool secure, CancellationToken ct = default)
    {
        Dispose(); // reset any prior state
        _cts = new CancellationTokenSource();
        _ws = new ClientWebSocket();
        if (secure)
            _ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;

        var scheme = secure ? "wss" : "ws";
        var uri = new Uri($"{scheme}://{_host}:{port}");
        await _ws.ConnectAsync(uri, ct).ConfigureAwait(false);
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
    }

    /// <summary>
    /// Performs the pairing handshake. If <paramref name="clientKey"/> is supplied the
    /// TV re-authorizes silently; otherwise it shows the on-screen prompt and this call
    /// completes once the user accepts. Returns the client-key to persist.
    /// </summary>
    public async Task<string> RegisterAsync(string? clientKey, CancellationToken ct = default)
    {
        if (_ws is null || _ws.State != WebSocketState.Open)
            throw new InvalidOperationException("Not connected.");

        _registerTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = NextId("register");
        var message = new JsonObject
        {
            ["id"] = id,
            ["type"] = "register",
            ["payload"] = SsapPayloads.BuildRegisterPayload(clientKey)
        };
        await SendRawAsync(message, ct).ConfigureAwait(false);

        using var reg = ct.Register(() => _registerTcs.TrySetCanceled(ct));
        return await _registerTcs.Task.ConfigureAwait(false);
    }

    /// <summary>Sends a request and awaits the correlated response payload.</summary>
    public async Task<JsonObject> RequestAsync(string uri, JsonObject? payload, CancellationToken ct = default)
    {
        if (_ws is null || _ws.State != WebSocketState.Open)
            throw new InvalidOperationException("Not connected.");

        var id = NextId("req");
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var message = new JsonObject
        {
            ["id"] = id,
            ["type"] = "request",
            ["uri"] = uri
        };
        if (payload is not null) message["payload"] = payload;

        await SendRawAsync(message, ct).ConfigureAwait(false);

        using var reg = ct.Register(() =>
        {
            if (_pending.TryRemove(id, out var t)) t.TrySetCanceled(ct);
        });
        return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Executes a privileged luna:// call indirectly via the notification-alert API — the
    /// only way unprivileged SSAP clients can write picture settings on stock TVs. Creates
    /// an alert whose button/onclose carries the luna URI + params, then closes it to fire it.
    /// Returns the createAlert response (its returnValue confirms the alert was accepted).
    /// </summary>
    public async Task<JsonObject> LunaRequestAsync(string lunaUri, JsonObject lunaParams, CancellationToken ct = default)
    {
        var full = "luna://" + lunaUri;
        JsonObject Clone() => (JsonObject)JsonNode.Parse(lunaParams.ToJsonString())!;

        var payload = new JsonObject
        {
            ["message"] = " ",
            ["modal"] = true,
            ["buttons"] = new JsonArray(new JsonObject
            {
                ["label"] = "",
                ["onClick"] = full,
                ["params"] = Clone()
            }),
            ["onclose"] = new JsonObject { ["uri"] = full, ["params"] = Clone() },
            ["type"] = "confirm"
        };

        var created = await RequestAsync(SsapPayloads.CreateAlert, payload, ct).ConfigureAwait(false);
        var alertId = created["alertId"]?.ToString();
        if (!string.IsNullOrEmpty(alertId))
            await RequestAsync(SsapPayloads.CloseAlert, new JsonObject { ["alertId"] = alertId }, ct).ConfigureAwait(false);
        return created;
    }

    private string NextId(string prefix) => $"{prefix}_{Interlocked.Increment(ref _nextId)}";

    private async Task SendRawAsync(JsonObject message, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await _ws!.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        var sb = new StringBuilder();
        try
        {
            while (!ct.IsCancellationRequested && _ws is not null && _ws.State == WebSocketState.Open)
            {
                sb.Clear();
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                        throw new WebSocketException("Closed by remote.");
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                }
                while (!result.EndOfMessage);

                HandleMessage(sb.ToString());
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch
        {
            // Surface drop so the controller can schedule a reconnect.
        }
        finally
        {
            FailAllPending();
            if (!ct.IsCancellationRequested) Disconnected?.Invoke();
        }
    }

    private void HandleMessage(string json)
    {
        JsonObject? msg;
        try { msg = JsonNode.Parse(json) as JsonObject; }
        catch { return; }
        if (msg is null) return;

        var type = msg["type"]?.GetValue<string>();
        var payload = msg["payload"] as JsonObject;

        // Registration: ignore the intermediate PROMPT response, complete on "registered".
        if (type == "registered")
        {
            var key = payload?["client-key"]?.GetValue<string>() ?? "";
            _registerTcs?.TrySetResult(key);
            return;
        }
        if (type == "error")
        {
            var error = msg["error"]?.GetValue<string>() ?? "TV returned an error";
            var id0 = msg["id"]?.GetValue<string>();
            if (id0 is not null && _pending.TryRemove(id0, out var t))
                t.TrySetException(new InvalidOperationException(error));
            else
                _registerTcs?.TrySetException(new InvalidOperationException(error));
            return;
        }

        var id = msg["id"]?.GetValue<string>();
        if (id is not null && _pending.TryRemove(id, out var tcs))
            tcs.TrySetResult(payload ?? new JsonObject());
    }

    private void FailAllPending()
    {
        foreach (var kv in _pending)
            if (_pending.TryRemove(kv.Key, out var t))
                t.TrySetException(new IOException("Connection lost."));
        _registerTcs?.TrySetException(new IOException("Connection lost."));
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _ws?.Abort(); } catch { }
        try { _ws?.Dispose(); } catch { }
        _ws = null;
        _cts?.Dispose();
        _cts = null;
    }
}
