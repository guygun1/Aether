using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MultiIde.Core;

namespace MultiIde.Views;

/// <summary>
/// The main toolbar (title bar). Items live in three zones and are fully customizable:
/// built-in widgets, any IDE action ("action:id") and plugin commands ("command:id").
/// </summary>
public partial class MainWindow
{
    public static readonly string[] DefaultToolbarLeft = { "menu", "project", "git" };
    public static readonly string[] DefaultToolbarCenter = { "back", "forward", "search-field" };
    public static readonly string[] DefaultToolbarRight =
        { "run-config", "build", "run", "rerun", "stop", "separator", "terminal", "problems", "notifications", "theme", "settings" };

    private TextBlock? _gitText;
    private TextBlock? _problemsBadge;
    private TextBlock? _bellBadge;
    private string? _gitBranch;
    private readonly List<(DateTime Time, string Message, bool Error)> _notificationLog = new();

    // Navigation history (Back / Forward, Ctrl+Alt+Left / Right).
    private readonly List<(string Path, int Line, int Column)> _backStack = new();
    private readonly List<(string Path, int Line, int Column)> _forwardStack = new();

    /// <summary>All toolbar items: id → (title, glyph, factory).</summary>
    private Dictionary<string, (string Title, Func<Control> Create)> ToolbarCatalog()
    {
        var items = new Dictionary<string, (string, Func<Control>)>
        {
            ["menu"] = ("Main Menu", () => Detach(MainMenuButton)),
            ["project"] = ("Project Widget", () => Detach(ProjectButton)),
            ["git"] = ("Git Branch", GitWidget),
            ["back"] = ("Back", () => Icon("", "Back (Ctrl+Alt+Left)", NavigateBack)),
            ["forward"] = ("Forward", () => Icon("", "Forward (Ctrl+Alt+Right)", NavigateForward)),
            ["search-field"] = ("Search Field", SearchField),
            ["run-config"] = ("Run Configurations", () => Detach(RunConfigBox)),
            ["build"] = ("Build", () => Icon("", "Build", BuildProject)),
            ["run"] = ("Run", () => Detach(RunButton)),
            ["rerun"] = ("Rerun", () => Icon("", "Rerun last", () => Run("run"))),
            ["stop"] = ("Stop", () => Detach(StopButton)),
            ["terminal"] = ("Terminal", () => Icon("", "Terminal (Alt+F12)", () => ToggleBottom("terminal"))),
            ["problems"] = ("Problems", ProblemsWidget),
            ["find"] = ("Find in Files", () => Icon("", "Find in Files (Ctrl+Shift+F)", ShowFindInFiles)),
            ["notifications"] = ("Notifications", BellWidget),
            ["theme"] = ("Toggle Light/Dark", () => Icon("", "Switch between light and dark theme", ToggleTheme)),
            ["search"] = ("Search Button", () => Detach(SearchButton)),
            ["settings"] = ("Settings", () => Detach(SettingsButton)),
            ["plugins"] = ("Plugins", () => Icon("", "Plugins", () => OpenSettings("Plugins"))),
            ["separator"] = ("Separator", () => new Border { Width = 1, Height = 18, Background = Res("Border"), Margin = new Thickness(6, 0) }),
        };
        foreach (var a in _actions)
        {
            var action = a;
            items.TryAdd("action:" + a.Id, ($"{a.Group}: {a.Title}", () => TextButton(action.Title, action.Keys, action.Run)));
        }
        return items;
    }

    private static Control Detach(Control c)
    {
        if (c.Parent is Panel p) p.Children.Remove(c);
        return c;
    }

    private static Button Icon(string glyph, string tip, Action click)
    {
        var b = new Button { Classes = { "flat" }, Content = new TextBlock { Classes = { "icon" }, Text = glyph } };
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    private static Button TextButton(string text, string? keys, Action click)
    {
        var b = new Button { Classes = { "flat" }, Content = new TextBlock { Text = text, FontSize = 12 } };
        ToolTip.SetTip(b, keys is null ? text : $"{text} ({keys})");
        b.Click += (_, _) => click();
        return b;
    }

    private void BuildToolbar()
    {
        foreach (var zone in new[] { ToolbarLeft, ToolbarCenter, ToolbarRight }) zone.Children.Clear();
        var catalog = ToolbarCatalog();
        void Fill(StackPanel zone, IEnumerable<string> ids)
        {
            foreach (var id in ids)
                if (catalog.TryGetValue(id, out var item))
                    zone.Children.Add(item.Create());
        }
        Fill(ToolbarLeft, Settings.ToolbarLeft ?? DefaultToolbarLeft.ToList());
        Fill(ToolbarCenter, Settings.ToolbarCenter ?? DefaultToolbarCenter.ToList());
        Fill(ToolbarRight, Settings.ToolbarRight ?? DefaultToolbarRight.ToList());

        var menu = new ContextMenu();
        var customize = new MenuItem { Header = "Customize Toolbar…" };
        customize.Click += (_, _) => ShowToolbarCustomizer();
        var reset = new MenuItem { Header = "Reset Toolbar" };
        reset.Click += (_, _) => { Settings.ToolbarLeft = Settings.ToolbarCenter = Settings.ToolbarRight = null; Settings.Save(); BuildToolbar(); };
        menu.Items.Add(customize);
        menu.Items.Add(reset);
        TitleBar.ContextMenu = menu;
        RefreshToolbarState();
    }

    /// <summary>Updates badges (problems, notifications, git) without rebuilding.</summary>
    private void RefreshToolbarState()
    {
        if (_problemsBadge is not null)
        {
            var all = _diagnostics.Values.SelectMany(d => d.Items).ToList();
            var errors = all.Count(d => d.Severity == 1);
            var warnings = all.Count(d => d.Severity == 2);
            _problemsBadge.Text = errors + warnings == 0 ? "✓" : $"✖{errors} ⚠{warnings}";
            _problemsBadge.Foreground = Res(errors > 0 ? "Error" : warnings > 0 ? "Warning" : "Success");
        }
        if (_bellBadge is not null)
        {
            var errors = _notificationLog.Count(n => n.Error);
            _bellBadge.Text = _notificationLog.Count == 0 ? "" : _notificationLog.Count.ToString();
            _bellBadge.IsVisible = _notificationLog.Count > 0;
            _bellBadge.Foreground = Res(errors > 0 ? "Error" : "Link");
        }
        if (_gitText is not null)
        {
            _gitText.Text = _gitBranch ?? "Version Control";
        }
    }

    // ---- Widgets ----------------------------------------------------------------

    private Control SearchField()
    {
        var field = new Border
        {
            Width = 280,
            Height = 26,
            CornerRadius = new CornerRadius(6),
            Background = Res("EditorBg"),
            BorderBrush = Res("Border"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new DockPanel
            {
                Children =
                {
                    new TextBlock { Text = "Double ⇧", Classes = { "muted" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, [DockPanel.DockProperty] = Dock.Right },
                    new TextBlock { Classes = { "icon", "muted" }, Text = "", FontSize = 12, Margin = new Thickness(0, 0, 8, 0), [DockPanel.DockProperty] = Dock.Left },
                    new TextBlock { Text = "Search Everywhere", Classes = { "muted" }, FontSize = 12, VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        field.PointerPressed += (_, e) => { ShowSearch(SearchMode.All); e.Handled = true; };
        return field;
    }

    private Control ProblemsWidget()
    {
        _problemsBadge = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var b = new Button { Classes = { "flat" }, Content = _problemsBadge };
        ToolTip.SetTip(b, "Problems (Alt+6)");
        b.Click += (_, _) => ToggleBottom("problems");
        return b;
    }

    private Control BellWidget()
    {
        _bellBadge = new TextBlock { FontSize = 10, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(-4, -2, 0, 0) };
        var b = new Button
        {
            Classes = { "flat" },
            Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { new TextBlock { Classes = { "icon" }, Text = "" }, _bellBadge } },
        };
        ToolTip.SetTip(b, "Notifications");
        b.Click += (_, _) => ShowNotificationLog(b);
        return b;
    }

    private void ShowNotificationLog(Control anchor)
    {
        var list = new StackPanel { Spacing = 6, Width = 380 };
        if (_notificationLog.Count == 0) list.Children.Add(new TextBlock { Text = "No notifications", Classes = { "muted" } });
        foreach (var (time, message, error) in Enumerable.Reverse(_notificationLog).Take(30))
            list.Children.Add(new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = time.ToString("HH:mm:ss"), Classes = { "muted" }, FontSize = 11 },
                    new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Res(error ? "Error" : "Fg") },
                },
            });
        var clear = new Button { Content = "Clear all", Classes = { "secondary" }, Margin = new Thickness(0, 6, 0, 0) };
        var flyout = new Flyout
        {
            Content = new StackPanel { Children = { new ScrollViewer { Content = list, MaxHeight = 420 }, clear } },
            Placement = PlacementMode.BottomEdgeAlignedRight,
        };
        clear.Click += (_, _) => { _notificationLog.Clear(); RefreshToolbarState(); flyout.Hide(); };
        flyout.ShowAt(anchor);
    }

    protected override void OnNotified(string message, bool error)
    {
        _notificationLog.Add((DateTime.Now, message, error));
        if (_notificationLog.Count > 200) _notificationLog.RemoveAt(0);
        RefreshToolbarState();
    }

    private void ToggleTheme()
    {
        var themes = ThemeManager.Available(Plugins);
        var target = ThemeManager.IsLight
            ? themes.FirstOrDefault(t => t.Id == "midnight") ?? themes.FirstOrDefault()
            : themes.FirstOrDefault(t => t.Id == "daylight");
        if (target is not null) App.SetTheme(target.Id);
    }

    private void BuildProject()
    {
        var build = AllRunConfigs.FirstOrDefault(c => c.Kind == "build");
        if (build is null) { Notify("No build configuration found. Add one with Kind = build, or use a project with Cargo.toml, CMakeLists.txt or a .csproj."); return; }
        _ = RunConfigAsync(build);
    }

    // ---- Git branch widget ------------------------------------------------------

    private Control GitWidget()
    {
        _gitText = new TextBlock { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
        var b = new Button
        {
            Classes = { "flat" },
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children = { new TextBlock { Text = "⎇", FontSize = 14, VerticalAlignment = VerticalAlignment.Center }, _gitText, new TextBlock { Classes = { "icon", "muted" }, Text = "", FontSize = 9 } },
            },
        };
        ToolTip.SetTip(b, "Git branch");
        b.Click += async (_, _) => await ShowGitMenuAsync(b);
        return b;
    }

    private async Task RefreshGitAsync()
    {
        // rev-parse finds the repository even when the project is a subfolder of it.
        _gitTopLevel = _root is null ? null : await GitAsync("rev-parse --show-toplevel");
        _gitBranch = _gitTopLevel is null ? null : await GitAsync("rev-parse --abbrev-ref HEAD") ?? "(no commits)";
        RefreshToolbarState();
    }

    private async Task<string?> GitAsync(string args)
    {
        if (_root is null || ProcessUtil.Resolve("git") is null) return null;
        try
        {
            var psi = new ProcessStartInfo("git", args) { WorkingDirectory = _root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            var output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            return p.ExitCode == 0 ? output.Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task ShowGitMenuAsync(Control anchor)
    {
        var flyout = new MenuFlyout();
        void Add(string header, Action run)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => run();
            flyout.Items.Add(item);
        }
        if (!IsGitRepo)
        {
            Add("Create Git Repository", () => _ = GitInitAsync());
            Add("Share Project on GitHub…", () => _ = ShareProjectAsync("github"));
            Add("Share Project on GitLab…", () => _ = ShareProjectAsync("gitlab"));
            flyout.ShowAt(anchor);
            return;
        }
        Add("Commit…   Ctrl+K", () => _ = ShowCommitDialogAsync());
        Add("Push   Ctrl+Shift+K", () => GitRun("push -u origin HEAD"));
        Add("Pull", () => GitRun("pull"));
        Add("Fetch", () => GitRun("fetch --all --prune"));
        flyout.Items.Add(new Separator());
        Add("Share Project on GitHub…", () => _ = ShareProjectAsync("github"));
        Add("Share Project on GitLab…", () => _ = ShareProjectAsync("gitlab"));
        var branches = (await GitAsync("branch --format=%(refname:short)"))?.Split('\n', StringSplitOptions.RemoveEmptyEntries) ?? [];
        if (branches.Length > 0)
        {
            flyout.Items.Add(new Separator());
            var checkout = new MenuItem { Header = "Branches" };
            foreach (var br in branches)
            {
                var name = br.Trim();
                var item = new MenuItem { Header = name == _gitBranch ? $"✓ {name}" : name };
                item.Click += (_, _) => { ShowBottom("run"); RunTab("Git", false).Run($"git checkout \"{name}\"", _root, exited: __ => { _ = RefreshGitAsync(); }); };
                checkout.Items.Add(item);
            }
            flyout.Items.Add(checkout);
            Add("New Branch…", async () =>
            {
                var name = await PromptAsync("New branch name", "");
                if (string.IsNullOrWhiteSpace(name)) return;
                ShowBottom("run");
                RunTab("Git", false).Run($"git checkout -b \"{name.Trim()}\"", _root, exited: __ => { _ = RefreshGitAsync(); });
            });
        }
        flyout.ShowAt(anchor);
    }

    private void ShowThemeSwitcher()
    {
        var items = ThemeManager.Available(Plugins)
            .Select(t => new SearchItem(t.Id == ThemeManager.CurrentId ? "✓" : "", t.Name, t.Source, () => { App.SetTheme(t.Id); return Task.CompletedTask; }))
            .ToList();
        ShowSearchPopup(SearchMode.Structure, items, "Switch Theme");
    }

    // ---- Navigation history -------------------------------------------------------

    /// <summary>Remembers where the caret was before a jump to another place.</summary>
    private void RecordLocation()
    {
        if (_active is null) return;
        var caret = _active.Editor.TextArea.Caret;
        var here = (_active.Doc.FilePath, caret.Line, caret.Column);
        if (_backStack.Count > 0 && _backStack[^1].Path == here.FilePath && Math.Abs(_backStack[^1].Line - here.Line) < 3) return;
        _backStack.Add(here);
        if (_backStack.Count > 100) _backStack.RemoveAt(0);
        _forwardStack.Clear();
    }

    private void NavigateBack() => Navigate(_backStack, _forwardStack);
    private void NavigateForward() => Navigate(_forwardStack, _backStack);

    private void Navigate(List<(string Path, int Line, int Column)> from, List<(string Path, int Line, int Column)> to)
    {
        if (from.Count == 0) return;
        if (_active is not null)
        {
            var c = _active.Editor.TextArea.Caret;
            to.Add((_active.Doc.FilePath, c.Line, c.Column));
        }
        var (path, line, col) = from[^1];
        from.RemoveAt(from.Count - 1);
        _suppressHistory = true;
        _ = OpenFileAsync(path, line, col).ContinueWith(_ => Dispatcher.UIThread.Post(() => _suppressHistory = false));
    }

    private bool _suppressHistory;

    // ---- Customize dialog -----------------------------------------------------------

    private void ShowToolbarCustomizer()
    {
        var catalog = ToolbarCatalog();
        var zones = new Dictionary<string, List<string>>
        {
            ["Left"] = (Settings.ToolbarLeft ?? DefaultToolbarLeft.ToList()).ToList(),
            ["Center"] = (Settings.ToolbarCenter ?? DefaultToolbarCenter.ToList()).ToList(),
            ["Right"] = (Settings.ToolbarRight ?? DefaultToolbarRight.ToList()).ToList(),
        };
        var zonePicker = new ComboBox { ItemsSource = zones.Keys.ToList(), SelectedIndex = 2, MinWidth = 120 };
        var current = new ListBox { Height = 330 };
        var available = new ListBox { Height = 330 };
        var filter = new TextBox { Watermark = "Filter (built-ins, actions, plugin commands)" };

        List<string> Zone() => zones[(string)zonePicker.SelectedItem!];
        string Title(string id) => catalog.TryGetValue(id, out var item) ? item.Title : id;
        List<string> availableIds = new();

        void Refresh(int select = -1)
        {
            current.ItemsSource = Zone().Select(Title).ToList();
            if (select >= 0) current.SelectedIndex = select;
            var q = filter.Text ?? "";
            availableIds = catalog.Keys.Where(id => id == "separator" || !zones.Values.Any(z => z.Contains(id)))
                .Where(id => q.Length == 0 || Title(id).Contains(q, StringComparison.OrdinalIgnoreCase))
                .OrderBy(id => id.Contains(':') ? 1 : 0).ThenBy(Title).ToList();
            available.ItemsSource = availableIds.Select(Title).ToList();
        }
        zonePicker.SelectionChanged += (_, _) => Refresh();
        filter.TextChanged += (_, _) => Refresh();

        void Add()
        {
            if (available.SelectedIndex < 0) return;
            var z = Zone();
            var at = current.SelectedIndex >= 0 ? current.SelectedIndex + 1 : z.Count;
            z.Insert(at, availableIds[available.SelectedIndex]);
            Refresh(at);
        }
        void Remove()
        {
            if (current.SelectedIndex < 0) return;
            var i = current.SelectedIndex;
            Zone().RemoveAt(i);
            Refresh(Math.Min(i, Zone().Count - 1));
        }
        void Move(int d)
        {
            var i = current.SelectedIndex;
            var z = Zone();
            if (i < 0 || i + d < 0 || i + d >= z.Count) return;
            (z[i], z[i + d]) = (z[i + d], z[i]);
            Refresh(i + d);
        }
        available.DoubleTapped += (_, _) => Add();

        var buttons = new StackPanel
        {
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                ToolButtonText("← Add", Add), ToolButtonText("Remove →", Remove),
                ToolButtonText("Move up", () => Move(-1)), ToolButtonText("Move down", () => Move(1)),
            },
        };
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,*"), ColumnSpacing = 12 };
        body.Children.Add(new StackPanel { Spacing = 6, Children = { new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = "Zone:", VerticalAlignment = VerticalAlignment.Center }, zonePicker } }, current } });
        body.Children.Add(new ContentControl { Content = buttons, [Grid.ColumnProperty] = 1 });
        body.Children.Add(new StackPanel { Spacing = 6, [Grid.ColumnProperty] = 2, Children = { filter, available } });
        Refresh();

        void Save()
        {
            Settings.ToolbarLeft = zones["Left"];
            Settings.ToolbarCenter = zones["Center"];
            Settings.ToolbarRight = zones["Right"];
            Settings.Save();
            BuildToolbar();
        }
        ShowOverlay(Dialog("Customize Toolbar", body, 760, ("OK", () => { Save(); _overlayCancel = null; CloseOverlay(); }), ("Apply", Save), ("Cancel", CloseOverlay)));
    }

    private static Button ToolButtonText(string text, Action click)
    {
        var b = new Button { Content = text, Classes = { "secondary" }, HorizontalAlignment = HorizontalAlignment.Stretch };
        b.Click += (_, _) => click();
        return b;
    }
}
