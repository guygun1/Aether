using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MultiIde.Core;
using MultiIde.Protocol;

namespace MultiIde.Views;

/// <summary>
/// Git integration: commit dialog, push/pull, init, and sharing a project on GitHub / GitLab.
/// Hosting goes through the official CLIs (gh, glab), so sign-in uses their own secure flow.
/// </summary>
public partial class MainWindow
{
    /// <summary>The repository containing the project (may be a parent folder), or null.</summary>
    private string? _gitTopLevel;
    private bool IsGitRepo => _gitTopLevel is not null;

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
        await RefreshGitAsync();
        if (IsGitRepo) { Notify($"This project is already in a Git repository ({_gitTopLevel})."); return; }
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

    private sealed record HostInfo(string Cli, string Display, string Install, string Url, string Login);

    private static HostInfo Host(string host) => host == "github"
        ? new("gh", "GitHub", "winget install --id GitHub.cli -e --accept-source-agreements --accept-package-agreements", "https://cli.github.com", "gh auth login")
        : new("glab", "GitLab", "winget install --id GLab.GLab -e --accept-source-agreements --accept-package-agreements", "https://gitlab.com/gitlab-org/cli", "glab auth login");

    /// <summary>Checks that the hosting CLI is installed and signed in; offers install / sign-in otherwise.</summary>
    private async Task<bool> RequireHostCliAsync(HostInfo h)
    {
        if (ProcessUtil.Resolve(h.Cli) is null)
        {
            ShowBalloon($"{h.Display} CLI ({h.Cli}) is not installed", $"{h.Display} integration uses the official {h.Cli} tool, which also handles sign-in securely.", false,
                ("Install with winget", () => { ShowBottom("run"); RunTab($"Install {h.Cli}", false).Run(h.Install, _root); }),
                ("Download", () => OpenUrl(h.Url)));
            return false;
        }
        var auth = await ProcessUtil.RunCaptureAsync($"{h.Cli} auth status", _root);
        if (auth.Code != 0)
        {
            ShowBalloon($"Sign in to {h.Display}", $"Run '{h.Login}' once; it opens a browser to authorize. Then try again.", false,
                ("Open sign-in console", () => OpenConsole(h.Login)));
            return false;
        }
        return true;
    }

    /// <summary>The signed-in account and the organizations / groups it can create repositories in.</summary>
    private async Task<(string? Account, List<string> Owners)> HostOwnersAsync(string host)
    {
        var owners = new List<string>();
        string? account;
        if (host == "github")
        {
            account = (await ProcessUtil.RunCaptureAsync("gh api user --jq .login", _root)).Stdout.Trim();
            var orgs = await ProcessUtil.RunCaptureAsync("gh api user/orgs --paginate --jq \".[].login\"", _root);
            if (orgs.Code == 0) owners.AddRange(orgs.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        else
        {
            account = (await ProcessUtil.RunCaptureAsync("glab api user", _root)) is { Code: 0 } u
                ? LspUtil.Str(System.Text.Json.Nodes.JsonNode.Parse(u.Stdout)?["username"]) : null;
            var groups = await ProcessUtil.RunCaptureAsync("glab api \"groups?min_access_level=30&per_page=100\"", _root);
            if (groups.Code == 0 && System.Text.Json.Nodes.JsonNode.Parse(groups.Stdout) is System.Text.Json.Nodes.JsonArray arr)
                owners.AddRange(arr.Select(g => LspUtil.Str(g?["full_path"])).Where(p => p.Length > 0));
        }
        if (string.IsNullOrWhiteSpace(account)) account = null;
        if (account is not null) owners.Insert(0, account);
        return (account, owners.Distinct().ToList());
    }

    /// <summary>
    /// Share Project on GitHub / GitLab, like IntelliJ: account and organizations are filled in,
    /// the name is checked for availability, and the first commit and push happen automatically.
    /// </summary>
    private async Task ShareProjectAsync(string host)
    {
        if (_root is null) { Notify("Open a project first"); return; }
        if (ProcessUtil.Resolve("git") is null) { RequireGit(); return; }
        var h = Host(host);
        if (!await RequireHostCliAsync(h)) return;

        Status($"Loading your {h.Display} account…");
        var (account, owners) = await HostOwnersAsync(host);
        await RefreshGitAsync();
        var remotes = IsGitRepo ? (await GitExecAsync("remote")).Output.Split('\n', StringSplitOptions.RemoveEmptyEntries) : [];

        var name = new TextBox { Text = Path.GetFileName(_root).Replace(' ', '-') };
        var owner = new ComboBox { ItemsSource = owners, SelectedIndex = owners.Count > 0 ? 0 : -1, MinWidth = 260 };
        var isPrivate = new CheckBox { Content = "Private", IsChecked = true };
        var internalVis = new CheckBox { Content = "Internal (visible to your organization)", IsVisible = host == "gitlab" };
        var remote = new TextBox { Text = remotes.Contains("origin") ? host : "origin" };
        var description = new TextBox { Watermark = "Optional" };
        var availability = new TextBlock { FontSize = 12 };
        var accountLine = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = account is null ? "Account unknown" : $"Signed in as {account}", Classes = { "muted" }, FontSize = 12, VerticalAlignment = VerticalAlignment.Center },
            },
        };
        var switchAccount = new Button { Classes = { "flat" }, Padding = new Thickness(0), Content = new TextBlock { Text = "Switch account…", Foreground = Res("Link"), FontSize = 12 } };
        switchAccount.Click += (_, _) => OpenConsole(h.Login);
        accountLine.Children.Add(switchAccount);

        // Live availability check (debounced) for owner/name.
        CancellationTokenSource? checkCts = null;
        async void CheckAvailability()
        {
            checkCts?.Cancel();
            var cts = checkCts = new CancellationTokenSource();
            var full = $"{owner.SelectedItem}/{(name.Text ?? "").Trim()}";
            if (owner.SelectedItem is null || (name.Text ?? "").Trim().Length == 0) { availability.Text = ""; return; }
            try
            {
                await Task.Delay(350, cts.Token);
                var probe = host == "github"
                    ? await ProcessUtil.RunCaptureAsync($"gh repo view \"{full}\" --json name", _root, ct: cts.Token)
                    : await ProcessUtil.RunCaptureAsync($"glab repo view \"{full}\" -F json", _root, ct: cts.Token);
                if (cts.IsCancellationRequested) return;
                var taken = probe.Code == 0;
                availability.Text = taken ? $"✖ {full} already exists" : $"✓ {full} is available";
                availability.Foreground = Res(taken ? "Error" : "Success");
            }
            catch (OperationCanceledException)
            {
            }
        }
        name.TextChanged += (_, _) => CheckAvailability();
        owner.SelectionChanged += (_, _) => CheckAvailability();
        CheckAvailability();

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
                accountLine,
                Row("Repository name:", name),
                Row(host == "github" ? "Owner:" : "Namespace:", owner),
                new ContentControl { Content = availability, Margin = new Thickness(130, -4, 0, 0) },
                Row("", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { isPrivate, internalVis } }),
                Row("Remote:", remote),
                Row("Description:", description),
                new TextBlock
                {
                    Text = IsGitRepo
                        ? "Your commits will be pushed to the new repository."
                        : "A Git repository will be created and all files committed first (respecting .gitignore).",
                    Classes = { "muted" }, TextWrapping = TextWrapping.Wrap, FontSize = 12,
                },
            },
        };

        async Task Share()
        {
            var repo = (name.Text ?? "").Trim();
            var remoteName = (remote.Text ?? "origin").Trim();
            if (repo.Length == 0 || owner.SelectedItem is not string ownerName) { Notify("Choose an owner and a repository name.", true); return; }
            if (remotes.Contains(remoteName)) { Notify($"A remote named '{remoteName}' already exists. Pick another remote name.", true); return; }
            _overlayCancel = null;
            CloseOverlay();

            if (!IsGitRepo) await GitInitAsync();
            if ((await GitExecAsync("rev-parse", "HEAD")).Code != 0)
            {
                await GitExecAsync("add", "-A");
                var first = await GitExecAsync("commit", "-m", "Initial commit");
                if (first.Code != 0) { Notify(first.Output, true); return; }
            }
            var full = $"{ownerName}/{repo}";
            var vis = internalVis.IsChecked == true ? "internal" : isPrivate.IsChecked == true ? "private" : "public";
            var desc = (description.Text ?? "").Replace("\"", "'");
            var command = host == "github"
                ? $"gh repo create \"{full}\" --{vis} --source=. --remote={remoteName} --push" + (desc.Length > 0 ? $" --description \"{desc}\"" : "")
                : $"glab repo create \"{full}\" --{vis} --remoteName {remoteName}" + (desc.Length > 0 ? $" --description \"{desc}\"" : "") + $" && git push -u {remoteName} HEAD";
            ShowBottom("run");
            RunTab($"Share on {h.Display}", false).Run(command, _gitTopLevel ?? _root, exited: code =>
            {
                if (code == 0)
                    ShowBalloon($"Shared on {h.Display}", $"{full} was created and your commits were pushed.", false,
                        ($"Open on {h.Display}", () => OpenUrl(host == "github" ? $"https://github.com/{full}" : $"https://gitlab.com/{full}")));
                _ = RefreshGitAsync();
            });
        }

        ShowOverlay(Dialog($"Share Project on {h.Display}", body, 580, ("Share", () => _ = Share()), ("Cancel", CloseOverlay)));
    }

    /// <summary>Which hosting service the "origin" (or first) remote points to: github, gitlab or null.</summary>
    private async Task<(string? Host, string Remote)> RemoteHostAsync()
    {
        var remotes = (await GitExecAsync("remote", "-v")).Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var line = remotes.FirstOrDefault(l => l.StartsWith("origin")) ?? remotes.FirstOrDefault() ?? "";
        var name = line.Split('\t', ' ').FirstOrDefault() ?? "origin";
        return (line.Contains("github.com") ? "github" : line.Contains("gitlab") ? "gitlab" : null, name);
    }

    /// <summary>Opens the current file (at the caret line) on GitHub / GitLab, like IntelliJ's "Open on GitHub".</summary>
    private async Task OpenOnHostAsync()
    {
        if (!RequireGit()) return;
        var (host, _) = await RemoteHostAsync();
        if (host is null) { Notify("This repository has no GitHub or GitLab remote. Use Share Project to create one."); return; }
        var h = Host(host);
        if (ProcessUtil.Resolve(h.Cli) is null) { await RequireHostCliAsync(h); return; }
        var target = _active is { } v && _gitTopLevel is not null
            ? $"\"{Path.GetRelativePath(_gitTopLevel, v.Doc.FilePath).Replace('\\', '/')}:{v.Editor.TextArea.Caret.Line}\""
            : "";
        var command = host == "github" ? $"gh browse {target}" : "glab repo view --web";
        var result = await ProcessUtil.RunCaptureAsync(command, _gitTopLevel ?? _root);
        if (result.Code != 0) Notify(Tail(result.Stderr.Length > 0 ? result.Stderr : result.Stdout), true);
    }

    /// <summary>Opens the host's page to create a pull / merge request for the current branch.</summary>
    private async Task CreatePullRequestAsync()
    {
        if (!RequireGit()) return;
        var (host, remoteName) = await RemoteHostAsync();
        if (host is null) { Notify("This repository has no GitHub or GitLab remote. Use Share Project to create one."); return; }
        var h = Host(host);
        if (!await RequireHostCliAsync(h)) return;
        ShowBottom("run");
        var push = $"git push -u {remoteName} HEAD";
        RunTab(host == "github" ? "Pull Request" : "Merge Request", false).Run(
            host == "github" ? $"{push} && gh pr create --web" : $"{push} && glab mr create --web", _gitTopLevel ?? _root);
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
