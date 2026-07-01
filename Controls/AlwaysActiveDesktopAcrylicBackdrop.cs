using System.Collections.Generic;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace LGforWin.Controls;

/// <summary>
/// A <see cref="SystemBackdrop"/> that renders desktop acrylic and stays in the active visual
/// state even when the hosting window is not activated.
/// </summary>
/// <remarks>
/// The built-in <see cref="DesktopAcrylicBackdrop"/> tracks the host window's IsInputActive state
/// and falls back to a flat solid colour whenever the window is not the foreground window. That
/// makes it unusable for the OSD, which is a non-activating tool window (shown with
/// <c>SW_SHOWNA</c> and <c>WS_EX_NOACTIVATE</c>) and therefore never activated by design. This
/// backdrop drives a <see cref="DesktopAcrylicController"/> whose <c>IsInputActive</c> is pinned
/// to <see langword="true"/>, so the acrylic is always rendered.
///
/// Ported from PowerToys' Common.UI.Controls (MIT). Used inside a <c>SystemBackdropElement</c> so
/// the acrylic can be clipped to the pill's rounded corners in XAML — see BrightnessOverlay.xaml.
/// </remarks>
public sealed partial class AlwaysActiveDesktopAcrylicBackdrop : SystemBackdrop
{
    /// <summary>Identifies the <see cref="Kind"/> dependency property.</summary>
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind),
        typeof(DesktopAcrylicKind),
        typeof(AlwaysActiveDesktopAcrylicBackdrop),
        new PropertyMetadata(DesktopAcrylicKind.Default, OnKindChanged));

    private readonly Dictionary<ICompositionSupportsSystemBackdrop, BackdropTarget> _targets = new();

    /// <summary>Gets or sets the desktop acrylic material variant to render.</summary>
    public DesktopAcrylicKind Kind
    {
        get => (DesktopAcrylicKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);

        var configuration = new SystemBackdropConfiguration
        {
            IsInputActive = true,
            Theme = ResolveTheme(xamlRoot),
        };

        var controller = new DesktopAcrylicController { Kind = Kind };
        controller.SetSystemBackdropConfiguration(configuration);
        controller.AddSystemBackdropTarget(connectedTarget);

        var target = new BackdropTarget(controller, configuration, xamlRoot);
        _targets[connectedTarget] = target;

        if (xamlRoot.Content is FrameworkElement rootElement)
        {
            rootElement.ActualThemeChanged += target.OnActualThemeChanged;
        }
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);

        if (_targets.Remove(disconnectedTarget, out var target))
        {
            if (target.XamlRoot.Content is FrameworkElement rootElement)
            {
                rootElement.ActualThemeChanged -= target.OnActualThemeChanged;
            }

            target.Controller.RemoveSystemBackdropTarget(disconnectedTarget);
            target.Controller.Dispose();
        }
    }

    private static void OnKindChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (AlwaysActiveDesktopAcrylicBackdrop)d;
        var kind = (DesktopAcrylicKind)e.NewValue;

        foreach (var target in self._targets.Values)
        {
            target.Controller.Kind = kind;
        }
    }

    private static SystemBackdropTheme ResolveTheme(XamlRoot xamlRoot) =>
        xamlRoot.Content is FrameworkElement rootElement
            ? rootElement.ActualTheme switch
            {
                ElementTheme.Dark => SystemBackdropTheme.Dark,
                ElementTheme.Light => SystemBackdropTheme.Light,
                _ => SystemBackdropTheme.Default,
            }
            : SystemBackdropTheme.Default;

    private sealed class BackdropTarget
    {
        public BackdropTarget(DesktopAcrylicController controller, SystemBackdropConfiguration configuration, XamlRoot xamlRoot)
        {
            Controller = controller;
            Configuration = configuration;
            XamlRoot = xamlRoot;
        }

        public DesktopAcrylicController Controller { get; }

        public SystemBackdropConfiguration Configuration { get; }

        public XamlRoot XamlRoot { get; }

        public void OnActualThemeChanged(FrameworkElement sender, object args)
        {
            Configuration.Theme = ResolveTheme(XamlRoot);
        }
    }
}
