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
| **Package Linux x64 / arm64** | Self-contained publish for `linux-x64` / `linux-arm64`, packed as `artifacts/dist/Aether-<version>-<rid>.tar.gz` |
| **Package macOS Apple Silicon / Intel** | Self-contained publish for `osx-arm64` / `osx-x64`, packed as an `Aether.app` bundle in `artifacts/dist/Aether-<version>-<rid>.tar.gz` |
| **Build Release (All Platforms)** | Runs all of the above, then copies every installer (MSI + four `.tar.gz`) into one fresh folder, `artifacts/release/`, with `SHA256SUMS.txt` |

Every platform can be built from Windows, including ReadyToRun. The packaging step is a small C# tool in `tools/Packager`. It writes `.tar.gz` files, which keep Unix permissions, so the executables stay executable. It also creates the macOS `.app` bundle (`Info.plist` and icon) and the Linux desktop entry.

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

## Linux and macOS packages

**Linux:**
- Extract the archive anywhere and run `./Aether`.
- Run `./install-desktop-entry.sh` once to add Aether to your applications menu.

**macOS:**
- **Install:** extract the archive and move `Aether.app` to Applications.
- **First launch:** the app isn't signed or notarized yet, so macOS blocks it. Allow it once with:
  ```bash
  xattr -cr /Applications/Aether.app
  ```
  Or right-click the app → Open.
- **Signing:** the .NET SDK ad-hoc signs the executable even when building on Windows, so Apple Silicon can run it.
- **If macOS still refuses to start it,** re-sign the whole bundle:
  ```bash
  codesign --force --deep -s - /Applications/Aether.app
  ```

## Manual MSI build

```bash
dotnet publish src/MultiIde/MultiIde.csproj -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o artifacts/publish
```

```bash
dotnet build installer/Aether.Installer.wixproj -c Release -p:ProductVersion=0.1.0
```
