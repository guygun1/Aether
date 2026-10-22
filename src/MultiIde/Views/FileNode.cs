using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Avalonia.Threading;
using MultiIde.Core;

namespace MultiIde.Views;

/// <summary>A lazily loaded node in the project tree.</summary>
public sealed class FileNode : INotifyPropertyChanged
{
    private static IBrush FolderBrush => ThemeManager.Brush("FgMuted");
    private static IBrush ExcludedBrush => ThemeManager.Brush("Warning");
    private static IBrush FileBrush => ThemeManager.Brush("FgMuted");
    private static IBrush NormalName => ThemeManager.Brush("Fg");
    private static IBrush ExcludedName => ThemeManager.Brush("Warning");
    private static readonly HashSet<string> ExcludedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "target", "bin", "obj", "build", "out", "node_modules", "cmake-build-debug", "cmake-build-release", ".git", ".vs", ".idea",
    };

    private static readonly FileNode Placeholder = new("…", "", false, null);

    private readonly PluginManager? _plugins;
    private bool _isExpanded;
    private bool _loaded;

    public FileNode(string name, string path, bool isDirectory, PluginManager? plugins)
    {
        Name = name;
        FullPath = path;
        IsDirectory = isDirectory;
        _plugins = plugins;
        if (isDirectory) Children.Add(Placeholder);

        if (isDirectory)
        {
            Glyph = "";
            var excluded = IdeServices.Settings.ExcludedFolderSet().Contains(name);
            IconBrush = excluded ? ExcludedBrush : FolderBrush;
            NameBrush = excluded ? ExcludedName : NormalName;
        }
        else
        {
            NameBrush = NormalName;
            var lang = plugins?.LanguageForFile(path)?.Lang;
            if (lang?.Icon is { Length: > 0 } badge)
            {
                Badge = badge;
                HasBadge = true;
                IconBrush = new SolidColorBrush(Color.Parse(lang.IconColor ?? "#6F737A"));
            }
            else
            {
                Glyph = "";
                IconBrush = FileBrush;
            }
        }
    }

    public string Name { get; private set; }
    public string FullPath { get; private set; }
    public bool IsDirectory { get; }
    public string Glyph { get; init; } = "";
    public string Badge { get; } = "";
    public bool HasBadge { get; }
    public IBrush IconBrush { get; init; }
    public IBrush NameBrush { get; }
    public ObservableCollection<FileNode> Children { get; } = new();

    /// <summary>Virtual group (e.g. "External Libraries"): shows these nodes instead of a folder's contents.</summary>
    public IReadOnlyList<FileNode>? FixedChildren { get; init; }
    /// <summary>Runs before the first load and returns the folder to show (e.g. decompile a DLL); null shows nothing.</summary>
    public Func<Task<string?>>? BeforeLoad { get; init; }
    /// <summary>Set by the project window: turns a library DLL into an expandable, decompiled node.</summary>
    public static Func<string, Task<string?>>? DllExpander { get; set; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            OnChanged();
            if (value && !_loaded) _ = LoadAsync();
        }
    }

    public async Task LoadAsync()
    {
        if (!IsDirectory) return;
        _loaded = true;
        if (FixedChildren is not null)
        {
            Children.Clear();
            foreach (var c in FixedChildren) Children.Add(c);
            return;
        }
        if (BeforeLoad is not null)
        {
            var prepared = await BeforeLoad();
            if (prepared is null) { Children.Clear(); _loaded = false; return; }
            FullPath = prepared;
        }
        var path = FullPath;
        var entries = await Task.Run(() => ReadEntries(path));

        Dispatcher.UIThread.VerifyAccess();
        // Keep expanded sub-folders when refreshing.
        var expanded = Children.Where(c => c.IsDirectory && c.IsExpanded).ToDictionary(c => c.FullPath, c => c, StringComparer.OrdinalIgnoreCase);
        Children.Clear();
        foreach (var e in entries)
        {
            if (e.Dir && expanded.TryGetValue(e.Path, out var existing))
            {
                Children.Add(existing);
                _ = existing.LoadAsync();
                continue;
            }
            FileNode node;
            if (!e.Dir && DllExpander is { } expand && e.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && IdePaths.IsLibraryPath(e.Path))
                node = new FileNode(e.Name, e.Path, true, _plugins) { Glyph = "", IconBrush = ThemeManager.Brush("Link"), BeforeLoad = () => expand(e.Path) };
            else
                node = new FileNode(e.Name, e.Path, e.Dir, _plugins);
            foreach (var nested in e.Nested) node.Children.Add(new FileNode(Path.GetFileName(nested), nested, false, _plugins));
            Children.Add(node);
        }
    }

    private sealed record Entry(string Path, string Name, bool Dir, List<string> Nested);

    /// <summary>Files shown under another file (IntelliJ "File Nesting"): parent suffix → child suffixes.</summary>
    private static readonly (string Parent, string[] Children)[] NestingRules =
    {
        (".axaml", [".axaml.cs"]), (".xaml", [".xaml.cs"]), (".razor", [".razor.cs", ".razor.css"]), (".cshtml", [".cshtml.cs"]),
        (".ts", [".js", ".js.map", ".d.ts"]), (".js", [".js.map", ".min.js"]), (".scss", [".css", ".css.map"]),
    };

    private static readonly (string Parent, string Child)[] NestedNames =
    {
        ("Cargo.toml", "Cargo.lock"), ("package.json", "package-lock.json"), ("package.json", "yarn.lock"), ("pyproject.toml", "poetry.lock"),
    };

    /// <summary>Lists a folder applying the Project view options (runs off the UI thread).</summary>
    private static List<Entry> ReadEntries(string path)
    {
        try
        {
            var settings = IdeServices.Settings;
            bool Visible(string p) => settings.ShowHiddenFiles || !Path.GetFileName(p).StartsWith('.');
            var excluded = settings.ShowExcludedFolders ? new HashSet<string>() : settings.ExcludedFolderSet();

            var dirs = Directory.GetDirectories(path).Where(Visible).Where(d => !excluded.Contains(Path.GetFileName(d)))
                .Select(d => Compact(d, settings.CompactDirectories)).ToList();
            var files = Directory.GetFiles(path).Where(Visible).Select(f => new Entry(f, Path.GetFileName(f), false, new())).ToList();

            if (settings.FileNesting)
            {
                var byName = files.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
                var nestedAway = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in files)
                {
                    foreach (var (parentSuffix, childSuffixes) in NestingRules)
                    {
                        if (!f.Name.EndsWith(parentSuffix, StringComparison.OrdinalIgnoreCase)) continue;
                        var stem = f.Name[..^parentSuffix.Length];
                        foreach (var cs in childSuffixes)
                            if (byName.TryGetValue(stem + cs, out var child) && child != f && !nestedAway.Contains(f.Name) && nestedAway.Add(child.Name))
                                f.Nested.Add(child.Path);
                    }
                    foreach (var (parent, childName) in NestedNames)
                        if (f.Name.Equals(parent, StringComparison.OrdinalIgnoreCase) && byName.TryGetValue(childName, out var child) && nestedAway.Add(child.Name))
                            f.Nested.Add(child.Path);
                }
                files.RemoveAll(f => nestedAway.Contains(f.Name));
            }

            IEnumerable<Entry> all = dirs.Concat(files);
            var ordered = settings.FoldersFirst ? all.OrderBy(e => e.Dir ? 0 : 1) : all.OrderBy(_ => 0);
            if (settings.SortByType) ordered = ordered.ThenBy(e => e.Dir ? "" : Path.GetExtension(e.Name), StringComparer.OrdinalIgnoreCase);
            return ordered.ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch
        {
            return new();
        }
    }

    /// <summary>IntelliJ "Compact Middle Packages": a chain of single-child folders shows as "a/b/c".</summary>
    private static Entry Compact(string dir, bool enabled)
    {
        var name = Path.GetFileName(dir);
        if (!enabled) return new Entry(dir, name, true, new());
        for (var depth = 0; depth < 8; depth++)
        {
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(dir); }
            catch { break; }
            if (entries.Length != 1 || !Directory.Exists(entries[0])) break;
            dir = entries[0];
            name += "/" + Path.GetFileName(dir);
        }
        return new Entry(dir, name, true, new());
    }

    /// <summary>Reloads this folder if it has been loaded before.</summary>
    public Task RefreshAsync() => _loaded ? LoadAsync() : Task.CompletedTask;

    public FileNode? Find(string path)
    {
        if (string.Equals(FullPath, path, StringComparison.OrdinalIgnoreCase)) return this;
        foreach (var c in Children)
        {
            if (c == Placeholder) continue;
            if (path.StartsWith(c.FullPath, StringComparison.OrdinalIgnoreCase) && c.Find(path) is { } hit) return hit;
        }
        return null;
    }

    public IEnumerable<FileNode> LoadedDirectories()
    {
        if (!IsDirectory || !_loaded) yield break;
        yield return this;
        foreach (var c in Children)
        foreach (var d in c.LoadedDirectories())
            yield return d;
    }

    public void CollapseAll()
    {
        foreach (var c in Children) c.CollapseAll();
        IsExpanded = false;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
