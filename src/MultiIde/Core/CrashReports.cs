using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Threading;

namespace MultiIde.Core;

/// <summary>
/// Crash reports in <c>~/.aether/crash-reports</c>. Errors on the UI thread (e.g. while typing) are reported and
/// recovered from instead of closing Aether; a fatal crash is reported and announced on the next start.
/// </summary>
public static class CrashReports
{
    public static string Folder => Path.Combine(IdePaths.Home, "crash-reports");
    private static string LastFatalMarker => Path.Combine(Folder, "last-fatal.txt");
    private const int KeepReports = 50;

    /// <summary>Raised on the UI thread after Aether recovered from an error: (report path, exception).</summary>
    public static event Action<string, Exception>? Recovered;

    /// <summary>Optional extra context for reports, e.g. the open file and language.</summary>
    public static Func<string?>? Context { get; set; }

    private static readonly Queue<DateTime> RecentRecoveries = new();

    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex && Write(ex, "fatal") is { } path)
                TryWrite(LastFatalMarker, path);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write(e.Exception, "background task");
            e.SetObserved();
        };
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            // An error that keeps repeating (e.g. every frame) can't be recovered from: let it end the app.
            var now = DateTime.UtcNow;
            RecentRecoveries.Enqueue(now);
            while (RecentRecoveries.Count > 0 && now - RecentRecoveries.Peek() > TimeSpan.FromSeconds(10)) RecentRecoveries.Dequeue();
            if (RecentRecoveries.Count > 30) return;

            var path = Write(e.Exception, "recovered");
            e.Handled = true;
            if (path is not null)
                try { Recovered?.Invoke(path, e.Exception); } catch { /* never fail while reporting */ }
        };
    }

    /// <summary>Writes a report; returns its path (null if even that failed).</summary>
    public static string? Write(Exception ex, string kind)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var time = DateTime.Now;
            var path = Path.Combine(Folder, $"crash-{time:yyyyMMdd-HHmmss-fff}.txt");
            var sb = new StringBuilder();
            sb.AppendLine($"Aether crash report ({kind})");
            sb.AppendLine($"Time:     {time:yyyy-MM-dd HH:mm:ss zzz}");
            sb.AppendLine($"Version:  {Version}");
            sb.AppendLine($"OS:       {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
            sb.AppendLine($".NET:     {RuntimeInformation.FrameworkDescription}");
            sb.AppendLine($"Process:  {Environment.ProcessPath}");
            string? context = null;
            try { context = Context?.Invoke(); } catch { /* context is optional */ }
            if (!string.IsNullOrEmpty(context)) sb.AppendLine($"Context:  {context}");
            sb.AppendLine();
            sb.AppendLine(ex.ToString());
            File.WriteAllText(path, sb.ToString());
            Prune();
            return path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The report of a fatal crash in the previous session, once (null if there was none).</summary>
    public static string? TakeLastFatal()
    {
        try
        {
            if (!File.Exists(LastFatalMarker)) return null;
            var path = File.ReadAllText(LastFatalMarker).Trim();
            File.Delete(LastFatalMarker);
            return File.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>First line of the exception, for notifications: "IndexOutOfRangeException in SnippetSyntax.Parse".</summary>
    public static string Summary(Exception ex)
    {
        var frame = new StackTrace(ex).GetFrames().FirstOrDefault(f => f.GetMethod()?.DeclaringType?.Namespace?.StartsWith("MultiIde") == true)?.GetMethod();
        return frame is null ? ex.GetType().Name : $"{ex.GetType().Name} in {frame.DeclaringType?.Name}.{frame.Name}";
    }

    public static void OpenFolder(string? select = null)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            if (select is not null && OperatingSystem.IsWindows()) Process.Start("explorer.exe", $"/select,\"{select}\"");
            else Process.Start(new ProcessStartInfo(Folder) { UseShellExecute = true });
        }
        catch
        {
            // No file manager available.
        }
    }

    private static string Version =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";

    private static void Prune()
    {
        foreach (var old in Directory.GetFiles(Folder, "crash-*.txt").OrderByDescending(f => f).Skip(KeepReports))
            try { File.Delete(old); } catch { /* in use */ }
    }

    private static void TryWrite(string path, string text)
    {
        try { File.WriteAllText(path, text); } catch { /* best effort */ }
    }
}
