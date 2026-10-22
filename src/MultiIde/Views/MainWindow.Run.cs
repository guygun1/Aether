using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using MultiIde.Core;

namespace MultiIde.Views;

/// <summary>Run configurations: selector, runner (with before-launch steps), Run tool window tabs and the editor dialog.</summary>
public partial class MainWindow
{
    private const string EditConfigurationsItem = "Edit Configurations…";

    private RunConfigStore _runStore = new();
    private List<RunConfig> _detectedRuns = new();
    private ProjectState? _projectState;
    private readonly Dictionary<string, ConsoleView> _runTabs = new();
    private string? _activeRunTab;
    private StackPanel _runTabStrip = null!;
    private ContentControl _runTabHost = null!;
    private bool _suppressRunSelection;

    private IEnumerable<RunConfig> AllRunConfigs =>
        _runStore.Configurations.Concat(_detectedRuns.Where(d => _runStore.Configurations.All(c => c.Name != d.Name)));

    private RunConfig? SelectedRunConfig => RunConfigBox.SelectedItem as RunConfig;

    private Macros.Context MacroContext() => new(
        _root,
        _active?.Doc.FilePath,
        _active?.Editor.TextArea.Caret.Line,
        _active?.Editor.SelectedText,
        null,
        Plugins.SettingValue);

    // ---- Selector -------------------------------------------------------------

    private void BuildRunToolWindow()
    {
        _runTabStrip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(6, 2) };
        _runTabHost = new ContentControl();
        var dock = new DockPanel();
        var strip = new Border { Child = new ScrollViewer { Content = _runTabStrip, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto }, BorderBrush = Res("Border"), BorderThickness = new Thickness(0, 0, 0, 1) };
        DockPanel.SetDock(strip, Dock.Top);
        dock.Children.Add(strip);
        dock.Children.Add(_runTabHost);

        var rerun = ToolButton("", "Rerun", () => { if (_activeRunTab is { } t && AllRunConfigs.FirstOrDefault(c => t.StartsWith(c.Name)) is { } rc) _ = RunConfigAsync(rc); else RunSelected(); });
        var stop = ToolButton("", "Stop", StopSelectedRun);
        var clear = ToolButton("", "Clear", () => { if (_activeRunTab is { } t && _runTabs.TryGetValue(t, out var c)) c.Clear(); });
        var edit = ToolButton("", "Edit Configurations…", ShowRunConfigEditor);
        _bottomTools["run"] = ("Run", dock, new Control[] { rerun, stop, clear, edit });

        RunConfigBox.ItemTemplate = new FuncDataTemplate<object>((item, _) => item switch
        {
            RunConfig rc => new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new TextBlock { Classes = { "icon" }, Text = rc.Type == "program" ? "" : "", FontSize = 11, Foreground = Res(rc.Detected ? "FgMuted" : "Success") },
                    new TextBlock { Text = rc.Name },
                    new TextBlock { Text = rc.Detected ? "detected" : "", Classes = { "muted" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center },
                },
            },
            string s => new TextBlock { Text = s, Foreground = Res("Link") },
            _ => new TextBlock(),
        });
        RunConfigBox.SelectionChanged += (_, _) =>
        {
            if (_suppressRunSelection) return;
            if (RunConfigBox.SelectedItem is string)
            {
                RefreshRunConfigs(); // restores the previous selection
                ShowRunConfigEditor();
                return;
            }
            if (SelectedRunConfig is { } rc && _projectState is not null && _root is not null)
            {
                _projectState.SelectedRunConfig = rc.Name;
                _projectState.Save(_root);
            }
        };
    }

    private void RefreshRunConfigs()
    {
        _suppressRunSelection = true;
        try
        {
            if (_root is null)
            {
                RunConfigBox.ItemsSource = null;
                return;
            }
            _projectState ??= ProjectState.Load(_root);
            _runStore = RunConfigStore.Load(_root);
            _detectedRuns = Plugins.RunConfigurationsFor(_root).Select(x => RunConfig.FromPlugin(x.Config, x.Plugin)).ToList();
            var items = AllRunConfigs.Cast<object>().Append(EditConfigurationsItem).ToList();
            RunConfigBox.ItemsSource = items;
            var wanted = _projectState.SelectedRunConfig;
            RunConfigBox.SelectedItem = items.OfType<RunConfig>().FirstOrDefault(c => c.Name == wanted) ?? items.OfType<RunConfig>().FirstOrDefault();
        }
        finally
        {
            _suppressRunSelection = false;
        }
    }

    // ---- Running --------------------------------------------------------------

    private async void RunSelected()
    {
        if (_root is null) { Notify("Open a project first"); return; }
        if (SelectedRunConfig is not { } rc)
        {
            ShowBalloon("No run configuration", "Add one in Edit Configurations, or open a project with Cargo.toml, CMakeLists.txt or a .csproj.", false,
                ("Edit Configurations…", ShowRunConfigEditor));
            return;
        }
        await RunConfigAsync(rc);
    }

    /// <summary>Runs a configuration after its before-launch steps; returns true when everything exited with code 0.</summary>
    private async Task<bool> RunConfigAsync(RunConfig rc, HashSet<string>? chain = null)
    {
        chain ??= new();
        if (!chain.Add(rc.Name))
        {
            Notify($"Run configuration '{rc.Name}' depends on itself through its Before launch steps.", true);
            return false;
        }
        if (rc.SaveBefore) await SaveAllAsync(quiet: true);

        foreach (var name in rc.BeforeLaunch)
        {
            if (AllRunConfigs.FirstOrDefault(c => c.Name == name) is not { } before)
            {
                Notify($"Before-launch step '{name}' of '{rc.Name}' does not exist.", true);
                return false;
            }
            if (!await RunConfigAsync(before, chain))
            {
                Notify($"'{name}' failed, so '{rc.Name}' was not started.", true);
                return false;
            }
        }

        var ctx = MacroContext();
        var command = Macros.Expand(rc.CommandLine, ctx);
        if (string.IsNullOrWhiteSpace(command))
        {
            Notify($"'{rc.Name}' has no command. Edit it in Edit Configurations.", true);
            return false;
        }
        var cwd = Macros.Expand(rc.WorkingDirectory, ctx);
        if (string.IsNullOrWhiteSpace(cwd)) cwd = _root!;
        var env = rc.Env.ToDictionary(kv => kv.Key, kv => Macros.Expand(kv.Value, ctx));

        var console = RunTab(rc.Name, rc.AllowParallel);
        if (rc.ActivateToolWindow && _bottomTool != "run") ShowBottom("run");
        var done = new TaskCompletionSource<bool>();
        console.Run(command, cwd, clear: Settings.ClearConsoleOnRun, exited: code => done.TrySetResult(code == 0), env: env);
        return await done.Task;
    }

    /// <summary>Gets (or creates) the Run tool window tab for a configuration.</summary>
    private ConsoleView RunTab(string name, bool parallel)
    {
        if (parallel && _runTabs.TryGetValue(name, out var busy) && busy.IsRunning)
        {
            var i = 2;
            while (_runTabs.TryGetValue($"{name} ({i})", out var other) && other.IsRunning) i++;
            name = $"{name} ({i})";
        }
        if (!_runTabs.TryGetValue(name, out var console))
        {
            console = new ConsoleView(interactive: false) { WorkingDirectory = _root ?? "", OpenFile = (p, l, c) => OpenFileAsync(p, l, c) };
            console.RunningChanged += () => { UpdateRunState(); BuildRunTabs(); };
            _runTabs[name] = console;
        }
        _activeRunTab = name;
        BuildRunTabs();
        return console;
    }

    private void BuildRunTabs()
    {
        _runTabStrip.Children.Clear();
        foreach (var (name, console) in _runTabs)
        {
            var tabName = name;
            var close = new Button { Classes = { "flat" }, Padding = new Thickness(3), Content = new TextBlock { Classes = { "icon" }, Text = "", FontSize = 8 } };
            close.Click += (_, _) =>
            {
                console.Stop();
                _runTabs.Remove(tabName);
                if (_activeRunTab == tabName) _activeRunTab = _runTabs.Keys.LastOrDefault();
                BuildRunTabs();
            };
            var tab = new Button
            {
                Classes = { "flat" },
                Padding = new Thickness(8, 3, 4, 3),
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children =
                    {
                        new TextBlock { Classes = { "icon" }, Text = console.IsRunning ? "" : "", FontSize = 10, Foreground = Res(console.IsRunning ? "Success" : "FgMuted") },
                        new TextBlock { Text = tabName, FontSize = 12 },
                        close,
                    },
                },
            };
            tab.Classes.Set("active", tabName == _activeRunTab);
            tab.Click += (_, _) => { _activeRunTab = tabName; BuildRunTabs(); };
            _runTabStrip.Children.Add(tab);
        }
        _runTabHost.Content = _activeRunTab is { } active && _runTabs.TryGetValue(active, out var c) ? c : new TextBlock
        {
            Text = "Nothing has run yet. Pick a configuration in the toolbar and press Run (Shift+F10).",
            Classes = { "muted" },
            Margin = new Thickness(12),
        };
    }

    private void UpdateRunState() => StopButton.IsEnabled = _runTabs.Values.Any(c => c.IsRunning);

    private void StopSelectedRun()
    {
        if (_activeRunTab is { } t && _runTabs.TryGetValue(t, out var active) && active.IsRunning) { active.Stop(); return; }
        foreach (var c in _runTabs.Values.Where(c => c.IsRunning)) c.Stop();
    }

    private void StopAllRuns()
    {
        foreach (var c in _runTabs.Values) c.Stop();
    }

    // ---- Edit Configurations dialog -------------------------------------------

    private void ShowRunConfigEditor()
    {
        if (_root is null) { Notify("Open a project first"); return; }
        var working = _runStore.Configurations.Select(c => c.Clone()).ToList();
        RunConfig? current = null;

        var list = new ListBox { Height = 300 };
        var detectedList = new ListBox { MaxHeight = 170 };
        var form = new ContentControl();

        var refreshing = false;
        void RefreshList(RunConfig? select = null)
        {
            refreshing = select is null;
            var keep = select ?? current;
            list.ItemsSource = working.Select(c => c.Name).ToList();
            if (keep is not null && working.Contains(keep)) list.SelectedIndex = working.IndexOf(keep);
            refreshing = false;
        }

        void Add(RunConfig rc)
        {
            var name = rc.Name;
            for (var i = 2; working.Any(c => c.Name == name); i++) name = $"{rc.Name} ({i})";
            rc.Name = name;
            working.Add(rc);
            RefreshList(rc);
        }

        list.SelectionChanged += (_, _) =>
        {
            if (refreshing || list.SelectedIndex < 0 || list.SelectedIndex >= working.Count) return;
            detectedList.SelectedIndex = -1;
            current = working[list.SelectedIndex];
            form.Content = ConfigForm(current, working, readOnly: false, () => RefreshList());
        };
        detectedList.ItemsSource = _detectedRuns.Select(d => $"{d.Name}   ·  {d.Source}").ToList();
        detectedList.SelectionChanged += (_, _) =>
        {
            if (detectedList.SelectedIndex < 0) return;
            list.SelectedIndex = -1;
            var d = _detectedRuns[detectedList.SelectedIndex];
            var copy = new Button { Content = "Make an editable copy", Classes = { "primary" } };
            copy.Click += (_, _) => Add(d.Clone());
            form.Content = new StackPanel { Spacing = 10, Children = { new TextBlock { Text = $"Detected by the {d.Source} plugin. Copy it to change its command, environment or before-launch steps.", TextWrapping = TextWrapping.Wrap, Classes = { "muted" } }, copy, ConfigForm(d, working, readOnly: true, () => { }) } };
        };

        var addMenu = new MenuFlyout();
        void AddItem(string header, Func<RunConfig> make)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => Add(make());
            addMenu.Items.Add(item);
        }
        AddItem("Shell Command", () => new RunConfig { Name = "New Shell Command", Type = "shell", Command = "" });
        AddItem("Program", () => new RunConfig { Name = "New Program", Type = "program" });
        AddItem("Run Current File", () => new RunConfig { Name = "Current File", Type = "shell", Command = IdePaths.IsWindows ? "\"${file}\"" : "\"${file}\"" });

        var addButton = ToolButton("", "Add New Configuration", () => { });
        addButton.Flyout = addMenu;
        var copyButton = ToolButton("", "Copy Configuration", () => { if (current is not null && working.Contains(current)) Add(current.Clone()); });
        var removeButton = ToolButton("", "Remove Configuration", () =>
        {
            if (current is null || !working.Contains(current)) return;
            working.Remove(current);
            current = null;
            form.Content = null;
            RefreshList();
        });

        var left = new StackPanel
        {
            Spacing = 6,
            Width = 250,
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Children = { addButton, copyButton, removeButton } },
                new TextBlock { Text = "Project configurations", Classes = { "muted" }, FontSize = 12 },
                list,
                new TextBlock { Text = "Detected by plugins", Classes = { "muted" }, FontSize = 12, Margin = new Thickness(0, 8, 0, 0) },
                detectedList,
            },
        };

        void Save()
        {
            _runStore.Configurations = working;
            _runStore.Save(_root!);
            if (current is not null && _projectState is not null) _projectState.SelectedRunConfig = current.Name;
            _projectState?.Save(_root!);
            RefreshRunConfigs();
        }

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Height = 470 };
        body.Children.Add(new Border { Child = left, BorderBrush = Res("Border"), BorderThickness = new Thickness(0, 0, 1, 0), Padding = new Thickness(0, 0, 12, 0) });
        body.Children.Add(new ScrollViewer { Content = form, Margin = new Thickness(16, 0, 0, 0), [Grid.ColumnProperty] = 1 });

        ShowOverlay(Dialog("Run Configurations", body, 920,
            ("OK", () => { Save(); _overlayCancel = null; CloseOverlay(); }),
            ("Apply", Save),
            ("Cancel", CloseOverlay)));

        RefreshList();
        if (working.Count > 0) list.SelectedIndex = Math.Max(0, working.FindIndex(c => c.Name == SelectedRunConfig?.Name));
        else if (_detectedRuns.Count > 0) detectedList.SelectedIndex = 0;
        else form.Content = new TextBlock { Text = "No configurations yet. Use + to add a shell command or a program.", Classes = { "muted" } };
    }

    private Control ConfigForm(RunConfig rc, List<RunConfig> all, bool readOnly, Action renamed)
    {
        TextBox Box(string text, Action<string> set, bool multiline = false)
        {
            var box = new TextBox { Text = text, Classes = { "mono" }, IsReadOnly = readOnly, AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.NoWrap : TextWrapping.NoWrap };
            if (multiline) { box.Height = 84; box.VerticalContentAlignment = VerticalAlignment.Top; }
            box.TextChanged += (_, _) => set(box.Text ?? "");
            return box;
        }
        Control Row(string label, Control input) => new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("150,*"),
            Children = { new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 0, 0) }, new ContentControl { Content = input, [Grid.ColumnProperty] = 1 } },
        };
        CheckBox Check(string label, bool value, Action<bool> set)
        {
            var cb = new CheckBox { Content = label, IsChecked = value, IsEnabled = !readOnly };
            cb.IsCheckedChanged += (_, _) => set(cb.IsChecked == true);
            return cb;
        }

        // The list is refreshed when the name field loses focus, not per keystroke (that would steal focus).
        TextBox NameBox()
        {
            var box = Box(rc.Name, v => rc.Name = v);
            box.LostFocus += (_, _) => renamed();
            return box;
        }

        var commandRows = new StackPanel { Spacing = 10 };
        void BuildCommandRows()
        {
            commandRows.Children.Clear();
            if (rc.Type == "program")
            {
                var browse = new Button { Content = "…", Classes = { "secondary" }, Padding = new Thickness(10, 4), Margin = new Thickness(6, 0, 0, 0), IsEnabled = !readOnly };
                var program = Box(rc.Program, v => rc.Program = v);
                browse.Click += async (_, _) =>
                {
                    var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Program" });
                    if (files.Count > 0 && files[0].TryGetLocalPath() is { } p) program.Text = p;
                };
                var programRow = new DockPanel { Children = { browse, program } };
                DockPanel.SetDock(browse, Dock.Right);
                commandRows.Children.Add(Row("Program:", programRow));
                commandRows.Children.Add(Row("Arguments:", Box(rc.Arguments, v => rc.Arguments = v)));
            }
            else
            {
                commandRows.Children.Add(Row("Command line:", Box(rc.Command, v => rc.Command = v)));
            }
        }
        BuildCommandRows();

        var type = new ComboBox { ItemsSource = new[] { "Shell command", "Program" }, SelectedIndex = rc.Type == "program" ? 1 : 0, IsEnabled = !readOnly, MinWidth = 200 };
        type.SelectionChanged += (_, _) => { rc.Type = type.SelectedIndex == 1 ? "program" : "shell"; BuildCommandRows(); };

        var env = Box(string.Join("\n", rc.Env.Select(kv => $"{kv.Key}={kv.Value}")), text =>
        {
            rc.Env = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim()).Where(l => l.Contains('='))
                .GroupBy(l => l[..l.IndexOf('=')].Trim())
                .ToDictionary(g => g.Key, g => g.Last()[(g.Last().IndexOf('=') + 1)..]);
        }, multiline: true);

        var before = new WrapPanel();
        foreach (var other in AllRunConfigs.Concat(all).Select(c => c.Name).Distinct().Where(n => n != rc.Name))
        {
            var name = other;
            before.Children.Add(Check(name, rc.BeforeLaunch.Contains(name), on =>
            {
                rc.BeforeLaunch.Remove(name);
                if (on) rc.BeforeLaunch.Add(name);
            }));
        }
        if (before.Children.Count == 0) before.Children.Add(new TextBlock { Text = "Add another configuration (e.g. a build) to run it first.", Classes = { "muted" } });

        var macros = new TextBlock
        {
            Text = "Macros: " + string.Join("   ", Macros.Documented.Select(m => m.Name)),
            TextWrapping = TextWrapping.Wrap,
            Classes = { "muted" },
            FontSize = 11,
        };

        return new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Row("Name:", NameBox()),
                Row("Type:", type),
                commandRows,
                Row("Working directory:", Box(rc.WorkingDirectory, v => rc.WorkingDirectory = v)),
                Row("Environment:", env),
                new TextBlock { Text = "One KEY=VALUE per line.", Classes = { "muted" }, FontSize = 11, Margin = new Thickness(150, -6, 0, 0) },
                Row("Before launch:", before),
                Check("Save all files before launch", rc.SaveBefore, v => rc.SaveBefore = v),
                Check("Show the Run tool window when started", rc.ActivateToolWindow, v => rc.ActivateToolWindow = v),
                Check("Allow multiple instances", rc.AllowParallel, v => rc.AllowParallel = v),
                Check("Store as a project file (.aether/runConfigurations/)", rc.FilePath is not null,
                    v => rc.FilePath = v ? RunConfigStore.FileNameFor(_root!, rc.Name) : null),
                macros,
            },
        };
    }
}
