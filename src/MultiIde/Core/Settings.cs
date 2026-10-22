using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultiIde.Core;

public static class IdePaths
{
    /// <summary><c>~/.aether</c>: settings and user plugins (migrated from the old <c>~/.multiide</c>).</summary>
    public static string Home { get; } = Migrate(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".multiide"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aether"));

    /// <summary>Per-project IDE folder (<c>.aether</c>), migrated from <c>.multiide</c>.</summary>
    public static string ProjectDir(string root) => Migrate(Path.Combine(root, ".multiide"), Path.Combine(root, ".aether"));

    /// <summary>Renames an old IDE folder to its new name once; returns the new path.</summary>
    private static string Migrate(string oldPath, string newPath)
    {
        try
        {
            if (!Directory.Exists(newPath) && Directory.Exists(oldPath)) Directory.Move(oldPath, newPath);
        }
        catch
        {
            // Locked or read-only: start fresh in the new folder.
        }
        return newPath;
    }

    public static string UserPlugins => Path.Combine(Home, "plugins");
    public static string SettingsFile => Path.Combine(Home, "settings.json");
    public static string BundledPlugins => Path.Combine(AppContext.BaseDirectory, "plugins");
    /// <summary>Decompiled dependency sources (cached).</summary>
    public static string Decompiled => Path.Combine(Home, "decompiled");

    /// <summary>Files from dependencies or toolchains: shown read-only, like library sources in IntelliJ.</summary>
    public static bool IsLibraryPath(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] roots =
        [
            Decompiled,
            Path.Combine(home, ".cargo", "registry"), Path.Combine(home, ".cargo", "git"), Path.Combine(home, ".rustup"),
            Path.Combine(home, ".nuget", "packages"),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        ];
        var full = Path.GetFullPath(path);
        return roots.Any(r => r.Length > 0 && full.StartsWith(r, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsWindows => OperatingSystem.IsWindows();

    /// <summary>Key for comparing file paths (case-insensitive on Windows).</summary>
    public static string Key(string path)
    {
        var full = Path.GetFullPath(path);
        return IsWindows ? full.ToLowerInvariant() : full;
    }
}

public sealed class Settings
{
    public double FontSize { get; set; } = 14;
    public int TabSize { get; set; } = 4;
    public bool InsertSpaces { get; set; } = true;
    public bool FormatOnSave { get; set; }
    public bool AutoSave { get; set; } = true;
    public bool ShowWhitespace { get; set; }
    public bool WordWrap { get; set; }
    /// <summary>Single-click a file in the project tree to open it in a preview tab.</summary>
    public bool PreviewTab { get; set; } = true;
    public bool AutoscrollFromSource { get; set; } = true;
    public bool FoldersFirst { get; set; } = true;
    public bool ShowExcludedFolders { get; set; } = true;
    public bool ShowExternalLibraries { get; set; } = true;
    public bool FileNesting { get; set; } = true;
    public bool CompactDirectories { get; set; } = true;
    public bool SortByType { get; set; }
    public List<string> DisabledPlugins { get; set; } = new();
    /// <summary>Values of plugin-declared settings: pluginId → key → value.</summary>
    public Dictionary<string, Dictionary<string, System.Text.Json.Nodes.JsonNode?>> PluginSettings { get; set; } = new();
    /// <summary>User keymap overrides: action id → shortcut ("" removes the shortcut).</summary>
    public Dictionary<string, string> Keymap { get; set; } = new();
    /// <summary>The user's own commands (Settings → Keymap → My Commands): shell commands with an optional shortcut.</summary>
    public List<CommandContribution> UserCommands { get; set; } = new();
    /// <summary>default, visualstudio or jetbrains (see KeymapPresets).</summary>
    public string KeymapPreset { get; set; } = "default";
    /// <summary>Code style preset: default, visualstudio or jetbrains.</summary>
    public string CodeStyle { get; set; } = "default";
    /// <summary>Braces on their own line (Allman) instead of at the end of the line.</summary>
    public bool BracesOnNewLine { get; set; }
    /// <summary>Project tool window on the left (default) or the right.</summary>
    public string ProjectPanelSide { get; set; } = "left";
    /// <summary>False until the first-run setup has been completed or skipped.</summary>
    public bool SetupDone { get; set; }
    public string Theme { get; set; } = "midnight";
    /// <summary>Syntax colours: "aether" (the theme's own), "jetbrains" or "visualstudio".</summary>
    public string SyntaxScheme { get; set; } = "aether";
    public string EditorFont { get; set; } = "JetBrains Mono, Cascadia Mono, Consolas";
    public double LineSpacing { get; set; } = 1.25;
    public bool HighlightOccurrences { get; set; } = true;
    public bool TrimTrailingWhitespace { get; set; } = true;
    public bool EnsureFinalNewline { get; set; } = true;
    public bool RestoreSession { get; set; } = true;
    public bool ReopenLastProject { get; set; }
    public bool ShowIndentGuides { get; set; } = true;
    // Appearance & motion
    /// <summary>"animated" or "instant".</summary>
    public string AnimationMode { get; set; } = "animated";
    /// <summary>Animation speed multiplier (2 = twice as fast).</summary>
    public double AnimationSpeed { get; set; } = 1.0;
    public bool SmoothScrolling { get; set; } = true;
    public int ScrollLines { get; set; } = 3;
    public double UiFontSize { get; set; } = 13;
    // Editor
    public bool HighlightCurrentLine { get; set; } = true;
    public bool ShowLineNumbers { get; set; } = true;
    public bool ShowFolding { get; set; } = true;
    public bool ScrollPastEnd { get; set; } = true;
    public bool BracketHighlight { get; set; } = true;
    public bool InlayHints { get; set; } = true;
    /// <summary>IntelliCode-style next-word suggestions learned from the project (Tab accepts).</summary>
    public bool SmartSuggestions { get; set; } = true;
    // Code completion & typing
    public bool AutoPopupCompletion { get; set; } = true;
    public int CompletionDelayMs { get; set; }
    public int CompletionMinChars { get; set; } = 1;
    public bool ShowSnippetsInCompletion { get; set; } = true;
    public bool WordCompletion { get; set; } = true;
    public bool AutoCloseBrackets { get; set; } = true;
    public bool AutoCloseQuotes { get; set; } = true;
    public bool ParameterHints { get; set; } = true;
    public bool HoverDocs { get; set; } = true;
    public bool MouseBackForward { get; set; } = true;
    // Project view & run
    public bool ShowHiddenFiles { get; set; } = true;
    public string ExcludedFolders { get; set; } = ".git;node_modules;target;bin;obj;build;out;cmake-build-debug;cmake-build-release;.vs;.idea";
    public bool ClearConsoleOnRun { get; set; } = true;
    public bool ConsoleWordWrap { get; set; } = true;
    /// <summary>Wait this long after the last keystroke before showing new errors/warnings.</summary>
    public int DiagnosticsDelayMs { get; set; } = 2000;
    // Toolbar layout (null = defaults).
    public List<string>? ToolbarLeft { get; set; }
    public List<string>? ToolbarCenter { get; set; }
    public List<string>? ToolbarRight { get; set; }

    public HashSet<string> ExcludedFolderSet() =>
        new(ExcludedFolders.Split(';', ',').Select(x => x.Trim()).Where(x => x.Length > 0), StringComparer.OrdinalIgnoreCase);
    public List<string> RecentProjects { get; set; } = new();
    public double LeftPanelWidth { get; set; } = 280;
    public double BottomPanelHeight { get; set; } = 240;
    public double RightPanelWidth { get; set; } = 340;

    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(IdePaths.SettingsFile))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(IdePaths.SettingsFile), Json) ?? new();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"settings.json is invalid, using defaults: {e.Message}");
        }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(IdePaths.Home);
        File.WriteAllText(IdePaths.SettingsFile, JsonSerializer.Serialize(this, Json));
    }

    public void AddRecent(string folder)
    {
        RecentProjects.RemoveAll(p => IdePaths.Key(p) == IdePaths.Key(folder));
        RecentProjects.Insert(0, folder);
        if (RecentProjects.Count > 15) RecentProjects.RemoveRange(15, RecentProjects.Count - 15);
    }
}
