using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MultiIde.Core;

/// <summary>
/// A question the New Project wizard asks for a template (like IntelliJ's project wizard steps):
/// a text field, a choice, a yes/no, or a version picked from a list loaded live from the web.
/// Answers are used in the template's command, files and properties as <c>${param.id}</c>.
/// </summary>
public sealed class TemplateParameter
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>string, choice, boolean or version.</summary>
    public string Type { get; set; } = "string";
    public string? Description { get; set; }
    /// <summary>Default value; may reference <c>${name}</c> and other parameters (e.g. "${name|id}").</summary>
    public string? Default { get; set; }
    /// <summary>Fixed options for "choice" (and fallback options for "version" when offline).</summary>
    public List<string> Options { get; set; } = new();
    /// <summary>Where "version" options come from.</summary>
    public VersionSource? Source { get; set; }
    /// <summary>Validation for "string" answers, e.g. "^[a-z][a-z0-9_]*$".</summary>
    public string? Pattern { get; set; }
    public string? PatternMessage { get; set; }
    /// <summary>Not shown in the wizard (still loaded), e.g. a Gradle plugin version that follows the latest release.</summary>
    public bool Hidden { get; set; }
}

/// <summary>
/// A list of versions (or other values) loaded from a URL. Text in <see cref="Url"/> and <see cref="Regex"/>
/// may reference other parameters (<c>${param.mc}</c>), so lists can depend on earlier answers.
/// </summary>
public sealed class VersionSource
{
    public string Url { get; set; } = "";
    /// <summary>json (default), xml (all &lt;version&gt;-style elements), gitBranches (a repository URL) or lines.</summary>
    public string Format { get; set; } = "json";
    /// <summary>JSON: dot path to the array ("" = root). An object there is flattened (its values' arrays are joined).</summary>
    public string? Path { get; set; }
    /// <summary>JSON: property of each item to use (null = the item itself). XML: element name (default "version").</summary>
    public string? Field { get; set; }
    /// <summary>JSON: only items whose properties equal these values, e.g. { "stable": true }.</summary>
    public Dictionary<string, JsonNode?> Where { get; set; } = new();
    /// <summary>Only values matching this; a group named <c>v</c> (<c>(?&lt;v&gt;…)</c>) becomes the value, e.g. "^MDK-(?&lt;v&gt;[\d.]+)-".</summary>
    public string? Regex { get; set; }
    /// <summary>"desc" sorts newest version first; "none" keeps the source order.</summary>
    public string Sort { get; set; } = "none";
    public int Limit { get; set; } = 200;
}

public static class TemplateParameters
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly Dictionary<string, string> Cache = new();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Aether-IDE/0.1 (project wizard)");
        return client;
    }

    private static readonly Regex Reference = new(@"\$\{(?<name>name|param\.[A-Za-z0-9_\-]+)(?:\|(?<filter>[A-Za-z]+))?\}", RegexOptions.Compiled);

    /// <summary>Replaces ${param.id}, ${name} and their filtered forms (${name|id}, ${param.mc|majorminor} …).</summary>
    public static string Expand(string text, IReadOnlyDictionary<string, string> values, string projectName) =>
        Reference.Replace(text, m =>
        {
            var key = m.Groups["name"].Value;
            string? value = key == "name" ? projectName : values.GetValueOrDefault(key["param.".Length..]);
            if (value is null) return m.Value;
            // Plain ${name} is left for the caller's own expansion (it also handles ${NAME}).
            if (key == "name" && !m.Groups["filter"].Success) return m.Value;
            return Filter(value, m.Groups["filter"].Success ? m.Groups["filter"].Value : null);
        });

    /// <summary>Which parameters a piece of text depends on.</summary>
    public static IEnumerable<string> References(string? text) =>
        text is null ? [] : Reference.Matches(text).Select(m => m.Groups["name"].Value).Select(n => n == "name" ? "name" : n["param.".Length..]).Distinct();

    /// <summary>
    /// Value filters: lower, upper, id (lower_snake identifier), pascal (ClassName), package (lowercase.dotted),
    /// path (dots → slashes), majorminor ("1.21.4" → "1.21"), neoforge (Minecraft → NeoForge version prefix).
    /// </summary>
    public static string Filter(string value, string? filter) => filter?.ToLowerInvariant() switch
    {
        null => value,
        "lower" => value.ToLowerInvariant(),
        "upper" => value.ToUpperInvariant(),
        "id" => IdOf(value),
        "pascal" => string.Concat(System.Text.RegularExpressions.Regex.Split(value, "[^A-Za-z0-9]+").Where(p => p.Length > 0).Select(p => char.ToUpperInvariant(p[0]) + p[1..])) is { Length: > 0 } s
            ? (char.IsDigit(s[0]) ? "_" + s : s) : "Main",
        "package" => string.Join('.', value.ToLowerInvariant().Split('.').Select(IdOf).Where(p => p.Length > 0)),
        "path" => value.Replace('.', '/'),
        "majorminor" => string.Join('.', value.Split('.').Take(2)),
        "neoforge" => NeoForgePrefix(value),
        "java" => MinecraftJava(value),
        _ => value,
    };

    /// <summary>The Java version a Minecraft version needs: 26+ → 25, 1.20.5+ → 21, 1.18+ → 17, 1.17 → 16, older → 8.</summary>
    private static string MinecraftJava(string mc)
    {
        if (!mc.StartsWith("1.")) return "25";
        var p = mc.Split('.').Select(s => int.TryParse(s, out var n) ? n : 0).ToArray();
        var (minor, patch) = (p.Length > 1 ? p[1] : 0, p.Length > 2 ? p[2] : 0);
        return minor > 20 || (minor == 20 && patch >= 5) ? "21" : minor >= 18 ? "17" : minor == 17 ? "16" : "8";
    }

    private static string IdOf(string value)
    {
        var s = System.Text.RegularExpressions.Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');
        return s.Length == 0 ? "project" : char.IsDigit(s[0]) ? "_" + s : s;
    }

    /// <summary>NeoForge versions follow Minecraft: 1.21.4 → 21.4, 1.21 → 21.0, 26.1 → 26.1.0, 26.1.2 → 26.1.2.</summary>
    private static string NeoForgePrefix(string mc)
    {
        if (mc.StartsWith("1."))
        {
            var rest = mc[2..].Split('.');
            return rest.Length == 1 ? $"{rest[0]}.0" : string.Join('.', rest.Take(2));
        }
        var parts = mc.Split('.').ToList();
        while (parts.Count < 3) parts.Add("0");
        return string.Join('.', parts.Take(3));
    }

    /// <summary>Loads the options of a "version" parameter. Throws on network or format errors.</summary>
    public static async Task<List<string>> LoadAsync(VersionSource source, IReadOnlyDictionary<string, string> values, string projectName, CancellationToken ct = default)
    {
        var url = Expand(source.Url, values, projectName);
        var raw = source.Format switch
        {
            "gitBranches" => await GitBranchesAsync(url, ct),
            _ => await FetchAsync(url, ct),
        };
        IEnumerable<string> items = source.Format switch
        {
            "xml" => XDocument.Parse(raw).Descendants().Where(e => e.Name.LocalName == (source.Field ?? "version") && !e.HasElements).Select(e => e.Value.Trim()),
            "lines" or "gitBranches" => raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            _ => FromJson(JsonNode.Parse(raw), source),
        };
        if (source.Regex is { Length: > 0 } pattern)
        {
            var regex = new Regex(Expand(pattern, values, projectName));
            items = items.Select(i => regex.Match(i)).Where(m => m.Success).Select(m => m.Groups["v"].Success ? m.Groups["v"].Value : m.Value);
        }
        var list = items.Where(i => i.Length > 0).Distinct().ToList();
        if (source.Sort == "desc") list.Sort((a, b) => CompareVersions(b, a));
        return list.Take(source.Limit).ToList();
    }

    private static async Task<string> FetchAsync(string url, CancellationToken ct)
    {
        lock (Cache) if (Cache.TryGetValue(url, out var cached)) return cached;
        var text = await Http.GetStringAsync(url, ct);
        lock (Cache) Cache[url] = text;
        return text;
    }

    /// <summary>Branch names of a git repository (git ls-remote), one per line.</summary>
    private static async Task<string> GitBranchesAsync(string repo, CancellationToken ct)
    {
        lock (Cache) if (Cache.TryGetValue("git:" + repo, out var cached)) return cached;
        var (code, stdout, stderr) = await ProcessUtil.RunCaptureAsync($"git ls-remote --heads \"{repo}\"", null, null);
        if (code != 0) throw new InvalidOperationException(stderr.Trim().Length > 0 ? stderr.Trim() : "git ls-remote failed (is git installed?)");
        var text = string.Join('\n', stdout.Split('\n').Select(l => l.Trim()).Where(l => l.Contains("refs/heads/")).Select(l => l[(l.IndexOf("refs/heads/") + 11)..]));
        lock (Cache) Cache["git:" + repo] = text;
        return text;
    }

    private static IEnumerable<string> FromJson(JsonNode? node, VersionSource source)
    {
        foreach (var part in (source.Path ?? "").Split('.', StringSplitOptions.RemoveEmptyEntries))
            node = node?[part];
        IEnumerable<JsonNode?> items = node switch
        {
            JsonArray a => a,
            // e.g. Paper: { "1.21": ["1.21.4", …], "1.20": [ … ] }
            JsonObject o => o.Select(kv => kv.Value).SelectMany(v => v as JsonArray ?? new JsonArray()),
            _ => [],
        };
        foreach (var item in items)
        {
            if (item is null) continue;
            if (source.Where.Count > 0 && item is JsonObject obj &&
                !source.Where.All(w => JsonNode.DeepEquals(obj[w.Key], w.Value))) continue;
            var value = source.Field is null ? item : item[source.Field];
            if (value is JsonValue v) yield return v.ToString();
        }
    }

    /// <summary>Compares version-like strings chunk by chunk (numbers numerically): 1.21.10 &gt; 1.21.9.</summary>
    public static int CompareVersions(string a, string b)
    {
        var pa = System.Text.RegularExpressions.Regex.Split(a, "[^A-Za-z0-9]+");
        var pb = System.Text.RegularExpressions.Regex.Split(b, "[^A-Za-z0-9]+");
        for (var i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            if (i >= pa.Length) return -1;
            if (i >= pb.Length) return 1;
            var na = long.TryParse(pa[i], out var x);
            var nb = long.TryParse(pb[i], out var y);
            var c = na && nb ? x.CompareTo(y) : na ? 1 : nb ? -1 : string.CompareOrdinal(pa[i], pb[i]);
            if (c != 0) return c;
        }
        return 0;
    }

    /// <summary>
    /// Changes <c>key=value</c> lines that already exist in a .properties file. Keys the file doesn't have are ignored,
    /// so one template can name both spellings used by different versions of a starter project (e.g. fabric_version / fabric_api_version).
    /// </summary>
    public static void SetProperties(string file, IReadOnlyDictionary<string, string> values)
    {
        if (!File.Exists(file)) return;
        var lines = File.ReadAllLines(file);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var eq = line.IndexOf('=');
            if (eq <= 0 || line.TrimStart().StartsWith('#')) continue;
            var key = line[..eq].Trim();
            if (values.TryGetValue(key, out var v)) lines[i] = $"{key}={v}";
        }
        File.WriteAllLines(file, lines);
    }
}
