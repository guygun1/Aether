using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace MultiIde.Core;

/// <summary>An installed IDE whose settings can be imported.</summary>
public sealed record ImportSource(string Kind, string Name, string Path);

/// <summary>
/// Imports editor font, tabs, light/dark preference and recent projects from
/// JetBrains IDEs and Visual Studio, by reading their own settings files.
/// </summary>
public static class SettingsImport
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static List<ImportSource> Detect()
    {
        var found = new List<ImportSource>();
        try
        {
            var jb = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JetBrains");
            if (Directory.Exists(jb))
                foreach (var dir in Directory.GetDirectories(jb).Where(d => Directory.Exists(Path.Combine(d, "options")))
                             .OrderByDescending(Directory.GetLastWriteTime))
                    found.Add(new ImportSource("jetbrains", PrettyJetBrainsName(Path.GetFileName(dir)), dir));
        }
        catch { /* not fatal */ }

        try
        {
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            foreach (var vsDir in Directory.GetDirectories(docs, "Visual Studio*").OrderByDescending(d => d))
            {
                var settings = Path.Combine(vsDir, "Settings");
                if (!Directory.Exists(settings)) continue;
                var file = Directory.GetFiles(settings, "CurrentSettings*.vssettings").OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
                if (file is not null) found.Add(new ImportSource("visualstudio", Path.GetFileName(vsDir), file));
            }
        }
        catch { /* not fatal */ }
        return found;
    }

    private static string PrettyJetBrainsName(string folder)
    {
        var products = new[] { ("IntelliJIdea", "IntelliJ IDEA Ultimate"), ("IdeaIC", "IntelliJ IDEA Community"), ("Rider", "Rider"), ("CLion", "CLion"),
            ("RustRover", "RustRover"), ("PyCharm", "PyCharm"), ("WebStorm", "WebStorm"), ("GoLand", "GoLand"), ("PhpStorm", "PhpStorm"), ("DataGrip", "DataGrip") };
        foreach (var (prefix, name) in products)
            if (folder.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return $"{name} {folder[prefix.Length..]}";
        return folder;
    }

    /// <summary>Applies what can be read from the source; returns a human-readable summary.</summary>
    public static List<string> Import(ImportSource source, Settings s) =>
        source.Kind == "jetbrains" ? ImportJetBrains(source.Path, s) : ImportVisualStudio(source.Path, s);

    // ---- JetBrains ----------------------------------------------------------------

    private static string? Option(XDocument doc, string name) =>
        doc.Descendants("option").FirstOrDefault(o => (string?)o.Attribute("name") == name)?.Attribute("value")?.Value;

    private static List<string> ImportJetBrains(string dir, Settings s)
    {
        var done = new List<string>();
        var options = Path.Combine(dir, "options");
        Try(() =>
        {
            var file = Path.Combine(options, "editor-font.xml");
            if (!File.Exists(file)) return;
            var doc = XDocument.Load(file);
            if (Option(doc, "FONT_FAMILY") is { Length: > 0 } family) { s.EditorFont = $"{family}, JetBrains Mono, Cascadia Mono, Consolas"; done.Add($"Editor font: {family}"); }
            var size = Option(doc, "FONT_SIZE_2D") ?? Option(doc, "FONT_SIZE");
            if (double.TryParse(size, System.Globalization.CultureInfo.InvariantCulture, out var px)) { s.FontSize = px; done.Add($"Font size: {px}"); }
            if (double.TryParse(Option(doc, "LINE_SPACING"), System.Globalization.CultureInfo.InvariantCulture, out var ls)) { s.LineSpacing = Math.Clamp(ls, 1, 2); done.Add($"Line spacing: {ls}"); }
        });
        Try(() =>
        {
            var file = Path.Combine(options, "laf.xml");
            if (!File.Exists(file)) return;
            var text = File.ReadAllText(file);
            s.Theme = text.Contains("Light", StringComparison.OrdinalIgnoreCase) ? "daylight"
                : text.Contains("Darcula", StringComparison.OrdinalIgnoreCase) ? "ember" : "midnight";
            done.Add($"Theme: {(s.Theme == "daylight" ? "light" : "dark")}");
        });
        Try(() =>
        {
            var file = Path.Combine(options, "recentProjects.xml");
            if (!File.Exists(file)) return;
            var paths = XDocument.Load(file).Descendants("entry")
                .Select(e => (string?)e.Attribute("key"))
                .Where(k => k is not null)
                .Select(k => k!.Replace("$USER_HOME$", Home).Replace('/', Path.DirectorySeparatorChar))
                .Where(Directory.Exists).Take(15).ToList();
            AddRecent(s, paths, done);
        });
        Try(() =>
        {
            var file = Path.Combine(options, "editor.xml");
            if (File.Exists(file) && Option(XDocument.Load(file), "USE_SOFT_WRAPS") is { Length: > 0 }) { s.WordWrap = true; done.Add("Soft wrap: on"); }
        });
        s.KeymapPreset = "jetbrains";
        s.CodeStyle = "jetbrains";
        done.Add("Keymap and code style: JetBrains");
        return done;
    }

    // ---- Visual Studio ----------------------------------------------------------------

    private const string TextEditorFontsGuid = "{A27B4E24-A735-4D1D-B8E7-9716E1E3D8E0}";
    private const string LightThemeGuid = "{de3dbbcd-f642-433c-8353-8f1df4370aba}";

    private static List<string> ImportVisualStudio(string vssettings, Settings s)
    {
        var done = new List<string>();
        Try(() =>
        {
            var doc = XDocument.Load(vssettings);
            var editorFonts = doc.Descendants("Category").FirstOrDefault(c => string.Equals((string?)c.Attribute("GUID"), TextEditorFontsGuid, StringComparison.OrdinalIgnoreCase));
            if (editorFonts?.Attribute("FontName")?.Value is { Length: > 0 } font) { s.EditorFont = $"{font}, Cascadia Mono, Consolas"; done.Add($"Editor font: {font}"); }
            if (double.TryParse(editorFonts?.Attribute("FontSize")?.Value, System.Globalization.CultureInfo.InvariantCulture, out var pt))
            {
                s.FontSize = Math.Round(pt * 4 / 3, 1); // points → pixels
                done.Add($"Font size: {pt}pt");
            }
            var csharp = doc.Descendants("Category").FirstOrDefault(c => (string?)c.Attribute("name") == "TextEditor_CSharp");
            string? Prop(XElement? cat, string name) => cat?.Elements("PropertyValue").FirstOrDefault(p => (string?)p.Attribute("name") == name)?.Value;
            if (int.TryParse(Prop(csharp, "TabSize"), out var tab)) { s.TabSize = tab; done.Add($"Tab size: {tab}"); }
            if (Prop(csharp, "InsertTabs") is { } insertTabs) s.InsertSpaces = !bool.Parse(insertTabs);
            if (Prop(csharp, "WordWrap") == "true") s.WordWrap = true;
            var theme = doc.Descendants("PropertyValue").FirstOrDefault(p => (string?)p.Attribute("name") == "ColorTheme")?.Value;
            if (theme is not null)
            {
                s.Theme = theme.Contains(LightThemeGuid, StringComparison.OrdinalIgnoreCase) ? "daylight" : "midnight";
                done.Add($"Theme: {(s.Theme == "daylight" ? "light" : "dark")}");
            }
        });
        Try(() =>
        {
            var vsData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "VisualStudio");
            if (!Directory.Exists(vsData)) return;
            var containers = Directory.GetDirectories(vsData)
                .Select(d => Path.Combine(d, "CodeContainers.json"))
                .Where(File.Exists).OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
            if (containers is null) return;
            var paths = (JsonNode.Parse(File.ReadAllText(containers)) as JsonArray ?? new JsonArray())
                .Select(e => e?["Value"]?["LocalProperties"]?["FullPath"]?.GetValue<string>() ?? e?["Key"]?.GetValue<string>())
                .Where(p => p is not null)
                .Select(p => File.Exists(p) ? Path.GetDirectoryName(p)! : p!)
                .Where(Directory.Exists).Distinct().Take(15).ToList();
            AddRecent(s, paths, done);
        });
        s.KeymapPreset = "visualstudio";
        s.CodeStyle = "visualstudio";
        s.ProjectPanelSide = "right";
        done.Add("Keymap, code style and layout: Visual Studio");
        return done;
    }

    private static void AddRecent(Settings s, List<string> paths, List<string> done)
    {
        foreach (var p in Enumerable.Reverse(paths)) s.AddRecent(p);
        if (paths.Count > 0) done.Add($"{paths.Count} recent projects");
    }

    private static void Try(Action a)
    {
        try { a(); }
        catch { /* skip what cannot be read */ }
    }
}

/// <summary>Code style presets: brace placement and the style files written into new projects.</summary>
public static class CodeStyles
{
    public static readonly (string Id, string Name, string Description)[] All =
    {
        ("default", "Default", "Each language's own convention: C# braces on a new line, C/C++ and Rust at the end of the line. 4 spaces."),
        ("visualstudio", "Visual Studio", "Braces on their own line (Allman) for C# and C++, Microsoft clang-format style, 4 spaces."),
        ("jetbrains", "JetBrains", "C# braces on a new line, C/C++ braces at the end of the line (K&R), 4 spaces."),
    };

    /// <summary>Whether "{" goes on its own line for this language under the chosen style.</summary>
    public static bool BracesOnNewLine(Settings s, string languageId) =>
        s.BracesOnNewLine || languageId switch
        {
            "csharp" => true,
            "cpp" or "c" => s.CodeStyle == "visualstudio",
            _ => false,
        };

    public static string ClangFormatStyle(Settings s) => s.CodeStyle == "visualstudio" ? "Microsoft" : "LLVM";

    /// <summary>.editorconfig and .clang-format content for a new project.</summary>
    public static IEnumerable<(string File, string Content)> ProjectFiles(Settings s)
    {
        var cppNewLine = BracesOnNewLine(s, "cpp");
        yield return (".editorconfig", string.Join("\n", new[]
        {
            "root = true",
            "",
            "[*]",
            "charset = utf-8",
            "end_of_line = lf",
            "insert_final_newline = true",
            "trim_trailing_whitespace = true",
            $"indent_style = {(s.InsertSpaces ? "space" : "tab")}",
            $"indent_size = {s.TabSize}",
            "",
            "[*.cs]",
            "csharp_new_line_before_open_brace = all",
            "csharp_new_line_before_else = true",
            "",
        }));
        yield return (".clang-format", string.Join("\n", new[]
        {
            $"BasedOnStyle: {ClangFormatStyle(s)}",
            $"IndentWidth: {s.TabSize}",
            $"UseTab: {(s.InsertSpaces ? "Never" : "Always")}",
            $"BreakBeforeBraces: {(cppNewLine ? "Allman" : "Attach")}",
            "ColumnLimit: 120",
            "",
        }));
    }

    /// <summary>Moves an opening brace at the end of a line onto its own line (for snippets and templates).</summary>
    public static string ApplyBraceStyle(string body, bool newLine)
    {
        if (!newLine) return body;
        var lines = body.Replace("\r\n", "\n").Split('\n');
        var result = new List<string>();
        foreach (var line in lines)
        {
            var trimmed = line.TrimEnd();
            if (trimmed.EndsWith('{') && trimmed.Trim().Length > 1 && !trimmed.TrimStart().StartsWith("//"))
            {
                var indent = line[..(line.Length - line.TrimStart().Length)];
                result.Add(trimmed[..^1].TrimEnd());
                result.Add(indent + "{");
            }
            else
            {
                result.Add(line);
            }
        }
        return string.Join("\n", result);
    }
}
