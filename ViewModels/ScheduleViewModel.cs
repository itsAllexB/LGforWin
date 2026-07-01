using CommunityToolkit.Mvvm.ComponentModel;
using LGforWin.Models;
using LGforWin.Services;

namespace LGforWin.ViewModels;

/// <summary>Editable wrapper around a <see cref="BrightnessSchedule"/>; persists on every change.</summary>
public sealed partial class ScheduleViewModel : ObservableObject
{
    private readonly Action _persist;

    public ScheduleViewModel(BrightnessSchedule model, Action persist)
    {
        Model = model;
        _persist = persist;
        _time = new TimeSpan(model.Hour, model.Minute, 0);
        _brightness = model.Brightness;
        _enabled = model.Enabled;
    }

    public BrightnessSchedule Model { get; }

    /// <summary>System-appropriate 12/24h format for the bound TimePicker.</summary>
    public string ClockId => ClockHelper.ClockIdentifier;

    [ObservableProperty] private TimeSpan _time;
    [ObservableProperty] private double _brightness;
    [ObservableProperty] private bool _enabled;

    partial void OnTimeChanged(TimeSpan value)
    {
        Model.Hour = value.Hours;
        Model.Minute = value.Minutes;
        _persist();
    }

    partial void OnBrightnessChanged(double value)
    {
        Model.Brightness = (int)value;
        _persist();
    }

    partial void OnEnabledChanged(bool value)
    {
        Model.Enabled = value;
        _persist();
    }
}
