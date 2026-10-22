using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MultiIde.Core;

/// <summary>
/// Compatibility layer for IntelliJ Platform plugins (.jar / .zip). IntelliJ plugin code runs on
/// the JVM against IntelliJ APIs and cannot run in Aether, but the declarative parts can:
/// file types → languages, live templates → snippets, color schemes (.icls) → themes,
/// keymaps → keybindings, file templates → file templates, bundled TextMate grammars → grammars.
/// </summary>
public static class IntelliJConverter
{
    public sealed class Report
    {
        public string PluginId { get; set; } = "";
        public string Name { get; set; } = "";
        public string? OutputDir { get; set; }
        public List<string> Converted { get; } = new();
        public List<string> Skipped { get; } = new();
    }

    /// <summary>Reads every file of the plugin (jars inside a distribution zip are opened too).</summary>
    private static Dictionary<string, byte[]> ReadAll(string path)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        void FromArchive(ZipArchive zip)
        {
            foreach (var e in zip.Entries)
            {
                if (e.Length == 0 || e.FullName.EndsWith('/')) continue;
                using var s = e.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                if (e.FullName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                {
                    ms.Position = 0;
                    try { using var inner = new ZipArchive(ms, ZipArchiveMode.Read); FromArchive(inner); }
                    catch { /* not a readable jar */ }
                }
                else
                {
                    files.TryAdd(e.FullName.Replace('\\', '/'), ms.ToArray());
                }
            }
        }
        if (Directory.Exists(path))
        {
            foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                if (f.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) { using var z = ZipFile.OpenRead(f); FromArchive(z); }
                else files.TryAdd(Path.GetRelativePath(path, f).Replace('\\', '/'), File.ReadAllBytes(f));
            }
        }
        else
        {
            using var zip = ZipFile.OpenRead(path);
            FromArchive(zip);
        }
        return files;
    }

    public static Report Convert(string source)
    {
        var report = new Report();
        var files = ReadAll(source);
        var pluginXml = files.FirstOrDefault(f => f.Key.EndsWith("META-INF/plugin.xml", StringComparison.OrdinalIgnoreCase));
        if (pluginXml.Value is null) throw new InvalidOperationException("No META-INF/plugin.xml found: this does not look like an IntelliJ plugin.");
        var xml = XDocument.Parse(Encoding.UTF8.GetString(pluginXml.Value));
        var root = xml.Root!;
        string? El(string name) => root.Element(name)?.Value.Trim();

        var ijId = El("id") ?? El("name") ?? Path.GetFileNameWithoutExtension(source);
        var id = "ij-" + Regex.Replace(ijId.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        report.PluginId = id;
        report.Name = El("name") ?? ijId;
        var dir = Path.Combine(IdePaths.UserPlugins, id);
        Directory.CreateDirectory(dir);
        report.OutputDir = dir;

        var contributes = new JsonObject();
        JsonArray Arr(string key) => (contributes[key] as JsonArray) ?? (JsonArray)(contributes[key] = new JsonArray());

        // File types → languages (highlighting comes from bundled TextMate grammars or the IDE's own).
        foreach (var ft in root.Descendants().Where(e => e.Name.LocalName == "fileType"))
        {
            var name = (string?)ft.Attribute("name") ?? (string?)ft.Attribute("language");
            var exts = ((string?)ft.Attribute("extensions") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries);
            var names = ((string?)ft.Attribute("fileNames") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries);
            if (name is null || exts.Length + names.Length == 0) continue;
            Arr("languages").Add(new JsonObject
            {
                ["id"] = ((string?)ft.Attribute("language") ?? name).ToLowerInvariant(),
                ["name"] = name,
                ["extensions"] = new JsonArray(exts.Select(e => (JsonNode)("." + e.TrimStart('.'))).ToArray()),
                ["filenames"] = new JsonArray(names.Select(n => (JsonNode)n).ToArray()),
                ["icon"] = name.Length >= 2 ? name[..2].ToUpperInvariant() : name.ToUpperInvariant(),
                ["iconColor"] = "#6F8AA8",
            });
            report.Converted.Add($"File type: {name} ({string.Join(", ", exts.Select(e => "." + e))})");
        }

        // Bundled TextMate grammars (JSON form).
        foreach (var (path, bytes) in files.Where(f => f.Key.EndsWith(".tmLanguage.json", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var grammar = JsonNode.Parse(bytes);
                var scope = grammar?["scopeName"]?.GetValue<string>();
                if (scope is null) continue;
                var fileName = Path.GetFileName(path);
                File.WriteAllBytes(Path.Combine(dir, fileName), bytes);
                var exts = (grammar?["fileTypes"] as JsonArray)?.Select(e => (JsonNode)("." + e!.GetValue<string>().TrimStart('.'))).ToArray() ?? [];
                var langName = grammar?["name"]?.GetValue<string>() ?? scope;
                var existing = Arr("languages").OfType<JsonObject>().FirstOrDefault(l => (l["extensions"] as JsonArray)?.Any(e => exts.Any(x => x!.GetValue<string>() == e!.GetValue<string>())) == true);
                if (existing is not null) existing["grammar"] = fileName;
                else Arr("languages").Add(new JsonObject { ["id"] = scope.Split('.').Last(), ["name"] = langName, ["extensions"] = new JsonArray(exts), ["grammar"] = fileName });
                report.Converted.Add($"TextMate grammar: {langName}");
            }
            catch
            {
                report.Skipped.Add($"Unreadable grammar: {path}");
            }
        }
        foreach (var p in files.Keys.Where(k => k.EndsWith(".tmLanguage", StringComparison.OrdinalIgnoreCase) || k.EndsWith(".plist", StringComparison.OrdinalIgnoreCase)))
            report.Skipped.Add($"TextMate grammar in plist format (convert to .tmLanguage.json to use): {p}");

        // Live templates → snippets.
        foreach (var (path, bytes) in files.Where(f => f.Key.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
        {
            XDocument doc;
            try { doc = XDocument.Parse(Encoding.UTF8.GetString(bytes)); }
            catch { continue; }
            if (doc.Root?.Name.LocalName == "templateSet")
            {
                foreach (var t in doc.Root.Elements("template"))
                {
                    var snippet = ConvertTemplate(t);
                    if (snippet is null) continue;
                    Arr("snippets").Add(snippet);
                }
                report.Converted.Add($"Live templates: {doc.Root.Attribute("group")?.Value ?? Path.GetFileName(path)} ({doc.Root.Elements("template").Count()})");
            }
            else if (doc.Root?.Name.LocalName == "keymap")
            {
                var (bindings, unmapped) = ConvertKeymap(doc.Root);
                foreach (var b in bindings) Arr("keybindings").Add(b);
                report.Converted.Add($"Keymap \"{doc.Root.Attribute("name")?.Value}\": {bindings.Count} shortcuts");
                if (unmapped > 0) report.Skipped.Add($"{unmapped} keymap actions have no Aether equivalent");
            }
            else if (doc.Root?.Name.LocalName == "scheme")
            {
                var themeId = $"{id}-{Regex.Replace((doc.Root.Attribute("name")?.Value ?? "scheme").ToLowerInvariant(), "[^a-z0-9]+", "-")}";
                var theme = ConvertColorScheme(doc.Root);
                File.WriteAllText(Path.Combine(dir, themeId + ".json"), theme.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                Arr("themes").Add(new JsonObject { ["id"] = themeId, ["name"] = theme["name"]!.GetValue<string>(), ["path"] = themeId + ".json" });
                report.Converted.Add($"Color scheme: {theme["name"]}");
            }
        }
        foreach (var (path, bytes) in files.Where(f => f.Key.EndsWith(".icls", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var scheme = XDocument.Parse(Encoding.UTF8.GetString(bytes)).Root!;
                var themeId = $"{id}-{Regex.Replace(Path.GetFileNameWithoutExtension(path).ToLowerInvariant(), "[^a-z0-9]+", "-")}";
                var theme = ConvertColorScheme(scheme);
                File.WriteAllText(Path.Combine(dir, themeId + ".json"), theme.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                Arr("themes").Add(new JsonObject { ["id"] = themeId, ["name"] = theme["name"]!.GetValue<string>(), ["path"] = themeId + ".json" });
                report.Converted.Add($"Color scheme: {theme["name"]}");
            }
            catch
            {
                report.Skipped.Add($"Unreadable color scheme: {path}");
            }
        }

        // File templates (Velocity) → file templates.
        foreach (var (path, bytes) in files.Where(f => f.Key.Contains("fileTemplates/", StringComparison.OrdinalIgnoreCase) && f.Key.EndsWith(".ft", StringComparison.OrdinalIgnoreCase)))
        {
            var file = Path.GetFileName(path)[..^3]; // "Java Class.java"
            var ext = Path.GetExtension(file);
            var body = string.Join("\n", Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n").Split('\n')
                .Where(l => !Regex.IsMatch(l.TrimStart(), @"^#(parse|if|end|set|else|elseif|foreach)\b"))
                .Select(l => l.Replace("${NAME}", "${name}").Replace("${PACKAGE_NAME}", "").Replace("${USER}", "${env:USERNAME}")));
            Arr("fileTemplates").Add(new JsonObject { ["name"] = Path.GetFileNameWithoutExtension(file), ["extension"] = ext, ["body"] = body.TrimStart() });
            report.Converted.Add($"File template: {Path.GetFileNameWithoutExtension(file)}");
        }

        // Everything implemented in code: report what the plugin relied on.
        var codeExtensions = root.Descendants()
            .Where(e => e.Parent?.Name.LocalName == "extensions" && e.Name.LocalName is not ("fileType" or "defaultLiveTemplates" or "bundledColorScheme" or "bundledKeymap" or "internalFileTemplate" or "liveTemplateContext"))
            .GroupBy(e => e.Name.LocalName)
            .Select(g => $"{g.Key} ×{g.Count()}")
            .ToList();
        if (codeExtensions.Count > 0)
            report.Skipped.Add("Code-based IntelliJ extensions (need the JVM/IntelliJ APIs, not convertible): " + string.Join(", ", codeExtensions));
        var actions = root.Descendants().Count(e => e.Name.LocalName == "action");
        if (actions > 0) report.Skipped.Add($"{actions} IntelliJ actions implemented in code");

        var manifest = new JsonObject
        {
            ["id"] = id,
            ["name"] = report.Name + " (converted)",
            ["version"] = El("version") ?? "0.0.0",
            ["description"] = Regex.Replace(El("description") ?? "Converted from an IntelliJ plugin.", "<[^>]+>", " ").Trim(),
            ["author"] = El("vendor") ?? "",
            ["contributes"] = contributes,
        };
        File.WriteAllText(Path.Combine(dir, "plugin.json"), manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        if (report.Converted.Count == 0) report.Skipped.Insert(0, "Nothing declarative to convert: this plugin is implemented entirely in code.");
        return report;
    }

    // ---- Live templates ------------------------------------------------------------

    private static readonly (string Prefix, string Language)[] ContextLanguages =
    {
        ("JAVA", "java"), ("KOTLIN", "kotlin"), ("RUST", "rust"), ("OC_", "cpp"), ("C_", "c"), ("CPP", "cpp"), ("CSHARP", "csharp"), ("RIDER", "csharp"),
        ("PYTHON", "python"), ("Python", "python"), ("JAVA_SCRIPT", "javascript"), ("JS_", "javascript"), ("TypeScript", "typescript"), ("TS_", "typescript"),
        ("GO", "go"), ("HTML", "html"), ("CSS", "css"), ("SQL", "sql"), ("XML", "xml"), ("PHP", "php"), ("RUBY", "ruby"), ("SHELL", "shellscript"),
    };

    private static JsonObject? ConvertTemplate(XElement t)
    {
        var name = (string?)t.Attribute("name");
        var value = (string?)t.Attribute("value");
        if (name is null || value is null) return null;
        var variables = t.Elements("variable").Select(v => ((string?)v.Attribute("name") ?? "", ((string?)v.Attribute("defaultValue") ?? "").Trim('"'))).ToList();
        var index = new Dictionary<string, int>();
        foreach (var (vn, _) in variables) if (vn.Length > 0 && !index.ContainsKey(vn)) index[vn] = index.Count + 1;

        var body = Regex.Replace(value.Replace("$$", "\u0001"), @"\$([A-Za-z_][A-Za-z0-9_]*)\$", m =>
        {
            var v = m.Groups[1].Value;
            if (v == "END") return "$0";
            if (v == "SELECTION") return "${TM_SELECTED_TEXT}";
            if (!index.TryGetValue(v, out var n)) index[v] = n = index.Count + 1;
            var def = variables.FirstOrDefault(x => x.Item1 == v).Item2;
            return string.IsNullOrEmpty(def) ? $"${{{n}:{v}}}" : $"${{{n}:{def}}}";
        }).Replace("\u0001", "\\$");

        var options = t.Element("context")?.Elements("option").Where(o => (string?)o.Attribute("value") == "true").Select(o => (string?)o.Attribute("name") ?? "").ToList() ?? new();
        var language = options.Select(o => ContextLanguages.FirstOrDefault(c => o.StartsWith(c.Prefix, StringComparison.Ordinal)).Language).FirstOrDefault(l => l is not null) ?? "*";
        return new JsonObject
        {
            ["language"] = language,
            ["prefix"] = name,
            ["body"] = body.Replace("&#10;", "\n"),
            ["description"] = (string?)t.Attribute("description") ?? name,
        };
    }

    // ---- Keymaps --------------------------------------------------------------------

    private static readonly Dictionary<string, string> ActionMap = new(StringComparer.Ordinal)
    {
        ["GotoDeclaration"] = "gotoDeclaration", ["ReformatCode"] = "reformat", ["RenameElement"] = "rename", ["FindUsages"] = "findUsages",
        ["SearchEverywhere"] = "searchEverywhere", ["GotoFile"] = "gotoFile", ["GotoAction"] = "findAction", ["GotoSymbol"] = "gotoSymbol",
        ["GotoClass"] = "gotoClass", ["RecentFiles"] = "recentFiles", ["FileStructurePopup"] = "fileStructure", ["GotoLine"] = "gotoLine",
        ["ShowIntentionActions"] = "contextActions", ["CommentByLineComment"] = "comment", ["EditorDuplicate"] = "duplicate",
        ["EditorDeleteLine"] = "deleteLine", ["QuickJavaDoc"] = "quickDoc", ["ParameterInfo"] = "paramInfo", ["CodeCompletion"] = "complete",
        ["Run"] = "run", ["Stop"] = "stop", ["CompileDirty"] = "build", ["SaveAll"] = "saveAll", ["CloseContent"] = "closeTab",
        ["ShowSettings"] = "settings", ["FindInPath"] = "findInFiles", ["Back"] = "back", ["Forward"] = "forward",
        ["GotoNextError"] = "nextProblem", ["GotoPreviousError"] = "prevProblem", ["ActivateProjectToolWindow"] = "viewProject",
        ["ActivateTerminalToolWindow"] = "viewTerminal", ["SurroundWith"] = "surroundWith", ["CheckinProject"] = "commit", ["Vcs.Push"] = "push",
        ["$Undo"] = "undo", ["$Redo"] = "redo", ["Find"] = "find", ["Replace"] = "replace", ["NextTab"] = "nextTab", ["PreviousTab"] = "prevTab",
    };

    private static (List<JsonObject> Bindings, int Unmapped) ConvertKeymap(XElement keymap)
    {
        var bindings = new List<JsonObject>();
        var unmapped = 0;
        foreach (var action in keymap.Elements("action"))
        {
            var ijAction = (string?)action.Attribute("id") ?? "";
            var shortcut = action.Elements("keyboard-shortcut").FirstOrDefault();
            if (shortcut is null) continue;
            if (!ActionMap.TryGetValue(ijAction, out var ours)) { unmapped++; continue; }
            var keys = Stroke((string?)shortcut.Attribute("first-keystroke"));
            if ((string?)shortcut.Attribute("second-keystroke") is { } second) keys += " " + Stroke(second);
            bindings.Add(new JsonObject { ["action"] = ours, ["keys"] = keys });
        }
        return (bindings, unmapped);
    }

    private static string Stroke(string? ij) => string.Join('+', (ij ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(p => p.ToLowerInvariant() switch
    {
        "control" or "ctrl" => "Ctrl",
        "alt" => "Alt",
        "shift" => "Shift",
        "meta" => "Meta",
        "enter" => "Enter",
        "space" => "Space",
        "slash" => "/",
        "equals" => "=",
        "minus" => "-",
        "back_space" => "Backspace",
        "delete" => "Delete",
        "back_quote" => "`",
        "period" => ".",
        "comma" => ",",
        _ => p.Length == 1 ? p.ToUpperInvariant() : char.ToUpperInvariant(p[0]) + p[1..].ToLowerInvariant(),
    }));

    // ---- Color schemes ---------------------------------------------------------------

    private static readonly (string Attribute, string[] Scopes)[] TokenMap =
    {
        ("DEFAULT_KEYWORD", ["keyword", "storage", "storage.type", "storage.modifier", "constant.language"]),
        ("DEFAULT_STRING", ["string", "punctuation.definition.string"]),
        ("DEFAULT_NUMBER", ["constant.numeric"]),
        ("DEFAULT_LINE_COMMENT", ["comment", "punctuation.definition.comment"]),
        ("DEFAULT_BLOCK_COMMENT", ["comment.block"]),
        ("DEFAULT_DOC_COMMENT", ["comment.block.documentation"]),
        ("DEFAULT_FUNCTION_DECLARATION", ["entity.name.function"]),
        ("DEFAULT_FUNCTION_CALL", ["meta.function-call entity.name.function", "support.function"]),
        ("DEFAULT_CLASS_NAME", ["entity.name.type", "entity.name.class", "support.class", "support.type"]),
        ("DEFAULT_INSTANCE_FIELD", ["variable.other.member", "variable.other.property"]),
        ("DEFAULT_CONSTANT", ["variable.other.constant", "entity.name.constant"]),
        ("DEFAULT_METADATA", ["meta.attribute", "meta.annotation", "storage.type.annotation", "meta.preprocessor"]),
        ("DEFAULT_VALID_STRING_ESCAPE", ["constant.character.escape"]),
        ("DEFAULT_PARAMETER", ["variable.parameter"]),
        ("DEFAULT_LOCAL_VARIABLE", ["variable.other"]),
    };

    private static JsonObject ConvertColorScheme(XElement scheme)
    {
        string? Color(string name) => scheme.Element("colors")?.Elements("option").FirstOrDefault(o => (string?)o.Attribute("name") == name)?.Attribute("value")?.Value;
        XElement? Attr(string name) => scheme.Element("attributes")?.Elements("option").FirstOrDefault(o => (string?)o.Attribute("name") == name)?.Element("value");
        string? AttrOpt(XElement? v, string name) => v?.Elements("option").FirstOrDefault(o => (string?)o.Attribute("name") == name)?.Attribute("value")?.Value;
        static string? Hex(string? v) => string.IsNullOrEmpty(v) ? null : "#" + v.PadLeft(6, '0');

        var name = (string?)scheme.Attribute("name") ?? "Converted scheme";
        var text = Attr("TEXT");
        var bg = Hex(AttrOpt(text, "BACKGROUND"));
        var fg = Hex(AttrOpt(text, "FOREGROUND"));
        var parent = (string?)scheme.Attribute("parent_scheme") ?? "";
        var isDark = parent.Contains("Darcula", StringComparison.OrdinalIgnoreCase) || (bg is not null && Avalonia.Media.Color.TryParse(bg, out var c) && (c.R + c.G + c.B) / 3 < 128);

        var tokens = new JsonArray(new JsonObject { ["settings"] = new JsonObject { ["foreground"] = fg ?? (isDark ? "#A9B7C6" : "#080808"), ["background"] = bg ?? (isDark ? "#2B2B2B" : "#FFFFFF") } });
        foreach (var (attribute, scopes) in TokenMap)
        {
            var value = Attr(attribute);
            if (Hex(AttrOpt(value, "FOREGROUND")) is not { } color) continue;
            var settings = new JsonObject { ["foreground"] = color };
            if (AttrOpt(value, "FONT_TYPE") is { } font) settings["fontStyle"] = font switch { "1" => "bold", "2" => "italic", "3" => "bold italic", _ => "" };
            tokens.Add(new JsonObject { ["scope"] = new JsonArray(scopes.Select(s => (JsonNode)s).ToArray()), ["settings"] = settings });
        }

        var editor = new JsonObject();
        void Put(string key, string? value) { if (value is not null) editor[key] = value; }
        Put("foreground", fg);
        Put("currentLine", Hex(Color("CARET_ROW_COLOR")));
        Put("selection", Hex(Color("SELECTION_BACKGROUND")));
        Put("caret", Hex(Color("CARET_COLOR")));
        Put("lineNumber", Hex(Color("LINE_NUMBERS_COLOR")));
        Put("lineNumberActive", Hex(Color("LINE_NUMBER_ON_CARET_ROW_COLOR")));
        Put("indentGuide", Hex(Color("INDENT_GUIDE")));
        Put("whitespace", Hex(Color("WHITESPACES")));
        var ui = new JsonObject();
        if (bg is not null) { ui["EditorBg"] = bg; ui["InputBg"] = bg; }
        if (fg is not null) ui["FgEditor"] = fg;
        return new JsonObject
        {
            ["name"] = name,
            ["type"] = isDark ? "dark" : "light",
            ["ui"] = ui,
            ["editor"] = editor,
            ["tokenColors"] = tokens,
        };
    }
}
