using LGforWin.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LGforWin.Views;

public sealed partial class HomePage : Page
{
    public MainViewModel VM => ((App)Application.Current).ViewModel!;

    public HomePage() => InitializeComponent();

    // Re-enumerate displays each time the page is shown, so freshly (un)plugged screens appear.
    private void OnPageLoaded(object sender, RoutedEventArgs e) => VM.RefreshMonitors();

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TvViewModel tv })
            VM.RemoveTvCommand.Execute(tv);
    }
}
