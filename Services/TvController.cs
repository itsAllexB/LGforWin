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
                await ConnectWithFallbackAsync(token).ConfigureAwait(false);

                if (string.IsNullOrEmpty(_device.ClientKey))
                    SetStatus(TvStatus.AwaitingPairing, "Accept the prompt on your TV");

                var key = await _client.RegisterAsync(
                    string.IsNullOrEmpty(_device.ClientKey) ? null : _device.ClientKey,
                    token).ConfigureAwait(false);

                if (!string.IsNullOrEmpty(key) && key != _device.ClientKey)
                {
                    _device.ClientKey = key;
                    ClientKeyUpdated?.Invoke(key);
                }

                SetStatus(TvStatus.Connected);
                delay = 1000; // reset backoff on success

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
    private async Task ConnectWithFallbackAsync(CancellationToken token)
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
                _client = new WebOsClient(_device.Host);
                _client.Disconnected += OnClientDisconnected;

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(6000);
                await _client.ConnectAsync(port, secure, timeout.Token).ConfigureAwait(false);

                _device.Secure = secure; // remember what worked for next time
                Log.Write($"Connected to {_device.Host} via {(secure ? "wss" : "ws")}:{port}");
                return;
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

    private async Task WaitWhileConnectedAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _client is { IsConnected: true })
            await Task.Delay(500, token).ConfigureAwait(false);
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
