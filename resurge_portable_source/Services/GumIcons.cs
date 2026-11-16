using System.IO;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ResurgeLauncherCSharp.Services;

/// <summary>
/// GobbleGum artwork is fetched from the Call of Duty wiki on first use and cached locally,
/// so the launcher package itself carries no game artwork.
/// </summary>
internal static class GumIcons
{
    private static readonly string CacheFolder = Path.Combine(GameLauncher.DataPath, "gum-icons");
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ResurgeWhiteout/2.7");
        return client;
    }

    public static string CachePath(string key) => Path.Combine(CacheFolder, key + ".png");

    public static ImageSource? Load(string key)
    {
        string path = CachePath(key);
        if (!File.Exists(path) || new FileInfo(path).Length == 0) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 160;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Downloads every missing icon. Returns how many are cached afterwards.</summary>
    public static async Task<int> EnsureAsync(IEnumerable<(string Key, string Url)> icons, CancellationToken cancellation)
    {
        Directory.CreateDirectory(CacheFolder);
        int ready = 0;
        using var gate = new SemaphoreSlim(6);
        var tasks = icons.Select(async icon =>
        {
            string path = CachePath(icon.Key);
            if (File.Exists(path) && new FileInfo(path).Length > 0)
            {
                Interlocked.Increment(ref ready);
                return;
            }
            // Fandom serves WebP by default; format=original returns the PNG that WPF decodes everywhere.
            string url = icon.Url.Contains('?') ? icon.Url : icon.Url + "?format=original";
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return;
            await gate.WaitAsync(cancellation);
            try
            {
                byte[] data = await Http.GetByteArrayAsync(uri, cancellation);
                if (data.Length < 8 || data[0] != 0x89 || data[1] != 0x50) return; // PNG only
                string temporary = path + "." + Guid.NewGuid().ToString("N");
                await File.WriteAllBytesAsync(temporary, data, cancellation);
                File.Move(temporary, path, overwrite: true);
                Interlocked.Increment(ref ready);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException) { }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);
        return ready;
    }
}
