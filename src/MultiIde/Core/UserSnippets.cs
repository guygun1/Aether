using System.Text.Json;
using System.Text.Json.Nodes;

namespace MultiIde.Core;

/// <summary>The user's own live templates, stored in <c>~/.aether/snippets.json</c>.</summary>
public static class UserSnippets
{
    private static List<SnippetContribution>? _all;

    public static string FilePath => Path.Combine(IdePaths.Home, "snippets.json");

    public static List<SnippetContribution> All
    {
        get
        {
            if (_all is not null) return _all;
            try
            {
                if (File.Exists(FilePath))
                    return _all = JsonSerializer.Deserialize<List<SnippetContribution>>(File.ReadAllText(FilePath), Settings.Json) ?? new();
            }
            catch
            {
                // A broken file shouldn't break completion.
            }
            return _all = new();
        }
    }

    public static IEnumerable<SnippetContribution> For(string languageId) =>
        All.Where(s => s.Language == languageId || s.Language == "*" || s.Language.Length == 0);

    public static void Save()
    {
        Directory.CreateDirectory(IdePaths.Home);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(All, Settings.Json));
    }

    public static SnippetContribution Create(string language, string prefix, string body, string description) => new()
    {
        Language = language,
        Prefix = prefix,
        Body = JsonValue.Create(body),
        Description = description,
    };
}
