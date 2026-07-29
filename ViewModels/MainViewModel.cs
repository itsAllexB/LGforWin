using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LGforWin.Models;
using LGforWin.Services;
using Microsoft.UI.Dispatching;

namespace LGforWin.ViewModels;

/// <summary>Outcome of a cursor-targeted brightness hotkey: a TV was adjusted, no paired TV was
/// under the cursor (prompt to pair), or there are no TVs at all.</summary>
public enum HotkeyTargetOutcome { Adjusted, Unpaired, None }

/// <summary>Top-level view model: TVs, schedules, settings and hotkey routing — shared across pages.</summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly DeviceStore _store;
    private readonly DispatcherQueue _dispatcher;

    public ObservableCollection<TvViewModel> Tvs { get; } = new();
    public ObservableCollection<ScheduleViewModel> Schedules { get; } = new();

    /// <summary>Connected displays, for pairing each TV to the screen it drives. Refreshed on demand.</summary>
    public ObservableCollection<MonitorInfo> Monitors { get; } = new();

    /// <summary>App version for the UI footer, e.g. "v1.0.0".</summary>
    public string AppVersion { get; } =
        "v" + (System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");

    public AppSettings Settings { get; }

    /// <summary>Raised when the user changes the theme (0=system,1=light,2=dark); App applies it.</summary>
    public event Action<int>? ThemeChanged;

    /// <summary>Raised when the hotkey modifier combo changes; App re-registers the global hotkeys.</summary>
    public event Action<uint>? HotkeyModifiersChanged;

    public MainViewModel(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _store = new DeviceStore();
        Settings = _store.LoadSettings();

        foreach (var device in _store.LoadDevices())
            Add(device, start: true);

        foreach (var schedule in Settings.Schedules)
            Schedules.Add(new ScheduleViewModel(schedule, PersistSchedules));
        SortSchedules();

        ActiveTv = Tvs.FirstOrDefault();
        RefreshMonitors();

        // Persist a new TV order when the user drag-reorders the list.
        Tvs.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Move) Persist();
        };
    }

    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private string _newHost = "";

    /// <summary>The TV that global hotkeys control. Bound to the list selection.</summary>
    [ObservableProperty] private TvViewModel? _activeTv;

    partial void OnActiveTvChanged(TvViewModel? oldValue, TvViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsActive = false;
        if (newValue is not null) newValue.IsActive = true;
    }

    // Settings surfaced for the UI; each setter persists immediately.
    public bool StartWithWindows
    {
        get => Settings.StartWithWindows;
        set
        {
            if (value == Settings.StartWithWindows) return;
            Settings.StartWithWindows = value;
            AutostartService.Apply(value);
            SaveSettings();
            OnPropertyChanged();
        }
    }

    /// <summary>How the window opens on launch: 0 = maximized, 1 = minimized, 2 = system tray.</summary>
    public int LaunchBehavior
    {
        get => Settings.LaunchBehavior;
        set
        {
            if (value == Settings.LaunchBehavior) return;
            Settings.LaunchBehavior = value;
            SaveSettings();
            OnPropertyChanged();
        }
    }

    public double HotkeyStep
    {
        get => Settings.HotkeyStep;
        set
        {
            var step = (int)Math.Clamp(value, 1, 25);
            if (step == Settings.HotkeyStep) return;
            Settings.HotkeyStep = step;
            SaveSettings();
            OnPropertyChanged();
        }
    }

    // Hotkey modifiers (Ctrl/Alt/Shift/Win), combined with the Up/Down arrows. At least one is
    // always kept — a bare arrow hotkey would swallow every arrow press system-wide.
    private const int ModAlt = 1, ModCtrl = 2, ModShift = 4, ModWin = 8;

    public bool HotkeyCtrl { get => HasMod(ModCtrl); set => SetMod(ModCtrl, value); }
    public bool HotkeyAlt { get => HasMod(ModAlt); set => SetMod(ModAlt, value); }
    public bool HotkeyShift { get => HasMod(ModShift); set => SetMod(ModShift, value); }
    public bool HotkeyWin { get => HasMod(ModWin); set => SetMod(ModWin, value); }

    /// <summary>Human-readable combo for display, e.g. "Ctrl + Alt + ↑ / ↓".</summary>
    public string HotkeyComboText
    {
        get
        {
            var parts = new List<string>();
            if (HotkeyCtrl) parts.Add("Ctrl");
            if (HotkeyWin) parts.Add("Win");
            if (HotkeyAlt) parts.Add("Alt");
            if (HotkeyShift) parts.Add("Shift");
            return parts.Count == 0 ? "↑ / ↓" : string.Join(" + ", parts) + " + ↑ / ↓";
        }
    }

    private bool HasMod(int bit) => (Settings.HotkeyModifiers & bit) != 0;

    private void SetMod(int bit, bool on)
    {
        var cur = Settings.HotkeyModifiers;
        var next = on ? cur | bit : cur & ~bit;
        if (next == 0) next = cur; // don't allow clearing the last modifier
        if (next != cur)
        {
            Settings.HotkeyModifiers = next;
            SaveSettings();
            HotkeyModifiersChanged?.Invoke((uint)next);
        }
        // Re-raise all four (so a rejected "clear the last one" reverts its checkbox) plus the label.
        OnPropertyChanged(nameof(HotkeyCtrl));
        OnPropertyChanged(nameof(HotkeyAlt));
        OnPropertyChanged(nameof(HotkeyShift));
        OnPropertyChanged(nameof(HotkeyWin));
        OnPropertyChanged(nameof(HotkeyComboText));
    }

    /// <summary>0 = follow system, 1 = light, 2 = dark.</summary>
    public int ThemeIndex
    {
        get => Settings.ThemeIndex;
        set
        {
            if (value == Settings.ThemeIndex) return;
            Settings.ThemeIndex = value;
            SaveSettings();
            OnPropertyChanged();
            ThemeChanged?.Invoke(value);
        }
    }

    // ----- On-screen display (OSD) -----

    /// <summary>Whether the brightness OSD appears when hotkeys change brightness.</summary>
    public bool OsdEnabled
    {
        get => Settings.Osd.Enabled;
        set
        {
            if (value == Settings.Osd.Enabled) return;
            Settings.Osd.Enabled = value;
            SaveSettings();
            OnPropertyChanged();
        }
    }

    /// <summary>Screen anchor (0=bottom-centre … 5=top-right). See <see cref="OsdSettings"/>.</summary>
    public int OsdPosition
    {
        get => Settings.Osd.Position;
        set
        {
            if (value == Settings.Osd.Position) return;
            Settings.Osd.Position = value;
            SaveSettings();
            OnPropertyChanged();
        }
    }

    /// <summary>Where the OSD shows (0=primary, 1=adjusted TV's screen, 2=cursor).</summary>
    public int OsdMonitor
    {
        get => Settings.Osd.Monitor;
        set
        {
            if (value == Settings.Osd.Monitor) return;
            Settings.Osd.Monitor = value;
            SaveSettings();
            OnPropertyChanged();
        }
    }

    /// <summary>How long the OSD lingers before fading, in seconds (0.5–5.0).</summary>
    public double OsdTimeoutSeconds
    {
        get => Settings.Osd.TimeoutSeconds;
        set
        {
            var clamped = Math.Clamp(value, 0.5, 5.0);
            if (Math.Abs(clamped - Settings.Osd.TimeoutSeconds) < 0.001) return;
            Settings.Osd.TimeoutSeconds = clamped;
            SaveSettings();
            OnPropertyChanged();
        }
    }

    /// <summary>Whether the targeted TV's name is shown in the OSD.</summary>
    public bool OsdShowTvName
    {
        get => Settings.Osd.ShowTvName;
        set
        {
            if (value == Settings.Osd.ShowTvName) return;
            Settings.Osd.ShowTvName = value;
            SaveSettings();
            OnPropertyChanged();
        }
    }

    /// <summary>Whether the OSD also appears when a schedule applies brightness.</summary>
    public bool OsdOnSchedule
    {
        get => Settings.Osd.ShowOnSchedule;
        set
        {
            if (value == Settings.Osd.ShowOnSchedule) return;
            Settings.Osd.ShowOnSchedule = value;
            SaveSettings();
            OnPropertyChanged();
        }
    }

    // ----- TV power automation (all TVs) -----

    /// <summary>Wake the TVs whenever the app starts (with autostart, that's Windows sign-in).</summary>
    public bool PowerOnAppStart
    {
        get => Settings.Power.OnAppStart;
        set { if (value != Settings.Power.OnAppStart) { Settings.Power.OnAppStart = value; SaveSettings(); OnPropertyChanged(); } }
    }

    /// <summary>Wake the TVs when the PC resumes from sleep.</summary>
    public bool PowerOnResume
    {
        get => Settings.Power.OnResume;
        set { if (value != Settings.Power.OnResume) { Settings.Power.OnResume = value; SaveSettings(); OnPropertyChanged(); } }
    }

    /// <summary>Turn the TVs off when the PC goes to sleep.</summary>
    public bool PowerOffOnSleep
    {
        get => Settings.Power.OffOnSleep;
        set { if (value != Settings.Power.OffOnSleep) { Settings.Power.OffOnSleep = value; SaveSettings(); OnPropertyChanged(); } }
    }

    /// <summary>Turn the TVs off when the PC shuts down, restarts or signs out.</summary>
    public bool PowerOffOnShutdown
    {
        get => Settings.Power.OffOnShutdown;
        set { if (value != Settings.Power.OffOnShutdown) { Settings.Power.OffOnShutdown = value; SaveSettings(); OnPropertyChanged(); } }
    }

    /// <summary>React when Windows turns the displays off after the idle timeout.</summary>
    public bool PowerFollowDisplayOff
    {
        get => Settings.Power.FollowDisplayOff;
        set { if (value != Settings.Power.FollowDisplayOff) { Settings.Power.FollowDisplayOff = value; SaveSettings(); OnPropertyChanged(); } }
    }

    /// <summary>0 = screen off (instant resume), 1 = full power off (wakes via WoL).</summary>
    public int PowerDisplayOffAction
    {
        get => Settings.Power.DisplayOffAction;
        set { if (value != Settings.Power.DisplayOffAction) { Settings.Power.DisplayOffAction = value; SaveSettings(); OnPropertyChanged(); } }
    }

    /// <summary>Wake the TVs when Windows turns the displays back on.</summary>
    public bool PowerFollowDisplayOn
    {
        get => Settings.Power.FollowDisplayOn;
        set { if (value != Settings.Power.FollowDisplayOn) { Settings.Power.FollowDisplayOn = value; SaveSettings(); OnPropertyChanged(); } }
    }

    /// <summary>Turns every TV fully off. Awaitable so shutdown/sleep handlers can block briefly on it.</summary>
    public Task TurnOffAllAsync() => Task.WhenAll(Tvs.Select(t => t.TurnOffAsync()));

    /// <summary>Blanks every TV's panel (webOS keeps running, resume is instant).</summary>
    public Task ScreenOffAllAsync() => Task.WhenAll(Tvs.Select(t => t.ScreenOffAsync()));

    /// <summary>Powers every TV on — un-blanks connected ones, wakes the rest via WoL.</summary>
    public Task PowerOnAllAsync() => Task.WhenAll(Tvs.Select(t => t.PowerOnAsync()));

    // ----- Adding TVs -----

    public bool CanAdd => !string.IsNullOrWhiteSpace(NewHost);
    partial void OnNewHostChanged(string value) => AddTvCommand.NotifyCanExecuteChanged();

    /// <summary>True when a TV with this host/IP is already in the list (used by discovery).</summary>
    public bool HasTvWithHost(string host) =>
        Tvs.Any(t => string.Equals(t.Host, host, StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds a TV found by network discovery (no-op if its IP is already added).</summary>
    public void AddDiscoveredTv(string name, string host)
    {
        if (HasTvWithHost(host)) return;
        Add(new TvDevice { Name = name, Host = host }, start: true);
        Persist();
    }

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void AddTv()
    {
        var device = new TvDevice
        {
            Name = string.IsNullOrWhiteSpace(NewName) ? "LG TV" : NewName.Trim(),
            Host = NewHost.Trim()
        };
        Add(device, start: true);
        Persist();
        NewName = "";
        NewHost = "";
    }

    [RelayCommand]
    private void RemoveTv(TvViewModel? vm)
    {
        if (vm is null) return;
        Tvs.Remove(vm);
        vm.Dispose();
        if (ReferenceEquals(ActiveTv, vm)) ActiveTv = Tvs.FirstOrDefault();
        Persist();
    }

    private void Add(TvDevice device, bool start)
    {
        var vm = new TvViewModel(device, _dispatcher, Persist, Monitors, StartupBrightness);
        Tvs.Add(vm);
        ActiveTv ??= vm;
        if (start) _ = vm.StartAsync();
    }

    /// <summary>Re-enumerates connected displays and refreshes each TV's screen picker.</summary>
    public void RefreshMonitors()
    {
        var list = MonitorService.List();
        Monitors.Clear();
        foreach (var m in list) Monitors.Add(m);
        foreach (var tv in Tvs) tv.RefreshScreens();
    }

    // Catch-up value applied to a TV on its first connect (null when disabled or no schedules).
    private int? StartupBrightness() =>
        Settings.ApplyScheduleOnStartup ? GetCurrentScheduledBrightness() : null;

    /// <summary>
    /// The brightness that should currently be in effect per the schedules: the most recent
    /// enabled schedule that has already passed today, wrapping to the latest one overall
    /// (i.e. last night's) if none have passed yet today. Null if there are no enabled schedules.
    /// </summary>
    public int? GetCurrentScheduledBrightness()
    {
        var enabled = Schedules.Where(s => s.Enabled).ToList();
        if (enabled.Count == 0) return null;

        var nowMinutes = DateTime.Now.Hour * 60 + DateTime.Now.Minute;
        static int Minutes(ScheduleViewModel s) => s.Model.Hour * 60 + s.Model.Minute;

        var inEffect = enabled
            .Where(s => Minutes(s) <= nowMinutes)
            .OrderByDescending(Minutes)
            .FirstOrDefault()
            ?? enabled.OrderByDescending(Minutes).First(); // wrap to last night's

        return inEffect.Model.Brightness;
    }

    /// <summary>
    /// Nudges brightness by a signed step from a global hotkey, targeting the TV on the screen the
    /// cursor is currently on. With a single TV it always targets that one (no pairing needed). With
    /// several TVs and the cursor on a screen no TV is paired to, it changes nothing and reports
    /// <see cref="HotkeyTargetOutcome.Unpaired"/> so the caller can prompt the user to pair.
    /// Must be invoked on the UI thread.
    /// </summary>
    public (HotkeyTargetOutcome Kind, string? Name, int Value, string? ScreenId) NudgeByCursor(int step)
    {
        if (Tvs.Count == 0) return (HotkeyTargetOutcome.None, null, 0, null);

        TvViewModel tv;
        if (Tvs.Count == 1)
        {
            tv = Tvs[0]; // only one TV → unambiguous, ignore the cursor
        }
        else
        {
            var cursorScreen = MonitorService.MonitorIdAtCursor();
            var match = string.IsNullOrEmpty(cursorScreen)
                ? null
                : Tvs.FirstOrDefault(t => t.Device.PairedScreenId == cursorScreen);
            if (match is null) return (HotkeyTargetOutcome.Unpaired, null, 0, null);
            tv = match;
        }

        tv.Nudge(step);
        return (HotkeyTargetOutcome.Adjusted, tv.Name, tv.Backlight, tv.Device.PairedScreenId);
    }

    /// <summary>Sets every TV to the given brightness (used by schedules). UI thread.</summary>
    public void ApplyBrightnessAll(int value)
    {
        foreach (var tv in Tvs) tv.Backlight = Math.Clamp(value, 0, 100);
    }

    // ----- Schedules -----

    /// <summary>When on, the in-effect schedule is applied to each TV on startup.</summary>
    public bool ApplyScheduleOnStartup
    {
        get => Settings.ApplyScheduleOnStartup;
        set
        {
            if (value == Settings.ApplyScheduleOnStartup) return;
            Settings.ApplyScheduleOnStartup = value;
            SaveSettings();
            OnPropertyChanged();
        }
    }

    public bool CanAddSchedule => Schedules.Count < 10;

    [RelayCommand(CanExecute = nameof(CanAddSchedule))]
    private void AddSchedule()
    {
        var model = new BrightnessSchedule();
        Schedules.Add(new ScheduleViewModel(model, PersistSchedules));
        PersistSchedules();
        AddScheduleCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void RemoveSchedule(ScheduleViewModel? schedule)
    {
        if (schedule is null) return;
        Schedules.Remove(schedule);
        PersistSchedules();
        AddScheduleCommand.NotifyCanExecuteChanged();
    }

    private void PersistSchedules()
    {
        SortSchedules();
        Settings.Schedules = Schedules.Select(s => s.Model).ToList();
        SaveSettings();
    }

    // Reorders the list by time in place (preserving item instances) so it stays chronological.
    private void SortSchedules()
    {
        var sorted = Schedules
            .OrderBy(s => s.Model.Hour * 60 + s.Model.Minute)
            .ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            var current = Schedules.IndexOf(sorted[i]);
            if (current != i) Schedules.Move(current, i);
        }
    }

    // ----- Backup / restore -----

    /// <summary>Returns the full configuration (TVs + settings) as a single JSON string.</summary>
    public string ExportSettings() => _store.SerializeBackup(Tvs.Select(t => t.Device), Settings);

    /// <summary>Replaces all TVs and settings from a backup string and reloads live state.</summary>
    public void ImportSettings(string json)
    {
        var data = _store.DeserializeBackup(json); // throws on invalid input (before we touch anything)
        _store.SaveDevices(data.Devices!);
        _store.SaveSettings(data.Settings!);
        ReloadFromStore();
    }

    private void ReloadFromStore()
    {
        // Rebuild TVs.
        foreach (var tv in Tvs) tv.Dispose();
        Tvs.Clear();
        ActiveTv = null;
        foreach (var device in _store.LoadDevices())
            Add(device, start: true);
        ActiveTv = Tvs.FirstOrDefault();
        RefreshMonitors();

        // Copy settings into the existing instance and refresh bound proxies.
        var s = _store.LoadSettings();
        Settings.HotkeyStep = s.HotkeyStep;
        Settings.HotkeyModifiers = s.HotkeyModifiers;
        Settings.StartWithWindows = s.StartWithWindows;
        Settings.LaunchBehavior = s.LaunchBehavior;
        Settings.WindowWidth = s.WindowWidth;
        Settings.WindowHeight = s.WindowHeight;
        Settings.ThemeIndex = s.ThemeIndex;
        Settings.ApplyScheduleOnStartup = s.ApplyScheduleOnStartup;
        Settings.Osd = s.Osd;
        Settings.Power = s.Power;
        Settings.Schedules = s.Schedules;

        Schedules.Clear();
        foreach (var schedule in Settings.Schedules)
            Schedules.Add(new ScheduleViewModel(schedule, PersistSchedules));
        SortSchedules();

        OnPropertyChanged(nameof(StartWithWindows));
        OnPropertyChanged(nameof(LaunchBehavior));
        OnPropertyChanged(nameof(HotkeyStep));
        OnPropertyChanged(nameof(ThemeIndex));
        OnPropertyChanged(nameof(ApplyScheduleOnStartup));
        OnPropertyChanged(nameof(OsdEnabled));
        OnPropertyChanged(nameof(OsdPosition));
        OnPropertyChanged(nameof(OsdMonitor));
        OnPropertyChanged(nameof(OsdTimeoutSeconds));
        OnPropertyChanged(nameof(OsdShowTvName));
        OnPropertyChanged(nameof(OsdOnSchedule));
        OnPropertyChanged(nameof(PowerOnAppStart));
        OnPropertyChanged(nameof(PowerOnResume));
        OnPropertyChanged(nameof(PowerOffOnSleep));
        OnPropertyChanged(nameof(PowerOffOnShutdown));
        OnPropertyChanged(nameof(PowerFollowDisplayOff));
        OnPropertyChanged(nameof(PowerDisplayOffAction));
        OnPropertyChanged(nameof(PowerFollowDisplayOn));
        OnPropertyChanged(nameof(HotkeyCtrl));
        OnPropertyChanged(nameof(HotkeyAlt));
        OnPropertyChanged(nameof(HotkeyShift));
        OnPropertyChanged(nameof(HotkeyWin));
        OnPropertyChanged(nameof(HotkeyComboText));

        AutostartService.Apply(Settings.StartWithWindows);
        ThemeChanged?.Invoke(Settings.ThemeIndex);
        HotkeyModifiersChanged?.Invoke((uint)Settings.HotkeyModifiers);
    }

    public void Persist() => _store.SaveDevices(Tvs.Select(t => t.Device));
    public void SaveSettings() => _store.SaveSettings(Settings);

    public void Dispose()
    {
        foreach (var tv in Tvs) tv.Dispose();
    }
}
