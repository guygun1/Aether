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

    public static RunConfigStore Load(string root)
    {
        try
        {
            var file = PathFor(root);
            if (File.Exists(file))
                return JsonSerializer.Deserialize<RunConfigStore>(File.ReadAllText(file), Settings.Json) ?? new();
        }
        catch
        {
            // A broken run.json falls back to detected configurations only.
        }
        return new();
    }

    public void Save(string root)
    {
        var file = PathFor(root);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(this, Settings.Json));
    }
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
