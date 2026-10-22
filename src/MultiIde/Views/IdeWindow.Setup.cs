using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using MultiIde.Core;

namespace MultiIde.Views;

/// <summary>
/// First-run setup (also reachable later from the Welcome window): import settings from
/// another IDE, then pick theme, keymap, code style, layout and motion.
/// </summary>
public abstract partial class IdeWindow
{
    /// <summary>Card-style single choice, used by the setup page.</summary>
    private Control ChoiceCards((string Id, string Title, string Description)[] options, string selected, Action<string> onSelect, double width = 210)
    {
        var panel = new WrapPanel();
        var cards = new List<(Border Card, string Id)>();
        void Mark(string id)
        {
            foreach (var (card, cid) in cards)
            {
                card.BorderBrush = Res(cid == id ? "Accent" : "Border");
                card.Background = Res(cid == id ? "Selection" : "PanelBg");
            }
        }
        foreach (var (id, title, description) in options)
        {
            var card = new Border
            {
                Width = width,
                MinHeight = 74,
                Margin = new Thickness(0, 0, 10, 10),
                Padding = new Thickness(12, 10),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(2),
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = new StackPanel
                {
                    Spacing = 4,
                    Children =
                    {
                        new TextBlock { Text = title, FontWeight = FontWeight.SemiBold },
                        new TextBlock { Text = description, Classes = { "muted" }, FontSize = 11.5, TextWrapping = TextWrapping.Wrap },
                    },
                },
            };
            var cid = id;
            card.PointerPressed += (_, e) => { Mark(cid); onSelect(cid); e.Handled = true; };
            cards.Add((card, id));
            panel.Children.Add(card);
        }
        Mark(selected);
        return panel;
    }

    private Control SetupPage(Action finished)
    {
        var s = Settings;
        var sources = SettingsImport.Detect();
        ImportSource? chosen = null;
        var themePicked = false;

        var importOptions = sources.Select(src => (src.Path, src.Name, src.Kind == "jetbrains" ? "Font, size, line spacing, light/dark, recent projects" : "Font, size, tabs, light/dark, recent solutions"))
            .Append(("none", "Don't import", "Start with the defaults"))
            .ToArray();

        var content = new StackPanel { Spacing = 6 };
        void Section(string title, string? hint = null)
        {
            content.Children.Add(new TextBlock { Text = title, Classes = { "header" }, FontSize = 15, Margin = new Thickness(0, 16, 0, 2) });
            if (hint is not null) content.Children.Add(new TextBlock { Text = hint, Classes = { "muted" }, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
        }

        // Keep the later sections in sync when an import picks presets.
        var themeHost = new ContentControl();
        var keymapHost = new ContentControl();
        var styleHost = new ContentControl();
        var layoutHost = new ContentControl();
        void BuildPresetCards()
        {
            var themes = ThemeManager.Available(Plugins).Select(t => (t.Id, t.Name, t.Id switch
            {
                "midnight" => "Deep neutral dark",
                "daylight" => "Clean light",
                "ember" => "Warm classic dark",
                "high-contrast" => "Maximum legibility",
                _ => t.Source,
            })).ToArray();
            themeHost.Content = ChoiceCards(themes, s.Theme, id => { s.Theme = id; themePicked = true; App.SetTheme(id); }, 170);
            keymapHost.Content = ChoiceCards(KeymapPresets.All, s.KeymapPreset, id => s.KeymapPreset = id);
            styleHost.Content = ChoiceCards(CodeStyles.All, s.CodeStyle, id => s.CodeStyle = id);
            layoutHost.Content = ChoiceCards(new[]
            {
                ("left", "Project on the left", "Project tree left, tool windows at the bottom."),
                ("right", "Project on the right", "Project tree right of the editor, like a solution explorer."),
            }, s.ProjectPanelSide, id => s.ProjectPanelSide = id);
        }

        Section("Import settings", sources.Count == 0
            ? "No other IDEs were found on this computer."
            : "Bring over your editor font, tabs, theme preference and recent projects. Presets below are pre-selected to match.");
        content.Children.Add(ChoiceCards(importOptions, "none", id =>
        {
            chosen = sources.FirstOrDefault(x => x.Path == id);
            if (chosen is not null)
            {
                s.KeymapPreset = chosen.Kind == "visualstudio" ? "visualstudio" : "jetbrains";
                s.CodeStyle = s.KeymapPreset;
                s.ProjectPanelSide = chosen.Kind == "visualstudio" ? "right" : "left";
                BuildPresetCards();
            }
        }, 260));

        Section("Theme");
        content.Children.Add(themeHost);
        Section("Keymap", "You can rebind anything later in Settings → Keymap.");
        content.Children.Add(keymapHost);
        Section("Code style", "Brace placement and the .editorconfig / .clang-format written into new projects.");
        content.Children.Add(styleHost);
        Section("Layout");
        content.Children.Add(layoutHost);
        Section("Motion");
        content.Children.Add(ChoiceCards(new[]
        {
            ("animated", "Animated", "Smooth scrolling, sliding panels, fading popups."),
            ("instant", "Instant", "No animations; everything snaps immediately."),
        }, s.AnimationMode, id => s.AnimationMode = id));
        BuildPresetCards();

        var status = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, Classes = { "muted" } };
        var finish = new Button { Content = "Finish setup", Classes = { "primary" } };
        var skip = new Button { Content = "Skip", Classes = { "secondary" } };
        finish.Click += (_, _) =>
        {
            var lines = new List<string>();
            if (chosen is not null)
            {
                var keymap = s.KeymapPreset;
                var style = s.CodeStyle;
                var side = s.ProjectPanelSide;
                var theme = s.Theme;
                lines = SettingsImport.Import(chosen, s);
                if (themePicked) s.Theme = theme;
                // The user's explicit picks on this page win over the importer's presets.
                (s.KeymapPreset, s.CodeStyle, s.ProjectPanelSide) = (keymap, style, side);
            }
            s.SetupDone = true;
            s.Save();
            App.SetTheme(s.Theme);
            App.BroadcastSettings();
            if (lines.Count > 0) Notify($"Imported from {chosen!.Name}:\n• " + string.Join("\n• ", lines));
            finished();
        };
        skip.Click += (_, _) => { s.SetupDone = true; s.Save(); finished(); };

        return new StackPanel
        {
            Children =
            {
                PageTitle("Welcome to Aether"),
                new TextBlock { Text = "A few choices to make it feel like home. Everything can be changed later in Settings.", Classes = { "muted" } },
                content,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 20, 0, 0), Children = { finish, skip } },
                status,
            },
        };
    }
}
