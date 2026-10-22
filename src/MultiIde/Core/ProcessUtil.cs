using System.Diagnostics;
using System.Text;

namespace MultiIde.Core;

public static class ProcessUtil
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>Well-known tool folders that are often missing from PATH for GUI apps.</summary>
    private static IEnumerable<string> ExtraDirs()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(home, ".cargo", "bin");
        yield return Path.Combine(home, ".dotnet", "tools");
        yield return Path.Combine(home, ".local", "bin");
        foreach (var dir in ToolInstaller.SearchDirs()) yield return dir;
        if (IdePaths.IsWindows)
        {
            yield return @"C:\Program Files\LLVM\bin";
            yield return @"C:\Program Files\CMake\bin";
        }
    }

    /// <summary>Resolves a program name through PATH (and PATHEXT on Windows).</summary>
    public static string? Resolve(string program)
    {
        if (Path.IsPathRooted(program))
            return File.Exists(program) ? program : null;

        var exts = IdePaths.IsWindows
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : new[] { "" };
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Concat(ExtraDirs());

        foreach (var dir in dirs)
        {
            if (IdePaths.IsWindows && Path.HasExtension(program))
            {
                var direct = Path.Combine(dir, program);
                if (File.Exists(direct)) return direct;
            }
            foreach (var ext in exts)
            {
                var candidate = Path.Combine(dir, program + ext.ToLowerInvariant());
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    /// <summary>Start info for a long-running stdio child (language / MCP server).</summary>
    public static ProcessStartInfo StdioStartInfo(string program, IEnumerable<string> args, string? cwd)
    {
        var resolved = Resolve(program) ?? program;
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8NoBom,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
        };
        var ext = Path.GetExtension(resolved).ToLowerInvariant();
        if (ext is ".cmd" or ".bat")
        {
            psi.FileName = "cmd.exe";
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(resolved);
        }
        else
        {
            psi.FileName = resolved;
        }
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (!string.IsNullOrEmpty(cwd) && Directory.Exists(cwd)) psi.WorkingDirectory = cwd;
        return psi;
    }

    /// <summary>Start info that runs a command line through the platform shell.</summary>
    public static ProcessStartInfo ShellStartInfo(string commandLine, string? cwd)
    {
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
        };
        if (IdePaths.IsWindows)
        {
            psi.FileName = "cmd.exe";
            psi.Arguments = "/d /s /c \"" + commandLine + "\"";
        }
        else
        {
            psi.FileName = "/bin/sh";
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(commandLine);
        }
        if (!string.IsNullOrEmpty(cwd) && Directory.Exists(cwd)) psi.WorkingDirectory = cwd;
        // Ask common tools for colour-free, UTF-8 output.
        psi.Environment["NO_COLOR"] = "1";
        psi.Environment["CARGO_TERM_COLOR"] = "never";
        psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        return psi;
    }

    /// <summary>Runs a shell command to completion, optionally feeding stdin, and captures its output.</summary>
    public static async Task<(int Code, string Stdout, string Stderr)> RunCaptureAsync(string commandLine, string? cwd, string? stdin = null, IDictionary<string, string>? env = null, CancellationToken ct = default)
    {
        var psi = ShellStartInfo(commandLine, cwd);
        psi.StandardInputEncoding = Utf8NoBom;
        if (env is not null) foreach (var (k, v) in env) psi.Environment[k] = v;
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Process did not start");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        if (stdin is not null) await p.StandardInput.WriteAsync(stdin);
        p.StandardInput.Close();
        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { /* already gone */ }
            throw;
        }
        return (p.ExitCode, await stdout, await stderr);
    }

    public static void OpenInFileManager(string path)
    {
        try
        {
            if (IdePaths.IsWindows)
                Process.Start(new ProcessStartInfo("explorer.exe", File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\""));
            else if (OperatingSystem.IsMacOS())
                Process.Start("open", File.Exists(path) ? new[] { "-R", path } : new[] { path });
            else
                Process.Start("xdg-open", File.Exists(path) ? Path.GetDirectoryName(path)! : path);
        }
        catch
        {
            // Best effort only.
        }
    }
}
