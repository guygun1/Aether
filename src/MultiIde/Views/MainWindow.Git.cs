using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MultiIde.Core;

namespace MultiIde.Views;

/// <summary>
/// Git integration: commit dialog, push/pull, init, and sharing a project on GitHub / GitLab.
/// Hosting goes through the official CLIs (gh, glab), so sign-in uses their own secure flow.
/// </summary>
public partial class MainWindow
{
    private bool IsGitRepo => _root is not null && Directory.Exists(Path.Combine(_root, ".git"));

    /// <summary>Runs git with arguments (no shell quoting issues) and captures the result.</summary>
    private async Task<(int Code, string Output)> GitExecAsync(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = _root!, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, ((await stdout) + (await stderr)).Trim());
    }

    private void GitRun(string args)
    {
        if (!RequireGit()) return;
        ShowBottom("run");
        RunTab("Git", false).Run("git " + args, _root, exited: __ => { _ = RefreshGitAsync(); });
    }

    private bool RequireGit()
    {
        if (_root is null) { Notify("Open a project first"); return false; }
        if (ProcessUtil.Resolve("git") is null)
        {
            ShowBalloon("Git is not installed", "Install Git to use version control.", true,
                ("Install with winget", () => { ShowBottom("run"); RunTab("Install Git", false).Run("winget install --id Git.Git -e --accept-source-agreements --accept-package-agreements", _root); }),
                ("Download", () => OpenUrl("https://git-scm.com/downloads")));
            return false;
        }
        if (!IsGitRepo)
        {
            ShowBalloon("Not a Git repository", "This project is not under version control yet.", false,
                ("Create Git Repository", () => _ = GitInitAsync()),
                ("Share on GitHub…", () => _ = ShareProjectAsync("github")),
                ("Share on GitLab…", () => _ = ShareProjectAsync("gitlab")));
            return false;
        }
        return true;
    }

    private async Task GitInitAsync()
    {
        if (_root is null) return;
        if (IsGitRepo) { Notify("This project already is a Git repository."); return; }
        var (code, output) = await GitExecAsync("init", "-b", "main");
        if (code != 0) { Notify(output, true); return; }
        var ignore = Path.Combine(_root, ".gitignore");
        if (!File.Exists(ignore)) await File.WriteAllTextAsync(ignore, "bin/\nobj/\ntarget/\nbuild/\n.vs/\n.idea/\n.aether/workspace.json\n");
        Notify("Git repository created. Use Commit (Ctrl+K) to make the first commit.");
        await RefreshGitAsync();
    }

    // ---- Commit dialog ----------------------------------------------------------

    private async Task ShowCommitDialogAsync()
    {
        if (!RequireGit()) return;
        await SaveAllAsync(quiet: true);
        var (code, status) = await GitExecAsync("status", "--porcelain=v1", "-uall");
        if (code != 0) { Notify(status, true); return; }
        var changes = status.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.Length > 3)
            .Select(l => (State: l[..2], Path: l[3..].Trim().Trim('"')))
            .Select(c => (c.State, Path: c.Path.Contains(" -> ") ? c.Path[(c.Path.IndexOf(" -> ") + 4)..] : c.Path))
            .ToList();
        if (changes.Count == 0) { Notify("Nothing to commit: the working tree is clean."); return; }

        var checks = new List<(CheckBox Box, string Path)>();
        var list = new StackPanel { Spacing = 2 };
        foreach (var (state, path) in changes)
        {
            var (label, color) = state.Trim() switch
            {
                "??" => ("new", "Error"),
                var s when s.Contains('A') => ("added", "Success"),
                var s when s.Contains('D') => ("deleted", "FgMuted"),
                var s when s.Contains('R') => ("renamed", "Link"),
                _ => ("modified", "Link"),
            };
            var box = new CheckBox
            {
                IsChecked = true,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    Children =
                    {
                        new TextBlock { Text = path, Foreground = Res(color), FontFamily = (FontFamily)Application.Current!.FindResource("MonoFont")!, FontSize = 12.5 },
                        new TextBlock { Text = label, Classes = { "muted" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center },
                    },
                },
            };
            checks.Add((box, path));
            list.Children.Add(box);
        }
        var all = new CheckBox { Content = $"{changes.Count} changed files", IsChecked = true, FontWeight = FontWeight.SemiBold };
        all.IsCheckedChanged += (_, _) => { foreach (var c in checks) c.Box.IsChecked = all.IsChecked; };
        var message = new TextBox { AcceptsReturn = true, Height = 100, Watermark = "Commit message", VerticalContentAlignment = VerticalAlignment.Top, TextWrapping = TextWrapping.Wrap };
        var amend = new CheckBox { Content = "Amend last commit" };
        var body = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                all,
                new Border { Child = new ScrollViewer { Content = list, MaxHeight = 260 }, BorderBrush = Res("Border"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(8) },
                message,
                amend,
            },
        };

        async Task Commit(bool push)
        {
            var files = checks.Where(c => c.Box.IsChecked == true).Select(c => c.Path).ToList();
            if (files.Count == 0) { Notify("Select at least one file.", true); return; }
            if (string.IsNullOrWhiteSpace(message.Text) && amend.IsChecked != true) { Notify("Write a commit message.", true); return; }
            _overlayCancel = null;
            CloseOverlay();
            var add = await GitExecAsync(new[] { "add", "-A", "--" }.Concat(files).ToArray());
            if (add.Code != 0) { Notify(add.Output, true); return; }
            var msgFile = Path.GetTempFileName();
            await File.WriteAllTextAsync(msgFile, message.Text ?? "");
            var args = new List<string> { "commit", "-F", msgFile };
            if (amend.IsChecked == true) args.Add("--amend");
            args.Add("--");
            args.AddRange(files);
            var commit = await GitExecAsync(args.ToArray());
            File.Delete(msgFile);
            if (commit.Code != 0) { Notify(commit.Output, true); return; }
            Notify($"Committed {files.Count} file(s): {commit.Output.Split('\n')[0]}");
            await RefreshGitAsync();
            if (push) GitRun("push -u origin HEAD");
        }

        ShowOverlay(Dialog("Commit Changes", body, 640,
            ("Commit", () => _ = Commit(false)),
            ("Commit and Push…", () => _ = Commit(true)),
            ("Cancel", CloseOverlay)));
    }

    // ---- Share on GitHub / GitLab -------------------------------------------------

    private async Task ShareProjectAsync(string host)
    {
        if (_root is null) { Notify("Open a project first"); return; }
        var (cli, display, install, url, login) = host == "github"
            ? ("gh", "GitHub", "winget install --id GitHub.cli -e --accept-source-agreements --accept-package-agreements", "https://cli.github.com", "gh auth login")
            : ("glab", "GitLab", "winget install --id GLab.GLab -e --accept-source-agreements --accept-package-agreements", "https://gitlab.com/gitlab-org/cli", "glab auth login");

        if (ProcessUtil.Resolve("git") is null) { RequireGit(); return; }
        if (ProcessUtil.Resolve(cli) is null)
        {
            ShowBalloon($"{display} CLI ({cli}) is not installed", $"Sharing on {display} uses the official {cli} tool, which also handles sign-in securely.", false,
                ("Install with winget", () => { ShowBottom("run"); RunTab($"Install {cli}", false).Run(install, _root); }),
                ("Download", () => OpenUrl(url)));
            return;
        }
        var auth = await ProcessUtil.RunCaptureAsync($"{cli} auth status", _root);
        if (auth.Code != 0)
        {
            ShowBalloon($"Sign in to {display}", $"Run '{login}' once; it opens a browser to authorize the CLI. Then share again.", false,
                ("Open sign-in console", () => OpenConsole(login)));
            return;
        }

        var name = new TextBox { Text = Path.GetFileName(_root) };
        var description = new TextBox { Watermark = "Optional" };
        var visibility = new ComboBox { ItemsSource = new[] { "Private", "Public", host == "gitlab" ? "Internal" : null }.Where(x => x is not null).ToList(), SelectedIndex = 0, MinWidth = 160 };
        var ns = new TextBox { Watermark = host == "github" ? "Your account (or an organization name)" : "Your namespace (or a group path)" };
        Control Row(string label, Control input) => new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("130,*"),
            Children = { new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }, new ContentControl { Content = input, [Grid.ColumnProperty] = 1 } },
        };
        var body = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Row("Repository name:", name),
                Row(host == "github" ? "Owner:" : "Group:", ns),
                Row("Visibility:", visibility),
                Row("Description:", description),
                new TextBlock { Text = IsGitRepo ? "Your commits will be pushed to the new repository." : "A Git repository will be created and all files committed first (respecting .gitignore).", Classes = { "muted" }, TextWrapping = TextWrapping.Wrap, FontSize = 12 },
            },
        };

        async Task Share()
        {
            var repo = (name.Text ?? "").Trim();
            if (repo.Length == 0) { Notify("Enter a repository name.", true); return; }
            _overlayCancel = null;
            CloseOverlay();
            if (!IsGitRepo) await GitInitAsync();
            var head = await GitExecAsync("rev-parse", "HEAD");
            if (head.Code != 0)
            {
                await GitExecAsync("add", "-A");
                var first = await GitExecAsync("commit", "-m", "Initial commit");
                if (first.Code != 0) { Notify(first.Output, true); return; }
            }
            var full = string.IsNullOrWhiteSpace(ns.Text) ? repo : $"{ns.Text.Trim()}/{repo}";
            var vis = ((string)visibility.SelectedItem!).ToLowerInvariant();
            var desc = (description.Text ?? "").Replace("\"", "'");
            var command = host == "github"
                ? $"gh repo create \"{full}\" --{vis} --source=. --remote=origin --push" + (desc.Length > 0 ? $" --description \"{desc}\"" : "")
                : $"glab repo create \"{full}\" --{vis} --remoteName origin" + (desc.Length > 0 ? $" --description \"{desc}\"" : "") + " && git push -u origin HEAD";
            ShowBottom("run");
            RunTab($"Share on {display}", false).Run(command, _root, exited: code =>
            {
                if (code == 0) Notify($"Project shared on {display} as {full}.");
                _ = RefreshGitAsync();
            });
        }

        ShowOverlay(Dialog($"Share Project on {display}", body, 560, ("Share", () => _ = Share()), ("Cancel", CloseOverlay)));
    }

    /// <summary>Opens a real console window (for interactive CLIs such as "gh auth login").</summary>
    private void OpenConsole(string command)
    {
        try
        {
            if (IdePaths.IsWindows)
                Process.Start(new ProcessStartInfo("cmd.exe", $"/k {command}") { UseShellExecute = true, WorkingDirectory = _root ?? "" });
            else
                Process.Start(new ProcessStartInfo("x-terminal-emulator", $"-e {command}") { UseShellExecute = true });
        }
        catch (Exception e)
        {
            Notify($"Could not open a console: {e.Message}. Run '{command}' in a terminal.", true);
        }
    }
}
