using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using TextMateSharp.Internal.Themes.Reader;
using TextMateSharp.Themes;

namespace MultiIde.Core;

public sealed record ThemeInfo(string Id, string Name, string File, string Source);

/// <summary>
/// Color themes. Every UI/editor color is a shared <see cref="SolidColorBrush"/> resource;
/// switching themes recolors those brushes in place, so open windows update live.
/// Theme files are JSON with <c>ui</c>, <c>editor</c> and <c>tokenColors</c> sections;
/// VS Code theme files (<c>colors</c> + <c>tokenColors</c>) are accepted too.
/// </summary>
public static class ThemeManager
{
    /// <summary>UI keys (same names as the resources in Themes/Colors.axaml).</summary>
    public static readonly string[] UiKeys =
    {
        "EditorBg", "PanelBg", "PopupBg", "InputBg", "Border", "BorderStrong", "Fg", "FgEditor", "FgMuted", "FgDisabled",
        "Hover", "Selection", "SelectionInactive", "Accent", "AccentHover", "Error", "Warning", "Success", "Link",
    };

    /// <summary>Editor keys, stored as resources named "Editor" + key (e.g. EditorSelection).</summary>
    public static readonly string[] EditorKeys =
    {
        "LineNumber", "LineNumberActive", "CurrentLine", "Selection", "Caret", "SearchMatch", "BracketMatch", "Occurrence", "IndentGuide", "Whitespace",
    };

    // VS Code color ids used when a theme only has a "colors" section.
    private static readonly Dictionary<string, string[]> VsCodeUi = new()
    {
        ["EditorBg"] = ["editor.background"],
        ["PanelBg"] = ["sideBar.background", "panel.background", "editorGroupHeader.tabsBackground", "editor.background"],
        ["PopupBg"] = ["editorWidget.background", "quickInput.background", "sideBar.background"],
        ["InputBg"] = ["input.background", "editor.background"],
        ["Border"] = ["panel.border", "sideBar.border", "editorGroup.border", "contrastBorder"],
        ["BorderStrong"] = ["input.border", "focusBorder", "contrastBorder"],
        ["Fg"] = ["foreground", "sideBar.foreground", "editor.foreground"],
        ["FgEditor"] = ["editor.foreground"],
        ["FgMuted"] = ["descriptionForeground", "editorLineNumber.foreground"],
        ["FgDisabled"] = ["disabledForeground", "editorLineNumber.foreground"],
        ["Hover"] = ["list.hoverBackground", "toolbar.hoverBackground"],
        ["Selection"] = ["list.activeSelectionBackground", "editor.selectionBackground"],
        ["SelectionInactive"] = ["list.inactiveSelectionBackground", "editor.inactiveSelectionBackground"],
        ["Accent"] = ["button.background", "focusBorder"],
        ["AccentHover"] = ["button.hoverBackground", "button.background"],
        ["Error"] = ["editorError.foreground", "errorForeground"],
        ["Warning"] = ["editorWarning.foreground"],
        ["Success"] = ["terminal.ansiGreen", "gitDecoration.addedResourceForeground"],
        ["Link"] = ["textLink.foreground", "focusBorder"],
    };

    private static readonly Dictionary<string, string[]> VsCodeEditor = new()
    {
        ["LineNumber"] = ["editorLineNumber.foreground"],
        ["LineNumberActive"] = ["editorLineNumber.activeForeground", "editor.foreground"],
        ["CurrentLine"] = ["editor.lineHighlightBackground"],
        ["Selection"] = ["editor.selectionBackground"],
        ["Caret"] = ["editorCursor.foreground", "editor.foreground"],
        ["SearchMatch"] = ["editor.findMatchHighlightBackground", "editor.findMatchBackground"],
        ["BracketMatch"] = ["editorBracketMatch.background"],
        ["Occurrence"] = ["editor.wordHighlightBackground", "editor.selectionHighlightBackground"],
        ["IndentGuide"] = ["editorIndentGuide.background1", "editorIndentGuide.background"],
        ["Whitespace"] = ["editorWhitespace.foreground"],
    };

    public static string CurrentId { get; private set; } = "";
    public static bool IsLight { get; private set; }
    public static IRawTheme? TokenTheme { get; private set; }

    /// <summary>Raised after a theme is applied (editors re-apply token colors).</summary>
    public static event Action? Changed;

    public static IBrush Brush(string key) =>
        Application.Current?.FindResource(key) as IBrush ?? Brushes.Magenta;

    public static Color Color(string key) => Brush(key) is ISolidColorBrush b ? b.Color : Colors.Magenta;

    public static List<ThemeInfo> Available(PluginManager plugins) =>
        plugins.Themes()
            .Select(t => new ThemeInfo(t.Theme.Id, t.Theme.Name, Path.Combine(t.Plugin.Directory, t.Theme.Path), t.Plugin.Manifest.Name))
            .Where(t => File.Exists(t.File))
            .ToList();

    /// <summary>Applies a theme by id; falls back to the built-in dark theme.</summary>
    public static bool Apply(string id, PluginManager plugins)
    {
        id = Migrate(id);
        var info = Available(plugins).FirstOrDefault(t => t.Id == id);
        try
        {
            var json = info is null
                ? ReadAsset("avares://Aether/Assets/default-dark.theme.json")
                : File.ReadAllText(info.File);
            ApplyJson(json);
            CurrentId = info?.Id ?? "midnight";
            Changed?.Invoke();
            return info is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Maps theme ids from older versions to the current names.</summary>
    public static string Migrate(string id) => id switch
    {
        "jetbrains-dark" => "midnight",
        "jetbrains-light" => "daylight",
        "darcula" => "ember",
        _ => id,
    };

    private static string ReadAsset(string uri)
    {
        using var stream = Avalonia.Platform.AssetLoader.Open(new Uri(uri));
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void ApplyJson(string json)
    {
        var doc = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })!;
        IsLight = (doc["type"]?.GetValue<string>() ?? "dark").Contains("light", StringComparison.OrdinalIgnoreCase);
        var ui = doc["ui"] as JsonObject;
        var editor = doc["editor"] as JsonObject;
        var vs = doc["colors"] as JsonObject;

        string? Pick(JsonObject? section, string key, Dictionary<string, string[]> map)
        {
            if (section?[key] is JsonValue v) return v.GetValue<string>();
            if (section?[char.ToLowerInvariant(key[0]) + key[1..]] is JsonValue v2) return v2.GetValue<string>();
            if (vs is not null && map.TryGetValue(key, out var ids))
                foreach (var id in ids)
                    if (vs[id] is JsonValue c) return c.GetValue<string>();
            return null;
        }

        foreach (var key in UiKeys)
            if (Pick(ui, key, VsCodeUi) is { } hex) SetBrush(key, hex);
        foreach (var key in EditorKeys)
            if (Pick(editor, key, VsCodeEditor) is { } hex) SetBrush("Editor" + key, hex);
        // Editor background/foreground default to the UI ones.
        SetBrush("EditorText", Pick(editor, "Foreground", new() { ["Foreground"] = ["editor.foreground"] }) ?? Color("FgEditor").ToString());

        if (Application.Current is { } app)
            app.RequestedThemeVariant = IsLight ? ThemeVariant.Light : ThemeVariant.Dark;

        // TextMate token colors: feed the same JSON to TextMateSharp's theme reader.
        using var reader = new StreamReader(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)));
        TokenTheme = ThemeReader.ReadThemeSync(reader);
    }

    private static void SetBrush(string key, string hex)
    {
        if (!Avalonia.Media.Color.TryParse(hex, out var color)) return;
        if (Application.Current?.FindResource(key) is SolidColorBrush brush) brush.Color = color;
        else if (Application.Current is { } app) app.Resources[key] = new SolidColorBrush(color);
    }
}
