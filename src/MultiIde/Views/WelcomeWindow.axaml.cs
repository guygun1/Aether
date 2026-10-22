using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace MultiIde.Views;

/// <summary>The Welcome window: recent projects, new project, customize, plugins, language servers.</summary>
public partial class WelcomeWindow : IdeWindow
{
    public WelcomeWindow(string page = "Projects")
    {
        InitializeComponent();
        _hubPage = !Settings.SetupDone ? "Setup" : page;
        EnableTitleBarDrag(TitleBar);
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape && OverlayOpen) { CloseOverlay(); e.Handled = true; }
        }, RoutingStrategies.Tunnel);
        Plugins.Changed += RefreshHub;
        Closed += (_, _) => Plugins.Changed -= RefreshHub;
        RefreshHub();
    }

    protected override Panel OverlayPanel => OverlayLayer;
    protected override StackPanel NotificationPanel => NotificationStack;

    protected override void RefreshHub() => HubHost.Content = BuildHub(inSettings: false, _hubPage);

    public void ShowPage(string page)
    {
        _hubPage = page;
        RefreshHub();
    }
}
