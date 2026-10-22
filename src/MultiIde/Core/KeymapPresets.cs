namespace MultiIde.Core;

/// <summary>
/// Keymap presets. The default keymap is defined on the actions themselves (a hybrid that
/// accepts the most common shortcuts of both families); presets override individual actions.
/// Syntax: alternatives with "|", chords as space-separated strokes ("Ctrl+K Ctrl+D").
/// </summary>
public static class KeymapPresets
{
    public static readonly (string Id, string Name, string Description)[] All =
    {
        ("default", "Default", "Aether's own keymap: accepts the most common shortcuts from both major IDE families."),
        ("visualstudio", "Visual Studio", "F5 to run, F12 to go to definition, Ctrl+K chords, Ctrl+. for quick actions."),
        ("jetbrains", "JetBrains", "Double Shift, Alt+Enter, Ctrl+B, Shift+F6, Ctrl+Alt+L, Shift+F10."),
    };

    private static readonly Dictionary<string, string> VisualStudio = new()
    {
        ["gotoDeclaration"] = "F12",
        ["findUsages"] = "Shift+F12",
        ["rename"] = "F2|Ctrl+R Ctrl+R",
        ["reformat"] = "Ctrl+K Ctrl+D",
        ["comment"] = "Ctrl+K Ctrl+C|Ctrl+/",
        ["contextActions"] = "Ctrl+.|Alt+Enter",
        ["complete"] = "Ctrl+Space|Ctrl+J",
        ["quickDoc"] = "Ctrl+K Ctrl+I",
        ["paramInfo"] = "Ctrl+Shift+Space",
        ["deleteLine"] = "Ctrl+Shift+L",
        ["build"] = "Ctrl+Shift+B",
        ["run"] = "F5|Ctrl+F5",
        ["stop"] = "Shift+F5",
        ["searchEverywhere"] = "Ctrl+T|Ctrl+,",
        ["gotoFile"] = "Ctrl+Shift+T",
        ["findAction"] = "Ctrl+Q",
        ["saveAll"] = "Ctrl+Shift+S|Ctrl+S",
        ["viewProject"] = "Ctrl+Alt+L",
        ["viewTerminal"] = "Ctrl+`",
        ["viewProblems"] = "Ctrl+\\ Ctrl+E",
        ["back"] = "Ctrl+-",
        ["forward"] = "Ctrl+Shift+-",
        ["fontUp"] = "Ctrl+Shift+.",
        ["fontDown"] = "Ctrl+Shift+,",
        ["fileStructure"] = "Ctrl+F12",
        ["switchTheme"] = "",
    };

    private static readonly Dictionary<string, string> JetBrains = new()
    {
        ["gotoDeclaration"] = "Ctrl+B",
        ["findUsages"] = "Alt+F7",
        ["rename"] = "Shift+F6",
        ["reformat"] = "Ctrl+Alt+L",
        ["comment"] = "Ctrl+/",
        ["contextActions"] = "Alt+Enter",
        ["run"] = "Shift+F10",
        ["build"] = "Ctrl+F9",
        ["searchEverywhere"] = "Double Shift",
        ["gotoFile"] = "Ctrl+Shift+N",
    };

    public static string? Override(string preset, string actionId) => preset switch
    {
        "visualstudio" => VisualStudio.GetValueOrDefault(actionId),
        "jetbrains" => JetBrains.GetValueOrDefault(actionId),
        _ => null,
    };
}
