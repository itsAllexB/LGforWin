using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using LGforWin.Models;
using LGforWin.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace LGforWin.ViewModels;

/// <summary>UI-facing wrapper around one <see cref="TvDevice"/> and its <see cref="TvController"/>.</summary>
public sealed partial class TvViewModel : ObservableObject, IDisposable
{
    private readonly TvController _controller;
    private readonly DispatcherQueue _dispatcher;
    private readonly Action _persist;
    private bool _suppressSend;

    public TvViewModel(TvDevice device, DispatcherQueue dispatcher, Action persist,
        ObservableCollection<MonitorInfo> screens, Func<int?>? startupBrightness = null)
    {
        Device = device;
        _dispatcher = dispatcher;
        _persist = persist;
        AvailableScreens = screens;
        _backlight = device.LastBacklight;

        _controller = new TvController(device) { GetStartupBrightness = startupBrightness };
        _controller.StatusChanged += OnStatusChanged;
        _controller.ClientKeyUpdated += _ => _persist();
        _controller.BacklightReported += OnBacklightReported;
    }

    public TvDevice Device { get; }

    public string Name => Device.Name;
    public string Host => Device.Host;

    /// <summary>Shared list of connected displays (owned by MainViewModel), for the OSD-screen picker.</summary>
    public ObservableCollection<MonitorInfo> AvailableScreens { get; }

    /// <summary>The display this TV is paired to for its OSD; null when unpaired or disconnected.</summary>
    public MonitorInfo? SelectedScreen
    {
        get => AvailableScreens.FirstOrDefault(m => m.Id == Device.PairedScreenId);
        set
        {
            // Ignore null (transient clears while the list refreshes / a paired screen is unplugged)
            // so we never wipe a saved pairing.
            if (value is null || value.Id == Device.PairedScreenId) return;
            Device.PairedScreenId = value.Id;
            _persist();
            OnPropertyChanged();
        }
    }

    /// <summary>Only show the picker when there's a choice to make (more than one display).</summary>
    public Visibility ScreenPickerVisibility =>
        AvailableScreens.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Re-evaluate the picker after the shared display list changes.</summary>
    public void RefreshScreens()
    {
        OnPropertyChanged(nameof(SelectedScreen));
        OnPropertyChanged(nameof(ScreenPickerVisibility));
    }

    [ObservableProperty] private int _backlight;
    [ObservableProperty] private string _statusText = "Disconnected";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusBrush))]
    private bool _isConnected;

    [ObservableProperty] private bool _isBusy;

    /// <summary>True when this is the TV that global hotkeys control. Set by MainViewModel.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveVisibility))]
    private bool _isActive;

    /// <summary>Green dot when connected, muted grey otherwise.</summary>
    public Brush StatusBrush => new SolidColorBrush(
        IsConnected ? Color.FromArgb(255, 38, 194, 129) : Color.FromArgb(255, 120, 120, 120));

    /// <summary>Shows the "Active" pill only for the active TV.</summary>
    public Microsoft.UI.Xaml.Visibility ActiveVisibility =>
        IsActive ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    partial void OnBacklightChanged(int value)
    {
        if (_suppressSend) return;
        _controller.SetBacklight(value);
        _persist();
    }

    /// <summary>Nudge brightness by a signed step (used by global hotkeys).</summary>
    public void Nudge(int step) => Backlight = Math.Clamp(Backlight + step, 0, 100);

    public Task StartAsync() => _controller.StartAsync();

    private void OnStatusChanged(TvStatus status, string? message)
    {
        _dispatcher.TryEnqueue(() =>
        {
            IsConnected = status == TvStatus.Connected;
            IsBusy = status is TvStatus.Connecting or TvStatus.AwaitingPairing;
            StatusText = message ?? status switch
            {
                TvStatus.Connected => "Connected",
                TvStatus.Connecting => "Connecting…",
                TvStatus.AwaitingPairing => "Accept the prompt on your TV",
                TvStatus.Error => "Error",
                _ => "Disconnected"
            };
        });
    }

    private void OnBacklightReported(int value)
    {
        _dispatcher.TryEnqueue(() =>
        {
            _suppressSend = true;
            Backlight = value;
            _suppressSend = false;
        });
    }

    public void Dispose() => _controller.Dispose();
}
