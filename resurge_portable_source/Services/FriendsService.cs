using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ResurgeLauncherCSharp.Services;

/// <summary>How a player relates to you. <see cref="None"/> is someone you only chatted with or looked up.</summary>
internal enum FriendState { Friend, Incoming, Outgoing, None, Self }

internal sealed record FriendEntry(string Id, string Name, FriendState State, bool Online, bool InGame, string Activity,
    long PlaySeconds, int BestRound, int Sessions, string? Avatar, DateTimeOffset LastSeen, int Unread, string About)
{
    public string StatusText => State switch
    {
        FriendState.Incoming => "Wants to be your friend",
        FriendState.Outgoing => "Request sent",
        _ => InGame ? (Activity.Length > 0 ? Activity : "In game") : Online ? "Online" : "Offline"
    };
}

internal sealed record ChatMessage(long Id, string Sender, string Recipient, string Body, DateTimeOffset SentAt, bool Mine);

internal sealed class FriendsException(string message) : Exception(message);

/// <summary>
/// Friends, presence and chat through a Supabase project (REST + anonymous auth). Each local profile
/// becomes an anonymous Supabase user; the refresh token is kept in the profile file.
/// </summary>
internal sealed class FriendsService
{
    private sealed record Config(string Url, string AnonKey);

    private const string PlayerColumns = "id,name,avatar,play_seconds,best_round,sessions,status,activity,last_seen,about";
    private const string MessageColumns = "id,sender,recipient,body,sent_at";
    public const int AboutLimit = 300;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly Config? Settings = LoadConfig();
    public static bool Configured => Settings is not null;

    private readonly SemaphoreSlim _refresh = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _expires;
    public string? UserId { get; private set; }
    public string? RefreshToken { get; private set; }
    public bool SignedIn => UserId is not null;

    /// <summary>The project's base URL and public key, for the live (Realtime) connection.</summary>
    public static (string Url, string Key)? Endpoint => Settings is null ? null : (Settings.Url, Settings.AnonKey);

    private static Config? LoadConfig()
    {
        try
        {
            string local = Path.Combine(GameLauncher.DataPath, "friends.json");
            string? json = File.Exists(local) ? File.ReadAllText(local) : null;
            if (json is null)
            {
                using var stream = typeof(FriendsService).Assembly.GetManifestResourceStream("Whiteout.Friends");
                if (stream is null) return null;
                json = new StreamReader(stream).ReadToEnd();
            }
            var node = JsonNode.Parse(json);
            string url = node?["url"]?.GetValue<string>()?.TrimEnd('/') ?? "";
            string key = node?["anonKey"]?.GetValue<string>() ?? "";
            return url.StartsWith("https://", StringComparison.Ordinal) && key.Length > 20 ? new Config(url, key) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException) { return null; }
    }

    // ------------------------------------------------------------------ auth

    /// <summary>Signs the profile in (or registers it the first time) and returns the refresh token to store.</summary>
    public async Task<string> SignInAsync(string profileName, string? refreshToken, CancellationToken cancel = default)
    {
        if (Settings is null) throw new FriendsException("Friends are not set up on this launcher.");
        JsonNode? session = null;
        if (!string.IsNullOrEmpty(refreshToken))
        {
            try
            {
                session = await AuthAsync("token?grant_type=refresh_token", new JsonObject { ["refresh_token"] = refreshToken }, cancel);
            }
            catch (FriendsException) { session = null; } // Expired or revoked: register a fresh account below.
        }
        bool created = session is null;
        session ??= await AuthAsync("signup", new JsonObject { ["data"] = new JsonObject() }, cancel);
        Accept(session);
        if (created || !await PlayerExistsAsync(cancel))
        {
            var row = new JsonObject { ["id"] = UserId, ["name"] = profileName };
            using var response = await SendAsync(HttpMethod.Post, "players", row, cancel, prefer: "return=minimal");
            if (response.StatusCode == HttpStatusCode.Conflict)
                throw new FriendsException($"The name \"{profileName}\" is already taken online. Make a profile with another name to use Friends.");
            await EnsureAsync(response);
        }
        return RefreshToken!;
    }

    private void Accept(JsonNode? session)
    {
        string? token = session?["access_token"]?.GetValue<string>();
        string? refresh = session?["refresh_token"]?.GetValue<string>();
        string? user = session?["user"]?["id"]?.GetValue<string>();
        if (token is null || user is null) throw new FriendsException("The friends server did not accept the sign-in.");
        int seconds = session?["expires_in"]?.GetValue<int>() ?? 3600;
        _accessToken = token;
        RefreshToken = refresh ?? RefreshToken;
        UserId = user;
        _expires = DateTimeOffset.UtcNow.AddSeconds(seconds - 120);
    }

    private async Task<JsonNode?> AuthAsync(string path, JsonObject body, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Settings!.Url}/auth/v1/{path}")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("apikey", Settings.AnonKey);
        using var response = await Http.SendAsync(request, cancel);
        string text = await response.Content.ReadAsStringAsync(cancel);
        if (!response.IsSuccessStatusCode) throw new FriendsException($"Friends sign-in failed ({(int)response.StatusCode}).");
        return JsonNode.Parse(text);
    }

    /// <summary>A valid access token, refreshed first when it is about to expire. Several callers may ask at once.</summary>
    public async Task<string> GetAccessTokenAsync(CancellationToken cancel = default)
    {
        if (_accessToken is null) throw new FriendsException("Not signed in to Friends.");
        if (DateTimeOffset.UtcNow < _expires || RefreshToken is null) return _accessToken;
        await _refresh.WaitAsync(cancel);
        try
        {
            if (DateTimeOffset.UtcNow >= _expires)
                Accept(await AuthAsync("token?grant_type=refresh_token", new JsonObject { ["refresh_token"] = RefreshToken }, cancel));
            return _accessToken!;
        }
        finally { _refresh.Release(); }
    }

    // ------------------------------------------------------------------ REST helpers

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken cancel,
        string? prefer = null)
    {
        if (Settings is null || _accessToken is null) throw new FriendsException("Not signed in to Friends.");
        string token = await GetAccessTokenAsync(cancel);
        var request = new HttpRequestMessage(method, $"{Settings.Url}/rest/v1/{path}");
        request.Headers.Add("apikey", Settings.AnonKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (prefer is not null) request.Headers.Add("Prefer", prefer);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return await Http.SendAsync(request, cancel);
    }

    private static async Task EnsureAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        string detail = await response.Content.ReadAsStringAsync();
        throw new FriendsException($"Friends server error ({(int)response.StatusCode}). {Short(detail)}");
    }

    private static string Short(string text) => text.Length > 160 ? text[..160] : text;

    private async Task<JsonArray> GetArrayAsync(string path, CancellationToken cancel)
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, cancel);
        await EnsureAsync(response);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(cancel)) as JsonArray ?? [];
    }

    private async Task<bool> PlayerExistsAsync(CancellationToken cancel) =>
        (await GetArrayAsync($"players?id=eq.{UserId}&select=id", cancel)).Count > 0;

    // ------------------------------------------------------------------ players

    private static string Text(JsonNode? node, string name) => node?[name] is JsonValue value && value.TryGetValue(out string? text) ? text ?? "" : "";
    private static long Number(JsonNode? node, string name) => node?[name] is JsonValue value && value.TryGetValue(out long number) ? number : 0;

    private FriendEntry ParsePlayer(JsonNode player, FriendState state, int unread)
    {
        string id = Text(player, "id");
        var lastSeen = DateTimeOffset.TryParse(Text(player, "last_seen"), out var seen) ? seen : DateTimeOffset.MinValue;
        string status = Text(player, "status") is { Length: > 0 } s ? s : "offline";
        bool online = status != "offline" && DateTimeOffset.UtcNow - lastSeen < TimeSpan.FromSeconds(100);
        string? avatar = Text(player, "avatar") is { Length: > 0 } a ? a : null;
        return new FriendEntry(id, Text(player, "name"), id == UserId ? FriendState.Self : state, online,
            online && status == "ingame", Text(player, "activity"), Number(player, "play_seconds"),
            (int)Number(player, "best_round"), (int)Number(player, "sessions"), avatar, lastSeen, unread, Text(player, "about"));
    }

    /// <summary>Players whose name starts with what was typed, best matches first (online, then shortest name).</summary>
    public async Task<FriendEntry[]> SearchPlayersAsync(string prefix, CancellationToken cancel = default)
    {
        prefix = prefix.Trim().ToLowerInvariant();
        if (prefix.Length == 0 || prefix.Any(c => !(char.IsLetterOrDigit(c) || c is ' ' or '_' or '-'))) return [];
        var rows = await GetArrayAsync($"players?name_key=like.{Uri.EscapeDataString(prefix)}*&order=name_key&limit=12&select={PlayerColumns}", cancel);
        return rows.OfType<JsonNode>().Select(row => ParsePlayer(row, FriendState.None, 0))
            .Where(player => player.Id != UserId && player.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(player => player.Name.Equals(prefix, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(player => player.Online).ThenBy(player => player.Name.Length).ThenBy(player => player.Name)
            .Take(8).ToArray();
    }

    /// <summary>One player's public profile (state is <see cref="FriendState.None"/>; the caller knows the friendship).</summary>
    public async Task<FriendEntry?> GetPlayerAsync(string id, CancellationToken cancel = default)
    {
        var rows = await GetArrayAsync($"players?id=eq.{Uri.EscapeDataString(id)}&select={PlayerColumns}", cancel);
        return rows.Count > 0 && rows[0] is { } row ? ParsePlayer(row, FriendState.None, 0) : null;
    }

    // ------------------------------------------------------------------ presence and profile

    public async Task UpdatePresenceAsync(bool inGame, string activity, PlayerProfile profile, CancellationToken cancel = default)
    {
        var row = new JsonObject
        {
            ["status"] = inGame ? "ingame" : "online",
            ["activity"] = activity.Length > 80 ? activity[..80] : activity,
            ["last_seen"] = DateTimeOffset.UtcNow.ToString("O"),
            ["play_seconds"] = profile.Seconds,
            ["best_round"] = profile.BestRound ?? 0,
            ["sessions"] = profile.Sessions
        };
        using var response = await SendAsync(HttpMethod.Patch, $"players?id=eq.{UserId}", row, cancel, "return=minimal");
        await EnsureAsync(response);
    }

    public async Task GoOfflineAsync()
    {
        if (!SignedIn) return;
        try
        {
            using var response = await SendAsync(HttpMethod.Patch, $"players?id=eq.{UserId}",
                new JsonObject { ["status"] = "offline", ["activity"] = "" }, CancellationToken.None, "return=minimal");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or FriendsException) { }
    }

    /// <summary>Uploads a small avatar (PNG, at most 64 KB) as base64.</summary>
    public async Task UploadAvatarAsync(byte[] png, CancellationToken cancel = default)
    {
        if (png.Length > 64_000) throw new FriendsException("The picture is too large for the friends list.");
        using var response = await SendAsync(HttpMethod.Patch, $"players?id=eq.{UserId}",
            new JsonObject { ["avatar"] = Convert.ToBase64String(png) }, cancel, "return=minimal");
        await EnsureAsync(response);
    }

    /// <summary>Saves the "About me" text everyone sees on your profile.</summary>
    public async Task SetAboutAsync(string about, CancellationToken cancel = default)
    {
        about = about.Trim().ReplaceLineEndings(" ");
        if (about.Length > AboutLimit) about = about[..AboutLimit];
        using var response = await SendAsync(HttpMethod.Patch, $"players?id=eq.{UserId}",
            new JsonObject { ["about"] = about }, cancel, "return=minimal");
        await EnsureAsync(response);
    }

    // ------------------------------------------------------------------ friends

    public async Task<string> SendRequestAsync(string name, CancellationToken cancel = default)
    {
        name = name.Trim();
        PlayerProfiles.ValidateName(name);
        var found = await GetArrayAsync($"players?name_key=eq.{Uri.EscapeDataString(name.ToLowerInvariant())}&select=id,name", cancel);
        if (found.Count == 0) throw new FriendsException($"No player called \"{name}\" was found.");
        return await SendRequestToAsync(found[0]!["id"]!.GetValue<string>(), found[0]!["name"]!.GetValue<string>(), cancel);
    }

    public async Task<string> SendRequestToAsync(string id, string realName, CancellationToken cancel = default)
    {
        if (id == UserId) throw new FriendsException("That's you.");

        // If they already asked us, accepting is the friendly answer.
        var incoming = await GetArrayAsync($"friendships?requester=eq.{id}&addressee=eq.{UserId}&select=accepted", cancel);
        if (incoming.Count > 0)
        {
            await AcceptAsync(id, cancel);
            return $"You and {realName} are now friends.";
        }
        using var response = await SendAsync(HttpMethod.Post, "friendships",
            new JsonObject { ["requester"] = UserId, ["addressee"] = id }, cancel, "return=minimal");
        if (response.StatusCode == HttpStatusCode.Conflict) return $"You already sent {realName} a request.";
        await EnsureAsync(response);
        return $"Friend request sent to {realName}.";
    }

    public async Task AcceptAsync(string requesterId, CancellationToken cancel = default)
    {
        using var response = await SendAsync(HttpMethod.Patch, $"friendships?requester=eq.{requesterId}&addressee=eq.{UserId}",
            new JsonObject { ["accepted"] = true }, cancel, "return=minimal");
        await EnsureAsync(response);
    }

    public async Task RemoveAsync(string otherId, CancellationToken cancel = default)
    {
        foreach (string filter in new[] { $"requester=eq.{UserId}&addressee=eq.{otherId}", $"requester=eq.{otherId}&addressee=eq.{UserId}" })
        {
            using var response = await SendAsync(HttpMethod.Delete, $"friendships?{filter}", null, cancel);
            await EnsureAsync(response);
        }
    }

    /// <summary>
    /// Friends, requests, and everyone you have chatted with (newest conversation first), with unread counts.
    /// </summary>
    public async Task<FriendEntry[]> GetFriendsAsync(CancellationToken cancel = default)
    {
        var linksTask = GetArrayAsync($"friendships?or=(requester.eq.{UserId},addressee.eq.{UserId})&select=requester,addressee,accepted", cancel);
        var recentTask = GetArrayAsync($"messages?or=(sender.eq.{UserId},recipient.eq.{UserId})&order=id.desc&limit=300&select=sender,recipient", cancel);
        var unreadTask = GetArrayAsync($"messages?recipient=eq.{UserId}&read=eq.false&select=sender", cancel);
        await Task.WhenAll(linksTask, recentTask, unreadTask);

        var states = new Dictionary<string, FriendState>();
        foreach (var link in linksTask.Result.OfType<JsonNode>())
        {
            string requester = Text(link, "requester");
            string addressee = Text(link, "addressee");
            bool accepted = link["accepted"]?.GetValue<bool>() == true;
            string other = requester == UserId ? addressee : requester;
            states[other] = accepted ? FriendState.Friend : requester == UserId ? FriendState.Outgoing : FriendState.Incoming;
        }
        // Conversation order: index of the newest message with each player.
        var recency = new Dictionary<string, int>();
        int index = 0;
        foreach (var row in recentTask.Result.OfType<JsonNode>())
        {
            string other = Text(row, "sender") == UserId ? Text(row, "recipient") : Text(row, "sender");
            if (other.Length > 0 && other != UserId) recency.TryAdd(other, index);
            index++;
        }
        foreach (string other in recency.Keys) states.TryAdd(other, FriendState.None);
        if (states.Count == 0) return [];

        var unreadBy = unreadTask.Result.OfType<JsonNode>().GroupBy(row => Text(row, "sender"))
            .ToDictionary(group => group.Key, group => group.Count());
        var players = await GetArrayAsync($"players?id=in.({string.Join(",", states.Keys)})&select={PlayerColumns}", cancel);
        var result = players.OfType<JsonNode>()
            .Select(player => ParsePlayer(player, states.GetValueOrDefault(Text(player, "id"), FriendState.None), unreadBy.GetValueOrDefault(Text(player, "id"))))
            .ToArray();
        int Group(FriendEntry entry) => entry.State switch
        {
            FriendState.Incoming => 0,
            _ when entry.Unread > 0 => 1,
            FriendState.Friend => 2,
            FriendState.None => 3,
            _ => 4
        };
        return result.OrderBy(Group)
            .ThenBy(entry => entry.State == FriendState.None || entry.Unread > 0 ? recency.GetValueOrDefault(entry.Id, int.MaxValue) : 0)
            .ThenByDescending(entry => entry.InGame).ThenByDescending(entry => entry.Online).ThenBy(entry => entry.Name).ToArray();
    }

    // ------------------------------------------------------------------ chat

    internal ChatMessage ParseMessage(JsonNode row) => new(Number(row, "id"), Text(row, "sender"), Text(row, "recipient"),
        Text(row, "body"), DateTimeOffset.TryParse(Text(row, "sent_at"), out var sent) ? sent : DateTimeOffset.UtcNow,
        Text(row, "sender") == UserId);

    public async Task<ChatMessage[]> GetMessagesAsync(string otherId, long afterId, CancellationToken cancel = default)
    {
        string pair = $"or=(and(sender.eq.{UserId},recipient.eq.{otherId}),and(sender.eq.{otherId},recipient.eq.{UserId}))";
        var rows = await GetArrayAsync($"messages?{pair}&id=gt.{afterId}&order=id.desc&limit=80&select={MessageColumns}", cancel);
        return rows.OfType<JsonNode>().Select(ParseMessage).OrderBy(message => message.Id).ToArray();
    }

    /// <summary>Messages sent to you after <paramref name="afterId"/> (the fallback when the live connection is down).</summary>
    public async Task<ChatMessage[]> GetIncomingAsync(long afterId, CancellationToken cancel = default)
    {
        var rows = await GetArrayAsync($"messages?recipient=eq.{UserId}&id=gt.{afterId}&order=id.asc&limit=50&select={MessageColumns}", cancel);
        return rows.OfType<JsonNode>().Select(ParseMessage).ToArray();
    }

    /// <summary>The id of the newest message sent to you, so only messages after sign-in raise notifications.</summary>
    public async Task<long> GetLatestIncomingIdAsync(CancellationToken cancel = default)
    {
        var rows = await GetArrayAsync($"messages?recipient=eq.{UserId}&order=id.desc&limit=1&select=id", cancel);
        return rows.Count > 0 ? Number(rows[0], "id") : 0;
    }

    public async Task<ChatMessage?> SendMessageAsync(string otherId, string text, CancellationToken cancel = default)
    {
        text = text.Trim();
        if (text.Length is 0) return null;
        if (text.Length > 500) text = text[..500];
        if (otherId == UserId) throw new FriendsException("You can't message yourself.");
        using var response = await SendAsync(HttpMethod.Post, $"messages?select={MessageColumns}",
            new JsonObject { ["sender"] = UserId, ["recipient"] = otherId, ["body"] = text }, cancel, "return=representation");
        await EnsureAsync(response);
        var rows = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancel)) as JsonArray;
        return rows is { Count: > 0 } && rows[0] is { } row ? ParseMessage(row) : null;
    }

    public async Task MarkReadAsync(string otherId, CancellationToken cancel = default)
    {
        using var response = await SendAsync(HttpMethod.Patch, $"messages?sender=eq.{otherId}&recipient=eq.{UserId}&read=eq.false",
            new JsonObject { ["read"] = true }, cancel, "return=minimal");
        await EnsureAsync(response);
    }
}
