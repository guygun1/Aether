# Architecture

Aether is one .NET 10 desktop app, `src/MultiIde` (the assembly is `Aether.exe`), built on:

- **Avalonia 11** for the UI.
- **AvaloniaEdit** for the text editor.
- **TextMateSharp** for syntax highlighting.
- Its own small **JSON-RPC / LSP client** for language servers.

Almost everything language-specific comes from JSON plugins in `plugins/`.

```
┌────────────────────────── Views ──────────────────────────┐
│ WelcomeWindow   MainWindow (one per project)   IdeWindow  │  Avalonia windows, tool windows, dialogs
└──────────────┬──────────────────────────────┬─────────────┘
               │ hosts (IEditorHost)          │ reads/writes
┌──────────────▼───────────┐   ┌──────────────▼────────────┐
│ Editor                   │   │ Core                      │
│ EditorView, Completion,  │   │ Settings, Plugins, Themes,│
│ Renderers, Snippets …    │   │ RunConfigs, Dependencies, │
└──────────────┬───────────┘   │ ToolInstaller, Search …   │
               │ LSP requests  └──────────────┬────────────┘
┌──────────────▼──────────────────────────────▼─────────────┐
│ Protocol: JsonRpc ▸ LspClient ▸ LspManager                │  one server process per server id
└───────────────────────────────────────────────────────────┘
```

## Folders

### `Core/` — no UI, no Avalonia controls

| File | Responsibility |
|---|---|
| `IdeServices.cs` | App-wide singletons shared by all windows: `Settings`, `Plugins`, `Syntax` |
| `Settings.cs` | `~/.aether/settings.json`; also `IdePaths` (where settings, plugins and tools live) |
| `SettingsSchema.cs` | One list of `SettingDef`s; the Settings page is generated from it |
| `SettingsImport.cs` | Reads settings from installed Visual Studio / JetBrains IDEs during first-run setup |
| `Plugins.cs` | Loads `plugin.json` manifests (bundled + `~/.aether/plugins`), resolves dependencies, expands macros like `${workspaceFolder}` and `${os}` |
| `ToolInstaller.cs` | Downloads and extracts language servers that a plugin declares (`download` section) into `~/.aether/tools/<id>` |
| `LanguageCatalog.cs` | Languages the "Add Language Support" wizard can set up in one click |
| `IntelliJConverter.cs` | Turns the declarative parts of IntelliJ plugins (file types, live templates, `.icls` schemes) into an Aether plugin |
| `Themes.cs` | `ThemeManager`: UI and editor colours are shared brushes that get recoloured in place, so a theme switch is live. Also applies the JetBrains / Visual Studio syntax scheme from `Assets/syntax/` |
| `SyntaxRegistry.cs` | TextMate grammar registry: TextMateSharp's bundled grammars plus plugin grammars |
| `RunConfigs.cs` | `RunConfig` model, `RunConfigStore` (`.aether/runConfigurations/*.json` and `.aether/run.json`; a project in a subfolder of a git repository also gets the repository root's configurations) and `Macros` (`${workspaceFolder}` …) |
| `ProjectState.cs` | Per-user project state, `.aether/workspace.json` (open tabs, selected run config); not committed |
| `Dependencies.cs` | Tolerant manifest parsers: Cargo, MSBuild/NuGet, vcpkg, CMake, Maven and Gradle |
| `FileSearch.cs` | Background file indexing and find-in-files; skips build and VCS folders |
| `TokenPredictor.cs` | Local, IntelliCode-style next-word prediction from bigrams and trigrams of your code |
| `CompletionUsage.cs` | Remembers which completions you pick, to rank them first |
| `KeymapPresets.cs` | Default / Visual Studio / JetBrains keymaps, with chords |
| `Appearance.cs` | `Anim`: hover transitions, popup and panel animation, smooth scrolling; honours *instant* mode and speed |
| `ProcessUtil.cs` | Starting processes and shell commands (`cmd /c` or `/bin/sh -c`), and PATH fixes for GUI apps |
| `UserSnippets.cs` | The user's own live templates (`~/.aether/snippets.json`) |

### `Protocol/` — language servers

| File | Responsibility |
|---|---|
| `JsonRpc.cs` | `Content-Length` framed JSON-RPC over stdio. Reading happens on a background thread; requests return `Task<JsonNode?>` |
| `Lsp.cs` | `LspClient`: starts a server (resolving `requires`, globs and downloads), runs the initialize handshake, and tracks capabilities (`Has("definitionProvider")`) and state |
| `LspManager.cs` | Starts at most one server per server id, lazily, the first time a file of that language opens |

### `Editor/` — one open file

| File | Responsibility |
|---|---|
| `EditorDocument.cs` | The text, language, dirty state and `didOpen` / `didChange` sync with the server |
| `EditorView.cs` | The editor control: key handling, completion triggering, hover, Ctrl+hover links, go to declaration, find usages, rename, format. Talks to its window only through `IEditorHost` |
| `Completion.cs` | The completion popup (IntelliJ-style kind badges, starred items) |
| `SnippetSession.cs` | Live-template expansion with linked placeholders and choices |
| `Renderers.cs` | Background renderers: diagnostics squiggles, occurrences, bracket match, indent guides, Ctrl+hover link |
| `EditorExtras.cs` | TODO highlighting, inlay hints, smart-suggestion ghost text |
| `FoldMargin.cs` | Code folding margin |

### `Views/` — windows

`IdeWindow` is the shared base class of the Welcome window and project windows. It provides in-window dialogs, notification balloons and the hub pages. `MainWindow` is a `partial` class split by area:

| File | Area |
|---|---|
| `MainWindow.axaml(.cs)` | Window shell, layout, tool-window stripes, the action registry (`Act(id, title, group, shortcut, handler)`), global keys |
| `MainWindow.Workspace.cs` | Project tree, tabs, opening files, starting language servers, Problems |
| `MainWindow.Run.cs` | Run configuration selector, runner (before-launch chains), Run tool window, Edit Configurations dialog |
| `MainWindow.Toolbar.cs` | Customizable title-bar toolbar, navigation history (Back / Forward) |
| `MainWindow.Search.cs` | Search Everywhere (files, symbols via `workspace/symbol`, actions, recent, structure) |
| `MainWindow.Git.cs` | Commit dialog, push / pull, branches, Share on GitHub / GitLab (through `gh` / `glab`) |
| `MainWindow.Dependencies.cs` | Dependencies tool window |
| `MainWindow.ExternalLibraries.cs` | "External Libraries" node: crate sources, Rust std, decompiled NuGet packages |
| `MainWindow.Plugins.cs` | Runtime for plugin commands, formatters, file templates and keybindings |
| `MainWindow.Keymap.cs` | Settings → Keymap |
| `MainWindow.Tools.cs` | Bottom tool windows (Run, Terminal, Problems, Find, Log, MCP) |
| `IdeWindow.Hub.cs` | Welcome hub pages (Projects, New Project, Plugins, Language Servers …) |
| `IdeWindow.Settings.cs` | The generated, searchable Settings page |
| `IdeWindow.Setup.cs` | First-run setup wizard |
| `IdeWindow.Languages.cs` | Add Language Support wizard and the IntelliJ converter UI |
| `IdeWindow.LiveTemplates.cs` | Settings → Live Templates |
| `ConsoleView.cs` | Process output console. Output is buffered and flushed at most every 50 ms, with a capped size |
| `FileNode.cs` | Lazily loaded project-tree node |

`Themes/Colors.axaml` declares every colour resource that `ThemeManager` recolours, and `Themes/Styles.axaml` holds the control styles.

## Key flows

**Startup.** `Program.Main` → `App.OnFrameworkInitializationCompleted`. This applies the theme, then opens one of: the folder passed on the command line, the last project (if *reopen last project* is on), or the Welcome window.

**Opening a file.**
1. `MainWindow.OpenFileAsync` creates an `EditorDocument` and `EditorView`.
2. It picks the language from plugin `languages` by file extension, and the grammar from `SyntaxRegistry`.
3. It calls `LspManager.EnsureAsync(languageId)`. If the server binary is missing, the user is offered an install: a download via `ToolInstaller`, or the plugin's `install` command.

**Running.**
1. `MainWindow.Run` resolves the selected `RunConfig`.
2. It saves files if asked, runs each `beforeLaunch` config in order (stopping on failure), expands macros, and starts the process.
3. Output goes to a `ConsoleView` tab in the Run tool window.

**Themes.** `ThemeManager.Apply` reads a theme JSON (`ui`, `editor`, `tokenColors`; VS Code themes also work), recolours the shared brushes, then raises `Changed`. Every `EditorView` then re-applies its TextMate theme.

## Performance rules

These rules keep the IDE from freezing:

- Never block the UI thread on I/O or a process. Use `async` / `Task.Run`, and marshal back with `Dispatcher.UIThread.Post`.
- Language-server replies are discarded if the text changed or the mouse moved on (`_editVersion`, probe counters).
- Process output is batched (`ConsoleView`), and the file list is cached (`FileSearch`).
- Large files (`EditorDocument.IsLarge`) skip expensive features such as inlay hints.

## Where things are stored

| Path | Contents |
|---|---|
| `~/.aether/settings.json` | User settings |
| `~/.aether/plugins/<id>/` | User plugins |
| `~/.aether/tools/<id>/` | Downloaded language servers |
| `~/.aether/snippets.json`, `completion-usage.json` | User templates, completion ranking |
| `<project>/.aether/runConfigurations/*.json` | Shared run configurations (commit these) |
| `<project>/.aether/workspace.json` | Per-user project state (don't commit) |
