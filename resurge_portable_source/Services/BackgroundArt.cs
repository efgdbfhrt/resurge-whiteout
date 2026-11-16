using System.IO;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ResurgeLauncherCSharp.Services;

/// <summary>
/// Launcher background: the Black Ops III key art from Steam (cached on first run), the bundled
/// Whiteout ridge, or a picture the player chooses.
/// </summary>
internal static class BackgroundArt
{
    public const string BlackOps = "bo3";
    public const string Whiteout = "whiteout";
    private const string HeroUrl = "https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/311210/library_hero.jpg";
    private static string SettingPath => Path.Combine(GameLauncher.DataPath, "background.txt");
    private static string HeroCache => Path.Combine(GameLauncher.DataPath, "art", "bo3-hero.jpg");
    private static string CustomCopy => Path.Combine(GameLauncher.DataPath, "art", "custom-background");

    public static string Mode
    {
        get
        {
            try { return File.Exists(SettingPath) ? File.ReadAllText(SettingPath).Trim() : BlackOps; }
            catch (IOException) { return BlackOps; }
        }
        set
        {
            Directory.CreateDirectory(GameLauncher.DataPath);
            File.WriteAllText(SettingPath, value);
        }
    }

    public static void SetCustom(string source)
    {
        var info = new FileInfo(source);
        if (!info.Exists || info.Length is 0 or > 25_000_000) throw new ArgumentException("Choose an image smaller than 25 MB.");
        // Decode once to make sure the file really is an image before it becomes the background.
        _ = Decode(source, 64) ?? throw new ArgumentException("That file is not a supported image.");
        Directory.CreateDirectory(Path.GetDirectoryName(CustomCopy)!);
        File.Copy(source, CustomCopy, overwrite: true);
        Mode = "custom";
    }

    /// <summary>Returns the image for the current mode, downloading the Black Ops III art if needed.</summary>
    public static async Task<ImageSource?> LoadAsync()
    {
        string mode = Mode;
        if (mode == "custom" && File.Exists(CustomCopy)) return Decode(CustomCopy, 2560);
        if (mode == Whiteout) return null;
        if (!File.Exists(HeroCache))
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("ResurgeWhiteout/2.7");
                byte[] data = await http.GetByteArrayAsync(HeroUrl);
                if (data.Length > 1000 && data[0] == 0xFF && data[1] == 0xD8)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(HeroCache)!);
                    await File.WriteAllBytesAsync(HeroCache, data);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException) { }
        }
        return File.Exists(HeroCache) ? Decode(HeroCache, 2560) : null;
    }

    private static BitmapImage? Decode(string path, int width)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = width;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }
}
