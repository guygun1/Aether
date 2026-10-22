using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MultiIde.Core;

/// <summary>A run configuration, like IntelliJ's "Run/Debug Configurations".</summary>
public sealed class RunConfig
{
    public string Name { get; set; } = "Unnamed";
    /// <summary><c>shell</c> runs <see cref="Command"/> through the shell; <c>program</c> runs <see cref="Program"/> with <see cref="Arguments"/>.</summary>
    public string Type { get; set; } = "shell";
    public string Command { get; set; } = "";
    public string Program { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string WorkingDirectory { get; set; } = "${workspaceFolder}";
    public Dictionary<string, string> Env { get; set; } = new();
    /// <summary>Names of configurations to run (and succeed) first, e.g. a build step.</summary>
    public List<string> BeforeLaunch { get; set; } = new();
    public bool SaveBefore { get; set; } = true;
    public bool ActivateToolWindow { get; set; } = true;
    public bool AllowParallel { get; set; }
    /// <summary>run, build or test (the toolbar Build button uses the first "build").</summary>
    public string Kind { get; set; } = "run";

    /// <summary>True for configurations detected from plugins (not stored in run.json).</summary>
    [JsonIgnore] public bool Detected { get; set; }
    [JsonIgnore] public string? Source { get; set; }
    /// <summary>Set when this configuration lives in its own file in .aether/runConfigurations/.</summary>
    [JsonIgnore] public string? FilePath { get; set; }
    /// <summary>Set for a configuration inherited from an enclosing folder's .aether; ${workspaceFolder} means that folder.</summary>
    [JsonIgnore] public string? BaseFolder { get; set; }

    public string CommandLine => Type == "program"
        ? $"{(Program.Contains(' ') && !Program.StartsWith('"') ? $"\"{Program}\"" : Program)} {Arguments}".Trim()
        : Command;

    public RunConfig Clone(string? name = null)
    {
        var copy = (RunConfig)MemberwiseClone();
        copy.Env = new(Env);
        copy.BeforeLaunch = new(BeforeLaunch);
        copy.Detected = false;
        copy.Source = null;
        copy.FilePath = null;
        if (name is not null) copy.Name = name;
        return copy;
    }

    public static RunConfig FromPlugin(RunConfiguration rc, Plugin plugin) => new()
    {
        Name = rc.Name,
        Command = rc.Command,
        Kind = rc.Kind,
        Detected = true,
        Source = plugin.Manifest.Name,
    };
}

/// <summary>Per-project run configurations in <c>.aether/run.json</c> (meant to be committed).</summary>
public sealed class RunConfigStore
{
    public List<RunConfig> Configurations { get; set; } = new();
    public string? Selected { get; set; }

    public static string PathFor(string root) => System.IO.Path.Combine(IdePaths.ProjectDir(root), "run.json");

    /// <summary>Folder of one-configuration-per-file run configurations (easy to share and review).</summary>
    public static string FolderFor(string root) => System.IO.Path.Combine(IdePaths.ProjectDir(root), "runConfigurations");

    public static RunConfigStore Load(string root)
    {
        var store = new RunConfigStore();
        try
        {
            var file = PathFor(root);
            if (File.Exists(file))
                store = JsonSerializer.Deserialize<RunConfigStore>(File.ReadAllText(file), Settings.Json) ?? new();
        }
        catch
        {
            // A broken run.json falls back to the other sources.
        }
        // The project's own configurations first, then those of enclosing folders up to the repository
        // root, so opening a subfolder (e.g. src/App) still offers the repository's configurations.
        foreach (var dir in WithAncestors(root))
        {
            var folder = FolderFor(dir);
            if (!Directory.Exists(folder)) continue;
            foreach (var f in Directory.GetFiles(folder, "*.json").OrderBy(f => f))
            {
                try
                {
                    if (JsonSerializer.Deserialize<RunConfig>(File.ReadAllText(f), Settings.Json) is { } rc && store.Configurations.All(c => c.Name != rc.Name))
                    {
                        rc.FilePath = f;
                        if (dir != root) rc.BaseFolder = dir;
                        store.Configurations.Add(rc);
                    }
                }
                catch
                {
                    // Skip a malformed file.
                }
            }
        }
        return store;
    }

    /// <summary>The folder itself, then its parents up to and including the enclosing git repository root.</summary>
    private static IEnumerable<string> WithAncestors(string root)
    {
        static bool IsRepo(string d) => Directory.Exists(System.IO.Path.Combine(d, ".git")) || File.Exists(System.IO.Path.Combine(d, ".git"));
        var chain = new List<string> { root };
        if (IsRepo(root)) return chain;
        for (var dir = Directory.GetParent(root); dir is not null; dir = dir.Parent)
        {
            chain.Add(dir.FullName);
            if (IsRepo(dir.FullName)) return chain;
        }
        return [root]; // not inside a repository: only the folder itself
    }

    public void Save(string root)
    {
        var folder = FolderFor(root);
        // File-backed configurations go to their own files; others to run.json.
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rc in Configurations.Where(c => c.FilePath is not null))
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(rc.FilePath!, JsonSerializer.Serialize(rc, Settings.Json));
            known.Add(rc.FilePath!);
        }
        if (Directory.Exists(folder))
            foreach (var f in Directory.GetFiles(folder, "*.json").Where(f => !known.Contains(f)))
                File.Delete(f); // removed in the dialog
        var shared = new RunConfigStore { Configurations = Configurations.Where(c => c.FilePath is null).ToList(), Selected = Selected };
        var file = PathFor(root);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(shared, Settings.Json));
    }

    public static string FileNameFor(string root, string name) =>
        System.IO.Path.Combine(FolderFor(root), string.Concat(name.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + ".json");
}

/// <summary>Expands IDE macros in commands, arguments and paths.</summary>
public static class Macros
{
    private static readonly Regex Pattern = new(@"\$\{(?<name>[A-Za-z]+)(?::(?<arg>[^}]+))?\}", RegexOptions.Compiled);

    public sealed record Context(string? Workspace, string? File = null, int? Line = null, string? SelectedText = null, string? PluginDir = null, Func<string, string?>? Config = null);

    public static readonly (string Name, string Description)[] Documented =
    {
        ("${workspaceFolder}", "Project root"),
        ("${workspaceFolderName}", "Project folder name"),
        ("${file}", "Current file"),
        ("${fileDir}", "Folder of the current file"),
        ("${fileName}", "Current file name"),
        ("${fileBasenameNoExtension}", "Current file name without extension"),
        ("${relativeFile}", "Current file relative to the project"),
        ("${lineNumber}", "Caret line"),
        ("${selectedText}", "Selected text"),
        ("${env:NAME}", "Environment variable"),
        ("${config:plugin.key}", "Plugin setting value"),
        ("${home}", "User home folder"),
    };

    public static string Expand(string value, Context ctx) => Pattern.Replace(value, m =>
    {
        var file = ctx.File ?? "";
        return m.Groups["name"].Value switch
        {
            "workspaceFolder" => ctx.Workspace ?? "",
            "workspaceFolderName" => ctx.Workspace is null ? "" : System.IO.Path.GetFileName(ctx.Workspace),
            "file" => file,
            "fileDir" => file.Length > 0 ? System.IO.Path.GetDirectoryName(file) ?? "" : "",
            "fileName" => System.IO.Path.GetFileName(file),
            "fileBasenameNoExtension" => System.IO.Path.GetFileNameWithoutExtension(file),
            "fileExtname" => System.IO.Path.GetExtension(file),
            "relativeFile" => ctx.Workspace is not null && file.Length > 0 ? System.IO.Path.GetRelativePath(ctx.Workspace, file) : file,
            "lineNumber" => ctx.Line?.ToString() ?? "",
            "selectedText" => ctx.SelectedText ?? "",
            "pluginDir" => ctx.PluginDir ?? "",
            "home" => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "env" => Environment.GetEnvironmentVariable(m.Groups["arg"].Value) ?? "",
            "config" => ctx.Config?.Invoke(m.Groups["arg"].Value) ?? "",
            "name" => m.Value, // project templates handle ${name} themselves
            _ => m.Value,
        };
    });
}
