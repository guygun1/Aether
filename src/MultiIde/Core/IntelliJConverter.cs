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
        /// <summary>A bundled Aether plugin that natively replaces this IntelliJ plugin (enable it).</summary>
        public NativeReplacement? Native { get; set; }
        /// <summary>What the plugin's code-based extensions map to in Aether, grouped.</summary>
        public List<Section> Sections { get; } = new();
        public List<string> Notes { get; } = new();
    }

    /// <summary>A group of IntelliJ extension points. Status: "native", "covered", "notNeeded" or "missing".</summary>
    public sealed record Section(string Title, string Explanation, string Status, List<string> Items);

    public sealed record NativeReplacement(string PluginId, string Name, string Covers);

    /// <summary>IntelliJ plugins whose features Aether provides natively (bundled plugin id) or through a catalog language.</summary>
    private static readonly (string IntelliJId, string? Bundled, string? Catalog, string Name, string Covers)[] KnownPlugins =
    {
        ("com.demonwav.minecraft-dev", "aether.minecraft", null, "Minecraft Modding",
            "New Project wizards for Paper, Velocity, Fabric, NeoForge and Forge with live Minecraft/loader version pickers, Gradle run tasks (runClient, runServer, genSources …), Mixin / listener / command templates."),
        ("org.rust.lang", "rust", null, "Rust", "rust-analyzer, Cargo run configurations and dependencies, project templates."),
        ("com.jetbrains.rust", "rust", null, "Rust", "rust-analyzer, Cargo run configurations and dependencies, project templates."),
        ("com.intellij.java", "aether.java", null, "Java", "Eclipse JDT language server, Gradle/Maven run configurations and dependencies, templates."),
        ("com.intellij.cidr.lang", "cpp", null, "C / C++", "clangd, CMake integration, templates."),
        ("com.intellij.clion", "cpp", null, "C / C++", "clangd, CMake integration, templates."),
        ("org.jetbrains.plugins.cmake", "cmake", null, "CMake", "CMake language server and run configurations."),
        ("com.intellij.modules.rider", "csharp", null, "C# / .NET", "csharp-ls, dotnet run configurations, NuGet."),
        ("com.jetbrains.plugins.glsl", "graphics", null, "Graphics & Shaders", "GLSL/WGSL highlighting and glsl_analyzer."),
        ("org.jetbrains.kotlin", null, "kotlin", "Kotlin", "Kotlin language server."),
        ("PythonCore", null, "python", "Python", "Pyright."),
        ("Pythonid", null, "python", "Python", "Pyright."),
        ("org.jetbrains.plugins.go", null, "go", "Go", "gopls."),
        ("Dart", null, "dart", "Dart", "Dart analysis server."),
        ("org.jetbrains.plugins.ruby", null, "ruby", "Ruby", "Ruby LSP."),
        ("com.jetbrains.php", null, "php", "PHP", "Intelephense."),
        ("com.jetbrains.sh", null, "shellscript", "Shell", "Bash language server."),
        ("org.jetbrains.plugins.yaml", null, "yaml", "YAML", "YAML language server."),
        ("org.intellij.plugins.markdown", null, "markdown", "Markdown", "Marksman."),
        ("com.tang", null, "lua", "Lua", "LuaLS."),
        ("com.falsepattern.zigbrains", null, "zig", "Zig", "ZLS."),
        ("JavaScript", null, "javascript", "JavaScript", "TypeScript language server."),
    };

    // ---- What IntelliJ extension points become in Aether ----------------------------------

    private static readonly (string Status, string Title, string Explanation, string[] Patterns)[] Categories =
    {
        ("partial", "Language features (completion, inspections, navigation …)",
            "In Aether these come from the language's server (LSP), so the language's general features work (e.g. Java's server for Java files). This plugin's own extra completions, inspections and navigation are its code and aren't available.",
            ["completion", "annotator", "inspection", "Inspection", "gotoDeclarationHandler", "referencesSearch", "findUsages", "rename", "refactoring",
             "parameterNameHints", "parameterInfo", "documentationProvider", "structureView", "StructureView", "gotoSymbolContributor", "gotoClassContributor",
             "referenceContributor", "referenceProvider", "intentionAction", "QuickFix", "quickFix", "importOptimizer", "superMethodsSearch", "implicitUsageProvider",
             "lang.formatter", "formattingService", "inlay", "Inlay", "typeInfo", "codeVision", "CodeVision", "lineMarkerProvider", "highlightUsages",
             "highlightingPass", "namesValidator", "definitionsSearch", "methodReferencesSearch", "targetElementEvaluator", "elementDescriptionProvider",
             "unscrambleSupport", "missingLVTChecker", "injectionPoint", "codeInsight", "daemon", "localInspection", "globalInspection", "lang.unwrapDescriptor",
             "readWriteAccessDetector", "usageTypeProvider", "lang.smartEnterProcessor", "statementUpDownMover", "lang.surroundDescriptor"]),
        ("missing", "Custom editors and banners",
            "Special editors (e.g. a binary file viewer) and editor banners are plugin code.",
            ["fileEditorProvider", "editorNotificationProvider", "fileTypeOverrider"]),
        ("covered", "Built into the Aether editor",
            "Brace matching, commenting, folding, quote pairing, colour schemes, file icons and file templates are editor features configured by plugin data.",
            ["braceMatcher", "commenter", "foldingBuilder", "codeFoldingOptionsProvider", "quoteHandler", "typedHandler", "enterHandlerDelegate",
             "backspaceHandlerDelegate", "colorSettingsPage", "additionalTextAttributes", "spellchecker",
             "fileIconProvider", "iconProvider", "iconLayerProvider", "fileTypeDetector", "fileType", "defaultLiveTemplates", "liveTemplateContext",
             "liveTemplateMacro", "fileTemplateGroup", "internalFileTemplate", "lang.fileViewProviderFactory",
             "elementManipulator", "multiHostInjector", "languageInjector", "lang.ast.factory", "stubElementTypeHolder", "lang.psiStructureViewFactory"]),
        ("highlighting", "Syntax highlighting",
            "Aether highlights with TextMate grammars. IntelliJ lexers/parsers are code, so a language from this plugin without a bundled grammar opens as plain text.",
            ["syntaxHighlighter", "parserDefinition"]),
        ("covered", "Run configurations",
            "Aether plugins declare run configurations as commands (e.g. \"gradlew runClient\") that appear when the project matches.",
            ["runConfigurationProducer", "configurationType", "programRunner", "runConfigurationExtension", "executor", "runLineMarkerContributor",
             "consoleFilterProvider", "stacktrace"]),
        ("notNeeded", "IntelliJ infrastructure (not needed in Aether)",
            "Services, startup activities, indexes, settings pages, notifications and project-model hooks belong to IntelliJ's architecture; Aether doesn't need them.",
            ["Service", "service", "Activity", "activity", "notificationGroup", "errorHandler", "registryKey", "Configurable", "configurable", "statistics",
             "iconMapper", "dependencySupport", "facetType", "moduleType", "projectResolve", "externalProjectDataService", "ExternalSystem", "externalSystem",
             "fileBasedIndex", "stubIndex", "weigher", "ScopeEnlarger", "Listener", "listener", "themeMetadataProvider", "library.presentationProvider",
             "attachSourcesProvider", "importFilter", "externalAnnotationsArtifactsResolver", "java.externalAnnotation", "projectViewNodeDecorator", "treeStructureProvider",
             "applicationInitializedListener", "postStartupActivity", "backgroundPostStartupActivity", "titleInfoProvider", "search.optionContributor",
             "localInspection.suppressor", "inspectionSuppressor", "lang.inspectionSuppressor", "deadCode", "cantBeStatic", "vetoRenameCondition"]),
        ("native", "Project wizards",
            "IntelliJ wizard steps are code. Aether templates ask the same questions through template parameters (versions loaded live, ids, groups).",
            ["Wizard", "wizard", "moduleBuilder", "creator", "Creator", "projectSetupFinalizer", "buildSystem", "projectTemplate", "directoryProjectGenerator",
             "gradleFileType", "newProjectWizard"]),
        ("missing", "Debugger extensions", "Aether has no debugger yet (planned: Debug Adapter Protocol).", ["debugger", "xdebugger"]),
    };

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "nbt", "dat", "mca", "mcr", "schematic", "schem", "litematic", "class", "jar", "png", "jpg", "gif", "ico", "zip", "bin", "exe", "dll", "so", "dylib",
    };

    private static (string Status, string Title, string Explanation) Classify(string extensionPoint)
    {
        foreach (var c in Categories)
            if (c.Patterns.Any(p => extensionPoint.Contains(p, StringComparison.Ordinal)))
                return (c.Status, c.Title, c.Explanation);
        return ("missing", "Plugin-specific features without an Aether equivalent",
            "These are implemented in the plugin's Java/Kotlin code against IntelliJ APIs and can't run outside IntelliJ.");
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
            // Binary formats (NBT, region files …) would show as garbage in a text editor.
            if (exts.Any(e => BinaryExtensions.Contains(e.TrimStart('.'))))
            {
                report.Notes.Add($"File type {name} ({string.Join(", ", exts.Select(e => "." + e))}) is binary; Aether doesn't open it as text.");
                continue;
            }
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

        // TextMate bundles in VS Code extension form (package.json with contributes.languages/grammars/snippets).
        var handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, bytes) in files.Where(f => f.Key.EndsWith("package.json", StringComparison.OrdinalIgnoreCase)).ToList())
            ConvertVsCodeBundle(path, bytes, files, dir, Arr, report, handled);

        // Loose TextMate grammars: JSON, or plist (.tmLanguage / .plist) converted to JSON.
        foreach (var (path, bytes) in files.Where(f => !handled.Contains(f.Key) && IsGrammarFile(f.Key)))
        {
            try
            {
                var grammar = ReadGrammar(path, bytes);
                var scope = grammar?["scopeName"]?.GetValue<string>();
                if (grammar is null || scope is null) continue;
                var fileName = GrammarFileName(path);
                File.WriteAllText(Path.Combine(dir, fileName), grammar.ToJsonString());
                var exts = (grammar["fileTypes"] as JsonArray)?.Select(e => (JsonNode)("." + e!.GetValue<string>().TrimStart('.'))).ToArray() ?? [];
                var langName = grammar["name"]?.GetValue<string>() ?? scope;
                var existing = Arr("languages").OfType<JsonObject>().FirstOrDefault(l => (l["extensions"] as JsonArray)?.Any(e => exts.Any(x => x!.GetValue<string>() == e!.GetValue<string>())) == true);
                if (existing is not null) existing["grammar"] = fileName;
                else Arr("languages").Add(new JsonObject { ["id"] = scope.Split('.').Last(), ["name"] = langName, ["extensions"] = new JsonArray(exts), ["grammar"] = fileName });
                report.Converted.Add($"TextMate grammar: {langName}");
            }
            catch
            {
                report.Notes.Add($"Unreadable grammar: {path}");
            }
        }

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
                if (unmapped > 0) report.Notes.Add($"{unmapped} keymap actions have no Aether equivalent");
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
                report.Notes.Add($"Unreadable color scheme: {path}");
            }
        }

        // File templates (Velocity) → file templates. Only the ones IntelliJ shows under New (fileTemplates/ and
        // fileTemplates/internal/); j2ee/, code/ and includes/ are used by the plugin's own code (wizards, intentions).
        var allFileTemplates = files.Keys.Where(k => k.Contains("fileTemplates/", StringComparison.OrdinalIgnoreCase) && k.EndsWith(".ft", StringComparison.OrdinalIgnoreCase)).ToList();
        var newFileTemplates = allFileTemplates.Where(k => Regex.IsMatch(k, @"(^|/)fileTemplates/(internal/)?[^/]+$", RegexOptions.IgnoreCase)).ToHashSet();
        if (allFileTemplates.Count > newFileTemplates.Count)
            report.Notes.Add($"{allFileTemplates.Count - newFileTemplates.Count} file templates are used by the plugin's code (wizards, intentions), not under New; not converted.");
        foreach (var (path, bytes) in files.Where(f => newFileTemplates.Contains(f.Key)))
        {
            var file = Path.GetFileName(path)[..^3]; // "Java Class.java"
            var ext = Path.GetExtension(file);
            var body = string.Join("\n", Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n").Split('\n')
                .Where(l => !Regex.IsMatch(l.TrimStart(), @"^#(parse|if|end|set|else|elseif|foreach)\b"))
                .Select(l => l.Replace("${NAME}", "${name}").Replace("${PACKAGE_NAME}", "").Replace("${USER}", "${env:USERNAME}")));
            Arr("fileTemplates").Add(new JsonObject { ["name"] = Path.GetFileNameWithoutExtension(file), ["extension"] = ext, ["body"] = body.TrimStart() });
            report.Converted.Add($"File template: {Path.GetFileNameWithoutExtension(file)}");
        }

        // Known plugins: Aether has a native equivalent (a bundled plugin, or a catalog language with its server).
        var known = KnownPlugins.FirstOrDefault(k => string.Equals(k.IntelliJId, ijId, StringComparison.OrdinalIgnoreCase));
        if (known.IntelliJId is not null)
        {
            if (known.Bundled is not null) report.Native = new NativeReplacement(known.Bundled, known.Name, known.Covers);
            else if (known.Catalog is not null && LanguageCatalog.All.FirstOrDefault(l => l.Id == known.Catalog) is { } cat)
            {
                LanguageCatalog.CreatePlugin(cat);
                report.Native = new NativeReplacement($"lang-{cat.Id}", $"{cat.Name} Language Support", known.Covers);
            }
        }

        // Converted languages get the standard language server for their file extensions, when Aether knows one.
        foreach (var lang in Arr("languages").OfType<JsonObject>().ToList())
        {
            var exts = (lang["extensions"] as JsonArray)?.Select(e => e?.GetValue<string>()).OfType<string>().ToList() ?? new();
            if (LanguageCatalog.All.FirstOrDefault(c => c.Command is not null && c.Extensions.Intersect(exts, StringComparer.OrdinalIgnoreCase).Any()) is not { } cat) continue;
            var langId = lang["id"]!.GetValue<string>();
            if (cat.LineComment is not null) lang["lineComment"] ??= cat.LineComment;
            var server = new JsonObject
            {
                ["id"] = $"{id}-{cat.Id}-ls",
                ["name"] = cat.ServerName ?? cat.Command,
                ["command"] = cat.Command,
                ["args"] = new JsonArray(cat.Args.Select(a => (JsonNode)a).ToArray()),
                ["languages"] = new JsonArray((JsonNode)langId),
            };
            if (cat.Install is not null) server["install"] = cat.Install;
            if (cat.InstallUrl is not null) server["installUrl"] = cat.InstallUrl;
            Arr("lspServers").Add(server);
            report.Converted.Add($"Language server: {cat.ServerName} for {lang["name"]} (provides completion, navigation, inspections …)");
        }

        // Everything implemented in code: what each extension point becomes in Aether.
        var extensionPoints = root.Descendants()
            .Where(e => e.Parent?.Name.LocalName == "extensions" && e.Name.LocalName is not ("fileType" or "defaultLiveTemplates" or "bundledColorScheme" or "bundledKeymap"))
            .GroupBy(e => e.Name.LocalName)
            .Select(g => (Name: g.Key, Count: g.Count()));
        var withoutGrammar = Arr("languages").OfType<JsonObject>().Where(l => l["grammar"] is null && l["scopeName"] is null).Select(l => l["name"]?.GetValue<string>()).ToList();
        foreach (var group in extensionPoints.GroupBy(ep => Classify(ep.Name)).OrderBy(g => g.Key.Status switch { "native" => 0, "covered" => 1, "highlighting" => 2, "partial" => 3, "notNeeded" => 4, _ => 5 }))
        {
            var (status, title, explanation) = group.Key;
            // With a native replacement, wizards are covered by its templates.
            if (status == "native" && report.Native is null) status = "missing";
            if (status == "highlighting")
            {
                status = withoutGrammar.Count == 0 ? "covered" : "partial";
                if (withoutGrammar.Count > 0) explanation += $" Without a grammar: {string.Join(", ", withoutGrammar)}.";
            }
            report.Sections.Add(new Section(title, explanation, status, group.Select(ep => ep.Count > 1 ? $"{ep.Name} ×{ep.Count}" : ep.Name).ToList()));
        }
        var actions = root.Descendants().Count(e => e.Name.LocalName == "action");
        if (actions > 0)
            report.Sections.Add(new Section("Menu actions",
                "IntelliJ actions are code. Aether plugins add actions as commands (a command line with an optional shortcut); you can add your own in Settings → Keymap → My Commands.",
                report.Native is null ? "missing" : "partial", [$"{actions} actions"]));

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
        if (report.Converted.Count == 0 && report.Native is null)
            report.Notes.Insert(0, "Nothing declarative to convert: this plugin is implemented entirely in code.");
        return report;
    }

    // ---- TextMate bundles ------------------------------------------------------------

    private static bool IsGrammarFile(string path) =>
        path.EndsWith(".tmLanguage.json", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".tmLanguage", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".plist", StringComparison.OrdinalIgnoreCase);

    private static string GrammarFileName(string path)
    {
        var name = Path.GetFileName(path);
        foreach (var ext in new[] { ".tmLanguage.json", ".tmLanguage", ".plist", ".json" })
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return name[..^ext.Length] + ".tmLanguage.json";
        return name + ".tmLanguage.json";
    }

    /// <summary>Reads a TextMate grammar in JSON or plist (XML) form as JSON.</summary>
    private static JsonObject? ReadGrammar(string path, byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes).TrimStart('﻿');
        if (text.TrimStart().StartsWith('{')) return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject;
        var plist = XDocument.Parse(text, LoadOptions.None).Root;
        return plist?.Elements().FirstOrDefault() is { } top ? PlistToJson(top) as JsonObject : null;
    }

    private static JsonNode? PlistToJson(XElement e) => e.Name.LocalName switch
    {
        "dict" => PlistDict(e),
        "array" => new JsonArray(e.Elements().Select(PlistToJson).ToArray()),
        "string" => JsonValue.Create(e.Value),
        "integer" => long.TryParse(e.Value, out var l) ? JsonValue.Create(l) : JsonValue.Create(e.Value),
        "real" => double.TryParse(e.Value, System.Globalization.CultureInfo.InvariantCulture, out var d) ? JsonValue.Create(d) : JsonValue.Create(e.Value),
        "true" => JsonValue.Create(true),
        "false" => JsonValue.Create(false),
        _ => JsonValue.Create(e.Value),
    };

    private static JsonObject PlistDict(XElement dict)
    {
        var obj = new JsonObject();
        var items = dict.Elements().ToList();
        for (var i = 0; i + 1 < items.Count; i += 2)
            if (items[i].Name.LocalName == "key") obj[items[i].Value] = PlistToJson(items[i + 1]);
        return obj;
    }

    /// <summary>Converts a VS Code-style TextMate bundle (many IntelliJ plugins ship one for highlighting).</summary>
    private static void ConvertVsCodeBundle(string packagePath, byte[] bytes, Dictionary<string, byte[]> files, string dir,
        Func<string, JsonArray> arr, Report report, HashSet<string> handled)
    {
        JsonNode? package;
        try { package = JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch { return; }
        if (package?["contributes"] is not JsonObject contributes) return;
        var baseDir = packagePath.Contains('/') ? packagePath[..(packagePath.LastIndexOf('/') + 1)] : "";
        string Resolve(string relative) => baseDir + relative.Replace('\\', '/').TrimStart('.', '/');
        byte[]? Read(string relative) => files.GetValueOrDefault(Resolve(relative));
        static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
        var jsonOptions = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

        // Grammars by language id.
        var grammars = new Dictionary<string, string>();
        foreach (var g in contributes["grammars"] as JsonArray ?? new JsonArray())
        {
            var relative = Str(g?["path"]);
            if (relative.Length == 0 || Read(relative) is not { } grammarBytes) continue;
            try
            {
                var grammar = ReadGrammar(relative, grammarBytes);
                if (grammar is null) continue;
                var fileName = GrammarFileName(relative);
                File.WriteAllText(Path.Combine(dir, fileName), grammar.ToJsonString());
                handled.Add(Resolve(relative));
                if (Str(g?["language"]) is { Length: > 0 } lang) grammars[lang] = fileName;
            }
            catch { report.Notes.Add($"Unreadable grammar: {relative}"); }
        }

        foreach (var l in contributes["languages"] as JsonArray ?? new JsonArray())
        {
            var langId = Str(l?["id"]);
            var exts = (l?["extensions"] as JsonArray)?.Select(Str).Where(s => s.Length > 0).ToList() ?? new();
            var names = (l?["filenames"] as JsonArray)?.Select(Str).Where(s => s.Length > 0).ToList() ?? new();
            if (langId.Length == 0 || exts.Count + names.Count == 0) continue;
            var name = (l?["aliases"] as JsonArray)?.Select(Str).FirstOrDefault(s => s.Length > 0) ?? langId;
            var language = new JsonObject
            {
                ["id"] = langId,
                ["name"] = name,
                ["extensions"] = new JsonArray(exts.Select(e => (JsonNode)e).ToArray()),
                ["filenames"] = new JsonArray(names.Select(e => (JsonNode)e).ToArray()),
                ["icon"] = name.Length >= 2 ? name[..2].ToUpperInvariant() : name.ToUpperInvariant(),
                ["iconColor"] = "#6F8AA8",
            };
            if (grammars.TryGetValue(langId, out var grammarFile)) language["grammar"] = grammarFile;
            if (Str(l?["configuration"]) is { Length: > 0 } configPath && Read(configPath) is { } configBytes)
            {
                try
                {
                    var config = JsonNode.Parse(configBytes, documentOptions: jsonOptions);
                    if (Str(config?["comments"]?["lineComment"]) is { Length: > 0 } lc) language["lineComment"] = lc;
                    if (config?["comments"]?["blockComment"] is JsonArray bc && bc.Count == 2)
                        language["blockComment"] = new JsonArray(JsonValue.Create(Str(bc[0])), JsonValue.Create(Str(bc[1])));
                }
                catch { /* comments are optional */ }
            }
            // A file type from plugin.xml with the same extensions is replaced by the richer bundle language.
            foreach (var old in arr("languages").OfType<JsonObject>().Where(o => (o["extensions"] as JsonArray)?.Any(e => exts.Contains(Str(e))) == true).ToList())
                arr("languages").Remove(old);
            arr("languages").Add(language);
            report.Converted.Add($"Language: {name} ({string.Join(", ", exts)}){(language["grammar"] is null ? "" : " with grammar")}");
        }

        foreach (var s in contributes["snippets"] as JsonArray ?? new JsonArray())
        {
            if (Read(Str(s?["path"])) is not { } snippetBytes) continue;
            try
            {
                if (JsonNode.Parse(snippetBytes, documentOptions: jsonOptions) is not JsonObject snippets) continue;
                var language = Str(s?["language"]);
                foreach (var (snippetName, body) in snippets)
                {
                    var prefixes = body?["prefix"] switch { JsonArray a => a.Select(Str).ToList(), var p => [Str(p)] };
                    foreach (var prefix in prefixes.Where(p => p.Length > 0))
                        arr("snippets").Add(new JsonObject
                        {
                            ["language"] = language.Length > 0 ? language : "*",
                            ["prefix"] = prefix,
                            ["body"] = body?["body"]?.DeepClone(),
                            ["description"] = Str(body?["description"]) is { Length: > 0 } d ? d : snippetName,
                        });
                }
                report.Converted.Add($"Snippets: {snippets.Count} for {language}");
            }
            catch { report.Notes.Add($"Unreadable snippets: {Str(s?["path"])}"); }
        }
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
