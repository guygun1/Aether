# Writing plugins

An Aether plugin is a folder with a `plugin.json` manifest, plus any files it references (grammars, themes). Plugins are declarative: they contain no code.

- **Bundled plugins** live in `plugins/` in this repository.
- **Your own plugins** go in `~/.aether/plugins/<id>/`. **Settings → Plugins → Create Plugin…** writes a starter for you.
- **Converted plugins.** **Add Language Support** and the **IntelliJ plugin converter** produce ordinary plugins in that same folder.

Enable or disable plugins in **Settings → Plugins**. Changes apply without a restart where possible.

## Manifest

```json
{
  "id": "mylang",
  "name": "MyLang",
  "version": "1.0.0",
  "description": "MyLang support",
  "author": "you",
  "dependsOn": ["java"],
  "contributes": { }
}
```

`dependsOn` lists plugin ids that must be installed and enabled for this plugin to load.

## Macros

These are expanded in commands, arguments and paths:

| Macro | Value |
|---|---|
| `${workspaceFolder}`, `${workspaceFolderName}` | Project root and its folder name |
| `${file}`, `${fileDir}`, `${fileName}`, `${fileBasenameNoExtension}`, `${relativeFile}` | The current file |
| `${lineNumber}`, `${selectedText}` | Caret line, selection |
| `${pluginDir}` | This plugin's folder |
| `${home}` | User home folder |
| `${os}` | `win`, `mac` or `linux` |
| `${env:NAME}` | Environment variable |
| `${config:pluginId.key}` | A plugin setting (see `settings`) |
| `${name}` | Project or file name (in templates only) |

## Contribution points (`contributes`)

### `languages`

```json
{ "id": "rust", "name": "Rust", "extensions": [".rs"], "filenames": [],
  "scopeName": "source.rust", "grammar": "rust.tmLanguage.json",
  "lineComment": "//", "blockComment": ["/*", "*/"],
  "icon": "RS", "iconColor": "#E8743B",
  "indentSize": 4, "useTabs": false, "folding": "braces" }
```

- `scopeName` uses a grammar bundled with the IDE. Alternatively, `grammar` points to a `.tmLanguage.json` in the plugin.
- `folding` can be `braces`, `indent` or `none`.

### `lspServers`

```json
{ "id": "rust-analyzer", "name": "rust-analyzer",
  "command": "rust-analyzer", "args": [], "languages": ["rust"],
  "initializationOptions": {}, "settings": {}, "env": {},
  "install": "rustup component add rust-analyzer",
  "installUrl": "https://rust-analyzer.github.io/",
  "download": {
    "url": { "windows": "https://…/x86_64-pc-windows-msvc.zip", "linux": "https://…/linux-gnu.gz", "macos": "https://…/apple-darwin.gz" },
    "executable": "rust-analyzer"
  },
  "requires": null,
  "prerequisite": { "command": "cargo", "name": "Rust toolchain", "install": "winget install --id Rustlang.Rustup -e", "url": "https://rustup.rs" } }
```

- **Server start.** The server starts lazily, the first time a file of one of its `languages` opens.
- **`download`.** When the command is missing, Aether downloads the file for this OS into `~/.aether/tools/<id>`. It extracts `.zip`, `.tar.gz` or `.gz` archives (or takes a raw executable) and finds `executable` inside. No admin rights are needed. This is preferred over `install` when the install tool itself is missing.
- **`requires`.** A file that must exist for the server to count as installed. Use it for servers launched through another program, like `java -jar …`.
- **Globs in `args`.** A `*` in the file-name part of an argument is resolved, e.g. `${home}/.aether/tools/jdtls/plugins/org.eclipse.equinox.launcher_*.jar`.
- **`prerequisite`.** A tool the server needs at run time; Aether offers to install it.

### `runConfigurations`

```json
{ "name": "cargo run", "command": "cargo run", "when": "Cargo.toml", "kind": "run" }
```

- The configuration is offered when `when` (a root-relative file or glob) exists.
- `kind` can be `run`, `build` or `test`. The toolbar Build button uses the first `build`.
- Users can copy a detected configuration into their own and edit it.

### `projectTemplates` and `projectTemplateOptions`

```json
{ "id": "rust-bin", "name": "Rust: Binary (Cargo)", "description": "…",
  "category": "Rust", "icon": "RS", "iconColor": "#E8743B",
  "command": "cargo new \"${name}\"",
  "files": { "README.md": "# ${name}\n" } }
```

- `command` runs in the parent folder.
- `files` are written into the new project afterwards. A value can be a string or an array of lines.

#### Template parameters (wizard questions)

A template can ask questions in **New Project**. Answers are used as `${param.<id>}` in `command`, `files` (paths and contents), `properties`, `commands` and `open`.

```json
"parameters": [
  { "id": "mc", "label": "Minecraft version", "type": "version",
    "source": { "url": "https://github.com/FabricMC/fabric-example-mod", "format": "gitBranches", "regex": "^\\d+\\.\\d+(?:\\.\\d+)?$", "sort": "desc" } },
  { "id": "fabricApi", "label": "Fabric API", "type": "version",
    "source": { "url": "https://api.modrinth.com/v2/project/P7dR8mSH/version?game_versions=%5B%22${param.mc}%22%5D", "field": "version_number" } },
  { "id": "java", "label": "Java version", "type": "choice", "options": ["25", "21", "17"], "default": "${param.mc|java}" },
  { "id": "group", "label": "Group", "type": "string", "default": "com.example", "pattern": "^[a-z][a-z0-9_.]*$" },
  { "id": "tests", "label": "Add tests", "type": "boolean", "default": "true" }
]
```

- **`type`**:
  - `string`: a text field, with optional `pattern` and `patternMessage` for validation.
  - `choice`: picks from `options`.
  - `boolean`: a checkbox whose value is `true` or `false`.
  - `version`: a list loaded live from `source`.
- **`hidden: true`** loads a value without showing it, e.g. the latest version of a Gradle plugin.
- **`default`** can reference `${name}`, other parameters and `${env:NAME}`.
- **Dependencies:** a parameter whose `default`, `source.url` or `source.regex` mentions `${param.x}` reloads when `x` changes.
- **`source`** fields:
  - `format`: `json` (the default), `xml` (e.g. a maven-metadata.xml), `gitBranches` (a repository's branches) or `lines`.
  - `path`: a JSON dot path. If it points to an object, the object's array values are joined.
  - `field`: which property of each item to use.
  - `where`: keeps only items whose properties equal given values, e.g. `{ "stable": true }`.
  - `regex`: keeps matching values only. A named group `(?<v>…)` becomes the value.
  - `sort`: `desc` puts the newest version first.
  - `limit`: maximum number of options.
- **Filters**, written as `${name|id}` or `${param.x|filter}`:

  | Filter | Effect |
  |---|---|
  | `lower`, `upper` | Change case |
  | `id` | `snake_case` identifier |
  | `pascal` | `ClassName` |
  | `package` | Lowercase dotted package name |
  | `path` | Dots become slashes |
  | `majorminor` | `1.21.4` becomes `1.21` |
  | `java` | The Java version that Minecraft version needs |
  | `neoforge` | The NeoForge version prefix for that Minecraft version |

Steps that run after the files are written:

```json
"properties": { "gradle.properties": { "mod_version": "${param.version}" } },
"commands": ["gradle wrapper"],
"delete": ["download.zip"],
"open": "src/main/java/Main.java"
```

- **`properties`** changes keys that already exist in `.properties` files (keys the file lacks are ignored).
- **`commands`** run in the new project; a failure is reported but the project is kept.
- **`delete`** removes files or folders.
- **`open`** is the file shown first.

#### Template options

Options add an opt-in checkbox to someone else's template:

```json
{ "template": "rust-bin", "id": "wgpu", "label": "Graphics: wgpu + winit", "description": "…",
  "files": { "src/main.rs": ["…"] }, "commands": ["cargo add wgpu winit"] }
```

### `snippets`

```json
{ "language": "rust", "prefix": "fn", "body": ["fn ${1:name}(${2}) {", "\t$0", "}"], "description": "Function" }
```

Snippets use TextMate/VS Code syntax: `$1`, `${1:default}`, `${1|a,b|}` and `$0`.

### `fileTemplates`

```json
{ "name": "Rust Module", "extension": ".rs", "body": ["// ${name}"], "companions": { "${name}.h": "…" } }
```

These appear under **New** in the project tree. `companions` are extra files created alongside, e.g. a header next to a `.cpp`.

### `dependencyManagers`

```json
{ "id": "cargo", "name": "Cargo", "manifest": "Cargo.toml", "format": "cargo",
  "add": "cargo add ${name}${version}", "remove": "cargo remove ${name}", "update": "cargo update -p ${name}",
  "restore": "cargo fetch", "docs": "https://docs.rs/${name}", "search": "https://crates.io/search?q=${query}" }
```

`format` selects the built-in parser: `cargo`, `msbuild`, `vcpkg`, `cmake`, `maven` or `gradle`.

### `commands`

These are external tools exposed as IDE actions.

```json
{ "id": "clippy", "title": "Run Clippy", "command": "cargo clippy", "workingDirectory": "${workspaceFolder}",
  "keys": "Ctrl+Alt+K", "languages": ["rust"], "output": "run", "input": null,
  "menus": ["tools", "editor"], "saveBefore": true }
```

- `output` can be:
  - `run`: the Run tool window
  - `notify`
  - `replaceSelection`
  - `insert`
  - `replaceDocument`
  - `silent`
- `input` sends the selection or the document to stdin.

### `formatters`

```json
{ "name": "rustfmt", "languages": ["rust"], "command": "rustfmt", "args": ["--emit", "stdout"], "preferred": false }
```

The formatter pipes the document through stdin and reads the result from stdout. If `preferred` is `true`, it is used even when the language server can format.

### `themes`

```json
{ "id": "midnight", "name": "Midnight", "path": "midnight.json" }
```

A theme file has `type` (`dark` or `light`), `ui`, `editor` and `tokenColors` sections. VS Code theme files (`colors` + `tokenColors`) also work.

### `keybindings`, `settings`, `fileIcons`

```json
"keybindings": [{ "action": "clippy", "keys": "Ctrl+Alt+K" }],
"settings": [{ "key": "features", "title": "Cargo features", "description": "…", "type": "string", "default": "" }],
"fileIcons": { "Cargo.toml": { "icon": "⚙", "color": "#E8743B" }, ".lock": { "icon": "🔒" } }
```

- A setting's `type` is `boolean`, `string`, `number` or `enum` (with `options`).
- Settings are read as `${config:pluginId.key}`.

## Examples in this repository

| Plugin | Shows |
|---|---|
| `plugins/rust` | LSP with download + prerequisite, Cargo dependencies, templates |
| `plugins/java` | Server launched through `java -jar` with `requires` and globs, Gradle/Maven |
| `plugins/graphics` | Custom grammars (GLSL, WGSL), `projectTemplateOptions` on other plugins' templates |
| `plugins/minecraft` | Templates that clone a starter repository, `when` on nested files |
| `plugins/themes` | Theme files |
