using LGforWin.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LGforWin.Views;

public sealed partial class HotkeysPage : Page
{
    public MainViewModel VM => ((App)Application.Current).ViewModel!;

    public HotkeysPage() => InitializeComponent();

    // A rejected change (e.g. clearing the last modifier) is reverted by the VM re-raising the
    // property, which the OneWay IsChecked binding picks up.
    private void OnModifierClick(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb) return;
        var on = cb.IsChecked == true;
        switch (cb.Tag as string)
        {
            case "ctrl": VM.HotkeyCtrl = on; break;
            case "alt": VM.HotkeyAlt = on; break;
            case "shift": VM.HotkeyShift = on; break;
            case "win": VM.HotkeyWin = on; break;
        }
    }
}
