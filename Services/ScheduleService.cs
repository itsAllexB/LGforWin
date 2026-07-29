using LGforWin.ViewModels;
using Microsoft.UI.Dispatching;

namespace LGforWin.Services;

/// <summary>
/// Polls once per ~20s and applies any enabled schedule whose time matches the current
/// minute, exactly once per occurrence. Applies the target brightness to all TVs.
///
/// The minute-match means a schedule whose time passes while the timer isn't ticking —
/// PC asleep or hibernating — would silently never fire. So each tick also watches for
/// a gap since the previous tick, and applies the schedule that should be in effect
/// after one. (If the TVs are still off at that moment, TvController holds the value
/// and delivers it when they come back.)
/// </summary>
public sealed class ScheduleService : IDisposable
{
    // Ticks are 20s apart; anything much longer means the process was suspended.
    private static readonly TimeSpan GapThreshold = TimeSpan.FromSeconds(90);

    private readonly MainViewModel _vm;
    private readonly DispatcherQueue _dispatcher;
    private Timer? _timer;

    private string _minuteKey = "";
    private readonly HashSet<int> _firedThisMinute = new();
    private DateTime _lastTick = DateTime.MinValue;

    /// <summary>Raised on the UI thread after a schedule applies its brightness (value in %).</summary>
    public event Action<int>? ScheduleApplied;

    public ScheduleService(MainViewModel vm, DispatcherQueue dispatcher)
    {
        _vm = vm;
        _dispatcher = dispatcher;
    }

    public void Start() => _timer = new Timer(_ => Tick(), null, TimeSpan.Zero, TimeSpan.FromSeconds(20));

    private void Tick()
    {
        var now = DateTime.Now;

        var previousTick = _lastTick;
        _lastTick = now;
        if (previousTick != DateTime.MinValue && now - previousTick > GapThreshold
            && AnyScheduleFiresBetween(previousTick, now))
        {
            Log.Write($"schedule: catching up after a {(int)(now - previousTick).TotalMinutes} min gap (PC was asleep)");
            _dispatcher.TryEnqueue(() =>
            {
                if (_vm.GetCurrentScheduledBrightness() is int value)
                {
                    _vm.ApplyBrightnessAll(value);
                    ScheduleApplied?.Invoke(value);
                }
            });
        }

        var minute = now.ToString("yyyyMMddHHmm");
        if (minute != _minuteKey)
        {
            _minuteKey = minute;
            _firedThisMinute.Clear();
        }

        var schedules = _vm.Settings.Schedules;
        for (var i = 0; i < schedules.Count; i++)
        {
            var s = schedules[i];
            if (s.Enabled && s.Hour == now.Hour && s.Minute == now.Minute && _firedThisMinute.Add(i))
            {
                var value = s.Brightness;
                _dispatcher.TryEnqueue(() =>
                {
                    _vm.ApplyBrightnessAll(value);
                    ScheduleApplied?.Invoke(value);
                });
                Log.Write($"schedule {s.Hour:00}:{s.Minute:00} fired -> {value}%");
            }
        }
    }

    // True when at least one enabled schedule's daily time falls inside (from, to] —
    // i.e. it would have fired during that window had the PC been awake.
    private bool AnyScheduleFiresBetween(DateTime from, DateTime to)
    {
        var enabled = _vm.Settings.Schedules.Where(s => s.Enabled).ToList();
        if (enabled.Count == 0) return false;
        if (to - from >= TimeSpan.FromDays(1)) return true; // slept a day+ — every schedule passed

        foreach (var s in enabled)
        {
            // First occurrence of this schedule's time strictly after `from`.
            var occurrence = from.Date.AddHours(s.Hour).AddMinutes(s.Minute);
            if (occurrence <= from) occurrence = occurrence.AddDays(1);
            if (occurrence <= to) return true;
        }
        return false;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
