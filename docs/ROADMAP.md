# Roadmap

Future plans, roughly in priority order. Nothing here is a promise. See [STATE.md](STATE.md) for what exists today.

## Guiding principles

- **Fast and never frozen.** All I/O, process work and language-server traffic stays off the UI thread. Output is batched, and nothing blocks on the network.
- **Plugins first.** If a feature is language-specific, it belongs in a plugin manifest, not in core code.
- **JetBrains comfort, Visual Studio reach.** Familiar shortcuts and UI, and first-class native toolchains.
- **Quiet by default.** Only notify when something needs the user.

## Next (0.2)

1. **Debugger (Debug Adapter Protocol).**
   - Plugins contribute `debugAdapters` just like `lspServers`, with auto-download.
   - Targets: CodeLLDB for Rust and C++, netcoredbg for C#, java-debug for Java.
   - UI: breakpoints in the gutter, a Debug tool window (frames, variables, watches), step controls on the toolbar, and a "Debug" button next to Run.
2. **Git, part 2.**
   - Side-by-side diff viewer and gutter change markers.
   - Blame, file history, stash, and a merge-conflict resolver.
3. **Semantic highlighting.** LSP semantic tokens on top of TextMate, so the JetBrains and Visual Studio schemes can colour locals, parameters, fields and types exactly like those IDEs do.
4. **Automated tests.**
   - Unit tests for `Core` (plugin loading, dependency parsers, run-config storage, keymap parsing, token predictor).
   - A smoke test that opens a sample project headless.
5. **Code signing** for the MSI and `Aether.exe`, to remove SmartScreen warnings.

## Soon (0.3)

- **Test runner UI.** Discover and run tests through plugins (cargo test, dotnet test, CTest, Gradle/Maven) with a results tree and re-run of failed tests.
- **In-app updates.** Check for a newer MSI and offer to install it; the installer already updates in place.
- **Split editors** (vertical and horizontal) and drag-to-dock tool windows.
- **Code plugins.** Optional C# plugin assemblies loaded in an isolated `AssemblyLoadContext`, for things the JSON manifest can't express (custom tool windows, inspections).
- **Plugin marketplace.** Browse and install plugins from a central index, with update notifications.
- **Refactorings beyond rename:** extract method or variable and change signature, wherever the language server supports them.

## Later

- **Linux and macOS packages** (AppImage / .deb, .dmg), plus CI that builds all three.
- **Remote development:** open a folder over SSH or in WSL, with language servers running remotely.
- **Profiler integration**, for example `dotnet-trace`, `perf` and the Visual Studio diagnostics tools.
- **Graphics tooling** for the OpenGL / game-dev template options: shader hot-reload helpers and a RenderDoc launch configuration.
- **Minecraft modding, part 2:** mixin target navigation, access-widener and AT support, and mappings lookup.
- **Notebook / scratch files** with run support for scripting languages.
- **Accessibility pass:** screen-reader names on all controls, and keyboard-only navigation everywhere.

## Code health tasks

- Split `EditorView.cs` (≈1300 lines) into completion, navigation and LSP-action parts.
- Split `MainWindow.Workspace.cs` (≈900 lines) the same way.
- Replace the ad-hoc version `0.1.0` in the run configurations with one version defined in `Directory.Build.props`.
