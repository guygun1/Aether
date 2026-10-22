using System.Text.RegularExpressions;

namespace MultiIde.Core;

/// <summary>
/// IntelliCode-style next-word prediction, entirely local. Learns which word follows which
/// (bigrams and trigrams of words separated only by spaces, e.g. "private static readonly")
/// from your project's code, per language, on top of a small built-in seed. When the next word
/// is clearly predictable, the editor shows it as gray text that Tab accepts.
/// </summary>
public static class TokenPredictor
{
    private static readonly Regex Word = new(@"[A-Za-z_#][A-Za-z0-9_]*|<|::|->|=>", RegexOptions.Compiled);
    private const int MaxFiles = 3000;
    private const long MaxFileBytes = 1_000_000;

    private sealed class Model
    {
        public readonly Dictionary<string, Dictionary<string, int>> Bi = new(StringComparer.Ordinal);
        public readonly Dictionary<string, Dictionary<string, int>> Tri = new(StringComparer.Ordinal);
    }

    private static readonly Dictionary<string, Model> Models = new();
    private static readonly object Gate = new();

    /// <summary>Common, unambiguous phrases so suggestions work in a brand-new project.</summary>
    private static readonly Dictionary<string, string> Seeds = new()
    {
        ["csharp"] = string.Join("\n",
            "using System", "using System", "namespace App", "public static void", "public static void", "private static readonly", "private static readonly",
            "private readonly int", "private readonly string", "public sealed class", "public sealed class", "public override string", "protected override void",
            "public async Task", "private async Task", "public static async Task", "return new", "return await", "throw new", "catch (", "else if", "else if",
            "foreach (var", "foreach (var", "public interface", "public abstract class", "public record", "public enum", "internal sealed class"),
        ["cpp"] = string.Join("\n",
            "#include <", "#include <", "#pragma once", "#pragma once", "using namespace std", "static const int", "static inline", "constexpr int",
            "template <typename", "template <typename", "std::unique_ptr <", "else if", "else if", "return 0", "int main", "const auto &", "virtual void",
            "public :", "private :", "explicit operator", "typename std", "static_cast <", "static_cast <", "#ifndef", "#endif"),
        ["c"] = string.Join("\n", "#include <", "#include <", "static const", "static inline", "typedef struct", "typedef struct", "else if", "return 0", "int main"),
        ["rust"] = string.Join("\n",
            "pub fn", "pub fn", "pub fn", "pub struct", "pub enum", "pub mod", "pub trait", "pub(crate) fn", "use std", "use std", "use crate", "impl Default for",
            "let mut", "let mut", "let mut", "else if", "else if", "mut self", "&mut self", "&self", "fn main", "Some (", "Ok (", "match self", "async fn", "pub async fn",
            "#[derive", "#[derive", "#[test]", "#[cfg", "impl From", "where T", "move ||", "Box < dyn"),
        ["python"] = string.Join("\n", "from typing import", "from typing import", "import os", "def __init__", "if __name__", "elif", "else :", "return None", "self ."),
        ["typescript"] = string.Join("\n", "export default", "export const", "export function", "import {", "import type", "private readonly", "public static", "async function", "return await", "else if"),
        ["javascript"] = string.Join("\n", "export default", "export const", "export function", "import {", "async function", "return await", "else if"),
        ["glsl"] = string.Join("\n", "#version 330", "#version 330", "layout (location", "uniform mat4", "uniform sampler2D", "in vec3", "out vec4", "out vec4"),
        ["go"] = string.Join("\n", "func main", "package main", "if err", "if err", "return nil", "return err", "else if"),
    };

    private static Model For(string languageId)
    {
        lock (Gate)
        {
            if (Models.TryGetValue(languageId, out var m)) return m;
            Models[languageId] = m = new Model();
            if (Seeds.TryGetValue(languageId, out var seed)) Learn(m, seed);
            return m;
        }
    }

    public static void Learn(string languageId, string text)
    {
        var model = For(languageId);
        lock (Gate) Learn(model, text);
    }

    /// <summary>Counts word pairs/triples that are separated only by spaces on the same line.</summary>
    private static void Learn(Model m, string text)
    {
        foreach (var line in text.Split('\n'))
        {
            string? a = null, b = null;
            var lastEnd = -1;
            foreach (Match w in Word.Matches(line))
            {
                var gap = lastEnd < 0 ? "" : line[lastEnd..w.Index];
                if (lastEnd >= 0 && gap.Trim().Length > 0) a = b = null; // punctuation breaks the phrase
                lastEnd = w.Index + w.Length;
                var c = w.Value;
                if (b is not null) Add(m.Bi, b, c);
                if (a is not null && b is not null) Add(m.Tri, a + " " + b, c);
                (a, b) = (b, c);
            }
        }
    }

    private static void Add(Dictionary<string, Dictionary<string, int>> table, string key, string next)
    {
        if (!table.TryGetValue(key, out var counts)) table[key] = counts = new(StringComparer.Ordinal);
        counts[next] = counts.GetValueOrDefault(next) + 1;
    }

    /// <summary>The next word if it's clearly predictable after (prev2, prev1), else null.</summary>
    public static string? Predict(string languageId, string? prev2, string prev1)
    {
        var m = For(languageId);
        lock (Gate)
        {
            if (prev2 is not null && m.Tri.TryGetValue(prev2 + " " + prev1, out var tri) && Best(tri) is { } t) return t;
            return m.Bi.TryGetValue(prev1, out var bi) ? Best(bi) : null;
        }
    }

    /// <summary>Confident only with enough evidence and a clear winner.</summary>
    private static string? Best(Dictionary<string, int> counts)
    {
        var total = counts.Values.Sum();
        if (total < 2) return null;
        var (word, count) = counts.MaxBy(kv => kv.Value);
        return count >= 2 && count / (double)total >= 0.6 ? word : null;
    }

    /// <summary>Learns from the project's source files in the background.</summary>
    public static Task IndexProjectAsync(string root, PluginManager plugins) => Task.Run(() =>
    {
        var excluded = IdeServices.Settings.ExcludedFolderSet();
        var files = 0;
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0 && files < MaxFiles)
        {
            var dir = stack.Pop();
            try
            {
                foreach (var d in Directory.GetDirectories(dir))
                    if (!excluded.Contains(Path.GetFileName(d)) && !Path.GetFileName(d).StartsWith('.')) stack.Push(d);
                foreach (var f in Directory.GetFiles(dir))
                {
                    if (plugins.LanguageForFile(f)?.Lang.Id is not { } lang) continue;
                    if (new FileInfo(f).Length > MaxFileBytes) continue;
                    Learn(lang, File.ReadAllText(f));
                    if (++files >= MaxFiles) break;
                }
            }
            catch
            {
                // Unreadable folder: skip.
            }
        }
    });
}
