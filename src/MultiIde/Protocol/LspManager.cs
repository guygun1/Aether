using MultiIde.Core;

namespace MultiIde.Protocol;

/// <summary>Starts at most one language server per server id, lazily, when a file needs it.</summary>
public sealed class LspManager
{
    private readonly PluginManager _plugins;
    private readonly Dictionary<string, LspClient> _clients = new();
    private readonly Dictionary<string, Task<LspClient?>> _starting = new();

    public string? Root { get; set; }
    public IEnumerable<LspClient> Clients => _clients.Values;

    /// <summary>Raised whenever any server changes state or progress (any thread).</summary>
    public event Action? Changed;
    public event Action<LspClient>? ClientCreated;

    public LspManager(PluginManager plugins) => _plugins = plugins;

    public LspClient? Running(string languageId)
    {
        var cfg = _plugins.LspFor(languageId);
        return cfg is not null && _clients.TryGetValue(cfg.Value.Config.Id, out var c) && c.State == ServerState.Running ? c : null;
    }

    public LspClient? Get(string serverId) => _clients.GetValueOrDefault(serverId);

    public Task<LspClient?> EnsureAsync(string languageId)
    {
        if (Root is null) return Task.FromResult<LspClient?>(null);
        var cfg = _plugins.LspFor(languageId);
        if (cfg is null) return Task.FromResult<LspClient?>(null);
        var (config, plugin) = cfg.Value;

        if (_starting.TryGetValue(config.Id, out var pending)) return pending;
        if (_clients.TryGetValue(config.Id, out var existing))
            return Task.FromResult(existing.State == ServerState.Running ? existing : null);

        var client = new LspClient(config, plugin, Root);
        _clients[config.Id] = client;
        client.StateChanged += () => Changed?.Invoke();
        ClientCreated?.Invoke(client);
        var task = Start(client);
        _starting[config.Id] = task;
        return task;

        async Task<LspClient?> Start(LspClient c)
        {
            await c.StartAsync();
            _starting.Remove(c.Config.Id);
            return c.State == ServerState.Running ? c : null;
        }
    }

    public async Task RestartAsync(string serverId)
    {
        if (_clients.Remove(serverId, out var client))
        {
            await client.DisposeAsync();
            Changed?.Invoke();
        }
    }

    public async Task StopAllAsync()
    {
        var all = _clients.Values.ToList();
        _clients.Clear();
        await Task.WhenAll(all.Select(c => c.DisposeAsync().AsTask()));
        Changed?.Invoke();
    }
}
