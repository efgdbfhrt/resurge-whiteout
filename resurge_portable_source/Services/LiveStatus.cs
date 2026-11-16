using System.IO;

namespace ResurgeLauncherCSharp.Services;

/// <summary>Turns the status file written by the Whiteout runtime into text for the friends list.</summary>
internal static class LiveStatus
{
    private static readonly Dictionary<string, string> MapNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zm_zod"] = "Shadows of Evil", ["zm_factory"] = "The Giant", ["zm_castle"] = "Der Eisendrache",
        ["zm_island"] = "Zetsubou No Shima", ["zm_stalingrad"] = "Gorod Krovi", ["zm_genesis"] = "Revelations",
        ["zm_prototype"] = "Nacht der Untoten", ["zm_asylum"] = "Verrückt", ["zm_sumpf"] = "Shi No Numa",
        ["zm_theater"] = "Kino der Toten", ["zm_cosmodrome"] = "Ascension", ["zm_temple"] = "Shangri-La",
        ["zm_moon"] = "Moon", ["zm_tomb"] = "Origins",
        ["mp_apartments"] = "Evac", ["mp_biodome"] = "Aquarium", ["mp_chinatown"] = "Exodus", ["mp_ethiopia"] = "Hunted",
        ["mp_havoc"] = "Havoc", ["mp_infection"] = "Infection", ["mp_metro"] = "Metro", ["mp_redwood"] = "Redwood",
        ["mp_sector"] = "Combine", ["mp_spire"] = "Breach", ["mp_stronghold"] = "Stronghold", ["mp_veiled"] = "Fringe",
        ["mp_nuketown_x"] = "Nuk3town", ["mp_crucible"] = "Gauntlet", ["mp_rise"] = "Rise", ["mp_skyjacked"] = "Skyjacked",
        ["mp_waterpark"] = "Splash", ["mp_aerospace"] = "Spire", ["mp_conduit"] = "Rift", ["mp_kung_fu"] = "Knockout",
        ["mp_banzai"] = "Verge", ["mp_cryogen"] = "Cryogen", ["mp_rome"] = "Empire", ["mp_shrine"] = "Berserk",
        ["mp_arena"] = "Rumble", ["mp_city"] = "Rupture", ["mp_miniature"] = "Micro", ["mp_western"] = "Outlaw",
        ["mp_ruins"] = "Ruins", ["mp_freerun_01"] = "Freerun"
    };

    public static string MapName(string map) =>
        MapNames.TryGetValue(map, out var name) ? name
        : map.StartsWith("zm_", StringComparison.OrdinalIgnoreCase) || map.StartsWith("mp_", StringComparison.OrdinalIgnoreCase)
            ? System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(map[3..].Replace('_', ' '))
            : map;

    /// <summary>"Zombies · The Giant · Round 12", "Multiplayer · Combine", "In the menus" or "In game".</summary>
    public static string Describe()
    {
        try
        {
            if (!File.Exists(GameLauncher.StatusPath)) return "In game";
            var values = File.ReadAllLines(GameLauncher.StatusPath)
                .Select(line => line.Split('=', 2)).Where(parts => parts.Length == 2)
                .ToDictionary(parts => parts[0], parts => parts[1]);
            string map = values.GetValueOrDefault("map", "");
            string mode = values.GetValueOrDefault("mode", "");
            int.TryParse(values.GetValueOrDefault("round", "0"), out int round);
            if (map.Length == 0 || map == "core_frontend") return "In the menus";
            string modeName = mode switch { "zm" => "Zombies", "mp" => "Multiplayer", "cp" => "Campaign", _ => "Playing" };
            return round > 0 ? $"{modeName} · {MapName(map)} · Round {round}" : $"{modeName} · {MapName(map)}";
        }
        catch (IOException) { return "In game"; }
        catch (UnauthorizedAccessException) { return "In game"; }
    }
}
