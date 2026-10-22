using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MultiIde.Core;
using MultiIde.Editor;

namespace MultiIde.Views;

/// <summary>Settings → Live Templates: the user's own snippets plus a view of plugin templates.</summary>
public abstract partial class IdeWindow
{
    private Control LiveTemplatesPage()
    {
        var languages = Plugins.Enabled.SelectMany(p => p.Manifest.Contributes.Languages).Select(l => (l.Id, l.Name)).DistinctBy(l => l.Id).OrderBy(l => l.Name).ToList();
        languages.Insert(0, ("*", "Any language"));
        var list = new ListBox { Height = 380, Width = 280 };
        var form = new ContentControl();
        var user = UserSnippets.All;
        List<(SnippetContribution Snippet, string Source)> rows = new();

        void Refresh(SnippetContribution? select = null)
        {
            rows = user.Select(s => (s, "Yours"))
                .Concat(Plugins.Enabled.SelectMany(p => p.Manifest.Contributes.Snippets.Select(s => (s, p.Manifest.Name))))
                .ToList();
            list.ItemsSource = rows.Select(r => $"{r.Snippet.Prefix}   ·  {r.Snippet.Language}   ·  {r.Source}").ToList();
            if (select is not null) list.SelectedIndex = rows.FindIndex(r => r.Snippet == select);
        }

        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedIndex < 0 || list.SelectedIndex >= rows.Count) return;
            var (snippet, source) = rows[list.SelectedIndex];
            form.Content = TemplateForm(snippet, source == "Yours", languages, () => { UserSnippets.Save(); });
        };

        var add = new Button { Content = "Add", Classes = { "primary" } };
        add.Click += (_, _) =>
        {
            var s = UserSnippets.Create("*", "mytemplate", "${1:value}$0", "My template");
            user.Add(s);
            UserSnippets.Save();
            Refresh(s);
        };
        var copy = new Button { Content = "Copy to mine", Classes = { "secondary" } };
        copy.Click += (_, _) =>
        {
            if (list.SelectedIndex < 0) return;
            var src = rows[list.SelectedIndex].Snippet;
            var s = UserSnippets.Create(src.Language, src.Prefix, src.BodyText, src.Description);
            user.Add(s);
            UserSnippets.Save();
            Refresh(s);
        };
        var remove = new Button { Content = "Delete", Classes = { "secondary" } };
        remove.Click += (_, _) =>
        {
            if (list.SelectedIndex < 0 || rows[list.SelectedIndex].Source != "Yours") return;
            user.Remove(rows[list.SelectedIndex].Snippet);
            UserSnippets.Save();
            form.Content = null;
            Refresh();
        };
        Refresh();
        if (rows.Count > 0) list.SelectedIndex = 0;

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        body.Children.Add(new StackPanel { Spacing = 8, Children = { new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { add, copy, remove } }, list } });
        body.Children.Add(new ContentControl { Content = form, Margin = new Thickness(20, 0, 0, 0), [Grid.ColumnProperty] = 1 });
        return new StackPanel
        {
            Children =
            {
                PageTitle("Live Templates"),
                new TextBlock
                {
                    Text = "Type a template's abbreviation and press Tab, or pick it from completion. Fields: $1, ${1:default}, ${1|a,b|}; $0 is where the caret ends. " +
                           "Use ${TM_SELECTED_TEXT} to make it available in Surround With (Ctrl+Alt+T). Other variables: TM_FILENAME, TM_FILENAME_BASE, CURRENT_YEAR, UUID.",
                    TextWrapping = TextWrapping.Wrap, Classes = { "muted" }, Margin = new Thickness(0, 0, 0, 12),
                },
                body,
            },
        };
    }

    private Control TemplateForm(SnippetContribution s, bool editable, List<(string Id, string Name)> languages, Action save)
    {
        var mono = (FontFamily)Application.Current!.FindResource("MonoFont")!;
        var preview = new TextBlock { FontFamily = mono, FontSize = 12.5 };
        void UpdatePreview()
        {
            try { preview.Text = SnippetSyntax.Parse(s.BodyText, new SnippetSyntax.Context("", "    ", "\n", "selection", "File.ext", "//")).Text; }
            catch { preview.Text = "(invalid template)"; }
        }
        var prefix = new TextBox { Text = s.Prefix, IsReadOnly = !editable, Classes = { "mono" } };
        prefix.TextChanged += (_, _) => { s.Prefix = prefix.Text ?? ""; save(); };
        var description = new TextBox { Text = s.Description, IsReadOnly = !editable };
        description.TextChanged += (_, _) => { s.Description = description.Text ?? ""; save(); };
        var language = new ComboBox { ItemsSource = languages.Select(l => l.Name).ToList(), SelectedIndex = Math.Max(0, languages.FindIndex(l => l.Id == s.Language)), IsEnabled = editable, MinWidth = 200 };
        language.SelectionChanged += (_, _) => { s.Language = languages[language.SelectedIndex].Id; save(); };
        var bodyBox = new TextBox
        {
            Text = s.BodyText,
            IsReadOnly = !editable,
            AcceptsReturn = true,
            AcceptsTab = true,
            Height = 150,
            FontFamily = mono,
            FontSize = 12.5,
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        bodyBox.TextChanged += (_, _) => { s.Body = System.Text.Json.Nodes.JsonValue.Create(bodyBox.Text ?? ""); save(); UpdatePreview(); };
        UpdatePreview();

        Control Row(string label, Control input) => new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("110,*"),
            Children = { new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }, new ContentControl { Content = input, [Grid.ColumnProperty] = 1 } },
        };
        return new StackPanel
        {
            Spacing = 10,
            Children =
            {
                editable ? new TextBlock() : new TextBlock { Text = "From a plugin (read-only). Use \"Copy to mine\" to customize it.", Classes = { "muted" }, FontSize = 12 },
                Row("Abbreviation:", prefix),
                Row("Description:", description),
                Row("Language:", language),
                new TextBlock { Text = "Template text:" },
                bodyBox,
                new TextBlock { Text = "Preview:", Classes = { "muted" } },
                new Border { Background = Res("EditorBg"), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 8), Child = preview },
            },
        };
    }
}
