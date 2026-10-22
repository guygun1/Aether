# Aether

A fast, plugin-driven desktop IDE built with C# / .NET 10 and Avalonia. Languages, themes, tools and package managers come from plugins.

Built-in plugins: **Rust** (rust-analyzer, Cargo), **C / C++** (clangd), **CMake** (+ vcpkg), **C# / .NET** (csharp-ls, NuGet), **Themes** (Midnight, Daylight, Ember, High Contrast) and common file formats.

## Run

```bash
dotnet run --project src/MultiIde
```

Open a folder (and optionally a file at a line) directly:

```bash
dotnet run --project src/MultiIde -- "C:\path\to\project" "src\main.rs:10"
```

On first launch the **Welcome window** walks you through setup: import settings from an installed Visual Studio or JetBrains IDE, then pick theme, keymap, code style, layout and animations. Opening a project switches to a project window; **Close Project** returns to Welcome.

## Highlights

- **Editor**: language-server completion, hover docs, go to declaration, find usages, rename, quick fixes, formatting, parameter info; errors appear after a typing pause (configurable).
- **Live templates**: Tab-expandable snippets with linked fields and choices, Surround With (Ctrl+Alt+T), your own templates in Settings → Live Templates.
- **Run configurations**: Edit Configurations dialog, shell or program, env vars, working dir, before-launch steps, macros like `${file}`; stored in `.aether/run.json` to share with your team.
- **Toolbar**: customizable (right-click → Customize Toolbar): Git branch, back/forward, search, build/run/stop, problems, notifications, theme toggle, or any action.
- **Git**: commit dialog (Ctrl+K), push/pull, branches, and **Share Project on GitHub / GitLab** through the official `gh` / `glab` CLIs.
- **Dependencies** tool window for Cargo, NuGet, vcpkg and CMake: add, remove, update, docs, sync.
- **Settings**: searchable; theme, animation mode and speed, smooth scrolling, fonts, completion, saving, keymap presets (Default / Visual Studio / JetBrains) with chords.

## Plugins

Bundled plugins live in `plugins/`; your own go in `~/.aether/plugins/<id>/plugin.json` (**Settings → Plugins → Create Plugin…** writes a starter). A plugin can contribute `languages`, `lspServers`, `themes`, `snippets`, `commands` (external tools), `formatters`, `fileTemplates`, `runConfigurations`, `projectTemplates`, `dependencyManagers`, `keybindings`, `fileIcons` and user-editable `settings` (read as `${config:pluginId.key}`).

## Language servers

Install from **Welcome → Language Servers**, or:

| Language | Server | Install |
|---|---|---|
| Rust | rust-analyzer | `rustup component add rust-analyzer` |
| C / C++ | clangd | `winget install LLVM.LLVM` |
| CMake | cmake-language-server | `pip install --user cmake-language-server` |
| C# | csharp-ls | `dotnet tool install --global csharp-ls` |
