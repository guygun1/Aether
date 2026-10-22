using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using MultiIde.Core;

namespace MultiIde.Views;

/// <summary>Settings → Keymap: rebind any action (built-in or plugin command).</summary>
public partial class MainWindow
{
    protected override Control KeymapPage()
    {
        var search = new TextBox { Watermark = "Search actions or shortcuts", Width = 360, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12) };
        var list = new StackPanel { Spacing = 2 };

        void Fill()
        {
            list.Children.Clear();
            var q = search.Text?.Trim() ?? "";
            var usage = _actions.Select(a => (a, Keys: KeysFor(a))).Where(x => x.Keys is { Length: > 0 })
                .GroupBy(x => NormalizeGesture(x.Keys!)).ToDictionary(g => g.Key, g => g.Count());
            foreach (var group in _actions
                         .Where(a => q.Length == 0 || $"{a.Group} {a.Title} {KeysFor(a)}".Contains(q, StringComparison.OrdinalIgnoreCase))
                         .GroupBy(a => a.Group))
            {
                list.Children.Add(new TextBlock { Text = group.Key, Classes = { "header" }, Margin = new Thickness(0, 12, 0, 4) });
                foreach (var a in group) list.Children.Add(Row(a, usage));
            }
        }

        Control Row(IdeAction a, Dictionary<string, int> usage)
        {
            var keys = KeysFor(a) ?? "";
            var conflict = keys.Length > 0 && !keys.Contains("Double") && usage.GetValueOrDefault(NormalizeGesture(keys)) > 1;
            var box = new TextBox { Text = keys, Width = 200, Classes = { "mono" }, Watermark = "Press a shortcut", IsReadOnly = true };
            // Record the shortcut by pressing it, like IntelliJ's "Add Keyboard Shortcut".
            box.AddHandler(KeyDownEvent, (_, e) =>
            {
                e.Handled = true;
                if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;
                if (e.Key is Key.Back or Key.Delete && e.KeyModifiers == KeyModifiers.None) { box.Text = ""; return; }
                if (GestureOf(e) is { } g) box.Text = g;
            }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            box.LostFocus += (_, _) =>
            {
                var text = box.Text ?? "";
                var normalizedDefault = a.Keys is null ? "" : NormalizeGesture(a.Keys);
                if ((text.Length == 0 && a.Keys is null) || (text.Length > 0 && NormalizeGesture(text) == normalizedDefault)) Settings.Keymap.Remove(a.Id);
                else Settings.Keymap[a.Id] = text.Length == 0 ? "" : NormalizeGesture(text);
                Settings.Save();
                RebuildKeymap();
                Fill();
            };
            var reset = new Button { Content = "Reset", Classes = { "flat" }, FontSize = 11, IsVisible = Settings.Keymap.ContainsKey(a.Id) };
            reset.Click += (_, _) => { Settings.Keymap.Remove(a.Id); Settings.Save(); RebuildKeymap(); Fill(); };
            return new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("320,Auto,Auto,*"),
                Margin = new Thickness(0, 1),
                Children =
                {
                    new TextBlock { Text = a.Title, VerticalAlignment = VerticalAlignment.Center },
                    new ContentControl { Content = box, [Grid.ColumnProperty] = 1 },
                    new ContentControl { Content = reset, [Grid.ColumnProperty] = 2, Margin = new Thickness(6, 0) },
                    new TextBlock { Text = conflict ? "⚠ also used by another action" : "", Foreground = Res("Warning"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, [Grid.ColumnProperty] = 3 },
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
        resetAll.Click += (_, _) => { Settings.Keymap.Clear(); Settings.Save(); RebuildKeymap(); Fill(); };
        search.TextChanged += (_, _) => Fill();
        Fill();
        return new StackPanel
        {
            Children =
            {
                PageTitle("Keymap"),
                new TextBlock { Text = "Click a shortcut field and press the new keys. Backspace clears it. Plugins can add default bindings; your choices win.", Classes = { "muted" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 0, 0, 10), Children = { new TextBlock { Text = "Preset:", VerticalAlignment = VerticalAlignment.Center }, preset, presetInfo } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { search, resetAll } },
                list,
            },
        };
    }
}
