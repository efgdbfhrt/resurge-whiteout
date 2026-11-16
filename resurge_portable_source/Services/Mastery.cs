using System.IO;

namespace ResurgeLauncherCSharp.Services;

/// <summary>
/// Mastery camos: four original animated camos earned with total zombie kills across all weapons.
/// The art lives in .wcam files (camos/make_camos.py) embedded in the launcher; the runtime draws each
/// earned tier over a host camo in the game's Extras tab.
/// </summary>
internal static class Mastery
{
    internal sealed record Tier(int Level, string Name, string Key, long Kills, string Host, string Blurb);

    public static readonly Tier[] Tiers =
    [
        new(1, "Molten Forge", "molten", 1_000, "Black Ops III", "Dark forged steel split by glowing lava."),
        new(2, "Glacier", "glacier", 10_000, "Cyborg", "Deep ice with shimmering, glowing fractures."),
        new(3, "Void Nebula", "nebula", 100_000, "Weaponized 115", "A living galaxy that swirls and twinkles."),
        new(4, "Prism", "prism", 1_000_000, "Nuk3Town", "Iridescent gem facets with rolling rainbow light.")
    ];

    public static string Folder => Path.Combine(GameLauncher.DataPath, "camos");

    public static int UnlockedTier(long kills) => Tiers.Count(tier => kills >= tier.Kills);

    public static Tier? NextTier(long kills) => Tiers.FirstOrDefault(tier => kills < tier.Kills);

    /// <summary>Copies the camo files out of the launcher (only when missing or different).</summary>
    public static void Install()
    {
        Directory.CreateDirectory(Folder);
        var assembly = typeof(Mastery).Assembly;
        foreach (var tier in Tiers)
        {
            using var source = assembly.GetManifestResourceStream("Whiteout.Camo." + tier.Key);
            if (source is null) continue;
            string target = Path.Combine(Folder, tier.Key + ".wcam");
            if (File.Exists(target) && new FileInfo(target).Length == source.Length && SameContent(target, source)) continue;
            source.Position = 0;
            string temporary = target + "." + Guid.NewGuid().ToString("N");
            try
            {
                using (var output = File.Create(temporary)) source.CopyTo(output);
                File.Move(temporary, target, overwrite: true);
            }
            finally { File.Delete(temporary); }
        }
    }

    private static bool SameContent(string path, Stream source)
    {
        using var existing = File.OpenRead(path);
        using var hash = System.Security.Cryptography.SHA256.Create();
        byte[] a = hash.ComputeHash(existing);
        source.Position = 0;
        byte[] b = hash.ComputeHash(source);
        return a.AsSpan().SequenceEqual(b);
    }

    /// <summary>Settings the runtime reads from resurge.ini.</summary>
    public static (string Section, string Key, string Value)[] Settings(long kills) =>
    [
        ("WhiteoutMastery", "enabled", "1"),
        ("WhiteoutMastery", "folder", Folder),
        ("WhiteoutMastery", "tier", UnlockedTier(kills).ToString(System.Globalization.CultureInfo.InvariantCulture))
    ];

    /// <summary>Career zombie kills reported by the runtime (status file), with the stats file they came from.</summary>
    public static (long Kills, string Source)? ReadStatus()
    {
        try
        {
            if (!File.Exists(GameLauncher.StatusPath)) return null;
            var values = File.ReadAllLines(GameLauncher.StatusPath)
                .Select(line => line.Split('=', 2)).Where(parts => parts.Length == 2)
                .ToDictionary(parts => parts[0], parts => parts[1]);
            if (!values.TryGetValue("zmkills", out var killsText) || !long.TryParse(killsText, out long kills) || kills < 0) return null;
            return (kills, "zm-" + values.GetValueOrDefault("zmstats", "?"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
