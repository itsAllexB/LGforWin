using LGforWin.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LGforWin.Views;

public sealed partial class PowerPage : Page
{
    public MainViewModel VM => ((App)Application.Current).ViewModel!;

    public PowerPage() => InitializeComponent();
}
