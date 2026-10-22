using System.Text.Json;

namespace MultiIde.Core;

/// <summary>
/// Per-user, per-project state in <c>.aether/workspace.json</c> (like IntelliJ's workspace.xml):
/// selected run configuration and the editor session. Not meant to be committed.
/// </summary>
public sealed class ProjectState
{
    public string? SelectedRunConfig { get; set; }
    public List<OpenFileState> OpenFiles { get; set; } = new();
    public string? ActiveFile { get; set; }
    public bool ProjectPanelVisible { get; set; } = true;

    public sealed class OpenFileState
    {
        public string Path { get; set; } = "";
        public int Line { get; set; } = 1;
        public int Column { get; set; } = 1;
    }

    private static string FileFor(string root) => System.IO.Path.Combine(IdePaths.ProjectDir(root), "workspace.json");

    public static ProjectState Load(string root)
    {
        try
        {
            if (File.Exists(FileFor(root)))
                return JsonSerializer.Deserialize<ProjectState>(File.ReadAllText(FileFor(root)), Settings.Json) ?? new();
        }
        catch
        {
            // Corrupt state is not worth an error; start fresh.
        }
        return new();
    }

    public void Save(string root)
    {
        try
        {
            var dir = IdePaths.ProjectDir(root);
            Directory.CreateDirectory(dir);
            var ignore = System.IO.Path.Combine(dir, ".gitignore");
            if (!File.Exists(ignore)) File.WriteAllText(ignore, "# Local IDE state; run.json is meant to be shared.\nworkspace.json\n");
            File.WriteAllText(FileFor(root), JsonSerializer.Serialize(this, Settings.Json));
        }
        catch
        {
            // Read-only project folders are fine.
        }
    }
}
