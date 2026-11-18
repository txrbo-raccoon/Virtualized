using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Virtualized.Qemu;

/// <summary>
/// QEMU Machine Protocol client over TCP. A background loop reads every message: replies complete the
/// command that sent the matching id, events go to <see cref="EventReceived"/>, and the end of the
/// stream (QEMU exited or the socket broke) raises <see cref="Disconnected"/> once.
/// </summary>
public sealed class QmpClient : IAsyncDisposable
{
    private readonly TcpClient _tcp = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly CancellationTokenSource _closing = new();
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private Task? _readLoop;
    private long _nextId;
    private int _disconnected;
    private volatile bool _closed;

    public event Action<string, JsonNode?>? EventReceived;

    public event Action? Disconnected;

    public JsonNode? Greeting { get; private set; }

    public bool IsConnected => _readLoop is not null && !_closed;

    public async Task ConnectAsync(string host, int port, CancellationToken ct = default)
    {
        await _tcp.ConnectAsync(host, port, ct);

        var stream = _tcp.GetStream();
        _reader = new StreamReader(stream, new UTF8Encoding(false));
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

        var greeting = await _reader.ReadLineAsync(ct) ?? throw new QmpException("QMP connection closed before the greeting.");
        Greeting = JsonNode.Parse(greeting);
        if (Greeting?["QMP"] is null)
            throw new QmpException("Unexpected QMP greeting.");

        _readLoop = Task.Run(_ReadLoopAsync, CancellationToken.None);
        await ExecuteAsync("qmp_capabilities", ct: ct);
    }

    public async Task<JsonNode?> ExecuteAsync(string command, object? arguments = null, CancellationToken ct = default)
    {
        if (_writer is null || !IsConnected)
            throw new QmpException("Not connected to QMP.");

        var id = Interlocked.Increment(ref _nextId).ToString();
        var reply = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = reply;

        // The read loop sets _closed before failing pending commands, so either it fails this one or we see the flag.
        if (_closed && _pending.TryRemove(id, out _))
            throw new QmpException("QMP connection closed.");

        var request = new JsonObject { ["execute"] = command, ["id"] = id };
        if (arguments is not null)
            request["arguments"] = JsonSerializer.SerializeToNode(arguments);

        await _writeLock.WaitAsync(ct);
        try
        {
            await _writer.WriteLineAsync(request.ToJsonString().AsMemory(), ct);
        }
        catch (IOException ex)
        {
            _pending.TryRemove(id, out _);
            throw new QmpException($"QMP connection closed: {ex.Message}");
        }
        finally
        {
            _writeLock.Release();
        }

        await using (ct.Register(() => reply.TrySetCanceled(ct)))
        {
            return await reply.Task;
        }
    }

    private async Task _ReadLoopAsync()
    {
        try
        {
            while (await _reader!.ReadLineAsync(_closing.Token) is { } line)
            {
                var message = JsonNode.Parse(line);
                if (message?["event"] is { } name)
                {
                    EventReceived?.Invoke(name.GetValue<string>(), message["data"]);
                }
                else if (message?["id"]?.GetValue<string>() is { } id && _pending.TryRemove(id, out var reply))
                {
                    if (message["error"] is { } error)
                        reply.TrySetException(new QmpException($"{error["class"]}: {error["desc"]}"));
                    else
                        reply.TrySetResult(message["return"]);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or JsonException)
        {
            // Any of these means the connection is gone; handled below.
        }

        _closed = true;
        foreach (var id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out var reply))
                reply.TrySetException(new QmpException("QMP connection closed."));
        }

        if (Interlocked.Exchange(ref _disconnected, 1) == 0)
            Disconnected?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        // Disposing is not a disconnection worth reporting: the owner already knows.
        Interlocked.Exchange(ref _disconnected, 1);
        await _closing.CancelAsync();
        _tcp.Dispose();
        if (_readLoop is not null)
            await _readLoop;
        _closing.Dispose();
        _writeLock.Dispose();
    }
}

public sealed class QmpException(string message) : Exception(message);
