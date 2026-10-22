using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MultiIde.Core;

namespace MultiIde.Views;

/// <summary>Dependencies tool window: packages from every manifest a plugin knows how to read.</summary>
public partial class MainWindow
{
    private StackPanel? _depsList;

    private void BuildDependenciesPanel()
    {
        _depsList = new StackPanel { Spacing = 6, Margin = new Thickness(10, 0, 10, 10) };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Height = 36, Margin = new Thickness(12, 0, 6, 0) };
        header.Children.Add(new TextBlock { Text = "Dependencies", Classes = { "header" }, VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            [Grid.ColumnProperty] = 1,
            Children = { ToolButton("", "Reload", () => _ = RefreshDependenciesAsync()), ToolButton("", "Hide", () => ToggleRight("deps")) },
        });
        var dock = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);
        dock.Children.Add(new ScrollViewer { Content = _depsList });
        RightContent.Content = dock;
    }

    private async Task RefreshDependenciesAsync()
    {
        if (_depsList is null || _root is null) return;
        _ = RefreshExternalLibrariesAsync();
        var root = _root;
        var managers = Plugins.DependencyManagers().ToList();
        var found = await Task.Run(() => managers.SelectMany(m =>
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, m.Manager.Manifest, SearchOption.AllDirectories).Where(f => !IsExcluded(f)).Take(20).ToList(); }
            catch { files = []; }
            return files.Select(f => (m.Manager, File: f, Deps: DependencyParsers.Parse(m.Manager.Format, f)));
        }).ToList());

        _depsList.Children.Clear();
        if (found.Count == 0)
        {
            _depsList.Children.Add(new TextBlock { Text = "No dependency manifests found (Cargo.toml, *.csproj, vcpkg.json, CMakeLists.txt).", TextWrapping = TextWrapping.Wrap, Classes = { "muted" } });
            return;
        }
        foreach (var (manager, file, deps) in found)
            _depsList.Children.Add(ManifestCard(manager, file, deps));
    }

    private bool IsExcluded(string path)
    {
        var excluded = Settings.ExcludedFolderSet();
        return Path.GetRelativePath(_root!, path).Split(Path.DirectorySeparatorChar).Any(excluded.Contains);
    }

    private Control ManifestCard(DependencyManagerContribution m, string manifest, List<Dependency> deps)
    {
        var dir = Path.GetDirectoryName(manifest)!;
        string Expand(string template, string name = "", string version = "") => template
            .Replace("${name}", name).Replace("${version}", version).Replace("${query}", name)
            .Replace("${manifest}", manifest).Replace("${manifestDir}", dir);

        void RunCommand(string title, string command)
        {
            ShowBottom("run");
            RunTab($"{m.Name}: {title}", false).Run(command, dir, exited: code =>
            {
                if (code == 0) _ = RefreshDependenciesAsync();
            });
        }

        var card = new StackPanel { Spacing = 4 };
        var title = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        title.Children.Add(new StackPanel
        {
            Children =
            {
                new TextBlock { Text = $"{m.Name} · {deps.Count}", FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = Rel(manifest), Classes = { "muted" }, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis },
            },
        });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, [Grid.ColumnProperty] = 1 };
        if (m.Add is { } add)
            actions.Children.Add(ToolButton("", "Add dependency…", async () =>
            {
                var input = await PromptAsync($"Add {m.Name} dependency (name or name@version)", "");
                if (string.IsNullOrWhiteSpace(input)) return;
                var parts = input.Trim().Split('@', 2);
                var version = parts.Length > 1 ? (m.Format == "cargo" ? "@" + parts[1] : " --version " + parts[1]) : "";
                RunCommand($"add {parts[0]}", Expand(add, parts[0], version));
            }));
        if (m.Search is { } search) actions.Children.Add(ToolButton("", "Search packages online", async () =>
        {
            var q = await PromptAsync($"Search {m.Name}", "");
            if (!string.IsNullOrWhiteSpace(q)) OpenUrl(Expand(search, Uri.EscapeDataString(q.Trim())));
        }));
        if (m.Restore is { } restore) actions.Children.Add(ToolButton("", "Sync / restore", () => RunCommand("sync", Expand(restore))));
        actions.Children.Add(ToolButton("", "Open manifest", () => _ = OpenFileAsync(manifest)));
        title.Children.Add(actions);
        card.Children.Add(title);

        foreach (var d in deps)
        {
            var dep = d;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(0, 1) };
            row.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = dep.Name, FontFamily = (FontFamily)Application.Current!.FindResource("MonoFont")!, FontSize = 12.5 },
                    new TextBlock { Text = dep.Version, Classes = { "muted" }, FontSize = 12 },
                },
            });
            row.Children.Add(new Border
            {
                [Grid.ColumnProperty] = 1,
                Padding = new Thickness(5, 0),
                Margin = new Thickness(6, 0),
                CornerRadius = new CornerRadius(4),
                Background = Res("Hover"),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = dep.Kind, FontSize = 10 },
            });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, [Grid.ColumnProperty] = 2 };
            if (dep.Line > 0) buttons.Children.Add(ToolButton("", "Go to declaration in manifest", () => _ = OpenFileAsync(manifest, dep.Line)));
            if (m.Docs is { } docs) buttons.Children.Add(ToolButton("", "Documentation", () => OpenUrl(Expand(docs, dep.Name))));
            if (m.Update is { } update) buttons.Children.Add(ToolButton("", "Update", () => RunCommand($"update {dep.Name}", Expand(update, dep.Name))));
            if (m.Remove is { } remove) buttons.Children.Add(ToolButton("", "Remove", async () =>
            {
                if (await ConfirmAsync("Remove dependency", $"Remove {dep.Name} from {Path.GetFileName(manifest)}?", "Remove"))
                    RunCommand($"remove {dep.Name}", Expand(remove, dep.Name));
            }));
            row.Children.Add(buttons);
            card.Children.Add(row);
        }
        return new Border { Background = Res("EditorBg"), CornerRadius = new CornerRadius(8), Padding = new Thickness(10), Child = card };
    }
}
