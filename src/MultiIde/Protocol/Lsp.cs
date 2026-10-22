using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using MultiIde.Core;

namespace MultiIde.Protocol;

public enum ServerState { Stopped, Starting, Running, Failed }

public sealed record LspDiagnostic(int StartLine, int StartCol, int EndLine, int EndCol, int Severity, string Message, string? Source, string? Code)
{
    public JsonNode Raw { get; init; } = new JsonObject();
}

public static class LspUtil
{
    public static string ToUri(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;

    public static string ToPath(string uri)
    {
        try { return new Uri(uri).LocalPath; }
        catch { return uri; }
    }

    public static JsonObject Pos(int line0, int char0) => new() { ["line"] = line0, ["character"] = char0 };

    public static (int Line, int Char) ReadPos(JsonNode? p) =>
        (p?["line"]?.GetValue<int>() ?? 0, p?["character"]?.GetValue<int>() ?? 0);

    /// <summary>Flattens MarkupContent / MarkedString / MarkedString[] into markdown text.</summary>
    public static string MarkupToMarkdown(JsonNode? contents)
    {
        switch (contents)
        {
            case null: return "";
            case JsonValue v: return v.GetValue<string>();
            case JsonArray a: return string.Join("\n\n---\n\n", a.Select(MarkupToMarkdown).Where(s => s.Length > 0));
            case JsonObject o when o["kind"] is not null: return o["value"]?.GetValue<string>() ?? "";
            case JsonObject o: return $"```{o["language"]?.GetValue<string>()}\n{o["value"]?.GetValue<string>()}\n```";
            default: return "";
        }
    }

    public static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
}

/// <summary>A running language server process and its JSON-RPC connection.</summary>
public sealed class LspClient : IAsyncDisposable
{
    private Process? _process;
    private JsonRpcConnection? _rpc;
    private readonly Dictionary<string, string> _progress = new();

    public LspServerConfig Config { get; }
    public Plugin Plugin { get; }
    public string Root { get; }
    public ServerState State { get; private set; } = ServerState.Stopped;
    public string? Error { get; private set; }
    public JsonObject Capabilities { get; private set; } = new();
    public string? ProgressText => _progress.Count == 0 ? null : _progress.Values.Last();

    public event Action? StateChanged;
    public event Action<string, List<LspDiagnostic>>? Diagnostics;
    public event Action<string>? Log;
    public event Action<int, string>? ShowMessage;
    /// <summary>Applies a WorkspaceEdit; set by the workspace.</summary>
    public Func<JsonNode?, Task<bool>>? ApplyEdit { get; set; }

    public LspClient(LspServerConfig config, Plugin plugin, string root)
    {
        Config = config;
        Plugin = plugin;
        Root = root;
    }

    public bool Has(string capability) =>
        Capabilities[capability] is { } c && !(c is JsonValue v && v.TryGetValue<bool>(out var b) && !b);

    public string[] TriggerCharacters(string provider) =>
        (Capabilities[provider]?["triggerCharacters"] as JsonArray)?.Select(LspUtil.Str).Where(s => s.Length > 0).ToArray() ?? [];

    /// <summary>1 = full sync, 2 = incremental.</summary>
    public int SyncKind => Capabilities["textDocumentSync"] switch
    {
        JsonValue v => v.GetValue<int>(),
        JsonObject o => o["change"]?.GetValue<int>() ?? 1,
        _ => 1,
    };

    private void SetState(ServerState state, string? error = null)
    {
        State = state;
        Error = error;
        StateChanged?.Invoke();
    }

    public async Task StartAsync()
    {
        SetState(ServerState.Starting);
        var command = PluginManager.Expand(Config.Command, Root, Plugin.Directory);
        if (ProcessUtil.Resolve(command) is null)
        {
            SetState(ServerState.Failed, $"`{command}` was not found on PATH.");
            return;
        }

        try
        {
            var args = Config.Args.Select(a => PluginManager.Expand(a, Root, Plugin.Directory));
            _process = Process.Start(ProcessUtil.StdioStartInfo(command, args, Root))
                       ?? throw new InvalidOperationException("Process did not start");
            _process.EnableRaisingEvents = true;
            _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Log?.Invoke(e.Data); };
            _process.BeginErrorReadLine();

            _rpc = new JsonRpcConnection(_process.StandardOutput.BaseStream, _process.StandardInput.BaseStream, Framing.ContentLength);
            _rpc.NotificationReceived += OnNotification;
            _rpc.RequestHandler = OnRequest;
            _rpc.Closed += _ =>
            {
                if (State == ServerState.Running) SetState(ServerState.Failed, "Language server exited unexpectedly.");
            };
            _rpc.Start();

            var init = await _rpc.RequestAsync("initialize", InitializeParams(), timeout: TimeSpan.FromSeconds(60));
            Capabilities = init?["capabilities"] as JsonObject ?? new JsonObject();
            _rpc.Notify("initialized", new JsonObject());
            if (Config.Settings is not null)
                _rpc.Notify("workspace/didChangeConfiguration", new JsonObject { ["settings"] = Config.Settings.DeepClone() });
            SetState(ServerState.Running);
        }
        catch (Exception e)
        {
            SetState(ServerState.Failed, e.Message);
            try { _process?.Kill(true); } catch { /* already gone */ }
        }
    }

    private JsonObject InitializeParams()
    {
        var rootUri = LspUtil.ToUri(Root);
        var markup = new JsonArray("markdown", "plaintext");
        return new JsonObject
        {
            ["processId"] = Environment.ProcessId,
            ["clientInfo"] = new JsonObject { ["name"] = "Aether", ["version"] = "0.1.0" },
            ["rootUri"] = rootUri,
            ["rootPath"] = Root,
            ["workspaceFolders"] = new JsonArray(new JsonObject { ["uri"] = rootUri, ["name"] = Path.GetFileName(Root) }),
            ["initializationOptions"] = Config.InitializationOptions?.DeepClone(),
            ["capabilities"] = new JsonObject
            {
                ["general"] = new JsonObject { ["positionEncodings"] = new JsonArray("utf-16") },
                ["window"] = new JsonObject { ["workDoneProgress"] = true, ["showMessage"] = new JsonObject() },
                ["workspace"] = new JsonObject
                {
                    ["applyEdit"] = true,
                    ["configuration"] = true,
                    ["workspaceFolders"] = true,
                    ["didChangeWatchedFiles"] = new JsonObject { ["dynamicRegistration"] = true },
                    ["symbol"] = new JsonObject(),
                    ["workspaceEdit"] = new JsonObject { ["documentChanges"] = true },
                },
                ["textDocument"] = new JsonObject
                {
                    ["synchronization"] = new JsonObject { ["didSave"] = true, ["willSave"] = false },
                    ["completion"] = new JsonObject
                    {
                        ["contextSupport"] = true,
                        ["completionItem"] = new JsonObject
                        {
                            ["snippetSupport"] = true,
                            ["documentationFormat"] = markup.DeepClone(),
                            ["deprecatedSupport"] = true,
                            ["insertReplaceSupport"] = true,
                            ["labelDetailsSupport"] = true,
                            ["resolveSupport"] = new JsonObject { ["properties"] = new JsonArray("documentation", "detail", "additionalTextEdits") },
                        },
                    },
                    ["hover"] = new JsonObject { ["contentFormat"] = markup.DeepClone() },
                    ["signatureHelp"] = new JsonObject
                    {
                        ["signatureInformation"] = new JsonObject
                        {
                            ["documentationFormat"] = markup.DeepClone(),
                            ["parameterInformation"] = new JsonObject { ["labelOffsetSupport"] = true },
                        },
                    },
                    ["definition"] = new JsonObject { ["linkSupport"] = true },
                    ["references"] = new JsonObject(),
                    ["documentSymbol"] = new JsonObject { ["hierarchicalDocumentSymbolSupport"] = true },
                    ["formatting"] = new JsonObject(),
                    ["rename"] = new JsonObject { ["prepareSupport"] = false },
                    ["codeAction"] = new JsonObject
                    {
                        ["codeActionLiteralSupport"] = new JsonObject
                        {
                            ["codeActionKind"] = new JsonObject { ["valueSet"] = new JsonArray("", "quickfix", "refactor", "refactor.extract", "refactor.inline", "refactor.rewrite", "source", "source.organizeImports") },
                        },
                        ["resolveSupport"] = new JsonObject { ["properties"] = new JsonArray("edit") },
                    },
                    ["publishDiagnostics"] = new JsonObject { ["relatedInformation"] = false },
                },
            },
        };
    }

    private void OnNotification(string method, JsonNode? p)
    {
        switch (method)
        {
            case "textDocument/publishDiagnostics":
            {
                var path = LspUtil.ToPath(LspUtil.Str(p?["uri"]));
                var list = new List<LspDiagnostic>();
                foreach (var d in p?["diagnostics"] as JsonArray ?? [])
                {
                    var (sl, sc) = LspUtil.ReadPos(d?["range"]?["start"]);
                    var (el, ec) = LspUtil.ReadPos(d?["range"]?["end"]);
                    list.Add(new LspDiagnostic(sl, sc, el, ec,
                        d?["severity"]?.GetValue<int>() ?? 1,
                        LspUtil.Str(d?["message"]),
                        d?["source"] is { } s ? LspUtil.Str(s) : null,
                        d?["code"]?.ToString())
                    { Raw = d!.DeepClone() });
                }
                Diagnostics?.Invoke(path, list);
                break;
            }
            case "window/logMessage":
                Log?.Invoke(LspUtil.Str(p?["message"]));
                break;
            case "window/showMessage":
                ShowMessage?.Invoke(p?["type"]?.GetValue<int>() ?? 3, LspUtil.Str(p?["message"]));
                break;
            case "$/progress":
            {
                var token = p?["token"]?.ToString() ?? "";
                var value = p?["value"];
                var kind = LspUtil.Str(value?["kind"]);
                if (kind == "end") _progress.Remove(token);
                else
                {
                    var title = LspUtil.Str(value?["title"]);
                    if (title.Length == 0 && _progress.TryGetValue(token, out var prev)) title = prev.Split(':')[0];
                    var message = LspUtil.Str(value?["message"]);
                    var pct = value?["percentage"] is { } n ? $" {n}%" : "";
                    _progress[token] = message.Length > 0 ? $"{title}: {message}{pct}" : title + pct;
                }
                StateChanged?.Invoke();
                break;
            }
        }
    }

    private async Task<JsonNode?> OnRequest(string method, JsonNode? p)
    {
        switch (method)
        {
            case "workspace/configuration":
            {
                var result = new JsonArray();
                foreach (var item in p?["items"] as JsonArray ?? [])
                {
                    var section = LspUtil.Str(item?["section"]);
                    JsonNode? value = Config.Settings;
                    if (section.Length > 0 && value is not null)
                        foreach (var part in section.Split('.'))
                            value = value?[part];
                    result.Add(value?.DeepClone());
                }
                return result;
            }
            case "workspace/workspaceFolders":
                return new JsonArray(new JsonObject { ["uri"] = LspUtil.ToUri(Root), ["name"] = Path.GetFileName(Root) });
            case "workspace/applyEdit":
            {
                var applied = ApplyEdit is not null && await ApplyEdit(p?["edit"]);
                return new JsonObject { ["applied"] = applied };
            }
            case "client/registerCapability":
                foreach (var reg in p?["registrations"] as JsonArray ?? [])
                    if (LspUtil.Str(reg?["method"]) == "workspace/didChangeWatchedFiles")
                        foreach (var w in reg?["registerOptions"]?["watchers"] as JsonArray ?? [])
                        {
                            var pattern = w?["globPattern"] is JsonValue g ? LspUtil.Str(g) : LspUtil.Str(w?["globPattern"]?["pattern"]);
                            if (pattern.Length > 0) _watchers.Add(Glob.ToRegex(pattern.Replace('\\', '/')));
                        }
                return null;
            case "window/workDoneProgress/create":
            case "client/unregisterCapability":
            case "window/showMessageRequest":
            case "workspace/semanticTokens/refresh":
            case "workspace/inlayHint/refresh":
            case "workspace/codeLens/refresh":
            case "workspace/diagnostic/refresh":
                return null;
            default:
                throw new JsonRpcException(-32601, $"Method not supported: {method}");
        }
    }

    private readonly List<System.Text.RegularExpressions.Regex> _watchers = new();

    /// <summary>Sends workspace/didChangeWatchedFiles for changes matching the server's registered globs.</summary>
    public void NotifyWatchedFiles(IEnumerable<(string Path, int Type)> events)
    {
        if (State != ServerState.Running || _watchers.Count == 0) return;
        var changes = new JsonArray();
        foreach (var (path, type) in events)
        {
            var rel = System.IO.Path.GetRelativePath(Root, path).Replace('\\', '/');
            var abs = path.Replace('\\', '/');
            if (_watchers.Any(w => w.IsMatch(rel) || w.IsMatch(abs) || w.IsMatch(System.IO.Path.GetFileName(path))))
                changes.Add(new JsonObject { ["uri"] = LspUtil.ToUri(path), ["type"] = type });
        }
        if (changes.Count > 0) Notify("workspace/didChangeWatchedFiles", new JsonObject { ["changes"] = changes });
    }

    public Task<JsonNode?> RequestAsync(string method, JsonNode? p, CancellationToken ct = default) =>
        _rpc is null || State != ServerState.Running
            ? Task.FromResult<JsonNode?>(null)
            : _rpc.RequestAsync(method, p, ct, TimeSpan.FromSeconds(30));

    public void Notify(string method, JsonNode? p)
    {
        if (State == ServerState.Running) _rpc?.Notify(method, p);
    }

    public async ValueTask DisposeAsync()
    {
        if (_rpc is not null && State == ServerState.Running)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _rpc.RequestAsync("shutdown", null, cts.Token);
                _rpc.Notify("exit", null);
                await Task.Delay(100);
            }
            catch { /* fall through to kill */ }
        }
        State = ServerState.Stopped;
        if (_rpc is not null) await _rpc.DisposeAsync();
        try { if (_process is { HasExited: false }) _process.Kill(true); } catch { /* already gone */ }
        StateChanged?.Invoke();
    }
}
