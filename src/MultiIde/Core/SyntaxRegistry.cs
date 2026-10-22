using System.Text.Json.Nodes;
using Avalonia.Platform;
using TextMateSharp.Grammars;
using TextMateSharp.Internal.Grammars.Reader;
using TextMateSharp.Internal.Themes.Reader;
using TextMateSharp.Internal.Types;
using TextMateSharp.Registry;
using TextMateSharp.Themes;

namespace MultiIde.Core;

/// <summary>
/// TextMate registry: the grammars bundled with TextMateSharp plus grammars
/// contributed by plugins, rendered with a JetBrains-dark theme.
/// </summary>
public sealed class SyntaxRegistry : IRegistryOptions
{
    private readonly RegistryOptions _builtin = new(ThemeName.DarkPlus);
    private readonly Dictionary<string, string> _pluginGrammars = new();
    private readonly Dictionary<string, IRawGrammar> _cache = new();
    private readonly IRawTheme _theme;

    public SyntaxRegistry()
    {
        using var stream = AssetLoader.Open(new Uri("avares://Aether/Assets/default-dark.theme.json"));
        using var reader = new StreamReader(stream);
        _theme = ThemeReader.ReadThemeSync(reader);
    }

    public void RegisterPlugins(PluginManager plugins)
    {
        _pluginGrammars.Clear();
        _cache.Clear();
        foreach (var p in plugins.Enabled)
        foreach (var lang in p.Manifest.Contributes.Languages)
        {
            if (string.IsNullOrEmpty(lang.Grammar)) continue;
            var file = Path.Combine(p.Directory, lang.Grammar);
            if (!File.Exists(file)) continue;
            try
            {
                var scope = JsonNode.Parse(File.ReadAllText(file))?["scopeName"]?.GetValue<string>();
                if (scope is null) continue;
                _pluginGrammars[scope] = file;
                lang.ScopeName = scope;
            }
            catch
            {
                // A broken grammar should not take the IDE down.
            }
        }
    }

    /// <summary>Scope for a language contribution, or a best guess from the file extension.</summary>
    public string? ScopeFor(LanguageContribution? lang, string path)
    {
        if (lang?.ScopeName is { } scope) return scope;
        try
        {
            return _builtin.GetScopeByExtension(Path.GetExtension(path));
        }
        catch
        {
            return null;
        }
    }

    public IRawGrammar GetGrammar(string scopeName)
    {
        if (_cache.TryGetValue(scopeName, out var cached)) return cached;
        if (_pluginGrammars.TryGetValue(scopeName, out var file))
        {
            using var reader = new StreamReader(file);
            return _cache[scopeName] = GrammarReader.ReadGrammarSync(reader);
        }
        return _builtin.GetGrammar(scopeName);
    }

    public ICollection<string> GetInjections(string scopeName) => _builtin.GetInjections(scopeName);
    public IRawTheme GetTheme(string scopeName) => ThemeManager.TokenTheme ?? _theme;
    public IRawTheme GetDefaultTheme() => ThemeManager.TokenTheme ?? _theme;
}
