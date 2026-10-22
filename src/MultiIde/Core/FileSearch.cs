using System.Collections.Concurrent;

namespace MultiIde.Core;

public sealed record FileHit(string Path, string Name, string Relative, int Score);
public sealed record TextHit(string Path, string Relative, int Line, int Column, string Text);

/// <summary>
/// Project-wide search. Runs on the thread pool, skips build output and VCS
/// folders, and caches the file list so "Search Everywhere" stays instant.
/// </summary>
public sealed class FileSearch
{
    private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", ".idea", ".vs", ".vscode", "node_modules", "target", "bin", "obj",
        "build", "cmake-build-debug", "cmake-build-release", "out", "dist", ".cache", "__pycache__", ".venv",
    };

    private const int MaxFiles = 200_000;
    private const long MaxGrepBytes = 4 * 1024 * 1024;

    private readonly string _root;
    private string[]? _files;
    private Task<string[]>? _indexing;

    public FileSearch(string root) => _root = root;

    public void Invalidate() => (_files, _indexing) = (null, null);

    private Task<string[]> FilesAsync()
    {
        if (_files is not null) return Task.FromResult(_files);
        return _indexing ??= Task.Run(() => _files = Enumerate().Take(MaxFiles).ToArray());
    }

    private IEnumerable<string> Enumerate()
    {
        var ignored = IdeServices.Settings.ExcludedFolderSet();
        ignored.UnionWith(IgnoredDirs);
        var stack = new Stack<string>();
        stack.Push(_root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] files, dirs;
            try
            {
                files = Directory.GetFiles(dir);
                dirs = Directory.GetDirectories(dir);
            }
            catch
            {
                continue;
            }
            foreach (var f in files) yield return f;
            foreach (var d in dirs)
            {
                var name = Path.GetFileName(d);
                if (!ignored.Contains(name) && !name.StartsWith('.')) stack.Push(d);
            }
        }
    }

    public string Relative(string path) => Path.GetRelativePath(_root, path).Replace('\\', '/');

    /// <summary>IntelliJ-style fuzzy score: subsequence with bonuses for runs and word starts.</summary>
    public static int? Fuzzy(string query, string text)
    {
        if (query.Length == 0) return 0;
        int qi = 0, score = 0, prev = -2;
        for (var i = 0; i < text.Length && qi < query.Length; i++)
        {
            var c = text[i];
            if (char.ToLowerInvariant(c) != char.ToLowerInvariant(query[qi])) continue;
            score += 1;
            if (prev == i - 1) score += 5;
            if (i == 0 || text[i - 1] is '_' or '-' or '.' or '/' or '\\' or ' ' || (char.IsUpper(c) && char.IsLower(text[i - 1])))
                score += 8;
            prev = i;
            qi++;
        }
        return qi == query.Length ? score * 10 - text.Length : null;
    }

    public async Task<List<FileHit>> FindFilesAsync(string query, int limit, CancellationToken ct)
    {
        var files = await FilesAsync();
        query = query.Replace(" ", "");
        return await Task.Run(() =>
        {
            var hits = new List<FileHit>();
            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(f);
                var score = Fuzzy(query, name) is { } s ? s + 1000 : Fuzzy(query, Relative(f));
                if (score is null) continue;
                hits.Add(new FileHit(f, name, Relative(f), score.Value));
            }
            return hits.OrderByDescending(h => h.Score).ThenBy(h => h.Relative.Length).Take(limit).ToList();
        }, ct);
    }

    public async Task<List<TextHit>> FindInFilesAsync(string query, bool matchCase, int limit, CancellationToken ct)
    {
        if (query.Length == 0) return new();
        var files = await FilesAsync();
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var hits = new ConcurrentBag<TextHit>();
        var count = 0;

        await Task.Run(() => Parallel.ForEach(files, new ParallelOptions { CancellationToken = ct }, (file, state) =>
        {
            if (Volatile.Read(ref count) >= limit) { state.Stop(); return; }
            try
            {
                var info = new FileInfo(file);
                if (info.Length > MaxGrepBytes) return;
                var bytes = File.ReadAllBytes(file);
                if (bytes.AsSpan(0, Math.Min(bytes.Length, 8000)).IndexOf((byte)0) >= 0) return; // binary
                var text = System.Text.Encoding.UTF8.GetString(bytes);
                var line = 0;
                foreach (var l in text.AsSpan().EnumerateLines())
                {
                    line++;
                    var idx = l.IndexOf(query.AsSpan(), comparison);
                    if (idx < 0) continue;
                    var preview = l.TrimStart().ToString();
                    if (preview.Length > 300) preview = preview[..300];
                    hits.Add(new TextHit(file, Relative(file), line, idx + 1, preview));
                    if (Interlocked.Increment(ref count) >= limit) { state.Stop(); return; }
                }
            }
            catch
            {
                // Unreadable file: skip.
            }
        }), ct);

        return hits.OrderBy(h => h.Relative, StringComparer.Ordinal).ThenBy(h => h.Line).Take(limit).ToList();
    }
}
