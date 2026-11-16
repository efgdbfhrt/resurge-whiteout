using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace ResurgeLauncherCSharp.Services;

internal sealed record WorkshopListing(string Id, string Title, string Preview, string Summary,
    long FileSize, int Subscriptions, int Stars, string[] Tags)
{
    public bool Installed { get; init; }
    public string InstallText => Installed ? "Installed ✓" : "Install";

    public string SizeText => FileSize switch
    {
        <= 0 => "",
        < 1L << 30 => $"{FileSize / (1024.0 * 1024):0} MB",
        _ => $"{FileSize / (1024.0 * 1024 * 1024):0.0} GB"
    };

    public string SubscribersText => Subscriptions >= 1000
        ? $"{Subscriptions / 1000.0:0.#}K players" : $"{Subscriptions} players";

    public string Thumbnail => string.IsNullOrEmpty(Preview) ? "" : Preview + "?imw=480&imh=270&ima=fit&impolicy=Letterbox";
    public string Kind => !Tags.Contains("Map") && Tags.Contains("Mod") ? "MOD"
        : Tags.Contains("Multiplayer") && !Tags.Contains("Zombies") ? "MP MAP" : "MAP";
}

internal sealed record WorkshopPage(WorkshopListing[] Items, int Page, int TotalPages, int TotalCount);

/// <summary>
/// Reads the public Steam Workshop browse page for Black Ops III. Steam renders the listing
/// server side and embeds the query result as JSON (valve-ssr-data), so no Web API key is needed.
/// </summary>
internal static class WorkshopBrowser
{
    private const string AppId = "311210";
    private static readonly HttpClient Http = CreateClient();

    public static readonly (string Label, string Sort)[] Sorts =
    [
        ("Most popular", "totaluniquesubscribers"),
        ("Trending", "trend"),
        ("Newest", "mostrecent"),
        ("Top rated", "toprated")
    ];

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) ResurgeWhiteout/2.7");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.8");
        return client;
    }

    /// <summary>
    /// category: "all", "maps" (every item tagged Map) or "mods". A search uses Steam's relevance
    /// search across the whole Workshop; the trending sort only covers a time window and hid
    /// older maps from search results.
    /// </summary>
    public static async Task<WorkshopPage> QueryAsync(string category, string sort, string search, int page,
        CancellationToken cancellation)
    {
        bool searching = !string.IsNullOrWhiteSpace(search);
        string order = searching ? "textsearch" : sort;
        var query = new List<string>
        {
            "appid=" + AppId,
            "section=readytouseitems",
            "browsesort=" + Uri.EscapeDataString(order),
            "actualsort=" + Uri.EscapeDataString(order),
            "p=" + page.ToString(CultureInfo.InvariantCulture)
        };
        if (order == "trend") query.Add("days=-1");
        string? tag = category switch { "maps" => "Map", "mods" => "Mod", _ => null };
        if (tag is not null) query.Add("requiredtags%5B%5D=" + tag);
        if (searching) query.Add("searchtext=" + Uri.EscapeDataString(search.Trim()));

        string url = "https://steamcommunity.com/workshop/browse/?" + string.Join("&", query);
        string html = await Http.GetStringAsync(url, cancellation);
        return Parse(html, page);
    }

    /// <summary>Details for one item, used when a Workshop link or ID is pasted into the search box.</summary>
    public static async Task<WorkshopListing?> GetItemAsync(string id, CancellationToken cancellation)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["itemcount"] = "1",
            ["publishedfileids[0]"] = id
        });
        using var response = await Http.PostAsync(
            "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/", content, cancellation);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
        if (!json.RootElement.TryGetProperty("response", out var body) ||
            !body.TryGetProperty("publishedfiledetails", out var list) || list.GetArrayLength() == 0) return null;
        var item = list[0];
        if (Number(item, "result") != 1 || Text(item, "consumer_app_id") != AppId) return null;
        string[] tags = item.TryGetProperty("tags", out var tagList) && tagList.ValueKind == JsonValueKind.Array
            ? tagList.EnumerateArray().Select(t => Text(t, "tag")).Where(t => t.Length > 0).ToArray() : [];
        long size = long.TryParse(Text(item, "file_size"), NumberStyles.None, CultureInfo.InvariantCulture, out long bytes) ? bytes : 0;
        return new WorkshopListing(id, Clean(Text(item, "title")), Text(item, "preview_url"), Clean(Text(item, "description")),
            size, Number(item, "subscriptions"), 0, tags);
    }

    internal static WorkshopPage Parse(string html, int page)
    {
        const string marker = "id=\"valve-ssr-data\"";
        int tag = html.IndexOf(marker, StringComparison.Ordinal);
        if (tag < 0) throw new InvalidDataException("Steam returned an unexpected Workshop page.");
        int start = html.IndexOf('>', tag) + 1;
        int end = html.IndexOf("</script>", start, StringComparison.Ordinal);
        if (start <= 0 || end < 0) throw new InvalidDataException("Steam returned an incomplete Workshop page.");

        using var outer = JsonDocument.Parse(html.AsMemory(start, end - start));
        return FindPage(outer.RootElement, page, 0) ?? new WorkshopPage([], page, page, 0);
    }

    // Steam keeps the query result in its dehydrated query cache. Some of that cache is stored as
    // JSON inside JSON strings, so string values that look like documents are parsed and searched too.
    private static WorkshopPage? FindPage(JsonElement element, int page, int depth)
    {
        if (depth > 24) return null;
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array &&
                    results.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.Object && item.TryGetProperty("publishedfileid", out _)))
                    return ReadPage(element, page);
                foreach (var property in element.EnumerateObject())
                    if (FindPage(property.Value, page, depth + 1) is { } found) return found;
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    if (FindPage(item, page, depth + 1) is { } found) return found;
                break;
            case JsonValueKind.String:
                string? text = element.GetString();
                if (text is null || text.Length < 2 || text[0] is not ('{' or '[') ||
                    !text.Contains("\"results\"", StringComparison.Ordinal) ||
                    !text.Contains("\"publishedfileid\"", StringComparison.Ordinal)) break;
                try
                {
                    using var inner = JsonDocument.Parse(text);
                    if (FindPage(inner.RootElement, page, depth + 1) is { } found) return found;
                }
                catch (JsonException) { }
                break;
        }
        return null;
    }

    private static WorkshopPage ReadPage(JsonElement data, int page)
    {
        int total = data.TryGetProperty("total_pages", out var pages) && pages.TryGetInt32(out int count) ? count : page;
        int totalCount = data.TryGetProperty("total_count", out var all) && all.TryGetInt32(out int items) ? items : 0;
        var listings = new List<WorkshopListing>();
        foreach (var item in data.GetProperty("results").EnumerateArray())
        {
            string id = Text(item, "publishedfileid");
            if (!WorkshopService.TryReadId(id, out id)) continue;
            string[] tags = item.TryGetProperty("tags", out var tagList) && tagList.ValueKind == JsonValueKind.Array
                ? tagList.EnumerateArray().Select(t => Text(t, "tag")).Where(t => t.Length > 0).ToArray()
                : [];
            long size = long.TryParse(Text(item, "file_size"), NumberStyles.None, CultureInfo.InvariantCulture, out long bytes) ? bytes : 0;
            listings.Add(new WorkshopListing(id, Clean(Text(item, "title")), Text(item, "preview_url"),
                Clean(Text(item, "short_description")), size, Number(item, "subscriptions"), Number(item, "star_rating"), tags));
        }
        return new WorkshopPage(listings.ToArray(), page, Math.Max(page, total), totalCount);
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? "",
                JsonValueKind.Number => value.GetRawText(),
                _ => ""
            }
            : "";

    private static int Number(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number)) return number;
        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number) ? number : 0;
    }

    private static string Clean(string value)
    {
        value = value.Replace("[b]", "").Replace("[/b]", "").Replace("[i]", "").Replace("[/i]", "")
            .Replace("\r", " ").Replace("\n", " ").Trim();
        return value.Length > 220 ? value[..220].TrimEnd() + "…" : value;
    }
}
