using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MultiIde.Core;
using MultiIde.Protocol;

namespace MultiIde.Views;

/// <summary>
/// Shared base of the Welcome window and project windows: in-window dialogs,
/// notification balloons, and the hub pages (see IdeWindow.Hub.cs).
/// </summary>
public abstract partial class IdeWindow : Window
{
    protected Action? _overlayCancel;

    public Settings Settings => IdeServices.Settings;
    public PluginManager Plugins => IdeServices.Plugins;
    public SyntaxRegistry Syntax => IdeServices.Syntax;

    /// <summary>Dimmed layer that hosts dialogs; defined in each window's XAML.</summary>
    protected abstract Panel OverlayPanel { get; }
    /// <summary>Bottom-right stack for notification balloons.</summary>
    protected abstract StackPanel NotificationPanel { get; }
    /// <summary>The open project's folder, or null in the Welcome window.</summary>
    public virtual string? ProjectRoot => null;

    protected bool OverlayOpen => OverlayPanel.IsVisible;

    protected IdeWindow()
    {
        Opened += (_, _) => Anim.InstallSmoothScrolling(this);
    }

    protected virtual void OnOverlayClosed() { }
    protected virtual void OnStatus(string message) { }
    protected virtual void OnNotified(string message, bool error) { }

    // ---- Title bar ------------------------------------------------------------

    /// <summary>
    /// Lets the window be dragged (and double-click maximized) from empty title-bar space.
    /// Presses on any interactive control are left alone; starting a drag there would
    /// swallow the click and make buttons and dropdowns need a second click.
    /// </summary>
    protected void EnableTitleBarDrag(Control titleBar)
    {
        titleBar.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            for (var v = e.Source as Avalonia.Visual; v is not null && v != titleBar; v = Avalonia.VisualTree.VisualExtensions.GetVisualParent(v))
                if (v is Button or ComboBox or TextBox or Avalonia.Controls.Primitives.ToggleButton or MenuItem or Avalonia.Controls.Primitives.ScrollBar)
                    return;
            if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            else BeginMoveDrag(e);
        };
    }

    // ---- Projects -------------------------------------------------------------

    public void OpenProject(string path, string? file = null) => App.OpenProject(path, file, this);

    public async Task PickAndOpenFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Open Project", AllowMultiple = false });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path) OpenProject(path);
    }

    public static string Initials(string name)
    {
        var parts = name.Split(new[] { ' ', '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries);
        var s = parts.Length >= 2 ? $"{parts[0][0]}{parts[1][0]}" : name.Length >= 2 ? name[..2] : name;
        return s.ToUpperInvariant();
    }

    public static IBrush BadgeBrush(string name)
    {
        string[] palette = ["#3574F0", "#5FB865", "#C77DBB", "#E08855", "#2AACB8", "#D5B778", "#E06C75", "#9876AA"];
        var hash = name.Aggregate(17, (h, c) => h * 31 + c);
        return new SolidColorBrush(Color.Parse(palette[Math.Abs(hash) % palette.Length]));
    }

    // ---- Overlays: dialogs and prompts ----------------------------------------

    public void ShowOverlay(Control content, Action? onCancel = null, bool top = false)
    {
        var layer = OverlayPanel;
        layer.Children.Clear();
        content.HorizontalAlignment = HorizontalAlignment.Center;
        content.VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Center;
        if (top) content.Margin = new Thickness(0, 90, 0, 0);
        layer.Children.Add(content);
        layer.IsVisible = true;
        Anim.Appear(content, 150, top ? -8 : 10);
        _overlayCancel = onCancel;
        layer.PointerPressed -= OnOverlayBackgroundPressed;
        layer.PointerPressed += OnOverlayBackgroundPressed;
    }

    private void OnOverlayBackgroundPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source == OverlayPanel) CloseOverlay();
    }

    public void CloseOverlay()
    {
        if (!OverlayPanel.IsVisible) return;
        OverlayPanel.IsVisible = false;
        OverlayPanel.Children.Clear();
        var cancel = _overlayCancel;
        _overlayCancel = null;
        cancel?.Invoke();
        OnOverlayClosed();
    }

    public static Border Card(Control child, double width) => new()
    {
        Width = width,
        Background = (IBrush)Application.Current!.FindResource("PopupBg")!,
        BorderBrush = (IBrush)Application.Current!.FindResource("BorderStrong")!,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(18, 16),
        BoxShadow = BoxShadows.Parse("0 8 32 0 #80000000"),
        Child = child,
    };

    public static Border Dialog(string title, Control body, double width, params (string Label, Action Click)[] buttons)
    {
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        for (var i = buttons.Length - 1; i >= 0; i--)
        {
            var (label, click) = buttons[i];
            var b = new Button { Content = label, Classes = { i == 0 ? "primary" : "secondary" } };
            b.Click += (_, _) => click();
            footer.Children.Add(b);
        }
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold });
        stack.Children.Add(body);
        if (buttons.Length > 0) stack.Children.Add(footer);
        return Card(stack, width);
    }

    public Task<string?> PromptAsync(string title, string initial)
    {
        var tcs = new TaskCompletionSource<string?>();
        var box = new TextBox { Text = initial, Classes = { "mono" } };
        void Ok() { tcs.TrySetResult(box.Text); _overlayCancel = null; CloseOverlay(); }
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Ok(); e.Handled = true; } };
        ShowOverlay(Dialog(title, box, 420, ("OK", Ok), ("Cancel", CloseOverlay)), () => tcs.TrySetResult(null), top: true);
        Dispatcher.UIThread.Post(() => { box.Focus(); box.SelectAll(); }, DispatcherPriority.Input);
        return tcs.Task;
    }

    public Task<bool> ConfirmAsync(string title, string message, string ok = "OK")
    {
        var tcs = new TaskCompletionSource<bool>();
        void Yes() { tcs.TrySetResult(true); _overlayCancel = null; CloseOverlay(); }
        ShowOverlay(Dialog(title, new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, 440, (ok, Yes), ("Cancel", CloseOverlay)),
            () => tcs.TrySetResult(false));
        return tcs.Task;
    }

    // ---- Notifications --------------------------------------------------------

    public void Notify(string message, bool error = false) => ShowBalloon(error ? "Error" : null, message, error);

    public void ShowBalloon(string? title, string message, bool error, params (string Label, Action Run)[] actions)
    {
        Dispatcher.UIThread.VerifyAccess();
        OnStatus(message.Split('\n')[0]);
        OnNotified(message, error);
        var stack = NotificationPanel;
        Border? balloon = null;
        var close = new Button { Classes = { "flat" }, Padding = new Thickness(4), VerticalAlignment = VerticalAlignment.Top, Content = new TextBlock { Classes = { "icon" }, Text = "", FontSize = 10 } };
        close.Click += (_, _) => stack.Children.Remove(balloon!);

        var body = new StackPanel { Spacing = 6 };
        if (title is not null) body.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold });
        body.Children.Add(new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Res("Fg") });
        if (actions.Length > 0)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
            foreach (var (label, run) in actions)
            {
                var link = new Button { Classes = { "flat" }, Padding = new Thickness(0), Content = new TextBlock { Text = label, Foreground = Res("Link") } };
                link.Click += (_, _) => { stack.Children.Remove(balloon!); run(); };
                row.Children.Add(link);
            }
            body.Children.Add(row);
        }

        balloon = new Border
        {
            Background = Res("PopupBg"),
            BorderBrush = Res("BorderStrong"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 6, 10),
            BoxShadow = BoxShadows.Parse("0 6 24 0 #70000000"),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                Children =
                {
                    new TextBlock
                    {
                        Classes = { "icon" }, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 10, 0),
                        Text = error ? "" : "",
                        Foreground = Res(error ? "Error" : "Link"),
                    },
                    new ContentControl { Content = body, [Grid.ColumnProperty] = 1 },
                    new ContentControl { Content = close, [Grid.ColumnProperty] = 2 },
                },
            },
        };
        stack.Children.Add(balloon);
        Anim.Appear(balloon, 200, 14, 1);
        while (stack.Children.Count > 4) stack.Children.RemoveAt(0);
        if (actions.Length == 0)
            DispatcherTimer.RunOnce(() => stack.Children.Remove(balloon), TimeSpan.FromSeconds(error ? 12 : 6));
    }

    public static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no browser */ }
    }

    // ---- Hooks the hub pages use; project windows override them ----------------

    /// <summary>Language server client for the Language Servers page (project windows only).</summary>
    protected virtual LspClient? RunningServer(string serverId) => null;
    protected virtual Task RestartServerAsync(LspServerConfig config) => Task.CompletedTask;
    protected virtual void InstallLanguageServer(LspServerConfig config) => RunInstallWithoutProject(config);
    public virtual void ApplySettingsToEditors() { }
    protected virtual void RefreshHub() { }
    protected virtual Control KeymapPage() => new TextBlock { Text = "Open a project to edit the keymap.", Classes = { "muted" } };
    protected virtual Task OpenInEditorAsync(string path)
    {
        ProcessUtil.OpenInFileManager(path);
        return Task.CompletedTask;
    }

    protected virtual void ShowKeymap()
    {
        var text = new TextBlock
        {
            Text = "Double Shift — Search Everywhere\nCtrl+Shift+A — Find Action\nCtrl+Shift+N — Go to File\nCtrl+E — Recent Files\nCtrl+Space — Completion\nAlt+Enter — Context actions\nCtrl+B — Go to declaration\nAlt+F7 — Find usages\nShift+F6 — Rename\nCtrl+Alt+L — Reformat\nCtrl+D / Ctrl+Y — Duplicate / delete line\nCtrl+/ — Comment\nShift+F10 — Run\nAlt+1 / Alt+F12 / Alt+6 — Project / Terminal / Problems",
            FontFamily = (FontFamily)Application.Current!.FindResource("MonoFont")!,
            LineHeight = 22,
        };
        ShowOverlay(Dialog("Keymap", text, 520, ("Close", CloseOverlay)));
    }
}
