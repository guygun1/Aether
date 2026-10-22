using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

// Packages a self-contained Aether publish folder for Linux or macOS as a .tar.gz.
// Tar (not zip) because it keeps Unix permissions, so the executables stay executable,
// and it can be written correctly from Windows.
//
//   linux <publishDir> <out.tar.gz> <version>   → aether-<version>-<rid>/ with Aether, icon and desktop entry
//   mac   <publishDir> <out.tar.gz> <version>   → Aether.app bundle (Info.plist, icon)

// clean <dir>: removes a folder (a cross-platform "rm -rf" for run configurations).
if (args is ["clean", var target])
{
    if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
    return 0;
}
// list <dir>...: prints the packages in the given folders (the summary of "Build All Platforms").
if (args is ["list", .. var folders])
{
    foreach (var f in folders.Where(Directory.Exists).SelectMany(d => Directory.EnumerateFiles(d)).Where(f => f.EndsWith(".msi") || f.EndsWith(".tar.gz")).Order())
        Console.WriteLine($"{new FileInfo(f).Length / 1024.0 / 1024.0,7:0.0} MB  {Path.GetRelativePath(Environment.CurrentDirectory, f)}");
    return 0;
}
if (args.Length < 4 || args[0] is not ("linux" or "mac"))
{
    Console.Error.WriteLine("usage: Packager linux|mac <publishDir> <out.tar.gz> <version>   or   Packager clean <dir>");
    return 2;
}
var (kind, publishDir, output, version) = (args[0], Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), args[3]);
if (!File.Exists(Path.Combine(publishDir, "Aether")))
{
    Console.Error.WriteLine($"{publishDir} has no 'Aether' executable; publish for a Linux or macOS runtime first.");
    return 1;
}
var repo = FindRepoRoot(publishDir);
var iconPng = Path.Combine(repo, "src", "MultiIde", "Assets", "aether.png");

Directory.CreateDirectory(Path.GetDirectoryName(output)!);
using (var file = File.Create(output))
using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize))
using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
{
    if (kind == "linux")
    {
        var root = Path.GetFileName(output).Replace(".tar.gz", "");
        AddDirectory(tar, root, publishDir);
        AddFile(tar, $"{root}/aether.png", File.ReadAllBytes(iconPng), executable: false);
        AddFile(tar, $"{root}/aether.desktop", Encoding.UTF8.GetBytes(DesktopEntry("Aether")), executable: false);
        AddFile(tar, $"{root}/install-desktop-entry.sh", Encoding.UTF8.GetBytes(InstallScript), executable: true);
    }
    else
    {
        const string app = "Aether.app/Contents";
        AddFile(tar, $"{app}/Info.plist", Encoding.UTF8.GetBytes(InfoPlist(version)), executable: false);
        AddFile(tar, $"{app}/Resources/aether.icns", Icns(File.ReadAllBytes(iconPng)), executable: false);
        AddDirectory(tar, $"{app}/MacOS", publishDir);
    }
}
Console.WriteLine($"{output} ({new FileInfo(output).Length / 1024.0 / 1024.0:0.0} MB)");
return 0;

static void AddDirectory(TarWriter tar, string archiveRoot, string dir)
{
    tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, archiveRoot + "/") { Mode = DirMode });
    foreach (var sub in Directory.EnumerateDirectories(dir).Order())
        AddDirectory(tar, $"{archiveRoot}/{Path.GetFileName(sub)}", sub);
    foreach (var path in Directory.EnumerateFiles(dir).Order())
        AddFile(tar, $"{archiveRoot}/{Path.GetFileName(path)}", File.ReadAllBytes(path), IsExecutable(path));
}

static void AddFile(TarWriter tar, string name, byte[] data, bool executable) =>
    tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name)
    {
        Mode = executable ? DirMode : FileMode,
        ModificationTime = DateTimeOffset.UtcNow,
        DataStream = new MemoryStream(data),
    });

// Native executables (ELF / Mach-O) and shell scripts get the executable bit; libraries and data don't need it.
static bool IsExecutable(string path)
{
    if (path.EndsWith(".sh")) return true;
    if (Path.HasExtension(path)) return false;
    Span<byte> magic = stackalloc byte[4];
    using var f = File.OpenRead(path);
    if (f.Read(magic) < 4) return false;
    var m = BitConverter.ToUInt32(magic);
    return (magic[0] == 0x7F && magic[1] == 'E' && magic[2] == 'L' && magic[3] == 'F') // ELF
           || m is 0xFEEDFACF or 0xCFFAEDFE or 0xFEEDFACE or 0xCEFAEDFE or 0xBEBAFECA or 0xCAFEBABE; // Mach-O, fat
}

static string FindRepoRoot(string start)
{
    for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
        if (Directory.Exists(Path.Combine(d.FullName, "src", "MultiIde"))) return d.FullName;
    return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}

// An .icns holding the 256×256 PNG as-is ("ic08" = 256×256 PNG).
static byte[] Icns(byte[] png)
{
    using var ms = new MemoryStream();
    void U32(uint v) { ms.WriteByte((byte)(v >> 24)); ms.WriteByte((byte)(v >> 16)); ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
    ms.Write("icns"u8);
    U32((uint)(8 + 8 + png.Length));
    ms.Write("ic08"u8);
    U32((uint)(8 + png.Length));
    ms.Write(png);
    return ms.ToArray();
}

static string InfoPlist(string version) => $"""
    <?xml version="1.0" encoding="UTF-8"?>
    <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
    <plist version="1.0">
    <dict>
      <key>CFBundleName</key><string>Aether</string>
      <key>CFBundleDisplayName</key><string>Aether</string>
      <key>CFBundleIdentifier</key><string>dev.aether.ide</string>
      <key>CFBundleExecutable</key><string>Aether</string>
      <key>CFBundleIconFile</key><string>aether.icns</string>
      <key>CFBundlePackageType</key><string>APPL</string>
      <key>CFBundleShortVersionString</key><string>{version}</string>
      <key>CFBundleVersion</key><string>{version}</string>
      <key>LSMinimumSystemVersion</key><string>12.0</string>
      <key>NSHighResolutionCapable</key><true/>
      <key>LSApplicationCategoryType</key><string>public.app-category.developer-tools</string>
      <key>NSHumanReadableCopyright</key><string>Copyright (c) 2026 guyde. MIT License.</string>
    </dict>
    </plist>
    """;

static string DesktopEntry(string exec) => $"""
    [Desktop Entry]
    Type=Application
    Name=Aether
    Comment=Plugin-driven IDE
    Exec={exec} %F
    Icon=aether
    Terminal=false
    Categories=Development;IDE;
    StartupWMClass=Aether

    """;

partial class Program
{
    private const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    private const UnixFileMode DirMode = FileMode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    // Adds Aether to the desktop's application menu, pointing at wherever the archive was extracted.
    private const string InstallScript = """
        #!/bin/sh
        set -e
        here="$(cd "$(dirname "$0")" && pwd)"
        mkdir -p "$HOME/.local/share/applications" "$HOME/.local/share/icons/hicolor/256x256/apps"
        cp "$here/aether.png" "$HOME/.local/share/icons/hicolor/256x256/apps/aether.png"
        sed "s|^Exec=.*|Exec=\"$here/Aether\" %F|" "$here/aether.desktop" > "$HOME/.local/share/applications/aether.desktop"
        echo "Aether added to your applications menu."

        """;
}
