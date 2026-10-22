using System.Text.Json;

namespace MultiIde.Core;

/// <summary>
/// Learns which completions you actually pick (per language) so they can be starred and
/// ranked first, like IntelliCode. Stored locally in ~/.aether/completion-usage.json.
/// </summary>
public static class CompletionUsage
{
    private static Dictionary<string, Dictionary<string, int>>? _counts;
    private static readonly object Gate = new();
    private static Timer? _saveTimer;

    private static string FilePath => Path.Combine(IdePaths.Home, "completion-usage.json");

    private static Dictionary<string, Dictionary<string, int>> Counts
    {
        get
        {
            if (_counts is not null) return _counts;
            try
            {
                if (File.Exists(FilePath))
                    _counts = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(File.ReadAllText(FilePath));
            }
            catch
            {
                // Start over if the file is unreadable.
            }
            return _counts ??= new();
        }
    }

    public static void Record(string languageId, string label)
    {
        lock (Gate)
        {
            if (!Counts.TryGetValue(languageId, out var lang)) Counts[languageId] = lang = new();
            lang[label] = lang.GetValueOrDefault(label) + 1;
        }
        // Save shortly after, in the background (no disk I/O while typing).
        _saveTimer?.Dispose();
        _saveTimer = new Timer(_ => Save(), null, 2000, Timeout.Infinite);
    }

    public static int Score(string languageId, string label)
    {
        lock (Gate) return Counts.TryGetValue(languageId, out var lang) ? lang.GetValueOrDefault(label) : 0;
    }

    private static void Save()
    {
        try
        {
            string json;
            lock (Gate) json = JsonSerializer.Serialize(Counts);
            Directory.CreateDirectory(IdePaths.Home);
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // Not important enough to report.
        }
    }
}
