namespace LGforWin.Models;

/// <summary>
/// A time-of-day rule: at <see cref="Hour"/>:<see cref="Minute"/> set every TV's OLED Light
/// to <see cref="Brightness"/>. Fires daily. Up to 5 are allowed.
/// </summary>
public sealed class BrightnessSchedule
{
    public int Hour { get; set; } = 8;
    public int Minute { get; set; }
    public int Brightness { get; set; } = 50;
    public bool Enabled { get; set; } = true;
}
