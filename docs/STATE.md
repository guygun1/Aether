# Current state

Aether is a working daily-driver editor for C / C++, C#, Rust and Java projects, but it is still pre-1.0 (version 0.1.0). Windows is the main target and the one that gets tested; Linux and macOS builds should work, but they are untested.

Legend: ✅ works · 🟡 partial · ❌ not yet

## Windows and workspace

| Feature | State | Notes |
|---|---|---|
| Welcome hub (separate window) | ✅ | Projects, New Project, Customize, Plugins, Language Servers, Learn |
| First-run setup | ✅ | Imports settings from installed Visual Studio / JetBrains IDEs, then theme, keymap, code style, layout and motion |
| One window per project | ✅ | Close Project returns to Welcome |
| Project tree | ✅ | Lazy loading; IntelliJ-style options (hidden files, excluded folders, autoscroll …) |
| External Libraries | ✅ | Cargo crate sources and the Rust std library; NuGet packages decompiled to C# via `ilspycmd` |
| Tool windows | ✅ | Run, Terminal, Problems, Find, Log, Dependencies, MCP |
| Customizable toolbar | ✅ | Any action or plugin command can be placed in it |
| Search Everywhere (double Shift) | ✅ | Files, actions, symbols |
| Navigation history | ✅ | Toolbar Back/Forward, Ctrl+Alt+Left/Right and the mouse side buttons |

## Editor

| Feature | State | Notes |
|---|---|---|
| Syntax highlighting | ✅ | TextMate grammars; colour scheme can be Aether, JetBrains or Visual Studio |
| LSP features | ✅ | Completion, hover docs, go to declaration (also into dependencies), find usages, rename, quick fixes, formatting, parameter info, inlay hints |
| Delayed diagnostics | ✅ | Errors appear after a configurable typing pause |
| Ctrl+hover links | ✅ | A blue underline appears only when Ctrl+click will actually navigate |
| Smart next-word suggestions | ✅ | Local, learned per language (e.g. `private` → `static`). Not AI |
| Completion ranking from usage | ✅ | Remembers which items you pick |
| Live templates / snippets | ✅ | Linked fields, choices, Surround With, user templates |
| Folding, indent guides, bracket matching, TODO highlighting | ✅ | |
| Semantic highlighting (LSP semantic tokens) | ❌ | Colours come from TextMate scopes only |
| Split editors | ❌ | |

## Languages (bundled plugins)

| Language | Language server | Install from Aether | Build / run |
|---|---|---|---|
| Rust | rust-analyzer | ✅ download, or rustup if present | Cargo run configs, Cargo dependencies |
| C / C++ | clangd | 🟡 opens winget / website | CMake run configs, vcpkg + FetchContent dependencies |
| CMake | cmake-language-server | 🟡 pip | |
| C# | csharp-ls | 🟡 `dotnet tool install` | dotnet run configs, NuGet dependencies |
| Java | Eclipse jdtls | ✅ download (needs JDK 21+) | Gradle / Maven run configs and dependencies |
| GLSL / WGSL | glsl_analyzer | ✅ download | (part of the graphics plugin) |
| Minecraft modding | uses Java | | Paper, Fabric and NeoForge templates, runClient / runServer configs |

The OpenGL / graphics options are template options on the existing C++, C# and Rust templates, not separate project types.

## Run, build, VCS

| Feature | State | Notes |
|---|---|---|
| Run configurations | ✅ | Shell or program, env vars, working directory, before-launch chains, macros. Shared as `.aether/runConfigurations/*.json` |
| Plugin-detected configurations | ✅ | e.g. `cargo run` appears when `Cargo.toml` exists |
| Debugger | ❌ | No DAP support yet. This is the biggest gap; see the [roadmap](ROADMAP.md) |
| Test runner UI | ❌ | Tests run as plain run configurations |
| Git | 🟡 | Commit dialog, push / pull, branches, init, Share on GitHub / GitLab (via `gh` / `glab`). No diff viewer, blame or merge tool yet |

## Extensibility

| Feature | State | Notes |
|---|---|---|
| JSON plugins | ✅ | Languages, language servers (with auto-download), themes, snippets, run configs, templates, dependency managers, commands, keybindings, settings. See [PLUGINS.md](PLUGINS.md) |
| Add Language Support wizard | ✅ | One-click setup for common languages |
| IntelliJ plugin converter | 🟡 | Converts the declarative parts (file types, live templates, colour schemes). JVM code cannot run |
| Code plugins (C# assemblies) | ❌ | Plugins are declarative only |

## Settings and appearance

- ✅ Searchable settings page, generated from one schema.
- ✅ Themes: Midnight, Daylight, Ember and High Contrast. VS Code theme files work too.
- ✅ Animations can be animated or instant, with a speed slider and smooth scrolling.
- ✅ Keymap presets (Default, Visual Studio, JetBrains). Any action can be rebound to several shortcuts and to two-stroke chords (Settings → Keymap).
- ✅ My Commands: your own shell commands with a shortcut, output target and stdin input. They also show up in Find Action and the toolbar.
- 🟡 The keymap can only be edited from a project window, not from Welcome.

## Distribution

- ✅ Per-user MSI (no admin rights needed), with Start menu and desktop shortcuts.
- ✅ Running a newer (or rebuilt) MSI over an existing install updates it in place.
- ❌ No auto-update inside the app, and no code signing yet, so SmartScreen will warn on first run.

## Known limitations

- **No automated tests.** Changes are verified by building and using the app.
- `EditorView.cs` and `MainWindow.Workspace.cs` are large and should be split up.
- When a language server is missing, LSP features for that language are silently unavailable. Only completion from words in the file still works.
- macOS and Linux are untested.
