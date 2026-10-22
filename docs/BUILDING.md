# Building and running

## Requirements

- **.NET 10 SDK**.
- **Windows** for the MSI. The WiX Toolset is restored from NuGet, so nothing extra is needed.
- Optional:
  - `git`
  - `gh` / `glab`, for Share on GitHub / GitLab
  - `ilspycmd`, for decompiled NuGet sources

## From the command line

```bash
dotnet run --project src/MultiIde
```

Arguments: `[folder] [file[:line]]`, for example:

```bash
dotnet run --project src/MultiIde -- "C:\code\game" "src\main.rs:10"
```

## From Aether itself

Open the **repository root**, or any folder inside it. The repository's run configurations live in `.aether/runConfigurations/`:

| Configuration | What it does |
|---|---|
| **Run Aether** | `dotnet run --project src/MultiIde` |
| **Publish Aether** | Deletes `artifacts/publish`, then does a self-contained, ReadyToRun `dotnet publish` for win-x64 into it |
| **Build MSI Installer** | Runs *Publish Aether* first (`beforeLaunch`), then builds `installer/Aether.Installer.wixproj` into `artifacts/installer/Aether-<version>-x64.msi` |

If you open a subfolder such as `src/MultiIde`, Aether also lists the configurations of the enclosing repository root. They keep running from the repository root.

If Aether itself is running from `src/MultiIde/bin`, building that same project can fail with "file is locked" (MSB3027). Run from the published or installed copy instead, or close it first.

## The installer

- **Per-user MSI.** Installs to `%LocalAppData%\Programs\Aether` with Start menu and desktop shortcuts, and needs no admin rights.
- **Updating.** Running a new (or rebuilt) MSI while Aether is installed shows **Update Aether**. It skips the license and folder pages and replaces the installed copy in its original folder. Settings in `~/.aether` are never touched.
- **Version.** Set by `-p:ProductVersion=…` in *Build MSI Installer* and `-p:Version=…` in *Publish Aether* (currently `0.1.0`). Installing the same version again also updates in place.
- **Files.**
  - `installer/Package.wxs`: the package, upgrade logic and UI.
  - `installer/Strings.wxl`: install-or-update wizard texts.
  - `installer/License.rtf`: the license page.

## Manual MSI build

```bash
dotnet publish src/MultiIde/MultiIde.csproj -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o artifacts/publish
```

```bash
dotnet build installer/Aether.Installer.wixproj -c Release -p:ProductVersion=0.1.0
```
