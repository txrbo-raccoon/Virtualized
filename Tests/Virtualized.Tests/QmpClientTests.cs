using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Virtualized.Qemu;

namespace Virtualized.Tests;

/// <summary>Runs <see cref="QmpClient"/> against a scripted fake QMP server on loopback.</summary>
public sealed class QmpClientTests : IAsyncDisposable
{
    private const string Greeting = """{"QMP": {"version": {"qemu": {"micro": 0, "minor": 1, "major": 11}}, "capabilities": ["oob"]}}""";

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private TcpClient? _server;
    private StreamReader? _reader;
    private StreamWriter? _writer;

    public QmpClientTests() => _listener.Start();

    private int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Accepts the client, sends the greeting and answers qmp_capabilities.</summary>
    private async Task<QmpClient> ConnectAsync()
    {
        var client = new QmpClient();
        var connect = client.ConnectAsync("127.0.0.1", Port);

        _server = await _listener.AcceptTcpClientAsync();
        var stream = _server.GetStream();
        _reader = new StreamReader(stream, Encoding.UTF8);
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        await _writer.WriteLineAsync(Greeting);

        var capabilities = await ReadRequestAsync();
        Assert.Equal("qmp_capabilities", capabilities["execute"]!.GetValue<string>());
        await ReplyAsync(capabilities, new JsonObject());

        await connect;
        return client;
    }

    private async Task<JsonNode> ReadRequestAsync() => JsonNode.Parse((await _reader!.ReadLineAsync())!)!;

    private Task ReplyAsync(JsonNode request, JsonNode result) =>
        _writer!.WriteLineAsync(new JsonObject { ["return"] = result, ["id"] = request["id"]!.GetValue<string>() }.ToJsonString());

    [Fact]
    public async Task ExecutesCommandsAndReadsTheGreeting()
    {
        await using var client = await ConnectAsync();
        Assert.Equal(11, client.Greeting!["QMP"]!["version"]!["qemu"]!["major"]!.GetValue<int>());

        var reply = client.ExecuteAsync("query-status");
        var request = await ReadRequestAsync();
        Assert.Equal("query-status", request["execute"]!.GetValue<string>());
        await ReplyAsync(request, new JsonObject { ["status"] = "running", ["running"] = true });

        Assert.Equal("running", (await reply)!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task RepliesAreMatchedById()
    {
        await using var client = await ConnectAsync();

        var first = client.ExecuteAsync("first");
        var second = client.ExecuteAsync("second");
        var firstRequest = await ReadRequestAsync();
        var secondRequest = await ReadRequestAsync();

        // Answer out of order.
        await ReplyAsync(secondRequest, JsonValue.Create("two"));
        await ReplyAsync(firstRequest, JsonValue.Create("one"));

        Assert.Equal("one", (await first)!.GetValue<string>());
        Assert.Equal("two", (await second)!.GetValue<string>());
    }

    [Fact]
    public async Task ErrorsBecomeExceptions()
    {
        await using var client = await ConnectAsync();

        var reply = client.ExecuteAsync("bogus");
        var request = await ReadRequestAsync();
        await _writer!.WriteLineAsync($$"""{"error": {"class": "CommandNotFound", "desc": "The command bogus has not been found"}, "id": "{{request["id"]}}"}""");

        var ex = await Assert.ThrowsAsync<QmpException>(() => reply);
        Assert.Contains("CommandNotFound", ex.Message);
    }

    [Fact]
    public async Task EventsAreRaisedWithoutACommandInFlight()
    {
        await using var client = await ConnectAsync();
        var received = new TaskCompletionSource<(string Name, JsonNode? Data)>();
        client.EventReceived += (name, data) => received.TrySetResult((name, data));

        await _writer!.WriteLineAsync("""{"timestamp": {"seconds": 1, "microseconds": 0}, "event": "SHUTDOWN", "data": {"guest": true, "reason": "guest-shutdown"}}""");

        var (name, data) = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("SHUTDOWN", name);
        Assert.Equal("guest-shutdown", data!["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task ServerClosingRaisesDisconnectedAndFailsPendingCommands()
    {
        await using var client = await ConnectAsync();
        var disconnected = new TaskCompletionSource();
        client.Disconnected += () => disconnected.TrySetResult();

        var pending = client.ExecuteAsync("quit");
        await ReadRequestAsync();
        _server!.Dispose();

        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<QmpException>(() => pending);
        Assert.False(client.IsConnected);
        await Assert.ThrowsAsync<QmpException>(() => client.ExecuteAsync("query-status"));
    }

    [Fact]
    public async Task DisposingDoesNotRaiseDisconnected()
    {
        var client = await ConnectAsync();
        var raised = false;
        client.Disconnected += () => raised = true;

        await client.DisposeAsync();

        Assert.False(raised);
    }

    public ValueTask DisposeAsync()
    {
        _server?.Dispose();
        _listener.Stop();
        return ValueTask.CompletedTask;
    }
}
