using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MultiIde.Core;

namespace MultiIde.Views;

/// <summary>The searchable Settings page, generated from <see cref="SettingsSchema"/>.</summary>
public abstract partial class IdeWindow
{
    private Control CustomizePage()
    {
        var search = new TextBox { Watermark = "Search settings", Width = 360, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12) };
        var list = new StackPanel { Spacing = 4 };

        void Changed(SettingDef def)
        {
            Settings.Save();
            if (def.Kind == "theme") App.SetTheme(Settings.Theme);
            App.BroadcastSettings();
        }

        void Fill()
        {
            list.Children.Clear();
            var q = search.Text?.Trim() ?? "";
            foreach (var group in SettingsSchema.All
                         .Where(d => q.Length == 0 || $"{d.Category} {d.Title} {d.Description}".Contains(q, StringComparison.OrdinalIgnoreCase))
                         .GroupBy(d => d.Category))
            {
                list.Children.Add(new TextBlock { Text = group.Key, Classes = { "header" }, FontSize = 14, Margin = new Thickness(0, 14, 0, 4) });
                foreach (var def in group) list.Children.Add(SettingRow(def, Changed));
            }
            if (list.Children.Count == 0) list.Children.Add(new TextBlock { Text = "Nothing matches.", Classes = { "muted" } });
        }

        search.TextChanged += (_, _) => Fill();
        Fill();
        return new StackPanel
        {
            Children =
            {
                PageTitle("Settings"),
                search,
                list,
                new TextBlock { Text = $"Stored in {IdePaths.SettingsFile}", Classes = { "muted" }, FontSize = 11, Margin = new Thickness(0, 18, 0, 0) },
            },
        };
    }

    private Control SettingRow(SettingDef def, Action<SettingDef> changed)
    {
        Control input;
        var value = def.Get(Settings);
        switch (def.Kind)
        {
            case "bool":
            {
                var cb = new CheckBox { Content = def.Title, IsChecked = (bool)value };
                cb.IsCheckedChanged += (_, _) => { def.Set(Settings, cb.IsChecked == true); changed(def); };
                return Described(cb, def.Description, indent: 28);
            }
            case "int":
            case "double":
            {
                var n = new NumericUpDown
                {
                    Value = Convert.ToDecimal(value),
                    Minimum = (decimal)def.Min,
                    Maximum = (decimal)def.Max,
                    Increment = (decimal)def.Step,
                    Width = 150,
                    FormatString = def.Kind == "int" ? "0" : "0.##",
                };
                n.ValueChanged += (_, _) => { if (n.Value is { } v) { def.Set(Settings, def.Kind == "int" ? (int)v : (double)v); changed(def); } };
                input = n;
                break;
            }
            case "choice":
            {
                var choices = def.Choices ?? [];
                var combo = new ComboBox { ItemsSource = choices.Select(c => c.Label).ToList(), MinWidth = 180, SelectedIndex = Array.FindIndex(choices, c => c.Value == (string)value) };
                combo.SelectionChanged += (_, _) => { if (combo.SelectedIndex >= 0) { def.Set(Settings, choices[combo.SelectedIndex].Value); changed(def); } };
                input = combo;
                break;
            }
            case "theme":
            {
                var themes = ThemeManager.Available(Plugins);
                var combo = new ComboBox { ItemsSource = themes.Select(t => t.Name).ToList(), MinWidth = 220, SelectedIndex = themes.FindIndex(t => t.Id == (string)value) };
                combo.SelectionChanged += (_, _) => { if (combo.SelectedIndex >= 0) { def.Set(Settings, themes[combo.SelectedIndex].Id); changed(def); } };
                input = combo;
                break;
            }
            default:
            {
                var box = new TextBox { Text = (string)value, MinWidth = 320 };
                box.LostFocus += (_, _) => { if (box.Text != (string)def.Get(Settings)) { def.Set(Settings, box.Text ?? ""); changed(def); } };
                input = box;
                break;
            }
        }
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("260,*"),
            Children =
            {
                new TextBlock { Text = def.Title, VerticalAlignment = VerticalAlignment.Center },
                new ContentControl { Content = input, [Grid.ColumnProperty] = 1, HorizontalAlignment = HorizontalAlignment.Left },
            },
        };
        return Described(row, def.Description, indent: 0);
    }

    private static Control Described(Control control, string? description, double indent)
    {
        if (string.IsNullOrEmpty(description)) return new Border { Child = control, Padding = new Thickness(0, 3) };
        return new StackPanel
        {
            Margin = new Thickness(0, 3),
            Children =
            {
                control,
                new TextBlock { Text = description, Classes = { "muted" }, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(indent, 2, 0, 0) },
            },
        };
    }
}
