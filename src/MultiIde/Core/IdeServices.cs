namespace MultiIde.Core;

/// <summary>App-wide services shared by the Welcome window and every project window.</summary>
public static class IdeServices
{
    private static PluginManager? _plugins;
    private static SyntaxRegistry? _syntax;

    public static Settings Settings { get; } = Settings.Load();

    public static PluginManager Plugins
    {
        get
        {
            if (_plugins is not null) return _plugins;
            _plugins = new PluginManager(Settings);
            _plugins.Load();
            _plugins.Changed += () => Syntax.RegisterPlugins(_plugins);
            return _plugins;
        }
    }

    public static SyntaxRegistry Syntax
    {
        get
        {
            if (_syntax is not null) return _syntax;
            _syntax = new SyntaxRegistry();
            _syntax.RegisterPlugins(Plugins);
            return _syntax;
        }
    }
}
