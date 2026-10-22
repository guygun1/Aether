using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using MultiIde.Core;
using MultiIde.Protocol;

namespace MultiIde.Views;

/// <summary>
/// "External Libraries" in the project tree, like IntelliJ: Cargo crate sources (cargo metadata),
/// the Rust standard library, and NuGet packages (obj/project.assets.json) whose DLLs expand
/// into decompiled C# (ilspycmd).
/// </summary>
public partial class MainWindow
{
    private FileNode? _externalNode;

    private async Task RefreshExternalLibrariesAsync()
    {
        FileNode.DllExpander = DecompileAsync;
        if (_root is null || _rootNode is null) return;
        if (!Settings.ShowExternalLibraries)
        {
            ProjectTree.ItemsSource = new[] { _rootNode };
            return;
        }
        var root = _root;
        var groups = new List<FileNode>();
        var cargo = await Task.Run(() => CargoCrates(root));
        if (cargo.Count > 0) groups.AddRange(cargo);
        var nuget = await Task.Run(() => NuGetPackages(root));
        if (nuget.Count > 0) groups.Add(Group("NuGet packages", nuget));
        if (_root != root) return;

        _externalNode = groups.Count == 0 ? null : Group("External Libraries", groups, "");
        ProjectTree.ItemsSource = _externalNode is null ? new[] { _rootNode } : new[] { _rootNode, _externalNode };
    }

    private FileNode Group(string name, List<FileNode> children, string glyph = "") =>
        new(name, "", true, Plugins) { FixedChildren = children, Glyph = glyph };

    // ---- Rust -------------------------------------------------------------------

    private List<FileNode> CargoCrates(string root)
    {
        var result = new List<FileNode>();
        if (!File.Exists(Path.Combine(root, "Cargo.toml")) || ProcessUtil.Resolve("cargo") is null) return result;
        try
        {
            var (code, stdout, _) = ProcessUtil.RunCaptureAsync("cargo metadata --format-version 1", root).GetAwaiter().GetResult();
            if (code != 0) return result;
            var crates = (JsonNode.Parse(stdout)?["packages"] as JsonArray ?? new JsonArray())
                .Where(p => p?["source"] is not null) // registry / git crates, not workspace members
                .Select(p => (Name: LspUtil.Str(p?["name"]), Version: LspUtil.Str(p?["version"]), Dir: Path.GetDirectoryName(LspUtil.Str(p?["manifest_path"]))!))
                .Where(c => Directory.Exists(c.Dir))
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Select(c => new FileNode($"{c.Name} {c.Version}", c.Dir, true, Plugins))
                .ToList();
            if (crates.Count > 0) result.Add(Group($"Cargo crates ({crates.Count})", crates));
        }
        catch
        {
            // cargo metadata failing (offline, broken manifest) just hides the group.
        }

        try
        {
            var (code, sysroot, _) = ProcessUtil.RunCaptureAsync("rustc --print sysroot", root).GetAwaiter().GetResult();
            var std = Path.Combine(sysroot.Trim(), "lib", "rustlib", "src", "rust", "library");
            if (code == 0 && Directory.Exists(std)) result.Add(new FileNode("Rust standard library", std, true, Plugins));
        }
        catch
        {
            // rust-src component not installed.
        }
        return result;
    }

    // ---- .NET -------------------------------------------------------------------

    private List<FileNode> NuGetPackages(string root)
    {
        var packages = new Dictionary<string, FileNode>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> projects;
        try { projects = Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories).Where(p => !IsExcluded(p)).Take(50).ToList(); }
        catch { return new(); }

        foreach (var project in projects)
        {
            var assets = Path.Combine(Path.GetDirectoryName(project)!, "obj", "project.assets.json");
            if (!File.Exists(assets)) continue;
            try
            {
                var doc = JsonNode.Parse(File.ReadAllText(assets));
                var folder = (doc?["packageFolders"] as JsonObject)?.FirstOrDefault().Key;
                if (folder is null) continue;
                foreach (var (key, lib) in doc?["libraries"] as JsonObject ?? new JsonObject())
                {
                    if (LspUtil.Str(lib?["type"]) != "package" || packages.ContainsKey(key)) continue;
                    var dir = Path.Combine(folder, LspUtil.Str(lib?["path"]).Replace('/', Path.DirectorySeparatorChar));
                    if (Directory.Exists(dir)) packages[key] = new FileNode(key.Replace('/', ' '), dir, true, Plugins);
                }
            }
            catch
            {
                // Skip unreadable assets files.
            }
        }
        return packages.Values.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Decompiles a library DLL to C# (cached) and returns the output folder.</summary>
    private async Task<string?> DecompileAsync(string dll)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dll + File.GetLastWriteTimeUtc(dll).Ticks)))[..10];
        var output = Path.Combine(IdePaths.Decompiled, $"{Path.GetFileNameWithoutExtension(dll)}-{hash}");
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) return output;

        if (ProcessUtil.Resolve("ilspycmd") is null)
        {
            Dispatcher.UIThread.Post(() => ShowBalloon("Decompiler not installed",
                "Viewing compiled dependencies uses ILSpy's command-line decompiler (ilspycmd).", false,
                ("Install ilspycmd", () =>
                {
                    ShowBottom("run");
                    RunTab("Install ilspycmd", false).Run("dotnet tool install --global ilspycmd", _root, exited: code =>
                    {
                        if (code == 0) Notify("ilspycmd installed. Expand the DLL again to decompile it.");
                    });
                })));
            return null;
        }

        Status($"Decompiling {Path.GetFileName(dll)}…");
        Directory.CreateDirectory(output);
        var (exit, _, stderr) = await ProcessUtil.RunCaptureAsync($"ilspycmd -p -o \"{output}\" \"{dll}\"", IdePaths.Decompiled);
        if (exit != 0 || !Directory.EnumerateFileSystemEntries(output).Any())
        {
            try { Directory.Delete(output, true); } catch { /* best effort */ }
            Notify($"Could not decompile {Path.GetFileName(dll)}:\n{Tail(stderr)}", true);
            return null;
        }
        Status($"Decompiled {Path.GetFileName(dll)}");
        return output;
    }
}
