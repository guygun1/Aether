using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MultiIde.Core;

/// <summary>A package manager a plugin teaches the IDE about (Cargo, NuGet, vcpkg, ...).</summary>
public sealed class DependencyManagerContribution
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Manifest file name or glob searched in the project (e.g. "Cargo.toml", "*.csproj").</summary>
    public string Manifest { get; set; } = "";
    /// <summary>Built-in parser: cargo, msbuild, vcpkg, cmake.</summary>
    public string Format { get; set; } = "";
    /// <summary>Commands; ${name}, ${version}, ${manifest}, ${manifestDir} are expanded.</summary>
    public string? Add { get; set; }
    public string? Remove { get; set; }
    public string? Update { get; set; }
    public string? Restore { get; set; }
    /// <summary>URLs; ${name} / ${query} are expanded.</summary>
    public string? Docs { get; set; }
    public string? Search { get; set; }
}

public sealed record Dependency(string Name, string Version, string Kind, string Manifest, int Line);

/// <summary>Reads dependencies from manifests. Parsers are deliberately tolerant: they never throw.</summary>
public static class DependencyParsers
{
    public static List<Dependency> Parse(string format, string manifest)
    {
        try
        {
            return format switch
            {
                "cargo" => Cargo(manifest),
                "msbuild" => MsBuild(manifest),
                "vcpkg" => Vcpkg(manifest),
                "cmake" => CMake(manifest),
                _ => new(),
            };
        }
        catch
        {
            return new();
        }
    }

    private static readonly Regex TomlSection = new(@"^\s*\[(?<name>[^\]]+)\]\s*$", RegexOptions.Compiled);
    private static readonly Regex TomlEntry = new(@"^\s*(?<name>[A-Za-z0-9_\-]+)\s*=\s*(?<value>.+)$", RegexOptions.Compiled);
    private static readonly Regex TomlVersion = new(@"version\s*=\s*""(?<v>[^""]+)""", RegexOptions.Compiled);

    private static List<Dependency> Cargo(string path)
    {
        var result = new List<Dependency>();
        string? kind = null;
        string? inlineTableDep = null;
        var lines = File.ReadAllLines(path);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (TomlSection.Match(line) is { Success: true } section)
            {
                var name = section.Groups["name"].Value.Trim();
                inlineTableDep = null;
                kind = name switch
                {
                    "dependencies" or "workspace.dependencies" => "normal",
                    "dev-dependencies" => "dev",
                    "build-dependencies" => "build",
                    _ when name.EndsWith(".dependencies") && name.StartsWith("target.") => "target",
                    _ => null,
                };
                // [dependencies.serde] style tables
                foreach (var prefix in new[] { "dependencies.", "dev-dependencies.", "build-dependencies." })
                    if (name.StartsWith(prefix))
                    {
                        inlineTableDep = name[prefix.Length..];
                        result.Add(new Dependency(inlineTableDep, "", prefix.StartsWith("dev") ? "dev" : prefix.StartsWith("build") ? "build" : "normal", path, i + 1));
                    }
                continue;
            }
            if (inlineTableDep is not null && TomlVersion.Match(line) is { Success: true } tv)
            {
                var idx = result.FindLastIndex(d => d.Name == inlineTableDep);
                if (idx >= 0) result[idx] = result[idx] with { Version = tv.Groups["v"].Value };
                continue;
            }
            if (kind is null || TomlEntry.Match(line) is not { Success: true } entry) continue;
            var value = entry.Groups["value"].Value.Trim();
            var version = value.StartsWith('"') ? value.Trim('"') : TomlVersion.Match(value) is { Success: true } m ? m.Groups["v"].Value : value.Contains("path") ? "path" : value.Contains("git") ? "git" : "";
            result.Add(new Dependency(entry.Groups["name"].Value, version, kind, path, i + 1));
        }
        return result;
    }

    private static List<Dependency> MsBuild(string path)
    {
        var doc = XDocument.Load(path, LoadOptions.SetLineInfo);
        var result = new List<Dependency>();
        foreach (var el in doc.Descendants())
        {
            var line = ((System.Xml.IXmlLineInfo)el).LineNumber;
            switch (el.Name.LocalName)
            {
                case "PackageReference":
                    var name = (string?)el.Attribute("Include") ?? (string?)el.Attribute("Update");
                    var version = (string?)el.Attribute("Version") ?? el.Element(el.Name.Namespace + "Version")?.Value ?? "";
                    if (name is not null) result.Add(new Dependency(name, version, "package", path, line));
                    break;
                case "ProjectReference":
                    if ((string?)el.Attribute("Include") is { } proj)
                        result.Add(new Dependency(Path.GetFileNameWithoutExtension(proj.Replace('\\', '/')), "", "project", path, line));
                    break;
                case "FrameworkReference":
                    if ((string?)el.Attribute("Include") is { } fw) result.Add(new Dependency(fw, "", "framework", path, line));
                    break;
            }
        }
        return result;
    }

    private static List<Dependency> Vcpkg(string path)
    {
        var deps = JsonNode.Parse(File.ReadAllText(path))?["dependencies"] as JsonArray ?? new JsonArray();
        return deps.Select(d => d switch
        {
            JsonValue v => new Dependency(v.GetValue<string>(), "", "vcpkg", path, 0),
            JsonObject o => new Dependency(o["name"]?.GetValue<string>() ?? "?", o["version>="]?.GetValue<string>() ?? "", "vcpkg", path, 0),
            _ => null,
        }).OfType<Dependency>().ToList();
    }

    private static readonly Regex FetchContent = new(@"FetchContent_Declare\s*\(\s*(?<name>\w+)(?<body>[^)]*)\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex GitTag = new(@"GIT_TAG\s+(?<tag>\S+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex FindPackage = new(@"find_package\s*\(\s*(?<name>\w+)(?:\s+(?<ver>[0-9][\w.]*))?", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static List<Dependency> CMake(string path)
    {
        var text = File.ReadAllText(path);
        int LineOf(int index) => text.AsSpan(0, index).Count('\n') + 1;
        var result = new List<Dependency>();
        foreach (Match m in FetchContent.Matches(text))
            result.Add(new Dependency(m.Groups["name"].Value, GitTag.Match(m.Groups["body"].Value) is { Success: true } t ? t.Groups["tag"].Value : "", "FetchContent", path, LineOf(m.Index)));
        foreach (Match m in FindPackage.Matches(text))
            result.Add(new Dependency(m.Groups["name"].Value, m.Groups["ver"].Value, "find_package", path, LineOf(m.Index)));
        return result;
    }
}

/// <summary>Minimal glob → regex for LSP file watchers ("**/*.rs", "**/{Cargo.toml,Cargo.lock}").</summary>
public static class Glob
{
    public static Regex ToRegex(string glob)
    {
        var sb = new System.Text.StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            switch (c)
            {
                case '*' when i + 1 < glob.Length && glob[i + 1] == '*':
                    sb.Append(".*");
                    i++;
                    if (i + 1 < glob.Length && glob[i + 1] == '/') i++;
                    break;
                case '*': sb.Append("[^/]*"); break;
                case '?': sb.Append("[^/]"); break;
                case '{': sb.Append('('); break;
                case '}': sb.Append(')'); break;
                case ',': sb.Append('|'); break;
                default: sb.Append(Regex.Escape(c.ToString())); break;
            }
        }
        return new Regex(sb.Append('$').ToString(), RegexOptions.IgnoreCase);
    }
}
