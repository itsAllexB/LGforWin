using LGforWin.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LGforWin.Views;

public sealed partial class SchedulePage : Page
{
    public MainViewModel VM => ((App)Application.Current).ViewModel!;

    public SchedulePage()
    {
        InitializeComponent();
        // The page isn't observable, so EmptyVisibility binds OneTime and is
        // re-evaluated here whenever the schedule list grows or shrinks.
        VM.Schedules.CollectionChanged += (_, _) => Bindings.Update();
    }

    public Visibility EmptyVisibility =>
        VM.Schedules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ScheduleViewModel s })
            VM.RemoveScheduleCommand.Execute(s);
    }
}
