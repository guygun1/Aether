# Aether

A fast, plugin-driven desktop IDE in C# (.NET 10 + Avalonia), with the comfort of JetBrains IDEs and the language reach of Visual Studio.
Languages, themes, tools, project templates and package managers all come from plugins; **C / C++ (CMake)**, **C#**, **Rust** and **Java** ship built in.

## Documentation

| File | What's in it |
|---|---|
| [docs/STATE.md](docs/STATE.md) | What works today, what is partial, known limitations |
| [docs/ROADMAP.md](docs/ROADMAP.md) | Future plans, roughly in priority order |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | How the codebase is organised and how the pieces fit together |
| [docs/PLUGINS.md](docs/PLUGINS.md) | Writing plugins: the `plugin.json` manifest and every contribution point |
| [docs/BUILDING.md](docs/BUILDING.md) | Building, running, run configurations and the MSI installer |

## Quick start

Requires the .NET 10 SDK.

```bash
dotnet run --project src/MultiIde
```

Open a folder (and optionally a file at a line) directly:

```bash
dotnet run --project src/MultiIde -- "C:\path\to\project" "src\main.rs:10"
```

Or open this repository in Aether and use the **Run Aether** run configuration. To produce an installer, run **Build MSI Installer** (see [docs/BUILDING.md](docs/BUILDING.md)).

## Repository layout

```
src/MultiIde/      the IDE (Core, Editor, Protocol, Views)
plugins/           bundled plugins: rust, cpp, cmake, csharp, java, graphics, minecraft, themes, common
installer/         WiX project for the per-user MSI
.aether/           this repo's own Aether run configurations
```

## License

[MIT](LICENSE) © 2026 guyde
