using System.Text.Json;
using System.Text.Json.Nodes;

namespace MultiIde.Core;

// ---- plugin.json model ---------------------------------------------------

public sealed class PluginManifest
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "0.0.0";
    public string Description { get; set; } = "";
    public string Author { get; set; } = "";
    public string? Homepage { get; set; }
    /// <summary>Ids of plugins that must be installed and enabled for this one to load.</summary>
    public List<string> DependsOn { get; set; } = new();
    public PluginContributions Contributes { get; set; } = new();
}

public sealed class PluginContributions
{
    public List<LanguageContribution> Languages { get; set; } = new();
    public List<LspServerConfig> LspServers { get; set; } = new();
    public List<SnippetContribution> Snippets { get; set; } = new();
    public List<RunConfiguration> RunConfigurations { get; set; } = new();
    public List<ProjectTemplate> ProjectTemplates { get; set; } = new();
    public List<ThemeContribution> Themes { get; set; } = new();
    public List<CommandContribution> Commands { get; set; } = new();
    public List<FormatterContribution> Formatters { get; set; } = new();
    public List<FileTemplateContribution> FileTemplates { get; set; } = new();
    public List<SettingContribution> Settings { get; set; } = new();
    public List<KeybindingContribution> Keybindings { get; set; } = new();
    public List<DependencyManagerContribution> DependencyManagers { get; set; } = new();
    /// <summary>Optional extras for another plugin's project template (a checkbox in New Project).</summary>
    public List<ProjectTemplateOption> ProjectTemplateOptions { get; set; } = new();
    /// <summary>File icons by exact file name ("Cargo.toml") or extension (".lock").</summary>
    public Dictionary<string, FileIconContribution> FileIcons { get; set; } = new();
}

/// <summary>A color theme: UI colors plus TextMate token colors (VS Code theme files work too).</summary>
public sealed class ThemeContribution
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Theme JSON, relative to the plugin folder.</summary>
    public string Path { get; set; } = "";
}

/// <summary>An external tool: a shell command exposed as an IDE action (Find Action, menus, keymap).</summary>
public sealed class CommandContribution
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>Shell command line; macros such as ${file} and ${selectedText} are expanded.</summary>
    public string Command { get; set; } = "";
    public string? WorkingDirectory { get; set; }
    /// <summary>Default shortcut, e.g. "Ctrl+Alt+R".</summary>
    public string? Keys { get; set; }
    /// <summary>Only offered for these language ids (empty = always).</summary>
    public List<string> Languages { get; set; } = new();
    /// <summary>run (Run tool window), notify (balloon), replaceSelection, insert, replaceDocument, silent.</summary>
    public string Output { get; set; } = "run";
    /// <summary>Send the selection (or the whole document) to the command's stdin.</summary>
    public string? Input { get; set; }
    /// <summary>Where it shows up besides Find Action: tools, editor (context menu), project (tree context menu).</summary>
    public List<string> Menus { get; set; } = new() { "tools" };
    public bool SaveBefore { get; set; } = true;
}

/// <summary>Formats a document by piping it through a command (stdin → stdout).</summary>
public sealed class FormatterContribution
{
    public string Name { get; set; } = "";
    public List<string> Languages { get; set; } = new();
    public string Command { get; set; } = "";
    public List<string> Args { get; set; } = new();
    /// <summary>Use this formatter even when a language server can format.</summary>
    public bool Preferred { get; set; }
}

/// <summary>Shown under New in the project tree, like IntelliJ file templates.</summary>
public sealed class FileTemplateContribution
{
    public string Name { get; set; } = "";
    /// <summary>Extension added to the entered name, e.g. ".rs".</summary>
    public string Extension { get; set; } = "";
    public JsonNode? Body { get; set; }
    /// <summary>Optional extra files, e.g. a header next to a .cpp ("${name}.h" → body).</summary>
    public Dictionary<string, JsonNode?> Companions { get; set; } = new();

    public static string Text(JsonNode? node) => node switch
    {
        JsonArray lines => string.Join("\n", lines.Select(l => l?.GetValue<string>() ?? "")),
        JsonValue v => v.GetValue<string>(),
        _ => "",
    };
}

/// <summary>A user-editable plugin setting, read in commands/LSP args as ${config:pluginId.key}.</summary>
public sealed class SettingContribution
{
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>boolean, string, number or enum.</summary>
    public string Type { get; set; } = "string";
    public JsonNode? Default { get; set; }
    public List<string> Options { get; set; } = new();
}

public sealed class KeybindingContribution
{
    /// <summary>An IDE action id (see Keymap) or a plugin command id.</summary>
    public string Action { get; set; } = "";
    public string Keys { get; set; } = "";
}

public sealed class FileIconContribution
{
    public string Icon { get; set; } = "";
    public string Color { get; set; } = "#6F737A";
}

public sealed class LanguageContribution
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Extensions { get; set; } = new();
    public List<string> Filenames { get; set; } = new();
    /// <summary>A TextMate scope bundled with the IDE, e.g. <c>source.rust</c>.</summary>
    public string? ScopeName { get; set; }
    /// <summary>Path (relative to the plugin) of a .tmLanguage.json grammar.</summary>
    public string? Grammar { get; set; }
    public string? LineComment { get; set; }
    public List<string>? BlockComment { get; set; }
    /// <summary>Badge shown in the project tree, e.g. "RS".</summary>
    public string? Icon { get; set; }
    public string? IconColor { get; set; }
    /// <summary>Per-language indentation, e.g. Makefiles and Go use tabs.</summary>
    public int? IndentSize { get; set; }
    public bool? UseTabs { get; set; }
    /// <summary>Folding strategy: braces (default), indent or none.</summary>
    public string Folding { get; set; } = "braces";
}

public sealed class LspServerConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public List<string> Args { get; set; } = new();
    public List<string> Languages { get; set; } = new();
    public JsonNode? InitializationOptions { get; set; }
    public JsonNode? Settings { get; set; }
    /// <summary>Shell command that installs the server, offered when it is missing.</summary>
    public string? Install { get; set; }
    public string? InstallUrl { get; set; }
    public Dictionary<string, string> Env { get; set; } = new();
    /// <summary>Where to download the server when the install command's tool is missing (per OS).</summary>
    public DownloadSpec? Download { get; set; }
    /// <summary>A file that must exist for the server to count as installed (e.g. a script run by "python").</summary>
    public string? Requires { get; set; }
    /// <summary>A tool the server needs at run time (e.g. cargo for rust-analyzer), offered for install.</summary>
    public Prerequisite? Prerequisite { get; set; }
}

public sealed class Prerequisite
{
    public string Command { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Install { get; set; }
    public string? Url { get; set; }
}

public sealed class SnippetContribution
{
    public string Language { get; set; } = "";
    public string Prefix { get; set; } = "";
    public JsonNode? Body { get; set; }
    public string Description { get; set; } = "";

    public string BodyText => Body switch
    {
        JsonArray lines => string.Join("\n", lines.Select(l => l?.GetValue<string>() ?? "")),
        JsonValue v => v.GetValue<string>(),
        _ => "",
    };
}

public sealed class RunConfiguration
{
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    /// <summary>File name or glob at the project root that enables this configuration.</summary>
    public string? When { get; set; }
    public string Kind { get; set; } = "run";
}

public sealed class ProjectTemplate
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>Short badge text (e.g. "RS") and color shown in the New Project list.</summary>
    public string? Icon { get; set; }
    public string? IconColor { get; set; }
    /// <summary>Group heading in the New Project list (e.g. "Rust", "C++", "Graphics").</summary>
    public string? Category { get; set; }
    /// <summary>Command run in the parent folder; <c>${name}</c> is the project name.</summary>
    public string? Command { get; set; }
    /// <summary>Files written into the new project folder (path → contents).</summary>
    public Dictionary<string, JsonNode?> Files { get; set; } = new();
}

/// <summary>An opt-in addition to a project template, e.g. "Graphics: GLFW" for the C++ template.</summary>
public sealed class ProjectTemplateOption
{
    /// <summary>Id of the project template this option extends.</summary>
    public string Template { get; set; } = "";
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>Files written (or replaced) in the new project after the template ran.</summary>
    public Dictionary<string, JsonNode?> Files { get; set; } = new();
    /// <summary>Commands run in the new project folder, in order (e.g. "cargo add winit").</summary>
    public List<string> Commands { get; set; } = new();
}

public sealed class Plugin
{
    public required PluginManifest Manifest { get; init; }
    public required string Directory { get; init; }
    public required bool Bundled { get; init; }
    public bool Enabled { get; set; } = true;
    public string Id => Manifest.Id;
}

// ---- Discovery and lookups -----------------------------------------------

public sealed class PluginManager
{
    private readonly Settings _settings;
    private List<Plugin> _plugins = new();
    private Dictionary<string, (LanguageContribution Lang, Plugin Plugin)> _byExtension = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, (LanguageContribution Lang, Plugin Plugin)> _byFilename = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, (LanguageContribution Lang, Plugin Plugin)> _byLanguageId = new();

    public PluginManager(Settings settings) => _settings = settings;

    public IReadOnlyList<Plugin> All => _plugins;
    /// <summary>Enabled plugins whose dependencies are also present and enabled.</summary>
    public IEnumerable<Plugin> Enabled => _plugins.Where(p => p.Enabled && MissingDependencies(p).Count == 0);

    public List<string> MissingDependencies(Plugin p) =>
        p.Manifest.DependsOn.Where(id => !_plugins.Any(o => o.Id == id && o.Enabled)).ToList();
    public List<string> LoadErrors { get; } = new();

    public event Action? Changed;

    public void Load()
    {
        LoadErrors.Clear();
        var seen = new HashSet<string>();
        var list = new List<Plugin>();
        // User plugins first so they can override bundled ones with the same id.
        Scan(IdePaths.UserPlugins, bundled: false);
        Scan(IdePaths.BundledPlugins, bundled: true);
        _plugins = list;
        Reindex();

        void Scan(string root, bool bundled)
        {
            if (!System.IO.Directory.Exists(root)) return;
            foreach (var dir in System.IO.Directory.EnumerateDirectories(root))
            {
                var file = Path.Combine(dir, "plugin.json");
                if (!File.Exists(file)) continue;
                try
                {
                    var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(file), Settings.Json);
                    if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id) || !seen.Add(manifest.Id)) continue;
                    list.Add(new Plugin
                    {
                        Manifest = manifest,
                        Directory = dir,
                        Bundled = bundled,
                        Enabled = !_settings.DisabledPlugins.Contains(manifest.Id),
                    });
                }
                catch (Exception e)
                {
                    LoadErrors.Add($"{file}: {e.Message}");
                }
            }
        }
    }

    public void SetEnabled(Plugin plugin, bool enabled)
    {
        plugin.Enabled = enabled;
        _settings.DisabledPlugins.Remove(plugin.Id);
        if (!enabled) _settings.DisabledPlugins.Add(plugin.Id);
        _settings.Save();
        Reindex();
        Changed?.Invoke();
    }

    public void Reload()
    {
        Load();
        Changed?.Invoke();
    }

    private void Reindex()
    {
        _byExtension = new(StringComparer.OrdinalIgnoreCase);
        _byFilename = new(StringComparer.OrdinalIgnoreCase);
        _byLanguageId = new();
        foreach (var p in Enabled)
        foreach (var lang in p.Manifest.Contributes.Languages)
        {
            _byLanguageId.TryAdd(lang.Id, (lang, p));
            foreach (var ext in lang.Extensions) _byExtension.TryAdd(ext.StartsWith('.') ? ext : "." + ext, (lang, p));
            foreach (var name in lang.Filenames) _byFilename.TryAdd(name, (lang, p));
        }
    }

    public (LanguageContribution Lang, Plugin Plugin)? LanguageForFile(string path)
    {
        if (_byFilename.TryGetValue(Path.GetFileName(path), out var byName)) return byName;
        // Longest compound extension first, e.g. ".cmake.in" before ".in".
        var name = Path.GetFileName(path);
        for (var i = name.IndexOf('.'); i >= 0; i = name.IndexOf('.', i + 1))
            if (_byExtension.TryGetValue(name[i..], out var byExt)) return byExt;
        return null;
    }

    public (LanguageContribution Lang, Plugin Plugin)? Language(string id) =>
        _byLanguageId.TryGetValue(id, out var v) ? v : null;

    public (LspServerConfig Config, Plugin Plugin)? LspFor(string languageId)
    {
        foreach (var p in Enabled)
        foreach (var s in p.Manifest.Contributes.LspServers)
            if (s.Languages.Contains(languageId))
                return (s, p);
        return null;
    }

    public IEnumerable<(LspServerConfig Config, Plugin Plugin)> AllLspServers() =>
        Enabled.SelectMany(p => p.Manifest.Contributes.LspServers.Select(s => (s, p)));

    public IEnumerable<SnippetContribution> SnippetsFor(string languageId) =>
        Enabled.SelectMany(p => p.Manifest.Contributes.Snippets).Where(s => s.Language == languageId || s.Language == "*");

    public IEnumerable<ProjectTemplateOption> TemplateOptions(string templateId) =>
        Enabled.SelectMany(p => p.Manifest.Contributes.ProjectTemplateOptions).Where(o => o.Template == templateId);

    public IEnumerable<ProjectTemplate> ProjectTemplates() =>
        Enabled.SelectMany(p => p.Manifest.Contributes.ProjectTemplates);

    public IEnumerable<(RunConfiguration Config, Plugin Plugin)> RunConfigurationsFor(string root)
    {
        foreach (var p in Enabled)
        foreach (var rc in p.Manifest.Contributes.RunConfigurations)
        {
            if (string.IsNullOrEmpty(rc.When)) { yield return (rc, p); continue; }
            bool match;
            try
            {
                match = rc.When.Contains('*')
                    ? System.IO.Directory.EnumerateFiles(root, rc.When, SearchOption.TopDirectoryOnly).Any()
                    : File.Exists(Path.Combine(root, rc.When));
            }
            catch { match = false; }
            if (match) yield return (rc, p);
        }
    }

    // ---- Extension points ---------------------------------------------------

    /// <summary>Value of a plugin setting, "pluginId.key" — the user's value or the plugin's default.</summary>
    public string? SettingValue(string qualified)
    {
        var dot = qualified.IndexOf('.');
        if (dot <= 0) return null;
        var (pluginId, key) = (qualified[..dot], qualified[(dot + 1)..]);
        if (_settings.PluginSettings.TryGetValue(pluginId, out var values) && values.TryGetValue(key, out var v) && v is not null)
            return v.ToString();
        var plugin = _plugins.FirstOrDefault(p => p.Id == pluginId);
        return plugin?.Manifest.Contributes.Settings.FirstOrDefault(x => x.Key == key)?.Default?.ToString();
    }

    public void SetSettingValue(string pluginId, string key, JsonNode? value)
    {
        if (!_settings.PluginSettings.TryGetValue(pluginId, out var values))
            _settings.PluginSettings[pluginId] = values = new();
        values[key] = value;
        _settings.Save();
    }

    public IEnumerable<(ThemeContribution Theme, Plugin Plugin)> Themes() =>
        Enabled.SelectMany(p => p.Manifest.Contributes.Themes.Select(t => (t, p)));

    public IEnumerable<(CommandContribution Command, Plugin Plugin)> Commands() =>
        Enabled.SelectMany(p => p.Manifest.Contributes.Commands.Select(c => (c, p)));

    public IEnumerable<(FileTemplateContribution Template, Plugin Plugin)> FileTemplates() =>
        Enabled.SelectMany(p => p.Manifest.Contributes.FileTemplates.Select(t => (t, p)));

    public IEnumerable<(DependencyManagerContribution Manager, Plugin Plugin)> DependencyManagers() =>
        Enabled.SelectMany(p => p.Manifest.Contributes.DependencyManagers.Select(m => (m, p)));

    public IEnumerable<KeybindingContribution> Keybindings() =>
        Enabled.SelectMany(p => p.Manifest.Contributes.Keybindings);

    public (FormatterContribution Formatter, Plugin Plugin)? FormatterFor(string languageId)
    {
        foreach (var p in Enabled)
        foreach (var f in p.Manifest.Contributes.Formatters)
            if (f.Languages.Contains(languageId))
                return (f, p);
        return null;
    }

    public FileIconContribution? FileIconFor(string path)
    {
        var name = Path.GetFileName(path);
        foreach (var p in Enabled)
        {
            var icons = p.Manifest.Contributes.FileIcons;
            if (icons.TryGetValue(name, out var exact)) return exact;
            var ext = Path.GetExtension(name);
            if (ext.Length > 0 && icons.TryGetValue(ext, out var byExt)) return byExt;
        }
        return null;
    }

    /// <summary>Expands <c>${workspaceFolder}</c>, <c>${pluginDir}</c>, <c>${home}</c> and <c>${name}</c>.</summary>
    public static string Expand(string value, string? workspace, string? pluginDir = null, string? name = null) =>
        value.Replace("${workspaceFolder}", workspace ?? "")
             .Replace("${pluginDir}", pluginDir ?? "")
             .Replace("${home}", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
             .Replace("${workspaceFolderName}", workspace is null ? "default" : Path.GetFileName(workspace))
             .Replace("${os}", OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "mac" : "linux")
             .Replace("${name}", name ?? "");

    /// <summary>Copies a plugin folder into the user plugin directory.</summary>
    public static string Install(string sourceDir)
    {
        var manifestPath = Path.Combine(sourceDir, "plugin.json");
        if (!File.Exists(manifestPath)) throw new InvalidOperationException("The folder has no plugin.json.");
        var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(manifestPath), Settings.Json)
                       ?? throw new InvalidOperationException("plugin.json is empty.");
        if (string.IsNullOrWhiteSpace(manifest.Id)) throw new InvalidOperationException("plugin.json needs an \"id\".");
        var dest = Path.Combine(IdePaths.UserPlugins, manifest.Id);
        if (System.IO.Directory.Exists(dest)) System.IO.Directory.Delete(dest, recursive: true);
        CopyDir(sourceDir, dest);
        return manifest.Id;
    }

    private static void CopyDir(string from, string to)
    {
        System.IO.Directory.CreateDirectory(to);
        foreach (var f in System.IO.Directory.EnumerateFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
        foreach (var d in System.IO.Directory.EnumerateDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
    }

    /// <summary>Writes a starter plugin into the user plugin folder and returns its manifest path.</summary>
    public static string CreateTemplate(string id)
    {
        var dir = Path.Combine(IdePaths.UserPlugins, id);
        System.IO.Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "plugin.json");
        if (!File.Exists(path))
        {
            File.WriteAllText(path, $$"""
            {
              "id": "{{id}}",
              "name": "My Language",
              "version": "0.1.0",
              "description": "Adds support for My Language.",
              "contributes": {
                "languages": [
                  { "id": "mylang", "name": "My Language", "extensions": [".my"], "lineComment": "#", "icon": "ML", "iconColor": "#C77DBB" }
                ],
                "lspServers": [
                  { "id": "mylang-ls", "name": "My Language Server", "command": "mylang-language-server", "args": ["--stdio"], "languages": ["mylang"] }
                ],
                "snippets": [
                  { "language": "mylang", "prefix": "main", "body": ["fn main() {", "\t$0", "}"], "description": "Entry point" }
                ],
                "runConfigurations": []
              }
            }
            """);
        }
        return path;
    }
}
