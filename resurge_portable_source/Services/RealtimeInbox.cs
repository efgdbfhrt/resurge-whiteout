using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ResurgeLauncherCSharp.Services;

/// <summary>
/// Live connection to Supabase Realtime (Phoenix channels over a WebSocket). Delivers new messages and
/// friend-request changes for the signed-in player within a fraction of a second. Reconnects on its own;
/// while it is down the launcher falls back to polling. Events are raised on the creating thread's context.
/// </summary>
internal sealed class RealtimeInbox : IDisposable
{
    private readonly FriendsService _service;
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private int _ref;

    public RealtimeInbox(FriendsService service) => _service = service;

    /// <summary>True once the channel has joined and the database subscription is live.</summary>
    public bool Connected { get; private set; }

    public event Action<ChatMessage>? MessageReceived;
    public event Action? FriendshipsChanged;
    public event Action<bool>? ConnectionChanged;

    public void Start() => _ = Task.Run(RunAsync);

    public void Dispose()
    {
        _stop.Cancel();
        SetConnected(false);
    }

    private void Raise(Action action)
    {
        if (_stop.IsCancellationRequested) return;
        if (_context is null) action();
        else _context.Post(_ => { if (!_stop.IsCancellationRequested) action(); }, null);
    }

    private void SetConnected(bool value)
    {
        if (Connected == value) return;
        Connected = value;
        Raise(() => ConnectionChanged?.Invoke(value));
    }

    private async Task RunAsync()
    {
        int delay = 2;
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await SessionAsync(_stop.Token);
                delay = 2;
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException or FriendsException
                                           or HttpRequestException or JsonException or InvalidOperationException)
            {
                System.Diagnostics.Debug.WriteLine($"Realtime: {ex.Message}");
            }
            SetConnected(false);
            if (_stop.IsCancellationRequested) break;
            try { await Task.Delay(TimeSpan.FromSeconds(delay), _stop.Token); }
            catch (OperationCanceledException) { break; }
            delay = Math.Min(delay * 2, 60);
        }
    }

    private string NextRef() => Interlocked.Increment(ref _ref).ToString();

    private static JsonObject Change(string evt, string table, string filter) => new()
    {
        ["event"] = evt, ["schema"] = "public", ["table"] = table, ["filter"] = filter
    };

    private async Task SessionAsync(CancellationToken stop)
    {
        if (FriendsService.Endpoint is not { } endpoint || _service.UserId is not { } uid)
            throw new FriendsException("Friends are not connected.");
        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        string wss = "wss://" + endpoint.Url["https://".Length..];
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(stop))
        {
            connect.CancelAfter(TimeSpan.FromSeconds(15));
            await socket.ConnectAsync(new Uri($"{wss}/realtime/v1/websocket?apikey={Uri.EscapeDataString(endpoint.Key)}&vsn=1.0.0"), connect.Token);
        }

        string token = await _service.GetAccessTokenAsync(stop);
        string topic = "realtime:whiteout-" + uid;
        string joinRef = NextRef();
        await SendAsync(socket, new JsonObject
        {
            ["topic"] = topic,
            ["event"] = "phx_join",
            ["payload"] = new JsonObject
            {
                ["config"] = new JsonObject
                {
                    ["broadcast"] = new JsonObject { ["ack"] = false, ["self"] = false },
                    ["presence"] = new JsonObject { ["key"] = "" },
                    ["postgres_changes"] = new JsonArray
                    {
                        Change("INSERT", "messages", $"recipient=eq.{uid}"),
                        Change("INSERT", "friendships", $"addressee=eq.{uid}"),
                        Change("UPDATE", "friendships", $"requester=eq.{uid}")
                    },
                    ["private"] = false
                },
                ["access_token"] = token
            },
            ["ref"] = joinRef,
            ["join_ref"] = joinRef
        }, stop);

        using var session = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var heartbeat = HeartbeatAsync(socket, topic, joinRef, token, session.Token);
        try { await ReceiveAsync(socket, joinRef, session.Token); }
        finally
        {
            session.Cancel();
            try { await heartbeat; } catch (Exception) { /* Ends with the socket. */ }
            if (socket.State == WebSocketState.Open)
            {
                try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); }
                catch (Exception ex) when (ex is WebSocketException or IOException) { }
            }
        }
    }

    /// <summary>Phoenix needs a heartbeat at least every 30 s; the access token is refreshed on the channel as it rotates.</summary>
    private async Task HeartbeatAsync(ClientWebSocket socket, string topic, string joinRef, string token, CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(25), cancel);
            await SendAsync(socket, new JsonObject { ["topic"] = "phoenix", ["event"] = "heartbeat", ["payload"] = new JsonObject(), ["ref"] = NextRef() }, cancel);
            string current = await _service.GetAccessTokenAsync(cancel);
            if (current == token) continue;
            token = current;
            await SendAsync(socket, new JsonObject
            {
                ["topic"] = topic, ["event"] = "access_token", ["payload"] = new JsonObject { ["access_token"] = token },
                ["ref"] = NextRef(), ["join_ref"] = joinRef
            }, cancel);
        }
    }

    private async Task SendAsync(ClientWebSocket socket, JsonObject message, CancellationToken cancel)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await _sendLock.WaitAsync(cancel);
        try { await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancel); }
        finally { _sendLock.Release(); }
    }

    private async Task ReceiveAsync(ClientWebSocket socket, string joinRef, CancellationToken cancel)
    {
        var buffer = new byte[16 * 1024];
        using var frame = new MemoryStream();
        while (socket.State == WebSocketState.Open && !cancel.IsCancellationRequested)
        {
            frame.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, cancel);
                if (result.MessageType == WebSocketMessageType.Close) return;
                frame.Write(buffer, 0, result.Count);
                if (frame.Length > 1_000_000) throw new InvalidOperationException("Realtime frame too large.");
            } while (!result.EndOfMessage);

            var message = JsonNode.Parse(frame.ToArray());
            string evt = message?["event"]?.GetValue<string>() ?? "";
            string reference = message?["ref"] is JsonValue refValue && refValue.TryGetValue(out string? r) ? r ?? "" : "";
            var payload = message?["payload"];
            string status = payload?["status"] is JsonValue statusValue && statusValue.TryGetValue(out string? s) ? s ?? "" : "";
            bool joinReply = evt == "phx_reply" && reference == joinRef;
            bool systemError = evt == "system" && status == "error";
            if (joinReply)
            {
                if (status != "ok")
                    throw new InvalidOperationException("Realtime join refused: " + payload?["response"]?.ToJsonString());
                SetConnected(true);
                continue;
            }
            if (systemError) throw new InvalidOperationException("Realtime: " + payload?["message"]?.ToJsonString());
            switch (evt)
            {
                case "phx_error" or "phx_close":
                    throw new InvalidOperationException("Realtime channel closed.");
                case "postgres_changes":
                    var data = payload?["data"];
                    string table = data?["table"]?.GetValue<string>() ?? "";
                    if (table == "messages" && data?["record"] is { } record)
                    {
                        var chat = _service.ParseMessage(record);
                        Raise(() => MessageReceived?.Invoke(chat));
                    }
                    else if (table == "friendships")
                        Raise(() => FriendshipsChanged?.Invoke());
                    break;
            }
        }
    }
}
