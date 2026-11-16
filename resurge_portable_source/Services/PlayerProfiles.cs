using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ResurgeLauncherCSharp.Services;

/// <summary>
/// A local player profile. Salt/Verifier hold the optional password; OnlineToken is the secret that
/// identifies this profile on the friends server once it is linked.
/// </summary>
internal sealed record PlayerProfile(string Name, string Salt, string Verifier, long Seconds = 0, int? BestRound = null,
    int Sessions = 0, string? OnlineToken = null, long MasteryKills = 0, Dictionary<string, long>? KillBaselines = null);

internal static class PlayerProfiles
{
    private static string DirectoryPath => Path.Combine(GameLauncher.DataPath, "profiles");
    private static string LastProfilePath => Path.Combine(DirectoryPath, "last-profile.txt");
    private static string AutoSignInPath => Path.Combine(DirectoryPath, "auto-sign-in.txt");

    public static bool HasPassword(PlayerProfile profile) => profile.Verifier.Length > 0 && profile.Salt.Length > 0;

    public static void ValidateName(string name)
    {
        if (name.Length is < 3 or > 24 || name.Any(c => !char.IsLetterOrDigit(c) && c is not (' ' or '_' or '-')))
            throw new ArgumentException("Use 3–24 letters, numbers, spaces, hyphens or underscores.");
    }

    /// <summary>Every local profile on this PC, most recently played first.</summary>
    public static PlayerProfile[] List()
    {
        if (!Directory.Exists(DirectoryPath)) return [];
        var profiles = new List<(PlayerProfile Profile, DateTime Written)>();
        foreach (var file in new DirectoryInfo(DirectoryPath).EnumerateFiles("*.json"))
        {
            try { profiles.Add((Read(file.FullName), file.LastWriteTimeUtc)); }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException) { }
        }
        return profiles.OrderByDescending(entry => entry.Written).Select(entry => entry.Profile).ToArray();
    }

    /// <summary>Opens a profile. Profiles without a password open directly.</summary>
    public static PlayerProfile SignIn(string name, string password)
    {
        string path = ProfilePath(name.Trim());
        if (!File.Exists(path)) throw new ArgumentException("That profile no longer exists.");
        PlayerProfile existing = Read(path);
        if (HasPassword(existing) &&
            (password.Length is 0 or > 128 || !CryptographicOperations.FixedTimeEquals(
                Hash(password, Convert.FromBase64String(existing.Salt)), Convert.FromBase64String(existing.Verifier))))
            throw new ArgumentException("Wrong password.");
        return existing;
    }

    /// <summary>Creates a profile. The password is optional; an empty one leaves the profile open.</summary>
    public static PlayerProfile Create(string name, string password)
    {
        name = name.Trim();
        ValidateName(name);
        if (password.Length is > 0 and < 4 or > 128)
            throw new ArgumentException("Use a password with at least 4 characters, or leave it empty.");
        Directory.CreateDirectory(DirectoryPath);
        string path = ProfilePath(name);
        if (File.Exists(path)) throw new ArgumentException("A profile with that name already exists.");
        PlayerProfile profile;
        if (password.Length == 0)
            profile = new PlayerProfile(name, "", "");
        else
        {
            byte[] salt = RandomNumberGenerator.GetBytes(16);
            profile = new PlayerProfile(name, Convert.ToBase64String(salt), Convert.ToBase64String(Hash(password, salt)));
        }
        Write(path, profile, overwrite: false);
        return profile;
    }

    /// <summary>The profile to open without showing the sign-in screen, if "remember me" is on.</summary>
    public static PlayerProfile? RememberedProfile()
    {
        try
        {
            if (!File.Exists(LastProfilePath)) return null;
            if (File.Exists(AutoSignInPath) && File.ReadAllText(AutoSignInPath).Trim() == "0") return null;
            string remembered = Path.Combine(DirectoryPath, File.ReadAllText(LastProfilePath).Trim());
            return Path.GetDirectoryName(remembered) == DirectoryPath && File.Exists(remembered) ? Read(remembered) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void SetAutoSignIn(PlayerProfile profile, bool enabled)
    {
        Directory.CreateDirectory(DirectoryPath);
        try
        {
            File.WriteAllText(LastProfilePath, Path.GetFileName(ProfilePath(profile.Name)));
            File.WriteAllText(AutoSignInPath, enabled ? "1" : "0");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static void SignOut()
    {
        try { File.WriteAllText(AutoSignInPath, "0"); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static PlayerProfile RecordSession(PlayerProfile profile, TimeSpan duration, int? round)
    {
        string path = ProfilePath(profile.Name);
        using var profileLock = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.Write,
            FileShare.None, 1, FileOptions.DeleteOnClose);
        PlayerProfile latest = Read(path);
        int? best = round is > 0 ? Math.Max(latest.BestRound ?? 0, round.Value) : latest.BestRound;
        var updated = latest with
        {
            Seconds = latest.Seconds + (long)duration.TotalSeconds,
            BestRound = best,
            Sessions = latest.Sessions + (duration.TotalSeconds >= 30 ? 1 : 0)
        };
        Write(path, updated, overwrite: true);
        return updated;
    }

    /// <summary>
    /// Adds zombie kills to the mastery total. <paramref name="careerKills"/> is the game's own career count
    /// for one stats file (<paramref name="source"/>); the first reading only sets the baseline, so kills
    /// made before mastery existed (or by unlock tools) never count. Returns the profile and the kills added.
    /// </summary>
    public static (PlayerProfile Profile, long Added) RecordKills(PlayerProfile profile, string source, long careerKills)
    {
        string path = ProfilePath(profile.Name);
        using var profileLock = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.Write,
            FileShare.None, 1, FileOptions.DeleteOnClose);
        PlayerProfile latest = Read(path);
        var baselines = new Dictionary<string, long>(latest.KillBaselines ?? []);
        long added = 0;
        if (baselines.TryGetValue(source, out long baseline))
        {
            // A drop means the stats were reset; take the new value as the baseline. Huge jumps (stat
            // editors) are capped at what a long Zombies session could plausibly reach.
            long delta = careerKills - baseline;
            if (delta > 0) added = Math.Min(delta, 50_000);
        }
        if (baselines.TryGetValue(source, out long previous) && previous == careerKills) return (latest, 0);
        baselines[source] = careerKills;
        var updated = latest with { MasteryKills = latest.MasteryKills + added, KillBaselines = baselines };
        Write(path, updated, overwrite: true);
        return (updated, added);
    }

    public static PlayerProfile SetOnlineToken(PlayerProfile profile, string? token)
    {
        string path = ProfilePath(profile.Name);
        var updated = Read(path) with { OnlineToken = token };
        Write(path, updated, overwrite: true);
        return updated;
    }

    public static string AvatarPath(PlayerProfile profile) => Path.ChangeExtension(ProfilePath(profile.Name), ".png");

    public static ImageSource? LoadAvatar(PlayerProfile profile)
    {
        string path = AvatarPath(profile);
        if (!File.Exists(path)) return null;
        using var input = File.OpenRead(path);
        return DecodeAvatar(input);
    }

    public static void SaveAvatar(PlayerProfile profile, string source)
    {
        using var input = File.OpenRead(source);
        if (input.Length is 0 or > 12_000_000) throw new ArgumentException("Choose an image smaller than 12 MB.");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(DecodeAvatar(input)));
        string path = AvatarPath(profile);
        string temporary = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = File.Create(temporary)) encoder.Save(output);
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    private static BitmapImage DecodeAvatar(Stream stream)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 256;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static string ProfilePath(string name) => Path.Combine(DirectoryPath,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name.Normalize().ToUpperInvariant()))) + ".json");
    private static byte[] Hash(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, 600_000, HashAlgorithmName.SHA256, 32);

    private static PlayerProfile Read(string path) => JsonSerializer.Deserialize<PlayerProfile>(File.ReadAllText(path))
        ?? throw new InvalidDataException("The local profile file could not be read.");

    private static void Write(string path, PlayerProfile profile, bool overwrite)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(profile));
            File.Move(temporary, path, overwrite);
        }
        finally { File.Delete(temporary); }
    }
}
