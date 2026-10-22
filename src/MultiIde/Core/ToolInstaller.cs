using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace MultiIde.Core;

/// <summary>
/// Where a plugin's language server (or other tool) can be downloaded, per operating system.
/// Aether extracts it into ~/.aether/tools/&lt;id&gt; and finds the executable there automatically.
/// </summary>
public sealed class DownloadSpec
{
    /// <summary>Archive (.zip / .tar.gz) or plain executable URL, keyed by "windows", "linux", "macos".</summary>
    public Dictionary<string, string> Url { get; set; } = new();
    /// <summary>Executable to look for inside the archive (".exe" is added on Windows).</summary>
    public string Executable { get; set; } = "";

    public string? UrlForThisOs() =>
        Url.GetValueOrDefault(OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux");
}

/// <summary>Downloads and unpacks tools for plugins. Never needs administrator rights.</summary>
public static class ToolInstaller
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public static string ToolsDir => Path.Combine(IdePaths.Home, "tools");

    /// <summary>Downloads and extracts a tool; returns the executable path.</summary>
    public static async Task<string> InstallAsync(string id, DownloadSpec spec, Action<string> log, CancellationToken ct = default)
    {
        var url = spec.UrlForThisOs() ?? throw new InvalidOperationException("No download is available for this operating system.");
        var target = Path.Combine(ToolsDir, id);
        var temp = Path.Combine(Path.GetTempPath(), $"aether-{id}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            log($"Downloading {url}");
            var file = Path.Combine(temp, Path.GetFileName(new Uri(url).LocalPath));
            using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                await using var output = File.Create(file);
                await response.Content.CopyToAsync(output, ct);
            }
            log($"Downloaded {new FileInfo(file).Length / 1024 / 1024.0:0.0} MB, unpacking…");

            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.CreateDirectory(target);
            if (file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                ZipFile.ExtractToDirectory(file, target, overwriteFiles: true);
            }
            else if (file.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
            {
                // Windows 10+ ships bsdtar as tar.exe.
                using var tar = Process.Start(new ProcessStartInfo("tar", $"-xzf \"{file}\" -C \"{target}\"") { UseShellExecute = false, CreateNoWindow = true })!;
                await tar.WaitForExitAsync(ct);
                if (tar.ExitCode != 0) throw new InvalidOperationException("Could not unpack the archive (tar failed).");
            }
            else if (file.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            {
                await using var input = new GZipStream(File.OpenRead(file), CompressionMode.Decompress);
                await using var output = File.Create(Path.Combine(target, ExecutableName(spec.Executable)));
                await input.CopyToAsync(output, ct);
            }
            else
            {
                File.Copy(file, Path.Combine(target, ExecutableName(spec.Executable)), overwrite: true);
            }

            var exe = Directory.EnumerateFiles(target, ExecutableName(spec.Executable), SearchOption.AllDirectories).FirstOrDefault()
                      ?? Directory.EnumerateFiles(target, spec.Executable, SearchOption.AllDirectories).FirstOrDefault()
                      ?? throw new InvalidOperationException($"{spec.Executable} was not found in the download.");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(exe, File.GetUnixFileMode(exe) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute);
            log($"Installed {exe}");
            return exe;
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* temp cleanup is best effort */ }
        }
    }

    private static string ExecutableName(string name) =>
        OperatingSystem.IsWindows() && !Path.HasExtension(name) ? name + ".exe" : name;

    /// <summary>Folders searched for tools installed this way (each tool folder and its bin/).</summary>
    public static IEnumerable<string> SearchDirs()
    {
        if (!Directory.Exists(ToolsDir)) yield break;
        foreach (var dir in Directory.GetDirectories(ToolsDir))
        {
            yield return dir;
            yield return Path.Combine(dir, "bin");
        }
    }

    public static string Platform => RuntimeInformation.OSArchitecture.ToString();
}
