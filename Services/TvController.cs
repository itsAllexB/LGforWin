using System.Text.Json.Nodes;
using LGforWin.Models;

namespace LGforWin.Services;

public enum TvStatus { Disconnected, Connecting, AwaitingPairing, Connected, Error }

/// <summary>
/// High-level controller for one TV: owns the WebOsClient, drives the connect/pair
/// lifecycle with auto-reconnect, and exposes a debounced SetBacklight so dragging the
/// slider doesn't flood the socket.
/// </summary>
public sealed class TvController : IDisposable
{
    private const int DebounceMs = 120;

    /// <summary>Cap on a single SSAP request, so a dead-but-open socket can't hang a caller forever.</summary>
    private const int RequestTimeoutMs = 3000;

    /// <summary>How many times, and how far apart, PowerOnAsync retries waking an unresponsive TV.</summary>
    private const int WakeAttempts = 4;
    private const int WakeRetryDelayMs = 3000;

    /// <summary>How often to prove the socket is still alive while connected.</summary>
    private const int HeartbeatMs = 30_000;

    private readonly TvDevice _device;
    private WebOsClient? _client;
    private CancellationTokenSource _lifetime = new();

    private readonly object _gate = new();
    private Timer? _debounce;
    private int _pendingValue;
    private bool _hasPending;
    private bool _connecting;

    public TvController(TvDevice device) => _device = device;

    public TvDevice Device => _device;
    public TvStatus Status { get; private set; } = TvStatus.Disconnected;

    /// <summary>
    /// Optional: returns the brightness to apply once, on the first successful connect this
    /// session (schedule catch-up). Return null to skip.
    /// </summary>
    public Func<int?>? GetStartupBrightness { get; set; }

    private bool _startupApplied;

    /// <summary>Fires (status, optional message) whenever the connection state changes.</summary>
    public event Action<TvStatus, string?>? StatusChanged;

    /// <summary>Fires after a successful pairing with a new client-key to persist.</summary>
    public event Action<string>? ClientKeyUpdated;

    /// <summary>Fires when the TV's current backlight is read, so the UI can sync the slider.</summary>
    public event Action<int>? BacklightReported;

    /// <summary>Fires after the TV's MAC address is learned via ARP, so it can be persisted for WoL.</summary>
    public event Action<string>? MacAddressResolved;

    /// <summary>Fires when the TV reports a new power state ("Active", "Screen Off", "Suspend", …),
    /// so the UI can distinguish a live socket from a TV that's actually switched on.</summary>
    public event Action<string>? PowerStateReported;

    private void SetStatus(TvStatus s, string? message = null)
    {
        Status = s;
        StatusChanged?.Invoke(s, message);
    }

    /// <summary>Connects and pairs, retrying with backoff until disposed.</summary>
    public async Task StartAsync()
    {
        lock (_gate)
        {
            if (_connecting) return;
            _connecting = true;
        }

        var token = _lifetime.Token;
        var delay = 1000;
        while (!token.IsCancellationRequested)
        {
            try
            {
                SetStatus(TvStatus.Connecting);
                var client = await ConnectWithFallbackAsync(token).ConfigureAwait(false);

                if (string.IsNullOrEmpty(_device.ClientKey))
                    SetStatus(TvStatus.AwaitingPairing, "Accept the prompt on your TV");

                var key = await client.RegisterAsync(
                    string.IsNullOrEmpty(_device.ClientKey) ? null : _device.ClientKey,
                    token).ConfigureAwait(false);

                if (!string.IsNullOrEmpty(key) && key != _device.ClientKey)
                {
                    _device.ClientKey = key;
                    ClientKeyUpdated?.Invoke(key);
                }

                SetStatus(TvStatus.Connected);
                delay = 1000; // reset backoff on success

                ResolveMacInBackground(token);
                await ReadBacklightAsync(token).ConfigureAwait(false);

                // Schedule catch-up: once per session, after the read, apply the in-effect value.
                if (!_startupApplied)
                {
                    _startupApplied = true;
                    if (GetStartupBrightness?.Invoke() is int target)
                    {
                        _device.LastBacklight = target;
                        BacklightReported?.Invoke(target); // sync the slider
                        SetBacklight(target);              // push to the TV
                    }
                }

                // Stay alive until the socket drops or we're disposed.
                await WaitWhileConnectedAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                SetStatus(TvStatus.Error, ex.Message);
            }

            CleanupClient();
            if (token.IsCancellationRequested) break;

            try { await Task.Delay(delay, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            delay = Math.Min(delay * 2, 15000); // exponential backoff, capped at 15s
        }

        lock (_gate) _connecting = false;
    }

    // webOS uses plain ws://:3000 on older firmware and secure wss://:3001 on newer
    // (2022+) models. Try the remembered transport first, then the other.
    private async Task<WebOsClient> ConnectWithFallbackAsync(CancellationToken token)
    {
        var transports = _device.Secure
            ? new[] { (port: 3001, secure: true), (port: 3000, secure: false) }
            : new[] { (port: 3000, secure: false), (port: 3001, secure: true) };

        Exception? last = null;
        foreach (var (port, secure) in transports)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var client = new WebOsClient(_device.Host);
                _client = client;
                client.Disconnected += OnClientDisconnected;

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(6000);
                await client.ConnectAsync(port, secure, timeout.Token).ConfigureAwait(false);

                _device.Secure = secure; // remember what worked for next time
                Log.Write($"Connected to {_device.Host} via {(secure ? "wss" : "ws")}:{port}");
                return client;
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                last = ex;
                CleanupClient();
            }
        }

        throw new IOException(
            $"Can't reach the TV at {_device.Host} on port 3000 or 3001. " +
            "Check the IP, that the TV is on, and that network/mobile control is enabled.",
            last);
    }

    // Stays parked while the socket is healthy. A TV that loses power (or has its HDMI signal cut
    // and powers itself off) doesn't close the socket — TCP can take minutes to notice — so poll
    // the TV periodically and treat silence as a drop. Without this the UI reports "Connected"
    // long after the TV is gone, and commands vanish into a dead socket.
    private async Task WaitWhileConnectedAsync(CancellationToken token)
    {
        var sinceProbe = 0;
        while (!token.IsCancellationRequested && _client is { IsConnected: true })
        {
            await Task.Delay(500, token).ConfigureAwait(false);

            sinceProbe += 500;
            if (sinceProbe < HeartbeatMs) continue;
            sinceProbe = 0;

            if (await GetPowerStateAsync(token).ConfigureAwait(false) is null
                && !token.IsCancellationRequested
                && _client is { IsConnected: true })
            {
                Log.Write($"{_device.Host}: heartbeat got no reply — dropping stale socket");
                DropStaleClient();
            }
        }
    }

    private void OnClientDisconnected()
    {
        if (!_lifetime.IsCancellationRequested)
            SetStatus(TvStatus.Disconnected, "Reconnecting…");
    }

    /// <summary>Queues a backlight change; only the latest value within the debounce window is sent.</summary>
    public void SetBacklight(int value)
    {
        value = Math.Clamp(value, 0, 100);
        _device.LastBacklight = value;
        lock (_gate)
        {
            _pendingValue = value;
            _hasPending = true;
            _debounce ??= new Timer(_ => _ = FlushAsync());
            _debounce.Change(DebounceMs, Timeout.Infinite);
        }
    }

    private async Task FlushAsync()
    {
        int value;
        lock (_gate)
        {
            if (!_hasPending) return;
            value = _pendingValue;
            _hasPending = false;
        }

        var client = _client;
        if (client is null || !client.IsConnected) return;

        // OLED Light is the "backlight" key in the picture category. Setting it requires
        // the privileged luna call routed through the alert API (see WebOsClient).
        var lunaParams = new JsonObject
        {
            ["category"] = "picture",
            ["settings"] = new JsonObject { ["backlight"] = value.ToString() }
        };

        try
        {
            var resp = await client.LunaRequestAsync(SsapPayloads.LunaSetSystemSettings, lunaParams, _lifetime.Token)
                .ConfigureAwait(false);
            var ok = resp["returnValue"]?.GetValue<bool>() ?? false;
            if (!ok) Log.Write($"setBacklight {value} rejected by TV: {resp.ToJsonString()}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Write($"setBacklight {value} FAILED: {ex.Message}");
        }
    }

    // ----- Power -----

    /// <summary>Turns the TV fully off. Returns false if it isn't connected or the TV refused.</summary>
    public async Task<bool> TurnOffAsync(CancellationToken ct = default)
    {
        var client = _client;
        if (client is null || !client.IsConnected) return false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RequestTimeoutMs);
            var resp = await client.RequestAsync(SsapPayloads.TurnOff, null, timeout.Token).ConfigureAwait(false);
            var ok = resp["returnValue"]?.GetValue<bool>() ?? true;
            Log.Write($"{_device.Host}: turnOff -> {(ok ? "ok" : resp.ToJsonString())}");
            return ok;
        }
        catch (Exception ex)
        {
            Log.Write($"{_device.Host}: turnOff FAILED: {ex.Message}");
            return false;
        }
    }

    /// <summary>How a power request to the TV ended — the distinction that matters is whether the
    /// TV answered at all, since an error reply still proves it's awake and the socket is live.</summary>
    private enum TvReply { Ok, Refused, Unreachable }

    /// <summary>Blanks the panel ("screen off") — webOS keeps running so the picture returns instantly.</summary>
    public async Task<bool> ScreenOffAsync(CancellationToken ct = default) =>
        await ScreenCommandAsync(SsapPayloads.TurnOffScreen, SsapPayloads.TurnOffScreenLegacy, ct)
            .ConfigureAwait(false) == TvReply.Ok;

    /// <summary>Un-blanks the panel after <see cref="ScreenOffAsync"/>.</summary>
    public async Task<bool> ScreenOnAsync(CancellationToken ct = default) =>
        await ScreenCommandAsync(SsapPayloads.TurnOnScreen, SsapPayloads.TurnOnScreenLegacy, ct)
            .ConfigureAwait(false) == TvReply.Ok;

    // Screen on/off moved services across firmware generations; try current then legacy. Each
    // attempt is capped, because a TV that died while the socket still looked open would
    // otherwise never answer at all.
    private async Task<TvReply> ScreenCommandAsync(string uri, string legacyUri, CancellationToken ct)
    {
        var client = _client;
        if (client is null || !client.IsConnected) return TvReply.Unreachable;

        var answered = false;
        foreach (var u in new[] { uri, legacyUri })
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(RequestTimeoutMs);
                var payload = new JsonObject { ["standbyMode"] = "active" };
                var resp = await client.RequestAsync(u, payload, timeout.Token).ConfigureAwait(false);
                answered = true;
                if (resp["returnValue"]?.GetValue<bool>() ?? true)
                {
                    Log.Write($"{_device.Host}: {u} ok");
                    return TvReply.Ok;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return TvReply.Unreachable; }
            catch (OperationCanceledException)
            {
                Log.Write($"{_device.Host}: {u} timed out"); // no reply at all — socket is dead
            }
            catch (Exception ex)
            {
                // The TV replied with an error (e.g. "500" when the screen is already on).
                // That's a refusal, not a dead socket.
                answered = true;
                Log.Write($"{_device.Host}: {u} refused: {ex.Message}");
            }
        }
        return answered ? TvReply.Refused : TvReply.Unreachable;
    }

    /// <summary>
    /// Asks the TV for its power state ("Active", "Screen Off", "Suspend", "Power Off").
    /// Returns null when it doesn't answer within the timeout — which is exactly how we tell a
    /// live socket from one that's open but dead.
    /// </summary>
    private async Task<string?> GetPowerStateAsync(CancellationToken ct)
    {
        var client = _client;
        if (client is null || !client.IsConnected) return null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RequestTimeoutMs);
            var resp = await client.RequestAsync(SsapPayloads.GetPowerState, null, timeout.Token)
                .ConfigureAwait(false);
            var state = resp["state"]?.ToString();
            if (state is not null && state != _lastPowerState)
            {
                _lastPowerState = state;
                Log.Write($"{_device.Host}: powerState = {state}");
                PowerStateReported?.Invoke(state);
            }
            return state;
        }
        catch
        {
            return null;
        }
    }

    private string? _lastPowerState;

    /// <summary>True for a state in which the panel is lit or can be lit without a full power-on.</summary>
    private static bool IsAwakeState(string state) =>
        !state.Contains("Suspend", StringComparison.OrdinalIgnoreCase) &&
        !state.Contains("Power Off", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Powers the TV on, retrying until it answers.
    ///
    /// Neither wake path is trustworthy alone. The TV may only have its panel blanked, in which
    /// case screen-on is what's needed. But it may instead have powered itself off after Windows
    /// stopped driving the HDMI output ("no signal"), and that leaves our socket open-but-dead
    /// for minutes — <see cref="WebOsClient.IsConnected"/> keeps saying true while nothing gets
    /// through. So we ask the TV what state it's in: an answer proves the socket is live and tells
    /// us whether to un-blank; silence means the socket is stale and only Wake-on-LAN will do.
    /// WoL is harmless to a TV that's already awake, so it goes out on every attempt.
    /// </summary>
    public async Task PowerOnAsync(CancellationToken ct = default)
    {
        var hasMac = !string.IsNullOrEmpty(_device.MacAddress);
        if (!hasMac && _client is not { IsConnected: true })
        {
            Log.Write($"{_device.Host}: can't wake — MAC address not learned yet");
            return;
        }

        for (var attempt = 0; attempt < WakeAttempts && !ct.IsCancellationRequested; attempt++)
        {
            if (hasMac) await WakeOnLan.SendAsync(_device.MacAddress, ct).ConfigureAwait(false);

            if (_client is { IsConnected: true })
            {
                var state = await GetPowerStateAsync(ct).ConfigureAwait(false);
                if (state is null)
                {
                    Log.Write($"{_device.Host}: no reply to getPowerState — stale socket, reconnecting");
                    DropStaleClient();
                }
                else if (state.Contains("Screen Off", StringComparison.OrdinalIgnoreCase))
                {
                    await ScreenOnAsync(ct).ConfigureAwait(false);
                    return;
                }
                else if (IsAwakeState(state))
                {
                    return; // already awake — nothing to do
                }
                // Any other state (Suspend / Power Off) means the TV is going down or already
                // down: keep the WoL retries coming.
            }

            try { await Task.Delay(WakeRetryDelayMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            if (_client is { IsConnected: true }) return; // it came back on its own
        }
    }

    // Tears down a socket the TV is no longer answering on. The reconnect loop is parked in
    // WaitWhileConnectedAsync, which exits as soon as the client is gone.
    private void DropStaleClient()
    {
        CleanupClient();
        if (!_lifetime.IsCancellationRequested) SetStatus(TvStatus.Disconnected, "Reconnecting…");
    }

    /// <summary>
    /// Turns the TV off if it's awake, wakes it if it isn't. Decided from the TV's own reported
    /// state rather than the socket flag, which keeps saying "connected" for minutes after a TV
    /// disappears — otherwise the button would try to switch off a TV that's already off.
    /// </summary>
    public async Task TogglePowerAsync(CancellationToken ct = default)
    {
        var state = await GetPowerStateAsync(ct).ConfigureAwait(false);
        if (state is not null && IsAwakeState(state))
        {
            await TurnOffAsync(ct).ConfigureAwait(false);
            return;
        }

        if (state is null && _client is { IsConnected: true }) DropStaleClient();
        await PowerOnAsync(ct).ConfigureAwait(false);
    }

    /// <summary>True when the TV can be woken over the network (MAC already learned).</summary>
    public bool CanWake => !string.IsNullOrEmpty(_device.MacAddress);

    // ARP the TV's MAC once per device while it's reachable, so WoL works after it goes off.
    private void ResolveMacInBackground(CancellationToken token)
    {
        if (!string.IsNullOrEmpty(_device.MacAddress)) return;
        _ = Task.Run(() =>
        {
            if (token.IsCancellationRequested) return;
            var mac = WakeOnLan.TryResolveMac(_device.Host);
            if (mac is null) return;
            _device.MacAddress = mac;
            Log.Write($"{_device.Host}: MAC learned {mac}");
            MacAddressResolved?.Invoke(mac);
        }, token);
    }

    private async Task ReadBacklightAsync(CancellationToken token)
    {
        var client = _client;
        if (client is null || !client.IsConnected) return;

        var payload = new JsonObject
        {
            ["category"] = "picture",
            ["keys"] = new JsonArray("backlight")
        };
        try
        {
            var resp = await client.RequestAsync(SsapPayloads.GetSystemSettings, payload, token)
                .ConfigureAwait(false);
            var settings = resp["settings"] as JsonObject;
            var raw = settings?["backlight"]?.ToString();
            if (int.TryParse(raw, out var value))
            {
                _device.LastBacklight = value;
                BacklightReported?.Invoke(value);
            }
        }
        catch { /* non-fatal: keep last-known value */ }
    }

    private void CleanupClient()
    {
        if (_client is not null)
        {
            _client.Disconnected -= OnClientDisconnected;
            _client.Dispose();
            _client = null;
        }
    }

    public void Dispose()
    {
        try { _lifetime.Cancel(); } catch { }
        lock (_gate)
        {
            _debounce?.Dispose();
            _debounce = null;
        }
        CleanupClient();
        _lifetime.Dispose();
    }
}
