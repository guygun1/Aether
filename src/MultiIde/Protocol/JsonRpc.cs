using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace MultiIde.Protocol;

public sealed class JsonRpcException(int code, string message, JsonNode? data = null) : Exception(message)
{
    public int Code { get; } = code;
    public JsonNode? Data { get; } = data;
}

public enum Framing
{
    /// <summary>LSP: <c>Content-Length</c> headers.</summary>
    ContentLength,
    /// <summary>MCP stdio: one JSON message per line.</summary>
    NewlineDelimited,
}

/// <summary>
/// Bidirectional JSON-RPC 2.0 over a pair of streams. Reading and writing
/// happen on background tasks; writes are serialized through a channel so
/// notifications and requests keep their order (important for didChange).
/// </summary>
public sealed class JsonRpcConnection : IAsyncDisposable
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly Framing _framing;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly Channel<string> _outbox = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private long _nextId;

    public event Action<string, JsonNode?>? NotificationReceived;
    public event Action<Exception?>? Closed;
    public Func<string, JsonNode?, Task<JsonNode?>>? RequestHandler { get; set; }

    public JsonRpcConnection(Stream input, Stream output, Framing framing)
    {
        _input = input;
        _output = output;
        _framing = framing;
    }

    public void Start()
    {
        _ = Task.Run(ReadLoop);
        _ = Task.Run(WriteLoop);
    }

    public async Task<JsonNode?> RequestAsync(string method, JsonNode? parameters, CancellationToken ct = default, TimeSpan? timeout = null)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var msg = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (parameters is not null) msg["params"] = parameters;
        Enqueue(msg);

        using var reg = ct.Register(() =>
        {
            if (_pending.TryRemove(id, out var p))
            {
                if (_framing == Framing.ContentLength) Notify("$/cancelRequest", new JsonObject { ["id"] = id });
                else Notify("notifications/cancelled", new JsonObject { ["requestId"] = id });
                p.TrySetCanceled(ct);
            }
        });
        if (timeout is { } t)
        {
            var finished = await Task.WhenAny(tcs.Task, Task.Delay(t, ct)).ConfigureAwait(false);
            if (finished != tcs.Task)
            {
                _pending.TryRemove(id, out _);
                throw new TimeoutException($"{method} timed out after {t.TotalSeconds:0}s");
            }
        }
        return await tcs.Task.ConfigureAwait(false);
    }

    public void Notify(string method, JsonNode? parameters)
    {
        var msg = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (parameters is not null) msg["params"] = parameters;
        Enqueue(msg);
    }

    private void Enqueue(JsonNode message) => _outbox.Writer.TryWrite(message.ToJsonString());

    private async Task WriteLoop()
    {
        try
        {
            await foreach (var json in _outbox.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                var body = Encoding.UTF8.GetBytes(json);
                if (_framing == Framing.ContentLength)
                {
                    var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
                    await _output.WriteAsync(header, _cts.Token).ConfigureAwait(false);
                    await _output.WriteAsync(body, _cts.Token).ConfigureAwait(false);
                }
                else
                {
                    await _output.WriteAsync(body, _cts.Token).ConfigureAwait(false);
                    await _output.WriteAsync("\n"u8.ToArray(), _cts.Token).ConfigureAwait(false);
                }
                await _output.FlushAsync(_cts.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            // Process ended; the read loop reports closure.
        }
    }

    private async Task ReadLoop()
    {
        Exception? error = null;
        try
        {
            var reader = new BufferedStream(_input, 64 * 1024);
            while (!_cts.IsCancellationRequested)
            {
                var body = _framing == Framing.ContentLength
                    ? await ReadFramed(reader).ConfigureAwait(false)
                    : await ReadLine(reader).ConfigureAwait(false);
                if (body is null) break;
                if (body.Length == 0) continue;
                JsonNode? node;
                try { node = JsonNode.Parse(body); }
                catch { continue; } // e.g. a server printing logs to stdout
                if (node is JsonObject obj) Dispatch(obj);
            }
        }
        catch (Exception e)
        {
            error = e;
        }

        foreach (var p in _pending.Values) p.TrySetException(new IOException("Connection closed"));
        _pending.Clear();
        Closed?.Invoke(error);
    }

    private static async Task<byte[]?> ReadFramed(Stream s)
    {
        var length = -1;
        var line = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            line.Clear();
            while (true)
            {
                if (await s.ReadAsync(one).ConfigureAwait(false) == 0) return null;
                if (one[0] == '\n') break;
                if (one[0] != '\r') line.Append((char)one[0]);
            }
            if (line.Length == 0)
            {
                if (length >= 0) break;
                continue;
            }
            var text = line.ToString();
            var colon = text.IndexOf(':');
            if (colon > 0 && text[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                length = int.Parse(text[(colon + 1)..].Trim());
        }
        var body = new byte[length];
        await s.ReadExactlyAsync(body).ConfigureAwait(false);
        return body;
    }

    private static async Task<byte[]?> ReadLine(Stream s)
    {
        var buffer = new MemoryStream();
        var one = new byte[1];
        while (true)
        {
            if (await s.ReadAsync(one).ConfigureAwait(false) == 0) return buffer.Length > 0 ? buffer.ToArray() : null;
            if (one[0] == '\n') return buffer.ToArray();
            if (one[0] != '\r') buffer.WriteByte(one[0]);
        }
    }

    private void Dispatch(JsonObject msg)
    {
        var method = msg["method"]?.GetValue<string>();
        var id = msg["id"];

        if (method is null && id is not null)
        {
            if (id.GetValueKind() == System.Text.Json.JsonValueKind.Number &&
                _pending.TryRemove(id.GetValue<long>(), out var tcs))
            {
                if (msg["error"] is JsonObject err)
                    tcs.TrySetException(new JsonRpcException(
                        err["code"]?.GetValue<int>() ?? -32603,
                        err["message"]?.GetValue<string>() ?? "Unknown error",
                        err["data"]?.DeepClone()));
                else
                    tcs.TrySetResult(msg["result"]?.DeepClone());
            }
            return;
        }

        if (method is null) return;
        var parameters = msg["params"]?.DeepClone();

        if (id is null)
        {
            try { NotificationReceived?.Invoke(method, parameters); }
            catch { /* handler bugs must not kill the connection */ }
            return;
        }

        var replyId = id.DeepClone();
        _ = Task.Run(async () =>
        {
            var reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = replyId };
            try
            {
                if (RequestHandler is null) throw new JsonRpcException(-32601, $"Unhandled method {method}");
                reply["result"] = await RequestHandler(method, parameters).ConfigureAwait(false);
            }
            catch (JsonRpcException e)
            {
                reply["error"] = new JsonObject { ["code"] = e.Code, ["message"] = e.Message };
            }
            catch (Exception e)
            {
                reply["error"] = new JsonObject { ["code"] = -32603, ["message"] = e.Message };
            }
            Enqueue(reply);
        });
    }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _outbox.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
