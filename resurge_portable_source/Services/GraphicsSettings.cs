using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ResurgeLauncherCSharp.Services;

internal sealed record GraphicsPreset(string Key, string Name, string Glyph, string Summary, IReadOnlyDictionary<string, string> Values);

/// <summary>
/// Black Ops III keeps its video options in players\config.ini (inside the launcher's save folders).
/// The launcher edits only the keys it manages and keeps the game's comments; edits made in the launcher
/// are stored as pending and written before the next launch, so options changed in game are kept otherwise.
/// </summary>
internal static partial class GraphicsSettings
{
    // Options every preset sets. Display options (FPS cap, resolution, FOV, window mode) are separate.
    public static readonly GraphicsPreset[] Presets =
    [
        new("fps", "Max FPS", "\uE945", "Smoothest play and lowest input lag. Effects kept simple.", new Dictionary<string, string>
        {
            ["MeshQuality"] = "2", ["TextureFilter"] = "1", ["TextureQuality"] = "2", ["TextureQualityFX"] = "4",
            ["TextureQualityProbes"] = "4", ["TextureQualityBakedSunShadows"] = "2",
            ["DisableDynamicLightShadows"] = "1", ["DisableDynamicSunShadows"] = "1",
            ["SpotShadowTextureSize"] = "512", ["OmniShadowTextureSize"] = "256", ["ShadowFiltering"] = "0", ["ActorShadows"] = "0",
            ["VolumetricLightingEnabled"] = "0", ["OIT"] = "0", ["SSAOTechnique"] = "Disabled", ["AATechnique"] = "FXAA",
            ["MotionBlur"] = "Off", ["SubsurfaceScattering"] = "0", ["CorpseCount"] = "8",
            ["SmoothFramerate"] = "0", ["MaxFrameLatency"] = "1", ["SerializeRender"] = "0"
        }),
        new("balanced", "Balanced", "\uE9D9", "Good looks with high frame rates on most PCs.", new Dictionary<string, string>
        {
            ["MeshQuality"] = "1", ["TextureFilter"] = "1", ["TextureQuality"] = "1", ["TextureQualityFX"] = "1",
            ["TextureQualityProbes"] = "1", ["TextureQualityBakedSunShadows"] = "1",
            ["DisableDynamicLightShadows"] = "0", ["DisableDynamicSunShadows"] = "0",
            ["SpotShadowTextureSize"] = "1024", ["OmniShadowTextureSize"] = "512", ["ShadowFiltering"] = "1", ["ActorShadows"] = "2",
            ["VolumetricLightingEnabled"] = "1", ["VolumetricLightingMaxSunSamples"] = "8", ["VolumetricLightingMaxLightSamples"] = "40",
            ["VolumetricLightingSkipSunSamples"] = "1", ["VolumetricLightingSkipLightSamples"] = "1",
            ["OIT"] = "0", ["SSAOTechnique"] = "GTAO Medium Quality", ["AATechnique"] = "Filmic SMAA 1x",
            ["MotionBlur"] = "Off", ["SubsurfaceScattering"] = "1", ["CorpseCount"] = "16",
            ["SmoothFramerate"] = "0", ["MaxFrameLatency"] = "1", ["SerializeRender"] = "0"
        }),
        new("quality", "Best Looks", "\uE7F4", "Highest quality for strong graphics cards.", new Dictionary<string, string>
        {
            ["MeshQuality"] = "0", ["TextureFilter"] = "2", ["TextureQuality"] = "0", ["TextureQualityFX"] = "0",
            ["TextureQualityProbes"] = "0", ["TextureQualityBakedSunShadows"] = "0",
            ["DisableDynamicLightShadows"] = "0", ["DisableDynamicSunShadows"] = "0",
            ["SpotShadowTextureSize"] = "2048", ["OmniShadowTextureSize"] = "1024", ["ShadowFiltering"] = "1", ["ActorShadows"] = "8",
            ["VolumetricLightingEnabled"] = "1", ["VolumetricLightingMaxSunSamples"] = "32", ["VolumetricLightingMaxLightSamples"] = "80",
            ["VolumetricLightingSkipSunSamples"] = "0", ["VolumetricLightingSkipLightSamples"] = "0",
            ["OIT"] = "1", ["OITLayers"] = "12", ["SSAOTechnique"] = "GTAO High Quality", ["AATechnique"] = "Filmic SMAA T2x",
            ["MotionBlur"] = "Off", ["SubsurfaceScattering"] = "1", ["CorpseCount"] = "32",
            ["SmoothFramerate"] = "0", ["MaxFrameLatency"] = "2", ["SerializeRender"] = "0", ["RestrictGraphicsOptions"] = "0"
        })
    ];

    private static string PendingPath => Path.Combine(GameLauncher.DataPath, "graphics.json");

    private static IEnumerable<string> ConfigFiles(string gamePath) =>
    [
        Path.Combine(GameLauncher.LocalSavesFolder(gamePath), "players", "config.ini"),
        Path.Combine(gamePath, ".whiteout-practice-saves", "players", "config.ini")
    ];

    [GeneratedRegex("^\\s*([A-Za-z0-9_]+)\\s*=\\s*\"([^\"]*)\"")]
    private static partial Regex Line();

    /// <summary>Current values: the game's config with any not-yet-applied launcher changes on top.</summary>
    public static Dictionary<string, string> Read(string? gamePath)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (gamePath is not null)
        {
            string file = ConfigFiles(gamePath).First();
            try
            {
                if (File.Exists(file))
                    foreach (string line in File.ReadAllLines(file))
                        if (Line().Match(line) is { Success: true } match)
                            values[match.Groups[1].Value] = match.Groups[2].Value;
            }
            catch (IOException) { }
        }
        foreach (var (key, value) in LoadPending()) values[key] = value;
        return values;
    }

    /// <summary>The preset whose options all match, or null for a custom mix.</summary>
    public static GraphicsPreset? Matching(IReadOnlyDictionary<string, string> values) =>
        Presets.FirstOrDefault(preset => preset.Values.All(pair =>
            values.TryGetValue(pair.Key, out var current) && string.Equals(current, pair.Value, StringComparison.OrdinalIgnoreCase)));

    private static Dictionary<string, string> LoadPending()
    {
        try
        {
            if (File.Exists(PendingPath) &&
                JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(PendingPath)) is { } pending)
                return pending;
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        return [];
    }

    /// <summary>Saves changes; writes them into the game's config now when the game is not running.</summary>
    public static bool Save(IReadOnlyDictionary<string, string> changes, string? gamePath, bool gameRunning)
    {
        var pending = LoadPending();
        foreach (var (key, value) in changes) pending[key] = value;
        Directory.CreateDirectory(GameLauncher.DataPath);
        File.WriteAllText(PendingPath, JsonSerializer.Serialize(pending));
        if (gamePath is null || gameRunning) return false;
        ApplyPending(gamePath);
        return true;
    }

    /// <summary>Called before each launch (and right after saving) to write launcher changes into config.ini.</summary>
    public static void ApplyPending(string gamePath)
    {
        var pending = LoadPending();
        if (pending.Count == 0) return;
        foreach (string file in ConfigFiles(gamePath))
        {
            try { Write(file, pending); }
            catch (IOException) { return; }
            catch (UnauthorizedAccessException) { return; }
        }
        File.Delete(PendingPath);
    }

    private static void Write(string file, IReadOnlyDictionary<string, string> values)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var lines = File.Exists(file) ? File.ReadAllLines(file).ToList() : [];
        var remaining = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < lines.Count; i++)
        {
            var match = Line().Match(lines[i]);
            if (!match.Success || !remaining.Remove(match.Groups[1].Value, out var value)) continue;
            // Keep the game's trailing range comment ( // 0 to 1000 ).
            int comment = lines[i].IndexOf("//", match.Length, StringComparison.Ordinal);
            string tail = comment >= 0 ? " " + lines[i][comment..] : "";
            lines[i] = $"{match.Groups[1].Value} = \"{value}\"{tail}";
        }
        foreach (var (key, value) in remaining) lines.Add($"{key} = \"{value}\"");
        File.WriteAllLines(file, lines, new UTF8Encoding(false));
    }

    /// <summary>Name of the main graphics card from the display driver list (no extra libraries).</summary>
    public static string? GraphicsCard()
    {
        try
        {
            using var classKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (classKey is null) return null;
            var names = classKey.GetSubKeyNames()
                .Select(name =>
                {
                    // Some subkeys (e.g. "Properties") are locked; skip them instead of giving up.
                    try { using var sub = classKey.OpenSubKey(name); return sub?.GetValue("DriverDesc") as string; }
                    catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
                })
                .OfType<string>()
                .Where(name => !name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) &&
                               !name.Contains("Virtual", StringComparison.OrdinalIgnoreCase) &&
                               !name.Contains("Parsec", StringComparison.OrdinalIgnoreCase))
                .Distinct().ToList();
            return names.FirstOrDefault(name => name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                                                name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ||
                                                name.Contains("Arc", StringComparison.OrdinalIgnoreCase))
                   ?? names.FirstOrDefault();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException) { return null; }
    }

    public static int Int(IReadOnlyDictionary<string, string> values, string key, int fallback) =>
        values.TryGetValue(key, out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? (int)Math.Round(value) : fallback;
}
