using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MultiIde.Core;

namespace MultiIde.Views;

/// <summary>
/// Settings → Keymap: your own commands with shortcuts ("My Commands"), and rebinding any action
/// (built-in or plugin command) with alternatives ("Ctrl+B | F12") and two-stroke chords ("Ctrl+K Ctrl+D").
/// </summary>
public partial class MainWindow
{
    private static readonly (string Value, string Label)[] CommandOutputs =
    {
        ("run", "Run tool window"),
        ("notify", "Notification"),
        ("replaceSelection", "Replace selection (or document)"),
        ("insert", "Insert at caret"),
        ("replaceDocument", "Replace document"),
        ("silent", "Nothing (run silently)"),
    };

    protected override Control KeymapPage()
    {
        var search = new TextBox { Watermark = "Search actions or shortcuts", Width = 360, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12) };
        var list = new StackPanel { Spacing = 2 };
        var myCommands = new StackPanel { Spacing = 4 };

        void Changed()
        {
            Settings.Save();
            EnsurePluginCommandActions();
            RebuildKeymap();
            FillCommands();
            Fill();
        }

        // ---- My Commands ----------------------------------------------------------

        void FillCommands(CommandContribution? editing = null, bool isNew = false)
        {
            myCommands.Children.Clear();
            if (Settings.UserCommands.Count == 0 && editing is null)
                myCommands.Children.Add(new TextBlock { Text = "No commands yet. A command runs a shell command line, optionally on a shortcut.", Classes = { "muted" } });
            foreach (var c in Settings.UserCommands)
                myCommands.Children.Add(c == editing ? CommandForm(c, isNew: false) : CommandRow(c));
            if (isNew && editing is not null) myCommands.Children.Add(CommandForm(editing, isNew: true));
            if (editing is null)
            {
                var add = new Button { Content = "+ New Command…", Classes = { "secondary" }, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
                add.Click += (_, _) => FillCommands(new CommandContribution { Title = "", Command = "", Output = "run" }, isNew: true);
                myCommands.Children.Add(add);
            }
        }

        Control CommandRow(CommandContribution c)
        {
            var edit = new Button { Content = "Edit", Classes = { "flat" }, FontSize = 11 };
            edit.Click += (_, _) => FillCommands(c);
            var run = new Button { Content = "Run", Classes = { "flat" }, FontSize = 11 };
            run.Click += (_, _) => _ = RunPluginCommandAsync(c, null);
            var keys = KeysFor(new IdeAction(UserCommandPrefix + c.Id, c.Title, "", c.Keys, () => { }));
            return new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("320,200,Auto,Auto,*"),
                Children =
                {
                    new StackPanel
                    {
                        Children =
                        {
                            new TextBlock { Text = c.Title },
                            new TextBlock { Text = c.Command, Classes = { "muted", "mono" }, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis },
                        },
                    },
                    new TextBlock { Text = keys ?? "no shortcut", Classes = { keys is null ? "muted" : "mono" }, VerticalAlignment = VerticalAlignment.Center, [Grid.ColumnProperty] = 1 },
                    new ContentControl { Content = edit, [Grid.ColumnProperty] = 2, Margin = new Thickness(6, 0) },
                    new ContentControl { Content = run, [Grid.ColumnProperty] = 3 },
                },
            };
        }

        Control CommandForm(CommandContribution original, bool isNew)
        {
            var title = new TextBox { Text = original.Title, Watermark = "Name, e.g. Run Clippy", Width = 420 };
            var command = new TextBox { Text = original.Command, Watermark = "Command line, e.g. cargo clippy -- -D warnings", Width = 420, Classes = { "mono" } };
            var cwd = new TextBox { Text = original.WorkingDirectory ?? "", Watermark = "${workspaceFolder}", Width = 420, Classes = { "mono" } };
            var output = new ComboBox
            {
                ItemsSource = CommandOutputs.Select(o => o.Label).ToList(),
                SelectedIndex = Math.Max(0, Array.FindIndex(CommandOutputs, o => o.Value == original.Output)),
                MinWidth = 260,
            };
            var input = new ComboBox { ItemsSource = new[] { "Nothing", "Selection (or document)", "Whole document" }, SelectedIndex = original.Input switch { "selection" => 1, "document" => 2, _ => 0 }, MinWidth = 260 };
            var saveBefore = new CheckBox { Content = "Save all files first", IsChecked = original.SaveBefore };
            var keys = original.Keys ?? "";
            var shortcut = ShortcutEditor(keys, k => keys = k);
            var error = new TextBlock { Foreground = Res("Error"), FontSize = 12, IsVisible = false };

            Control Field(string label, Control control) => new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("150,*"),
                Children =
                {
                    new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center },
                    new ContentControl { Content = control, [Grid.ColumnProperty] = 1, HorizontalAlignment = HorizontalAlignment.Left },
                },
            };

            var save = new Button { Content = isNew ? "Add Command" : "Save", Classes = { "primary" } };
            var cancel = new Button { Content = "Cancel", Classes = { "secondary" } };
            var delete = new Button { Content = "Delete", Classes = { "flat" }, Foreground = Res("Error"), IsVisible = !isNew };
            save.Click += (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(title.Text) || string.IsNullOrWhiteSpace(command.Text))
                {
                    error.Text = "A command needs a name and a command line.";
                    error.IsVisible = true;
                    return;
                }
                original.Title = title.Text.Trim();
                original.Command = command.Text.Trim();
                original.WorkingDirectory = string.IsNullOrWhiteSpace(cwd.Text) ? null : cwd.Text.Trim();
                original.Output = CommandOutputs[Math.Max(0, output.SelectedIndex)].Value;
                original.Input = input.SelectedIndex switch { 1 => "selection", 2 => "document", _ => null };
                original.SaveBefore = saveBefore.IsChecked == true;
                original.Keys = keys.Length == 0 ? null : keys;
                if (isNew)
                {
                    original.Id = UniqueCommandId(original.Title);
                    Settings.UserCommands.Add(original);
                }
                Settings.Keymap.Remove(UserCommandPrefix + original.Id); // the form's shortcut is the one that counts
                Changed();
            };
            cancel.Click += (_, _) => FillCommands();
            delete.Click += (_, _) =>
            {
                Settings.UserCommands.Remove(original);
                Settings.Keymap.Remove(UserCommandPrefix + original.Id);
                Changed();
            };

            var form = new Border
            {
                BorderBrush = Res("BorderStrong"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14, 12),
                Margin = new Thickness(0, 4),
                Child = new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        Field("Name", title),
                        Field("Command", command),
                        Field("Working directory", cwd),
                        Field("Shortcut", shortcut),
                        Field("Send to stdin", input),
                        Field("Show output in", output),
                        Field("", saveBefore),
                        new TextBlock
                        {
                            Text = "Macros: " + string.Join("   ", Macros.Documented.Select(m => m.Name)),
                            Classes = { "muted" }, FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxWidth = 580, HorizontalAlignment = HorizontalAlignment.Left,
                        },
                        error,
                        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { save, cancel, delete } },
                    },
                },
            };
            Dispatcher.UIThread.Post(() => title.Focus(), DispatcherPriority.Input);
            return form;
        }

        // ---- Key mappings ------------------------------------------------------------

        void Fill()
        {
            list.Children.Clear();
            var q = search.Text?.Trim() ?? "";
            var owners = new Dictionary<string, List<IdeAction>>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in _actions)
                if (KeysFor(a) is { Length: > 0 } k)
                    foreach (var g in NormalizeKeys(k))
                        (owners.TryGetValue(g, out var l) ? l : owners[g] = new()).Add(a);
            foreach (var group in _actions
                         .Where(a => q.Length == 0 || $"{a.Group} {a.Title} {KeysFor(a)}".Contains(q, StringComparison.OrdinalIgnoreCase))
                         .GroupBy(a => a.Group))
            {
                list.Children.Add(new TextBlock { Text = group.Key, Classes = { "header" }, Margin = new Thickness(0, 12, 0, 4) });
                foreach (var a in group) list.Children.Add(Row(a, owners));
            }
        }

        Control Row(IdeAction a, Dictionary<string, List<IdeAction>> owners)
        {
            var keys = KeysFor(a) ?? "";
            var others = NormalizeKeys(keys)
                .SelectMany(g => owners.GetValueOrDefault(g) ?? new())
                .Where(o => o != a).Select(o => o.Title).Distinct().ToList();
            var editor = ShortcutEditor(keys, text =>
            {
                var normalizedDefault = a.Keys is null ? "" : string.Join(" | ", NormalizeKeys(a.Keys));
                if ((text.Length == 0 && a.Keys is null) || (text.Length > 0 && text == normalizedDefault)) Settings.Keymap.Remove(a.Id);
                else Settings.Keymap[a.Id] = text;
                Settings.Save();
                RebuildKeymap();
                Fill();
            });
            var reset = new Button { Content = "Reset", Classes = { "flat" }, FontSize = 11, IsVisible = Settings.Keymap.ContainsKey(a.Id) };
            reset.Click += (_, _) => { Settings.Keymap.Remove(a.Id); Settings.Save(); RebuildKeymap(); Fill(); };
            return new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("320,Auto,Auto,*"),
                Margin = new Thickness(0, 1),
                Children =
                {
                    new TextBlock { Text = a.Title, VerticalAlignment = VerticalAlignment.Center },
                    new ContentControl { Content = editor, [Grid.ColumnProperty] = 1 },
                    new ContentControl { Content = reset, [Grid.ColumnProperty] = 2, Margin = new Thickness(6, 0) },
                    new TextBlock
                    {
                        Text = others.Count > 0 ? "⚠ also: " + string.Join(", ", others) : "",
                        Foreground = Res("Warning"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                        [Grid.ColumnProperty] = 3,
                    },
                },
            };
        }

        var preset = new ComboBox { ItemsSource = KeymapPresets.All.Select(p => p.Name).ToList(), SelectedIndex = Math.Max(0, Array.FindIndex(KeymapPresets.All, p => p.Id == Settings.KeymapPreset)), MinWidth = 180 };
        var presetInfo = new TextBlock { Classes = { "muted" }, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 };
        presetInfo.Text = KeymapPresets.All[preset.SelectedIndex].Description;
        preset.SelectionChanged += (_, _) =>
        {
            Settings.KeymapPreset = KeymapPresets.All[preset.SelectedIndex].Id;
            presetInfo.Text = KeymapPresets.All[preset.SelectedIndex].Description;
            Settings.Save();
            RebuildKeymap();
            Fill();
        };
        var resetAll = new Button { Content = "Reset all to the preset", Classes = { "secondary" }, Margin = new Thickness(0, 0, 0, 8) };
        resetAll.Click += (_, _) => { Settings.Keymap.Clear(); Settings.Save(); RebuildKeymap(); Fill(); FillCommands(); };
        search.TextChanged += (_, _) => Fill();
        FillCommands();
        Fill();
        return new StackPanel
        {
            Children =
            {
                PageTitle("Keymap"),
                new TextBlock { Text = "My Commands", Classes = { "header" }, Margin = new Thickness(0, 0, 0, 4) },
                new TextBlock { Text = "Your own commands: any command line, run with a shortcut, from Find Action (Ctrl+Shift+A) or from the toolbar.", Classes = { "muted" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) },
                myCommands,
                new TextBlock { Text = "Key Mappings", Classes = { "header" }, Margin = new Thickness(0, 20, 0, 4) },
                new TextBlock
                {
                    Text = "Click a shortcut field and press the keys; Backspace clears it. \"or…\" adds another shortcut for the same action, \"then…\" adds a second keystroke (a chord like Ctrl+K Ctrl+D). Plugins can add default bindings; your choices win.",
                    Classes = { "muted" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
                },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 0, 0, 10), Children = { new TextBlock { Text = "Preset:", VerticalAlignment = VerticalAlignment.Center }, preset, presetInfo } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { search, resetAll } },
                list,
            },
        };
    }

    /// <summary>
    /// A shortcut recorder: press keys to set; "or…" records an alternative, "then…" records a chord's second stroke.
    /// <paramref name="committed"/> receives the canonical text ("Ctrl+B | Ctrl+K Ctrl+D") when the field loses focus.
    /// </summary>
    private Control ShortcutEditor(string initial, Action<string> committed)
    {
        var box = new TextBox { Text = initial, Width = 220, Classes = { "mono" }, Watermark = "Press a shortcut", IsReadOnly = true };
        var mode = "replace";
        var original = initial;
        box.AddHandler(KeyDownEvent, (_, e) =>
        {
            e.Handled = true;
            if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;
            if (e.Key is Key.Back or Key.Delete && e.KeyModifiers == KeyModifiers.None) { box.Text = ""; mode = "replace"; return; }
            if (GestureOf(e) is not { } g) return;
            var current = box.Text ?? "";
            box.Text = mode switch
            {
                "or" when current.Length > 0 => $"{current} | {g}",
                "then" when current.Length > 0 => $"{current} {g}",
                _ => g,
            };
            mode = "replace";
            box.Watermark = "Press a shortcut";
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        void Arm(string m, string hint)
        {
            mode = m;
            box.Watermark = hint;
            box.Focus();
            StatusMessage.Text = hint;
        }
        var or = new Button { Content = "or…", Classes = { "flat" }, FontSize = 11, Padding = new Thickness(6, 2) };
        var then = new Button { Content = "then…", Classes = { "flat" }, FontSize = 11, Padding = new Thickness(6, 2) };
        ToolTip.SetTip(or, "Add another shortcut for the same action");
        ToolTip.SetTip(then, "Add a second keystroke (chord), e.g. Ctrl+K then Ctrl+D");
        or.Click += (_, _) => Arm("or", "Press the additional shortcut");
        then.Click += (_, _) => Arm("then", "Press the second keystroke");

        box.LostFocus += (_, _) =>
        {
            // Clicking "or…" / "then…" moves focus briefly; only commit real changes.
            var text = string.Join(" | ", NormalizeKeys(box.Text ?? ""));
            if (text == string.Join(" | ", NormalizeKeys(original))) return;
            original = text;
            committed(text);
        };
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Children = { box, or, then } };
    }

    private string UniqueCommandId(string title)
    {
        var slug = new string(title.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');
        if (slug.Length == 0) slug = "command";
        var id = slug;
        for (var i = 2; Settings.UserCommands.Any(c => c.Id == id); i++) id = $"{slug}-{i}";
        return id;
    }
}
