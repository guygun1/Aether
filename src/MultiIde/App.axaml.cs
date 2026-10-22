using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MultiIde.Core;
using MultiIde.Views;

namespace MultiIde;

/// <summary>
/// Owns the windows: the Welcome window when no project is open, and one
/// project window per open project (like JetBrains IDEs).
/// </summary>
public partial class App : Application
{
    private static IClassicDesktopStyleApplicationLifetime? Desktop =>
        Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
            IdeServices.Settings.Theme = ThemeManager.Migrate(IdeServices.Settings.Theme);
            ThemeManager.Apply(IdeServices.Settings.Theme, IdeServices.Plugins);
            Anim.ApplyGlobalStyles();
            Anim.InstallPopupAnimations();
            CrashReports.Recovered += OnRecoveredFromError;
            if (CrashReports.TakeLastFatal() is { } lastCrash)
                Avalonia.Threading.Dispatcher.UIThread.Post(() => ActiveWindow?.ShowBalloon("Aether closed unexpectedly last time",
                    "A crash report was saved. Sharing it helps fix the problem.",
                    true, ("Open report", () => OpenFile(lastCrash)), ("Show folder", () => CrashReports.OpenFolder(lastCrash))),
                    Avalonia.Threading.DispatcherPriority.Background);
            // Usage: MultiIde [folder] [file[:line]]
            if (desktop.Args is { Length: > 0 } args && Directory.Exists(args[0]))
            {
                var (file, line) = args.Length > 1 ? ParseFileArg(args[0], args[1]) : (null, null);
                var window = new MainWindow(Path.GetFullPath(args[0]), file, line);
                desktop.MainWindow = window;
            }
            else if (IdeServices.Settings.ReopenLastProject &&
                     IdeServices.Settings.RecentProjects.FirstOrDefault() is { } last && Directory.Exists(last))
            {
                desktop.MainWindow = new MainWindow(last);
            }
            else
            {
                desktop.MainWindow = new WelcomeWindow();
            }
        }
        base.OnFrameworkInitializationCompleted();
    }



    private static (string? File, int? Line) ParseFileArg(string folder, string arg)
    {
        int? line = null;
        var colon = arg.LastIndexOf(':');
        if (colon > 2 && int.TryParse(arg[(colon + 1)..], out var l)) (arg, line) = (arg[..colon], l);
        return (Path.IsPathRooted(arg) ? arg : Path.Combine(folder, arg), line);
    }

    private static IEnumerable<Window> Windows => Desktop?.Windows ?? (IEnumerable<Window>)Array.Empty<Window>();

    private static IdeWindow? ActiveWindow =>
        Windows.OfType<IdeWindow>().FirstOrDefault(w => w.IsActive) ?? Desktop?.MainWindow as IdeWindow ?? Windows.OfType<IdeWindow>().FirstOrDefault();

    private static DateTime _lastErrorBalloon;

    /// <summary>Aether caught an error and kept running: tell the user once in a while, with the report.</summary>
    private static void OnRecoveredFromError(string report, Exception ex)
    {
        if (DateTime.UtcNow - _lastErrorBalloon < TimeSpan.FromSeconds(15)) return;
        _lastErrorBalloon = DateTime.UtcNow;
        ActiveWindow?.ShowBalloon("Aether hit an error and recovered",
            $"{CrashReports.Summary(ex)}. Your work is safe; a report was saved.",
            true, ("Open report", () => OpenFile(report)), ("Show folder", () => CrashReports.OpenFolder(report)));
    }

    /// <summary>Opens a report in the active project window's editor, or with the system viewer.</summary>
    private static void OpenFile(string path)
    {
        if ((Windows.OfType<MainWindow>().FirstOrDefault(w => w.IsActive) ?? Windows.OfType<MainWindow>().FirstOrDefault()) is { } main)
            _ = main.OpenFileAsync(path);
        else
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); } catch { /* no viewer */ }
    }

    /// <summary>Opens a project in its own window (or focuses it if already open) and closes the Welcome window.</summary>
    public static void OpenProject(string path, string? file = null, Window? from = null)
    {
        if (!Directory.Exists(path))
        {
            (from as IdeWindow)?.Notify($"Folder not found: {path}", true);
            IdeServices.Settings.RecentProjects.Remove(path);
            IdeServices.Settings.Save();
            return;
        }
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        var existing = Windows.OfType<MainWindow>().FirstOrDefault(w => w.ProjectRoot is { } r && IdePaths.Key(r) == IdePaths.Key(full));
        if (existing is not null)
        {
            existing.Activate();
            if (file is not null) _ = existing.OpenFileAsync(file);
        }
        else
        {
            var window = new MainWindow(full, file);
            if (Desktop is { } d) d.MainWindow = window;
            window.Show();
        }
        foreach (var welcome in Windows.OfType<WelcomeWindow>().ToList()) welcome.Close();
    }

    /// <summary>Shows (or focuses) the Welcome window.</summary>
    public static void ShowWelcome(string page = "Projects")
    {
        var welcome = Windows.OfType<WelcomeWindow>().FirstOrDefault();
        if (welcome is null)
        {
            welcome = new WelcomeWindow(page);
            if (Desktop is { } d) d.MainWindow = welcome;
            welcome.Show();
        }
        else
        {
            welcome.ShowPage(page);
            welcome.Activate();
        }
    }

    /// <summary>Switches the color theme everywhere and remembers it.</summary>
    public static void SetTheme(string id)
    {
        if (!ThemeManager.Apply(id, IdeServices.Plugins)) return;
        IdeServices.Settings.Theme = id;
        IdeServices.Settings.Save();
    }

    /// <summary>Re-applies editor settings in every open project window.</summary>
    public static void BroadcastSettings()
    {
        Anim.ApplyGlobalStyles();
        foreach (var w in Windows.OfType<IdeWindow>()) w.ApplySettingsToEditors();
    }
}
