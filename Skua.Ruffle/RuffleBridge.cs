using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;

namespace Skua.Ruffle;

public delegate void RuffleEventHandler(string name, object?[] args);

public sealed class RuffleCallException(string message) : Exception(message);

/// <summary>
/// WebSocket server the bridge page (web/public/skua-bridge.js) connects to.
///
/// Protocol, one JSON object per text message:
/// <list type="bullet">
/// <item>host → page <c>{"id":1,"fn":"getGameObject","args":["world.strMapName"]}</c></item>
/// <item>page → host <c>{"id":1,"ok":true,"value":"\"battleon\""}</c> or <c>{"id":1,"ok":false,"error":"..."}</c></item>
/// <item>page → host <c>{"ev":"packetFromServer","args":[[...]]}</c> for the SWF's ExternalInterface.call</item>
/// </list>
///
/// <see cref="Invoke"/> is synchronous, like Flash's CallFunction, because
/// Skua.Core's IFlashUtil is. Events are delivered in order on one background
/// thread, never on the receive loop, so a handler may call back into the SWF.
/// </summary>
public sealed class RuffleBridge : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly HashSet<string> _allowedOrigins;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Channel<(string Name, object?[] Args)> _events = Channel.CreateUnbounded<(string, object?[])>(new() { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private WebSocket? _socket;
    private int _nextId;

    /// <param name="prefix">HttpListener prefix, e.g. <c>http://127.0.0.1:8790/</c>.</param>
    /// <param name="allowedOrigins">
    /// Origins allowed to connect (the page's). Any web page can open a
    /// WebSocket to localhost, so this is what keeps other pages from driving
    /// the game. A connection without an Origin header (a non-browser client)
    /// is always allowed.
    /// </param>
    public RuffleBridge(string prefix, IEnumerable<string> allowedOrigins)
    {
        _listener.Prefixes.Add(prefix);
        _allowedOrigins = new HashSet<string>(allowedOrigins, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Raised for each ExternalInterface.call the SWF makes.</summary>
    public event RuffleEventHandler? FlashCall;

    public event Action<bool>? ConnectionChanged;

    public bool IsConnected => _socket?.State == WebSocketState.Open;

    public TimeSpan CallTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public void Start()
    {
        _listener.Start();
        _ = AcceptLoop();
        _ = EventLoop();
    }

    /// <summary>Waits until a page is connected, or the timeout passes.</summary>
    public bool WaitForConnection(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (!IsConnected && DateTime.UtcNow < until)
            Thread.Sleep(50);
        return IsConnected;
    }

    /// <summary>
    /// Calls a SWF callback and returns its raw result, or null when no page
    /// is connected.
    /// </summary>
    /// <exception cref="RuffleCallException">The call threw in the page.</exception>
    /// <exception cref="TimeoutException">No reply within <see cref="CallTimeout"/>.</exception>
    public JsonElement? Invoke(string function, params object?[] args)
    {
        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open)
            return null;

        int id = Interlocked.Increment(ref _nextId);
        var reply = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = reply;
        try
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var w = new Utf8JsonWriter(buffer))
            {
                w.WriteStartObject();
                w.WriteNumber("id", id);
                w.WriteString("fn", function);
                w.WritePropertyName("args");
                w.WriteStartArray();
                foreach (var arg in args)
                    FlashValue.Write(w, arg);
                w.WriteEndArray();
                w.WriteEndObject();
            }

            _sendLock.Wait();
            try
            {
                socket.SendAsync(buffer.WrittenMemory, WebSocketMessageType.Text, true, _cts.Token)
                    .AsTask().GetAwaiter().GetResult();
            }
            finally
            {
                _sendLock.Release();
            }

            if (!reply.Task.Wait(CallTimeout))
                throw new TimeoutException($"No reply to {function} within {CallTimeout.TotalSeconds:0.#}s");
            var message = reply.Task.Result;
            if (message.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
                throw new RuffleCallException(message.TryGetProperty("error", out var err) ? err.ToString() : function + " failed");
            return message.TryGetProperty("value", out var value) ? value : default(JsonElement);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch when (_cts.IsCancellationRequested)
            {
                return;
            }

            string? origin = ctx.Request.Headers["Origin"];
            if (!ctx.Request.IsWebSocketRequest || (origin is not null && !_allowedOrigins.Contains(origin)))
            {
                ctx.Response.StatusCode = ctx.Request.IsWebSocketRequest ? 403 : 400;
                ctx.Response.Close();
                continue;
            }

            var socket = (await ctx.AcceptWebSocketAsync(null)).WebSocket;
            var previous = Interlocked.Exchange(ref _socket, socket);
            if (previous is not null)
            {
                FailPending("the page reconnected");
                previous.Abort();
            }
            ConnectionChanged?.Invoke(true);
            _ = ReceiveLoop(socket);
        }
    }

    private async Task ReceiveLoop(WebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        var message = new ArrayBufferWriter<byte>();
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, _cts.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
                message.Write(buffer.AsSpan(0, result.Count));
                if (!result.EndOfMessage)
                    continue;
                Dispatch(message.WrittenSpan);
                message.Clear();
            }
        }
        catch (Exception) when (_cts.IsCancellationRequested || socket.State != WebSocketState.Open)
        {
        }
        finally
        {
            if (Interlocked.CompareExchange(ref _socket, null, socket) == socket)
            {
                FailPending("the page disconnected");
                ConnectionChanged?.Invoke(false);
            }
        }
    }

    private void Dispatch(ReadOnlySpan<byte> json)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(json.ToArray());
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return;
        }

        if (root.TryGetProperty("id", out var id) && id.TryGetInt32(out int n))
        {
            if (_pending.TryGetValue(n, out var reply))
                reply.TrySetResult(root);
        }
        else if (root.TryGetProperty("ev", out var ev) && ev.GetString() is { } name)
        {
            object?[] args = root.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().Select(FlashValue.Read).ToArray()
                : [];
            _events.Writer.TryWrite((name, args));
        }
    }

    private async Task EventLoop()
    {
        await foreach (var (name, args) in _events.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
        {
            try
            {
                FlashCall?.Invoke(name, args);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[bridge] {name} handler threw: {e.Message}");
            }
        }
    }

    private void FailPending(string why)
    {
        foreach (var (_, reply) in _pending)
            reply.TrySetException(new RuffleCallException(why));
    }

    public void Dispose()
    {
        _cts.Cancel();
        _socket?.Abort();
        _listener.Close();
        FailPending("the bridge was disposed");
    }
}
