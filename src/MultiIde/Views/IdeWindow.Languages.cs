using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using MultiIde.Core;

namespace MultiIde.Views;

/// <summary>"Add Language Support" wizard and the IntelliJ plugin converter.</summary>
public abstract partial class IdeWindow
{
    private void ShowLanguageWizard(Action reload)
    {
        var list = new ListBox
        {
            Height = 400,
            Width = 260,
            ItemsSource = LanguageCatalog.All,
            ItemTemplate = new FuncDataTemplate<CatalogLanguage>((l, _) => l is null ? new TextBlock() : new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Children =
                {
                    new Border
                    {
                        Width = 26, Height = 22, CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(Color.Parse(l.Color)),
                        Child = new TextBlock { Text = l.Icon, FontSize = 9.5, FontWeight = FontWeight.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                    },
                    new TextBlock { Text = l.Name, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock
                    {
                        Text = Plugins.All.Any(p => p.Id == $"lang-{l.Id}") ? "added" : "", Classes = { "muted" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            }),
        };
        var details = new StackPanel { Spacing = 8 };
        var installServer = new CheckBox { Content = "Also install the language server now", IsChecked = true };

        void Show()
        {
            details.Children.Clear();
            if (list.SelectedItem is not CatalogLanguage l) return;
            var found = l.Command is not null && ProcessUtil.Resolve(l.Command) is not null;
            details.Children.Add(new TextBlock { Text = l.Name, FontSize = 16, FontWeight = FontWeight.SemiBold });
            details.Children.Add(new TextBlock { Text = "Files: " + string.Join(" ", l.Extensions), Classes = { "muted" } });
            details.Children.Add(new TextBlock { Text = $"Language server: {l.ServerName ?? "none"}  {(found ? "✓ installed" : l.Command is null ? "" : "✖ not installed")}", Foreground = Res(found ? "Success" : "FgMuted") });
            if (l.Install is not null) details.Children.Add(new SelectableTextBlock { Text = $"Install: {l.Install}", FontFamily = (FontFamily)Application.Current!.FindResource("MonoFont")!, FontSize = 12 });
            else if (l.InstallUrl is not null) details.Children.Add(new TextBlock { Text = $"Install from {l.InstallUrl}", TextWrapping = TextWrapping.Wrap, Classes = { "muted" } });
            if (l.RunCommand is not null) details.Children.Add(new TextBlock { Text = $"Run configuration: {l.RunCommand}", Classes = { "muted" } });
            installServer.IsVisible = l.Install is not null && !found;
            details.Children.Add(installServer);
        }
        list.SelectionChanged += (_, _) => Show();
        list.SelectedIndex = 0;

        // Custom language: name, extensions, comment, server, grammar.
        var cName = new TextBox { Watermark = "Language name, e.g. Odin" };
        var cExt = new TextBox { Watermark = ".odin .od" };
        var cComment = new TextBox { Watermark = "//", Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
        var cServer = new TextBox { Watermark = "Language server command (optional), e.g. ols", Classes = { "mono" } };
        var cArgs = new TextBox { Watermark = "Arguments, e.g. --stdio", Classes = { "mono" } };
        var cInstall = new TextBox { Watermark = "Install command (optional)", Classes = { "mono" } };
        string? grammar = null;
        var grammarLabel = new TextBlock { Text = "No grammar (plain text, or the IDE's own if one matches)", Classes = { "muted" }, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var pickGrammar = new Button { Content = "Choose .tmLanguage.json…", Classes = { "secondary" } };
        pickGrammar.Click += async (_, _) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "TextMate grammar (.tmLanguage.json)", FileTypeFilter = [new FilePickerFileType("TextMate grammar") { Patterns = ["*.json"] }] });
            if (files.Count > 0 && files[0].TryGetLocalPath() is { } p) { grammar = p; grammarLabel.Text = Path.GetFileName(p); }
        };
        Control Row(string label, Control c) => new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("120,*"),
            Children = { new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }, new ContentControl { Content = c, [Grid.ColumnProperty] = 1 } },
        };
        var custom = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Any other language: VS Code and TextMate grammars (.tmLanguage.json) work as-is.", Classes = { "muted" }, TextWrapping = TextWrapping.Wrap },
                Row("Name:", cName), Row("Extensions:", cExt), Row("Line comment:", cComment),
                Row("Server:", cServer), Row("Server args:", cArgs), Row("Install:", cInstall),
                Row("Grammar:", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { pickGrammar, grammarLabel } }),
            },
        };

        var tabs = new TabControl
        {
            Items =
            {
                new TabItem { Header = "Popular languages", Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { list, details } } },
                new TabItem { Header = "Custom language", Content = custom },
            },
        };

        void Add()
        {
            try
            {
                if (tabs.SelectedIndex == 0 && list.SelectedItem is CatalogLanguage l)
                {
                    LanguageCatalog.CreatePlugin(l);
                    Plugins.Reload();
                    Notify($"{l.Name} support added.");
                    if (installServer.IsVisible && installServer.IsChecked == true && l.Install is not null) RunInstallWithoutProject(new LspServerConfig { Name = l.ServerName ?? l.Name, Install = l.Install });
                }
                else
                {
                    var name = (cName.Text ?? "").Trim();
                    var exts = (cExt.Text ?? "").Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries);
                    if (name.Length == 0 || exts.Length == 0) { Notify("Enter a name and at least one extension.", true); return; }
                    var id = new string(name.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
                    LanguageCatalog.CreateCustom(id, name, exts, cComment.Text, cServer.Text?.Trim(), (cArgs.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries), cInstall.Text, grammar);
                    Plugins.Reload();
                    Notify($"{name} support added. Edit it any time in ~/.aether/plugins/lang-{id}/plugin.json.");
                }
            }
            catch (Exception e)
            {
                Notify(e.Message, true);
                return;
            }
            _overlayCancel = null;
            CloseOverlay();
            reload();
        }

        ShowOverlay(Dialog("Add Language Support", tabs, 720, ("Add", Add), ("Cancel", CloseOverlay)));
    }

    private async Task ConvertIntelliJPluginAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "IntelliJ plugin (.jar or .zip from the JetBrains Marketplace)",
            FileTypeFilter = [new FilePickerFileType("IntelliJ plugin") { Patterns = ["*.jar", "*.zip"] }],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
        IntelliJConverter.Report report;
        try { report = await Task.Run(() => IntelliJConverter.Convert(path)); }
        catch (Exception e) { Notify($"Could not convert: {e.Message}", true); return; }
        Plugins.Reload();

        var body = new StackPanel { Spacing = 6, MaxWidth = 640 };
        body.Children.Add(new TextBlock { Text = $"Installed as \"{report.Name} (converted)\" in {report.OutputDir}", TextWrapping = TextWrapping.Wrap, Classes = { "muted" } });
        body.Children.Add(new TextBlock { Text = $"Converted ({report.Converted.Count})", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
        foreach (var c in report.Converted) body.Children.Add(new TextBlock { Text = "✓ " + c, Foreground = Res("Success"), TextWrapping = TextWrapping.Wrap });
        if (report.Skipped.Count > 0)
        {
            body.Children.Add(new TextBlock { Text = "Not converted", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
            foreach (var s in report.Skipped) body.Children.Add(new TextBlock { Text = "• " + s, Classes = { "muted" }, TextWrapping = TextWrapping.Wrap });
        }
        ShowOverlay(Dialog("IntelliJ plugin converted", new ScrollViewer { Content = body, MaxHeight = 480 }, 700, ("Close", CloseOverlay)));
    }
}
