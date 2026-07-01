using LGforWin.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace LGforWin.Views;

public sealed partial class OsdPage : Page
{
    public MainViewModel VM => ((App)Application.Current).ViewModel!;

    public OsdPage()
    {
        InitializeComponent();
        DurationValue.Text = VM.OsdTimeoutSeconds.ToString("0.0");
    }

    // Update the live label as the slider moves (the value is also bound to the VM for persistence).
    private void OnDurationChanged(object sender, RangeBaseValueChangedEventArgs e) =>
        DurationValue.Text = e.NewValue.ToString("0.0");

    private void OnPreviewClick(object sender, RoutedEventArgs e) =>
        ((App)Application.Current).PreviewOverlay();
}
