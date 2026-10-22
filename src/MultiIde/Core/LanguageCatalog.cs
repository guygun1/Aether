using System.Text.Json;
using System.Text.Json.Nodes;

namespace MultiIde.Core;

/// <summary>A language the "Add Language Support" wizard can set up in one click.</summary>
public sealed record CatalogLanguage(
    string Id, string Name, string[] Extensions, string Icon, string Color, string? LineComment,
    string? ServerName, string? Command, string[] Args, string? Install, string? InstallUrl,
    string? RunCommand = null, string? RunWhen = null, string[]? Filenames = null);

/// <summary>Popular languages and their standard language servers. Highlighting uses the bundled TextMate grammars.</summary>
public static class LanguageCatalog
{
    public static readonly CatalogLanguage[] All =
    {
        new("python", "Python", [".py", ".pyw", ".pyi"], "PY", "#3C78AA", "#", "Pyright", "pyright-langserver", ["--stdio"], "npm install -g pyright", "https://github.com/microsoft/pyright", "python \"${file}\"", "*.py"),
        new("go", "Go", [".go"], "GO", "#2AACB8", "//", "gopls", "gopls", [], "go install golang.org/x/tools/gopls@latest", "https://pkg.go.dev/golang.org/x/tools/gopls", "go run .", "go.mod"),
        new("typescript", "TypeScript", [".ts", ".tsx", ".mts", ".cts"], "TS", "#3574F0", "//", "TypeScript LS", "typescript-language-server", ["--stdio"], "npm install -g typescript typescript-language-server", "https://github.com/typescript-language-server/typescript-language-server", "npx tsx \"${file}\""),
        new("javascript", "JavaScript", [".js", ".mjs", ".cjs", ".jsx"], "JS", "#D5B778", "//", "TypeScript LS", "typescript-language-server", ["--stdio"], "npm install -g typescript typescript-language-server", null, "node \"${file}\""),
        new("kotlin", "Kotlin", [".kt", ".kts"], "KT", "#A97BFF", "//", "Kotlin LS", "kotlin-language-server", [], null, "https://github.com/fwcd/kotlin-language-server"),
        new("lua", "Lua", [".lua"], "LU", "#000080", "--", "LuaLS", "lua-language-server", [], "winget install --id LuaLS.lua-language-server -e", "https://luals.github.io", "lua \"${file}\""),
        new("zig", "Zig", [".zig", ".zon"], "ZG", "#F7A41D", "//", "ZLS", "zls", [], "winget install --id zigtools.zls -e", "https://github.com/zigtools/zls", "zig build run", "build.zig"),
        new("dart", "Dart", [".dart"], "DA", "#0175C2", "//", "Dart analysis server", "dart", ["language-server", "--protocol=lsp"], null, "https://dart.dev/get-dart", "dart run"),
        new("ruby", "Ruby", [".rb", ".rake", ".gemspec"], "RB", "#CC342D", "#", "Ruby LSP", "ruby-lsp", [], "gem install ruby-lsp", "https://shopify.github.io/ruby-lsp", "ruby \"${file}\"", null, ["Gemfile", "Rakefile"]),
        new("php", "PHP", [".php", ".phtml"], "PHP", "#777BB3", "//", "Intelephense", "intelephense", ["--stdio"], "npm install -g intelephense", "https://intelephense.com", "php \"${file}\""),
        new("swift", "Swift", [".swift"], "SW", "#F05138", "//", "SourceKit-LSP", "sourcekit-lsp", [], null, "https://github.com/swiftlang/sourcekit-lsp", "swift run", "Package.swift"),
        new("fsharp", "F#", [".fs", ".fsi", ".fsx"], "F#", "#378BBA", "//", "FsAutoComplete", "fsautocomplete", [], "dotnet tool install --global fsautocomplete", "https://github.com/ionide/FsAutoComplete", "dotnet run", "*.fsproj"),
        new("haskell", "Haskell", [".hs", ".lhs"], "HS", "#5E5086", "--", "HLS", "haskell-language-server-wrapper", ["--lsp"], null, "https://haskell-language-server.readthedocs.io"),
        new("shellscript", "Shell / Bash", [".sh", ".bash", ".zsh"], "SH", "#5FB865", "#", "Bash LS", "bash-language-server", ["start"], "npm install -g bash-language-server", "https://github.com/bash-lsp/bash-language-server", "bash \"${file}\""),
        new("yaml", "YAML", [".yml", ".yaml"], "YM", "#C77DBB", "#", "YAML LS", "yaml-language-server", ["--stdio"], "npm install -g yaml-language-server", "https://github.com/redhat-developer/yaml-language-server"),
        new("markdown", "Markdown", [".md", ".markdown"], "MD", "#548AF7", null, "Marksman", "marksman", [], "winget install --id Artempyanykh.Marksman -e", "https://github.com/artempyanykh/marksman"),
        new("sql", "SQL", [".sql"], "SQL", "#E08855", "--", "sqls", "sqls", [], "go install github.com/sqls-server/sqls@latest", "https://github.com/sqls-server/sqls"),
    };

    /// <summary>Writes a plugin for a catalog language into the user plugin folder; returns its folder.</summary>
    public static string CreatePlugin(CatalogLanguage l)
    {
        var language = new JsonObject
        {
            ["id"] = l.Id,
            ["name"] = l.Name,
            ["extensions"] = new JsonArray(l.Extensions.Select(e => (JsonNode)e).ToArray()),
            ["icon"] = l.Icon,
            ["iconColor"] = l.Color,
        };
        if (l.Filenames is { Length: > 0 }) language["filenames"] = new JsonArray(l.Filenames.Select(e => (JsonNode)e).ToArray());
        if (l.LineComment is not null) language["lineComment"] = l.LineComment;
        var contributes = new JsonObject { ["languages"] = new JsonArray(language) };
        if (l.Command is not null)
        {
            var server = new JsonObject
            {
                ["id"] = $"{l.Id}-ls",
                ["name"] = l.ServerName ?? l.Command,
                ["command"] = l.Command,
                ["args"] = new JsonArray(l.Args.Select(a => (JsonNode)a).ToArray()),
                ["languages"] = new JsonArray((JsonNode)l.Id),
            };
            if (l.Install is not null) server["install"] = l.Install;
            if (l.InstallUrl is not null) server["installUrl"] = l.InstallUrl;
            contributes["lspServers"] = new JsonArray(server);
        }
        if (l.RunCommand is not null)
        {
            var run = new JsonObject { ["name"] = $"{l.Name}: {l.RunCommand}", ["command"] = l.RunCommand };
            if (l.RunWhen is not null) run["when"] = l.RunWhen;
            contributes["runConfigurations"] = new JsonArray(run);
        }
        return Write($"lang-{l.Id}", $"{l.Name} Language Support", $"{l.Name} highlighting, the {l.ServerName ?? "standard"} language server and a run configuration.", contributes);
    }

    /// <summary>Writes a custom language plugin; copies the grammar file into the plugin if given.</summary>
    public static string CreateCustom(string id, string name, string[] extensions, string? lineComment, string? lspCommand, string[] lspArgs, string? install, string? grammarFile)
    {
        var dir = Path.Combine(IdePaths.UserPlugins, $"lang-{id}");
        Directory.CreateDirectory(dir);
        var language = new JsonObject
        {
            ["id"] = id,
            ["name"] = name,
            ["extensions"] = new JsonArray(extensions.Select(e => (JsonNode)(e.StartsWith('.') ? e : "." + e)).ToArray()),
            ["icon"] = name.Length >= 2 ? name[..2].ToUpperInvariant() : name.ToUpperInvariant(),
            ["iconColor"] = "#6F8AA8",
        };
        if (!string.IsNullOrWhiteSpace(lineComment)) language["lineComment"] = lineComment;
        if (grammarFile is not null && File.Exists(grammarFile))
        {
            var target = Path.GetFileName(grammarFile);
            File.Copy(grammarFile, Path.Combine(dir, target), overwrite: true);
            language["grammar"] = target;
        }
        var contributes = new JsonObject { ["languages"] = new JsonArray(language) };
        if (!string.IsNullOrWhiteSpace(lspCommand))
        {
            var server = new JsonObject
            {
                ["id"] = $"{id}-ls", ["name"] = lspCommand, ["command"] = lspCommand,
                ["args"] = new JsonArray(lspArgs.Select(a => (JsonNode)a).ToArray()), ["languages"] = new JsonArray((JsonNode)id),
            };
            if (!string.IsNullOrWhiteSpace(install)) server["install"] = install;
            contributes["lspServers"] = new JsonArray(server);
        }
        return Write($"lang-{id}", $"{name} Language Support", $"{name} support created with Add Language Support.", contributes);
    }

    private static string Write(string pluginId, string name, string description, JsonObject contributes)
    {
        var dir = Path.Combine(IdePaths.UserPlugins, pluginId);
        Directory.CreateDirectory(dir);
        var manifest = new JsonObject
        {
            ["id"] = pluginId,
            ["name"] = name,
            ["version"] = "0.1.0",
            ["description"] = description,
            ["author"] = Environment.UserName,
            ["contributes"] = contributes,
        };
        File.WriteAllText(Path.Combine(dir, "plugin.json"), manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return dir;
    }
}
