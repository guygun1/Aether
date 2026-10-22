namespace MultiIde.Core;

/// <summary>One user-facing setting; the Settings page is generated from these.</summary>
public sealed record SettingDef(
    string Category,
    string Title,
    string Kind, // bool, int, double, text, choice, theme
    Func<Settings, object> Get,
    Action<Settings, object> Set,
    string? Description = null,
    double Min = 0,
    double Max = 100,
    double Step = 1,
    (string Value, string Label)[]? Choices = null);

public static class SettingsSchema
{
    private static SettingDef Bool(string cat, string title, Func<Settings, bool> get, Action<Settings, bool> set, string? desc = null) =>
        new(cat, title, "bool", s => get(s), (s, v) => set(s, (bool)v), desc);

    private static SettingDef Int(string cat, string title, Func<Settings, int> get, Action<Settings, int> set, double min, double max, string? desc = null) =>
        new(cat, title, "int", s => get(s), (s, v) => set(s, Convert.ToInt32(v)), desc, min, max);

    private static SettingDef Num(string cat, string title, Func<Settings, double> get, Action<Settings, double> set, double min, double max, double step, string? desc = null) =>
        new(cat, title, "double", s => get(s), (s, v) => set(s, Convert.ToDouble(v)), desc, min, max, step);

    private static SettingDef Text(string cat, string title, Func<Settings, string> get, Action<Settings, string> set, string? desc = null) =>
        new(cat, title, "text", s => get(s), (s, v) => set(s, (string)v), desc);

    public static readonly List<SettingDef> All = new()
    {
        // Appearance & motion
        new("Appearance", "Theme", "theme", s => s.Theme, (s, v) => s.Theme = (string)v, "Themes come from plugins; TextMate-style theme files (colors + tokenColors) work too."),
        new("Appearance", "Animations", "choice", s => s.AnimationMode, (s, v) => s.AnimationMode = (string)v,
            "Animated: eased hover, popups, panels and scrolling. Instant: everything snaps immediately.",
            Choices: [("animated", "Animated"), ("instant", "Instant")]),
        Num("Appearance", "Animation speed", s => s.AnimationSpeed, (s, v) => s.AnimationSpeed = v, 0.25, 4, 0.25, "1 = normal, 2 = twice as fast, 0.5 = half speed."),
        Bool("Appearance", "Smooth scrolling", s => s.SmoothScrolling, (s, v) => s.SmoothScrolling = v, "Eases mouse-wheel scrolling in editors, trees and lists."),
        Int("Appearance", "Lines per wheel notch", s => s.ScrollLines, (s, v) => s.ScrollLines = v, 1, 12),
        Num("Appearance", "UI font size", s => s.UiFontSize, (s, v) => s.UiFontSize = v, 10, 20, 0.5),

        // Editor
        Text("Editor", "Font family", s => s.EditorFont, (s, v) => s.EditorFont = v, "Comma-separated fallbacks, e.g. \"JetBrains Mono, Consolas\"."),
        Num("Editor", "Font size", s => s.FontSize, (s, v) => s.FontSize = v, 8, 40, 0.5),
        Num("Editor", "Line spacing", s => s.LineSpacing, (s, v) => s.LineSpacing = v, 1.0, 2.0, 0.05),
        Int("Editor", "Tab size", s => s.TabSize, (s, v) => s.TabSize = v, 1, 16),
        Bool("Editor", "Use spaces instead of tabs", s => s.InsertSpaces, (s, v) => s.InsertSpaces = v),
        Bool("Editor", "Soft-wrap lines", s => s.WordWrap, (s, v) => s.WordWrap = v),
        Bool("Editor", "Show line numbers", s => s.ShowLineNumbers, (s, v) => s.ShowLineNumbers = v),
        Bool("Editor", "Show whitespace", s => s.ShowWhitespace, (s, v) => s.ShowWhitespace = v),
        Bool("Editor", "Show indent guides", s => s.ShowIndentGuides, (s, v) => s.ShowIndentGuides = v),
        Bool("Editor", "Highlight current line", s => s.HighlightCurrentLine, (s, v) => s.HighlightCurrentLine = v),
        Bool("Editor", "Highlight usages of the symbol at the caret", s => s.HighlightOccurrences, (s, v) => s.HighlightOccurrences = v),
        Bool("Editor", "Highlight matching brackets", s => s.BracketHighlight, (s, v) => s.BracketHighlight = v),
        Bool("Editor", "Code folding", s => s.ShowFolding, (s, v) => s.ShowFolding = v, "Takes effect for newly opened files."),
        Bool("Editor", "Scroll past the end of the file", s => s.ScrollPastEnd, (s, v) => s.ScrollPastEnd = v),

        // Code completion
        Bool("Code Completion", "Show suggestions as you type", s => s.AutoPopupCompletion, (s, v) => s.AutoPopupCompletion = v, "Ctrl+Space always works."),
        Int("Code Completion", "Popup delay (ms)", s => s.CompletionDelayMs, (s, v) => s.CompletionDelayMs = v, 0, 1000),
        Int("Code Completion", "Minimum typed characters", s => s.CompletionMinChars, (s, v) => s.CompletionMinChars = v, 1, 5),
        Bool("Code Completion", "Include snippets / live templates", s => s.ShowSnippetsInCompletion, (s, v) => s.ShowSnippetsInCompletion = v),
        Bool("Code Completion", "Suggest words from the file when no language server", s => s.WordCompletion, (s, v) => s.WordCompletion = v),
        Bool("Code Completion", "Parameter info popup", s => s.ParameterHints, (s, v) => s.ParameterHints = v),
        Int("Code Completion", "Show errors after a typing pause of (ms)", s => s.DiagnosticsDelayMs, (s, v) => s.DiagnosticsDelayMs = v, 0, 10000,
            "New errors and warnings wait until you stop typing this long. Stale marks on the line you edit are hidden while typing. 0 = immediately."),
        Bool("Code Completion", "Quick documentation on mouse hover", s => s.HoverDocs, (s, v) => s.HoverDocs = v),

        // Typing
        Bool("Typing", "Insert pair brackets", s => s.AutoCloseBrackets, (s, v) => s.AutoCloseBrackets = v),
        Bool("Typing", "Insert pair quotes", s => s.AutoCloseQuotes, (s, v) => s.AutoCloseQuotes = v),

        // Saving
        Bool("Saving", "Save files when the IDE loses focus", s => s.AutoSave, (s, v) => s.AutoSave = v),
        Bool("Saving", "Reformat code on save", s => s.FormatOnSave, (s, v) => s.FormatOnSave = v, "Uses a plugin formatter or the language server."),
        Bool("Saving", "Remove trailing whitespace on save", s => s.TrimTrailingWhitespace, (s, v) => s.TrimTrailingWhitespace = v),
        Bool("Saving", "Ensure a line break at the end of the file", s => s.EnsureFinalNewline, (s, v) => s.EnsureFinalNewline = v),

        // Project view
        Bool("Project View", "Open files with a single click (preview tab)", s => s.PreviewTab, (s, v) => s.PreviewTab = v),
        Bool("Project View", "Show hidden files (dot files)", s => s.ShowHiddenFiles, (s, v) => s.ShowHiddenFiles = v),
        Text("Project View", "Excluded folders", s => s.ExcludedFolders, (s, v) => s.ExcludedFolders = v, "Semicolon-separated. Skipped by search and dimmed in the tree."),

        // Run
        Bool("Run", "Clear the console when a configuration starts", s => s.ClearConsoleOnRun, (s, v) => s.ClearConsoleOnRun = v),
        Bool("Run", "Wrap long console lines", s => s.ConsoleWordWrap, (s, v) => s.ConsoleWordWrap = v),

        // Startup
        Bool("Startup", "Reopen the last project on startup", s => s.ReopenLastProject, (s, v) => s.ReopenLastProject = v),
        Bool("Startup", "Restore open files when a project opens", s => s.RestoreSession, (s, v) => s.RestoreSession = v),
    };
}
