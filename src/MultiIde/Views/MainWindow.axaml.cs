using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MultiIde.Core;
using MultiIde.Editor;
using MultiIde.Protocol;

namespace MultiIde.Views;

public sealed record IdeAction(string Id, string Title, string Group, string? Keys, Action Run);

/// <summary>Main window shell: layout, tool windows, keymap, actions, notifications.</summary>
public partial class MainWindow : IdeWindow
{
    private readonly List<IdeAction> _actions = new();
    private readonly Dictionary<string, Button> _stripeButtons = new();
    private bool _leftVisible = true;
    private string? _bottomTool;
    private string? _rightTool;
    private DateTime _lastShiftUp;
    private bool _shiftClean;

    public LspManager Lsp { get; }
    public MainWindow Workspace => this;
    public IReadOnlyList<IdeAction> Actions => _actions;

    public MainWindow(string projectPath, string? file = null, int? line = null)
    {
        InitializeComponent();

        Lsp = new LspManager(Plugins);

        Plugins.Changed += OnPluginsChanged;
        Closed += (_, _) => Plugins.Changed -= OnPluginsChanged;
        Lsp.Changed += () => Dispatcher.UIThread.Post(UpdateStatusBar);
        Lsp.ClientCreated += WireLspClient;

        BuildActions();
        EnsurePluginCommandActions();
        RebuildKeymap();
        BuildTitleBar();
        BuildToolbar();
        Activated += (_, _) => _ = RefreshGitAsync();
        BuildStripes();
        BuildToolWindows();
        BuildDependenciesPanel();
        WireTree();

        AddHandler(KeyDownEvent, OnGlobalKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnGlobalKeyUp, RoutingStrategies.Tunnel);
        Deactivated += (_, _) => { if (Settings.AutoSave) _ = SaveAllAsync(quiet: true); };
        Closing += OnClosing;

        _leftVisible = false;
        ApplyPanelSide();
        ApplyLayout();
        UpdateStatusBar();
        Loaded += async (_, _) =>
        {
            LoadProject(projectPath);
            foreach (var error in Plugins.LoadErrors) Notify(error, true);
            if (file is not null) await OpenFileAsync(file, line);
        };
    }

    protected override Panel OverlayPanel => OverlayLayer;
    protected override StackPanel NotificationPanel => NotificationStack;
    protected override void OnOverlayClosed() => _active?.FocusEditor();
    protected override void OnStatus(string message) => StatusMessage.Text = message;

    /// <summary>Settings dialog: the Welcome hub pages shown over the project window.</summary>
    public void OpenSettings(string page = "Customize") => ShowHubOverlay(page);

    private void ShowNewProject() => ShowHubOverlay("New Project");

    private void ShowHubOverlay(string page)
    {
        var card = Card(BuildHub(inSettings: true, page), 940);
        card.Height = 620;
        card.Padding = new Thickness(0);
        card.ClipToBounds = true;
        ShowOverlay(card);
    }

    // ---- Title bar ------------------------------------------------------------

    private void BuildTitleBar()
    {
        EnableTitleBarDrag(TitleBar);

        // Main menu, grouped like an IntelliJ menu bar.
        var menu = new MenuFlyout();
        foreach (var group in _actions.GroupBy(a => a.Group))
        {
            var top = new MenuItem { Header = group.Key };
            foreach (var a in group)
            {
                var item = new MenuItem { Header = a.Title };
                if (a.Keys is { } keys && TryGesture(keys) is { } g) item.InputGesture = g;
                item.Click += (_, _) => a.Run();
                top.Items.Add(item);
            }
            menu.Items.Add(top);
        }
        MainMenuButton.Flyout = menu;

        ProjectButton.Click += (_, _) => ShowProjectMenu();
        RunButton.Click += (_, _) => RunSelected();
        StopButton.Click += (_, _) => StopSelectedRun();
        SearchButton.Click += (_, _) => ShowSearch(SearchMode.All);
        SettingsButton.Click += (_, _) => OpenSettings();
        StatusLsp.Click += (_, _) => OpenSettings("Language Servers");
        CollapseAllButton.Click += (_, _) => { if (_rootNode is not null) { _rootNode.CollapseAll(); _rootNode.IsExpanded = true; } };
        RefreshTreeButton.Click += (_, _) => _ = _rootNode?.RefreshAsync();
        HideLeftButton.Click += (_, _) => ToggleLeft(false);
        HideBottomButton.Click += (_, _) => ShowBottom(null);
    }

    private static KeyGesture? TryGesture(string keys)
    {
        keys = keys.Split('|')[0].Trim();
        if (keys.Contains(' ')) return null; // chords cannot be shown as a single gesture
        try { return KeyGesture.Parse(keys.Replace("Ctrl+=", "Ctrl+OemPlus").Replace("Ctrl+-", "Ctrl+OemMinus").Replace("Ctrl+/", "Ctrl+OemQuestion")); }
        catch { return null; }
    }

    private void ShowProjectMenu()
    {
        var flyout = new MenuFlyout();
        void Add(string header, Action run)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => run();
            flyout.Items.Add(item);
        }
        Add("New Project…", ShowNewProject);
        Add("Open…", () => _ = PickAndOpenFolderAsync());
        Add("Close Project", () => _ = CloseProjectAsync());
        if (Settings.RecentProjects.Count > 0)
        {
            flyout.Items.Add(new Separator());
            foreach (var p in Settings.RecentProjects.Where(Directory.Exists).Take(10))
                Add($"{Path.GetFileName(p)}   —   {p}", () => OpenProject(p));
        }
        flyout.ShowAt(ProjectButton);
    }

    // ---- Tool window stripes and layout ---------------------------------------

    private Button StripeButton(string id, string glyph, string tip, Action click)
    {
        var b = new Button { Classes = { "flat", "stripe" }, Content = new TextBlock { Classes = { "icon" }, Text = glyph, FontSize = 16 } };
        ToolTip.SetTip(b, tip);
        ToolTip.SetPlacement(b, PlacementMode.Right);
        b.Click += (_, _) => click();
        _stripeButtons[id] = b;
        return b;
    }

    private void BuildStripes()
    {
        LeftStripeTop.Children.Add(StripeButton("project", "", "Project (Alt+1)", () => ToggleLeft(!_leftVisible)));
        LeftStripeBottom.Children.Add(StripeButton("run", "", "Run (Alt+4)", () => ToggleBottom("run")));
        LeftStripeBottom.Children.Add(StripeButton("terminal", "", "Terminal (Alt+F12)", () => ToggleBottom("terminal")));
        LeftStripeBottom.Children.Add(StripeButton("problems", "", "Problems (Alt+6)", () => ToggleBottom("problems")));
        LeftStripeBottom.Children.Add(StripeButton("find", "", "Find (Alt+3)", () => ToggleBottom("find")));
        LeftStripeBottom.Children.Add(StripeButton("log", "", "Language Server & MCP Log", () => ToggleBottom("log")));
        RightStripe.Children.Add(StripeButton("deps", "\uE8F1", "Dependencies", () => { ToggleRight("deps"); if (_rightTool == "deps") _ = RefreshDependenciesAsync(); }));
        RightStripe.Children.Add(StripeButton("plugins", "", "Plugins", () => OpenSettings("Plugins")));
    }

    private void ToggleLeft(bool show)
    {
        if (!show) RememberSizes();
        _leftVisible = show && _root is not null;
        ApplyLayout();
        if (_leftVisible) ProjectTree.Focus();
    }

    public void ToggleBottom(string tool)
    {
        if (_bottomTool == tool) ShowBottom(null);
        else ShowBottom(tool);
    }

    private void ToggleRight(string tool)
    {
        RememberSizes();
        _rightTool = _rightTool == tool ? null : tool;
        ApplyLayout();
    }

    private void RememberSizes()
    {
        var cols = Body.ColumnDefinitions;
        var lc = Grid.GetColumn(LeftPanel);
        var rc = Grid.GetColumn(RightPanel);
        if (_leftVisible && cols[lc].ActualWidth > 80) Settings.LeftPanelWidth = cols[lc].ActualWidth;
        if (_rightTool is not null && cols[rc].ActualWidth > 80) Settings.RightPanelWidth = cols[rc].ActualWidth;
        var rows = CenterGrid.RowDefinitions;
        if (_bottomTool is not null && rows[2].ActualHeight > 60) Settings.BottomPanelHeight = rows[2].ActualHeight;
    }

    private void ApplyLayout()
    {
        var cols = Body.ColumnDefinitions;
        var rows = CenterGrid.RowDefinitions;
        int lc = Grid.GetColumn(LeftPanel), ls = Grid.GetColumn(LeftSplitter), rc = Grid.GetColumn(RightPanel), rs = Grid.GetColumn(RightSplitter);
        SlidePanel(LeftPanel, LeftSplitter, w => cols[lc].Width = new GridLength(w), cols[lc].ActualWidth, _leftVisible, Settings.LeftPanelWidth, s => cols[ls].Width = new GridLength(s));
        SlidePanel(RightPanel, RightSplitter, w => cols[rc].Width = new GridLength(w), cols[rc].ActualWidth, _rightTool is not null, Settings.RightPanelWidth, s => cols[rs].Width = new GridLength(s));
        SlidePanel(BottomPanel, BottomSplitter, h => rows[2].Height = new GridLength(h), rows[2].ActualHeight, _bottomTool is not null, Settings.BottomPanelHeight, s => rows[1].Height = new GridLength(s));

        foreach (var (id, button) in _stripeButtons)
        {
            var active = id == "project" ? _leftVisible : id == _bottomTool || id == _rightTool;
            button.Classes.Set("active", active);
        }
    }

    /// <summary>Opens or closes a tool window with an eased slide (instant when animations are off).</summary>
    private void SlidePanel(Control panel, Control splitter, Action<double> setSize, double current, bool show, double size, Action<double> setSplitter)
    {
        var target = show ? size : 0;
        var wasVisible = panel.IsVisible;
        if (show == wasVisible && Math.Abs(current - target) < 1 && (show || current == 0)) return;
        if (show)
        {
            panel.IsVisible = splitter.IsVisible = true;
            setSplitter(1);
            Anim.Tween(this, wasVisible ? current : 0, target, 170, setSize);
        }
        else
        {
            setSplitter(0);
            splitter.IsVisible = false;
            Anim.Tween(this, wasVisible ? current : 0, 0, 140, setSize, () =>
            {
                // A quick re-open during the animation wins.
                if (panel == LeftPanel ? !_leftVisible : panel == RightPanel ? _rightTool is null : _bottomTool is null)
                    panel.IsVisible = false;
            });
        }
    }

    // ---- Actions & keymap -----------------------------------------------------

    private void Act(string id, string title, string group, string? keys, Action run) =>
        _actions.Add(new IdeAction(id, title, group, keys, run));

    private void OnEditor(Action<EditorView> run)
    {
        if (_active is not null) run(_active);
        else Notify("Open a file first");
    }

    private void BuildActions()
    {
        Act("newProject", "New Project…", "File", null, ShowNewProject);
        Act("openFolder", "Open…", "File", null, () => _ = PickAndOpenFolderAsync());
        Act("saveAll", "Save All", "File", "Ctrl+S", () => _ = SaveAllAsync());
        Act("closeTab", "Close Tab", "File", "Ctrl+F4", () => { if (_active is not null) _ = CloseTabAsync(_active); });
        Act("closeProject", "Close Project", "File", null, () => _ = CloseProjectAsync());
        Act("settings", "Settings…", "File", "Ctrl+Alt+S", () => OpenSettings());
        Act("exit", "Exit", "File", null, Close);

        Act("undo", "Undo", "Edit", "Ctrl+Z", () => OnEditor(v => v.Editor.Undo()));
        Act("redo", "Redo", "Edit", "Ctrl+Shift+Z", () => OnEditor(v => v.Editor.Redo()));
        Act("find", "Find…", "Edit", "Ctrl+F", () => OnEditor(v => v.Editor.SearchPanel.Open()));
        Act("replace", "Replace…", "Edit", "Ctrl+R", () => OnEditor(v => { v.Editor.SearchPanel.Open(); v.Editor.SearchPanel.IsReplaceMode = true; }));
        Act("findInFiles", "Find in Files…", "Edit", "Ctrl+Shift+F", ShowFindInFiles);
        Act("duplicate", "Duplicate Line or Selection", "Edit", "Ctrl+D", () => OnEditor(v => Send(v, Key.D, KeyModifiers.Control)));
        Act("deleteLine", "Delete Line", "Edit", "Ctrl+Y", () => OnEditor(v => Send(v, Key.Y, KeyModifiers.Control)));

        Act("viewProject", "Project", "View", "Alt+1", () => ToggleLeft(!_leftVisible));
        Act("viewFind", "Find", "View", "Alt+3", () => ToggleBottom("find"));
        Act("viewRun", "Run", "View", "Alt+4", () => ToggleBottom("run"));
        Act("viewProblems", "Problems", "View", "Alt+6", () => ToggleBottom("problems"));
        Act("viewTerminal", "Terminal", "View", "Alt+F12", () => ToggleBottom("terminal"));
        Act("viewLog", "Language Server Log", "View", null, () => ToggleBottom("log"));
        Act("fontUp", "Increase Font Size", "View", "Ctrl+=", () => ChangeFontSize(+1));
        Act("fontDown", "Decrease Font Size", "View", "Ctrl+-", () => ChangeFontSize(-1));
        Act("wordWrap", "Toggle Soft Wrap", "View", null, () => { Settings.WordWrap = !Settings.WordWrap; ApplySettingsToEditors(); });
        Act("whitespace", "Toggle Whitespace", "View", null, () => { Settings.ShowWhitespace = !Settings.ShowWhitespace; ApplySettingsToEditors(); });

        Act("searchEverywhere", "Search Everywhere", "Navigate", "Double Shift|Ctrl+T", () => ShowSearch(SearchMode.All));
        Act("gotoFile", "Go to File…", "Navigate", "Ctrl+Shift+N", () => ShowSearch(SearchMode.Files));
        Act("gotoSymbol", "Go to Symbol…", "Navigate", "Ctrl+Alt+Shift+N", () => ShowSearch(SearchMode.Symbols));
        Act("findAction", "Find Action…", "Navigate", "Ctrl+Shift+A", () => ShowSearch(SearchMode.Actions));
        Act("recentFiles", "Recent Files", "Navigate", "Ctrl+E", () => ShowSearch(SearchMode.Recent));
        Act("fileStructure", "File Structure", "Navigate", "Ctrl+F12", () => OnEditor(v => _ = v.FileStructureAsync()));
        Act("gotoLine", "Go to Line:Column…", "Navigate", "Ctrl+G", () => _ = GoToLineAsync());
        Act("gotoDeclaration", "Go to Declaration", "Navigate", "Ctrl+B|F12", () => OnEditor(v => _ = v.GoToDefinitionAsync(v.Editor.CaretOffset)));
        Act("nextTab", "Select Next Tab", "Navigate", "Alt+Right", () => CycleTab(1));
        Act("prevTab", "Select Previous Tab", "Navigate", "Alt+Left", () => CycleTab(-1));
        Act("gotoClass", "Go to Class…", "Navigate", "Ctrl+N", () => ShowSearch(SearchMode.Symbols));
        Act("back", "Back", "Navigate", "Ctrl+Alt+Left", NavigateBack);
        Act("forward", "Forward", "Navigate", "Ctrl+Alt+Right", NavigateForward);

        Act("complete", "Code Completion", "Code", "Ctrl+Space", () => OnEditor(v => _ = v.TriggerCompletionAsync(true, null)));
        Act("contextActions", "Show Context Actions", "Code", "Alt+Enter|Ctrl+.", () => OnEditor(v => _ = v.CodeActionsAsync()));
        Act("reformat", "Reformat Code", "Code", "Ctrl+Alt+L|Shift+Alt+F", () => OnEditor(v => _ = v.FormatAsync()));
        Act("rename", "Rename…", "Code", "Shift+F6|F2", () => OnEditor(v => _ = v.RenameAsync()));
        Act("findUsages", "Find Usages", "Code", "Alt+F7|Shift+F12", () => OnEditor(v => _ = v.FindUsagesAsync()));
        Act("comment", "Comment with Line Comment", "Code", "Ctrl+/", () => OnEditor(v => Send(v, Key.OemQuestion, KeyModifiers.Control)));
        Act("quickDoc", "Quick Documentation", "Code", "Ctrl+Q", () => OnEditor(v => Send(v, Key.Q, KeyModifiers.Control)));
        Act("surroundWith", "Surround With…", "Code", "Ctrl+Alt+T", () => OnEditor(v => v.SurroundWith()));
        Act("paramInfo", "Parameter Info", "Code", "Ctrl+P", () => OnEditor(v => Send(v, Key.P, KeyModifiers.Control)));

        Act("run", "Run", "Run", "Shift+F10|F5", RunSelected);
        Act("stop", "Stop", "Run", "Ctrl+F2", StopSelectedRun);
        Act("build", "Build Project", "Run", "Ctrl+F9", BuildProject);
        Act("editRunConfigs", "Edit Configurations…", "Run", null, ShowRunConfigEditor);
        Act("restartLsp", "Restart Language Servers", "Run", null, () => _ = RestartLanguageServersAsync());

        Act("plugins", "Plugins…", "Tools", null, () => OpenSettings("Plugins"));
        Act("installPlugin", "Install Plugin from Disk…", "Tools", null, () => _ = InstallPluginFromDiskAsync());
        Act("createPlugin", "Create Plugin…", "Tools", null, () => _ = CreatePluginAsync());
        Act("pluginsFolder", "Open Plugins Folder", "Tools", null, () => { Directory.CreateDirectory(IdePaths.UserPlugins); ProcessUtil.OpenInFileManager(IdePaths.UserPlugins); });

        Act("commit", "Commit…", "Git", "Ctrl+K", () => _ = ShowCommitDialogAsync());
        Act("push", "Push", "Git", "Ctrl+Shift+K", () => GitRun("push"));
        Act("pull", "Pull", "Git", null, () => GitRun("pull"));
        Act("shareGithub", "Share Project on GitHub…", "Git", null, () => _ = ShareProjectAsync("github"));
        Act("shareGitlab", "Share Project on GitLab…", "Git", null, () => _ = ShareProjectAsync("gitlab"));
        Act("dependencies", "Dependencies", "View", null, () => { ToggleRight("deps"); _ = RefreshDependenciesAsync(); });
        Act("gitInit", "Create Git Repository", "Git", null, () => _ = GitInitAsync());

        Act("toggleTheme", "Toggle Light/Dark Theme", "View", null, ToggleTheme);
        Act("switchTheme", "Switch Theme…", "View", "Ctrl+`", ShowThemeSwitcher);
        Act("customizeToolbar", "Customize Toolbar…", "View", null, ShowToolbarCustomizer);
        Act("keymapSettings", "Keymap…", "File", null, () => OpenSettings("Keymap"));

        Act("keymap", "Keymap Reference", "Help", null, ShowKeymap);
        Act("about", "About Aether", "Help", null, () => Notify("Aether 0.1 — a fast, plugin-driven IDE. Plugins live in ~/.aether/plugins."));
    }

    /// <summary>Replays an editor shortcut so menu items and the keymap share one implementation.</summary>
    private void Send(EditorView v, Key key, KeyModifiers mods)
    {
        v.Editor.TextArea.Focus();
        // The replayed key must reach the editor's own handler, not the window keymap again.
        _replayingKey = true;
        try { v.Editor.TextArea.RaiseEvent(new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = key, KeyModifiers = mods, Source = v.Editor.TextArea }); }
        finally { _replayingKey = false; }
    }

    private void Run(string id) => _actions.First(a => a.Id == id).Run();

    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftShift or Key.RightShift)
        {
            _shiftClean = e.KeyModifiers == KeyModifiers.Shift;
            return;
        }
        _shiftClean = false;
        _lastShiftUp = default;

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);

        if (OverlayLayer.IsVisible)
        {
            if (e.Key == Key.Escape) { CloseOverlay(); e.Handled = true; }
            return;
        }

        // Data-driven keymap: action defaults, plugin keybindings and user overrides.
        if (DispatchKeymap(e))
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Tab && ctrl)
        {
            CycleTab(shift ? -1 : 1);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && shift)
        {
            if (_bottomTool is not null) ShowBottom(null); else ToggleLeft(false);
            _active?.FocusEditor();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _active is not null && !_active.Editor.TextArea.IsFocused && !_active.IsKeyboardFocusWithin)
        {
            // IntelliJ: Esc in a tool window returns focus to the editor.
            _active.FocusEditor();
        }
    }

    private void OnGlobalKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.LeftShift or Key.RightShift) || !_shiftClean) return;
        var now = DateTime.UtcNow;
        if (now - _lastShiftUp < TimeSpan.FromMilliseconds(400))
        {
            _lastShiftUp = default;
            if (!OverlayLayer.IsVisible) ShowSearch(SearchMode.All);
        }
        else
        {
            _lastShiftUp = now;
        }
    }

    private void ChangeFontSize(int delta)
    {
        Settings.FontSize = Math.Clamp(Settings.FontSize + delta, 8, 40);
        ApplySettingsToEditors();
        Settings.Save();
    }

    /// <summary>Puts the Project tool window on the left or the right of the editor.</summary>
    private void ApplyPanelSide()
    {
        var right = Settings.ProjectPanelSide == "right";
        if (right == (Grid.GetColumn(LeftPanel) == 5)) return;
        RememberSizes();
        var cols = Body.ColumnDefinitions;
        for (var i = 1; i <= 5; i++) if (i != 3) cols[i].Width = new GridLength(0);
        Grid.SetColumn(LeftPanel, right ? 5 : 1);
        Grid.SetColumn(LeftSplitter, right ? 4 : 2);
        Grid.SetColumn(RightPanel, right ? 1 : 5);
        Grid.SetColumn(RightSplitter, right ? 2 : 4);
        if (_stripeButtons.TryGetValue("project", out var projectButton) && projectButton.Parent is Panel parent)
        {
            parent.Children.Remove(projectButton);
            if (right) RightStripe.Children.Insert(0, projectButton);
            else LeftStripeTop.Children.Insert(0, projectButton);
        }
        LeftPanel.IsVisible = RightPanel.IsVisible = false;
        ApplyLayout();
    }

    public override void ApplySettingsToEditors()
    {
        ApplyPanelSide();
        FontSize = Settings.UiFontSize;
        foreach (var v in _views) v.ApplySettings(Settings);
        foreach (var c in _runTabs.Values.Append(_terminal)) c.WordWrap = Settings.ConsoleWordWrap;
        UpdateStatusBar();
    }

    protected override void ShowKeymap()
    {
        var grid = new StackPanel { Spacing = 2 };
        foreach (var group in _actions.Where(a => a.Keys is not null).GroupBy(a => a.Group))
        {
            grid.Children.Add(new TextBlock { Text = group.Key, Classes = { "header" }, Margin = new Thickness(0, 10, 0, 4) });
            foreach (var a in group)
                grid.Children.Add(new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                    Children =
                    {
                        new TextBlock { Text = a.Title },
                        new TextBlock { Text = a.Keys, Classes = { "muted" }, [Grid.ColumnProperty] = 1 },
                    },
                });
        }
        ShowOverlay(Dialog("Keymap", new ScrollViewer { Content = grid, MaxHeight = 560 }, 520, ("Close", CloseOverlay)));
    }

    private async Task GoToLineAsync()
    {
        if (_active is null) return;
        var caret = _active.Editor.TextArea.Caret;
        var input = await PromptAsync("Go to Line:Column", $"{caret.Line}:{caret.Column}");
        if (input is null) return;
        var parts = input.Split(':', ',');
        if (int.TryParse(parts[0].Trim(), out var line))
            _active.GoTo(line, parts.Length > 1 && int.TryParse(parts[1].Trim(), out var col) ? col : 1);
    }

    // ---- Status bar -----------------------------------------------------------

    private void UpdateStatusBar()
    {
        Breadcrumbs.Children.Clear();
        if (_root is not null)
        {
            var parts = new List<string> { Path.GetFileName(_root) };
            if (_active is not null) parts.AddRange(Path.GetRelativePath(_root, _active.Doc.FilePath).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            for (var i = 0; i < parts.Count; i++)
            {
                if (i > 0) Breadcrumbs.Children.Add(new TextBlock { Classes = { "icon", "muted" }, Text = "", FontSize = 8 });
                Breadcrumbs.Children.Add(new TextBlock { Text = parts[i], FontSize = 12, Classes = { i == parts.Count - 1 ? "" : "muted" } });
            }
        }

        if (_active is { } v)
        {
            var caret = v.Editor.TextArea.Caret;
            var sel = v.Editor.SelectionLength;
            StatusPosition.Text = sel > 0 ? $"{caret.Line}:{caret.Column} ({sel} chars)" : $"{caret.Line}:{caret.Column}";
            StatusEol.Text = v.Doc.LineEnding;
            StatusIndent.Text = Settings.InsertSpaces ? $"{Settings.TabSize} spaces" : "Tab";
            StatusLanguage.Text = v.Doc.LanguageName;
            var errors = v.Doc.Diagnostics.Count(d => d.Severity == 1);
            var warnings = v.Doc.Diagnostics.Count(d => d.Severity == 2);
            StatusProblems.Text = errors + warnings == 0 ? "" : $"✖ {errors}  ⚠ {warnings}";
            StatusProblems.Foreground = (IBrush)Application.Current!.FindResource(errors > 0 ? "Error" : "Warning")!;

            var cfg = Plugins.LspFor(v.Doc.LanguageId);
            var client = cfg is null ? null : Lsp.Get(cfg.Value.Config.Id);
            StatusLsp.IsVisible = cfg is not null && !v.Doc.IsLarge;
            if (cfg is not null)
            {
                var (dot, color) = client?.State switch
                {
                    ServerState.Running => ("●", "#5FB865"),
                    ServerState.Starting => ("◌", "#E0BB65"),
                    ServerState.Failed => ("●", "#F75464"),
                    _ => ("○", "#868A91"),
                };
                StatusLsp.Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 5,
                    Children =
                    {
                        new TextBlock { Text = dot, Foreground = new SolidColorBrush(Color.Parse(color)), FontSize = 11, VerticalAlignment = VerticalAlignment.Center },
                        new TextBlock { Text = cfg.Value.Config.Name, Classes = { "muted" }, FontSize = 12 },
                    },
                };
                ToolTip.SetTip(StatusLsp, client?.Error ?? $"{cfg.Value.Config.Name}: {client?.State ?? ServerState.Stopped}");
            }
            StatusProgress.Text = client?.ProgressText ?? "";
        }
        else
        {
            StatusPosition.Text = StatusEol.Text = StatusIndent.Text = StatusLanguage.Text = StatusProblems.Text = "";
            StatusLsp.IsVisible = false;
            StatusProgress.Text = Lsp.Clients.Select(c => c.ProgressText).FirstOrDefault(p => p is not null) ?? "";
        }
    }

    // ---- Shutdown -------------------------------------------------------------

    private bool _closing;

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closing) return;
        e.Cancel = true;
        _closing = true;
        RememberSizes();
        SaveSession();
        await SaveAllAsync(quiet: true);
        Settings.Save();
        StopAllRuns();
        _terminal.Stop();
        try
        {
            await Task.WhenAny(Lsp.StopAllAsync(), Task.Delay(2500));
        }
        catch
        {
            // Shutting down regardless.
        }
        Close();
    }
}
