using LGforWin.ViewModels;
using Microsoft.UI.Dispatching;

namespace LGforWin.Services;

/// <summary>
/// Polls once per ~20s and applies any enabled schedule whose time matches the current
/// minute, exactly once per occurrence. Applies the target brightness to all TVs.
/// </summary>
public sealed class ScheduleService : IDisposable
{
    private readonly MainViewModel _vm;
    private readonly DispatcherQueue _dispatcher;
    private Timer? _timer;

    private string _minuteKey = "";
    private readonly HashSet<int> _firedThisMinute = new();

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

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
