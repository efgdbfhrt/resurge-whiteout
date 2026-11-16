using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace ResurgeLauncherCSharp.Services;

internal sealed record ControllerAction(string Key, string Label, string Command, string DefaultButton);

/// <summary>
/// Gamepad layout for Black Ops III. The game keeps binds in players\bindings_N.cfg inside the
/// launcher's local save folder; only the gamepad lines are rewritten, keyboard binds stay as they are.
/// </summary>
internal static class ControllerBindings
{
    public static readonly ControllerAction[] Actions =
    [
        new("jump", "Jump", "+gostand", "BUTTON_A"),
        new("stance", "Crouch / Prone", "+stance", "BUTTON_B"),
        new("usereload", "Use / Reload", "+usereload", "BUTTON_X"),
        new("switch", "Switch weapon", "+weapnext_inventory", "BUTTON_Y"),
        new("aim", "Aim", "+speed_throw", "BUTTON_LTRIG"),
        new("fire", "Fire", "+attack", "BUTTON_RTRIG"),
        new("tactical", "Tactical", "+smoke", "BUTTON_LSHLDR"),
        new("lethal", "Lethal", "+frag", "BUTTON_RSHLDR"),
        new("sprint", "Sprint / Hold breath", "+breath_sprint", "BUTTON_LSTICK"),
        new("melee", "Melee", "+melee", "BUTTON_RSTICK"),
        new("slot1", "Action slot 1", "+actionslot 1", "DPAD_UP"),
        new("slot2", "Action slot 2", "+actionslot 2", "DPAD_DOWN"),
        new("slot3", "Action slot 3", "+actionslot 3", "DPAD_LEFT"),
        new("slot4", "Action slot 4", "+actionslot 4", "DPAD_RIGHT"),
        new("scores", "Scoreboard", "togglescores", "BUTTON_BACK"),
        new("menu", "Pause menu", "togglemenu", "BUTTON_START")
    ];

    public static readonly (string Button, string Label)[] Buttons =
    [
        ("BUTTON_A", "A"), ("BUTTON_B", "B"), ("BUTTON_X", "X"), ("BUTTON_Y", "Y"),
        ("BUTTON_LSHLDR", "LB"), ("BUTTON_RSHLDR", "RB"), ("BUTTON_LTRIG", "LT"), ("BUTTON_RTRIG", "RT"),
        ("BUTTON_LSTICK", "LS"), ("BUTTON_RSTICK", "RS"),
        ("DPAD_UP", "D-pad ↑"), ("DPAD_DOWN", "D-pad ↓"), ("DPAD_LEFT", "D-pad ←"), ("DPAD_RIGHT", "D-pad →"),
        ("BUTTON_BACK", "View"), ("BUTTON_START", "Menu")
    ];

    private static readonly Dictionary<string, string> PlayStationLabels = new()
    {
        ["BUTTON_A"] = "✕", ["BUTTON_B"] = "○", ["BUTTON_X"] = "□", ["BUTTON_Y"] = "△",
        ["BUTTON_LSHLDR"] = "L1", ["BUTTON_RSHLDR"] = "R1", ["BUTTON_LTRIG"] = "L2", ["BUTTON_RTRIG"] = "R2",
        ["BUTTON_LSTICK"] = "L3", ["BUTTON_RSTICK"] = "R3", ["BUTTON_BACK"] = "Create", ["BUTTON_START"] = "Options"
    };

    /// <summary>Shows PlayStation names while a DualSense is connected, Xbox names otherwise.</summary>
    public static bool PlayStationNames { get; set; }

    public static string Label(string button) =>
        PlayStationNames && PlayStationLabels.TryGetValue(button, out var ps) ? ps
        : Buttons.FirstOrDefault(entry => entry.Button == button).Label ?? button;

    public sealed class Layout
    {
        public Dictionary<string, string> Binds { get; set; } = Actions.ToDictionary(action => action.Key, action => action.DefaultButton);
        public bool Southpaw { get; set; }
        public Layout Clone() => new() { Binds = new Dictionary<string, string>(Binds), Southpaw = Southpaw };
    }

    public static readonly (string Name, Action<Layout> Apply)[] Presets =
    [
        ("Default", layout => { }),
        ("Tactical", layout => { Set(layout, "stance", "BUTTON_RSTICK"); Set(layout, "melee", "BUTTON_B"); }),
        ("Bumper Jumper", layout => { Set(layout, "jump", "BUTTON_LSHLDR"); Set(layout, "tactical", "BUTTON_A"); }),
        ("Flipped", layout =>
        {
            Set(layout, "aim", "BUTTON_LSHLDR"); Set(layout, "fire", "BUTTON_RSHLDR");
            Set(layout, "tactical", "BUTTON_LTRIG"); Set(layout, "lethal", "BUTTON_RTRIG");
        })
    ];

    /// <summary>Assigns a button; the action that had it takes the old button, so nothing is lost.</summary>
    public static void Set(Layout layout, string action, string button)
    {
        string previous = layout.Binds[action];
        foreach (var key in layout.Binds.Keys.ToList())
            if (key != action && layout.Binds[key] == button) layout.Binds[key] = previous;
        layout.Binds[action] = button;
    }

    private static string SettingPath => Path.Combine(GameLauncher.DataPath, "controller.json");

    public static Layout Load()
    {
        try
        {
            if (File.Exists(SettingPath) && JsonSerializer.Deserialize<Layout>(File.ReadAllText(SettingPath)) is { } saved)
            {
                foreach (var action in Actions)
                    saved.Binds.TryAdd(action.Key, action.DefaultButton);
                return saved;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        return new Layout();
    }

    /// <summary>Saves the layout and writes it into the game's binding files when they exist.</summary>
    public static int Save(Layout layout, string? gamePath)
    {
        Directory.CreateDirectory(GameLauncher.DataPath);
        File.WriteAllText(SettingPath, JsonSerializer.Serialize(layout));
        return gamePath is null ? 0 : ApplyTo(gamePath, layout);
    }

    /// <summary>Called before each launch so a layout saved before the first game start still applies.</summary>
    public static void ApplyPending(string gamePath)
    {
        if (File.Exists(SettingPath)) ApplyTo(gamePath, Load());
    }

    private static int ApplyTo(string gamePath, Layout layout)
    {
        int written = 0;
        foreach (string saves in new[] { GameLauncher.LocalSavesFolder(gamePath), Path.Combine(gamePath, ".whiteout-practice-saves") })
        {
            foreach (string file in new[] { "bindings_0.cfg", "bindings_1.cfg" })
            {
                string path = Path.Combine(saves, "players", file);
                if (!File.Exists(path)) continue;
                try
                {
                    File.WriteAllText(path, Rewrite(File.ReadAllLines(path), layout), new UTF8Encoding(false));
                    written++;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        return written;
    }

    private static bool IsPadKey(string key) => key.StartsWith("BUTTON_", StringComparison.Ordinal) || key.StartsWith("DPAD_", StringComparison.Ordinal);

    private static string Rewrite(string[] lines, Layout layout)
    {
        var output = new List<string>();
        bool padWritten = false;
        bool axisWritten = false;
        foreach (string line in lines)
        {
            string[] parts = line.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            bool padBind = parts.Length >= 2 && parts[0] is "bind" or "bind2" && IsPadKey(parts[1]);
            bool axisBind = parts.Length >= 1 && parts[0] == "bindaxis";
            if (padBind)
            {
                if (!padWritten) { output.AddRange(PadLines(layout)); padWritten = true; }
                continue;
            }
            if (axisBind)
            {
                if (!axisWritten) { output.AddRange(AxisLines(layout)); axisWritten = true; }
                continue;
            }
            output.Add(line);
        }
        if (!padWritten) output.AddRange(PadLines(layout));
        if (!axisWritten) output.AddRange(AxisLines(layout));
        return string.Join("\r\n", output) + "\r\n";
    }

    private static IEnumerable<string> PadLines(Layout layout)
    {
        foreach (var action in Actions)
        {
            string button = layout.Binds[action.Key];
            yield return $"bind {button} \"{action.Command}\"";
            // Vehicles use the switch-weapon button to change seats.
            if (action.Key == "switch") yield return $"bind2 {button} \"+switchseat\"";
        }
    }

    private static IEnumerable<string> AxisLines(Layout layout)
    {
        string move = layout.Southpaw ? "A_RSTICK" : "A_LSTICK";
        string look = layout.Southpaw ? "A_LSTICK" : "A_RSTICK";
        yield return $"bindaxis {move}_X VA_SIDE MAP_SQUARED";
        yield return $"bindaxis {move}_Y VA_FORWARD MAP_SQUARED";
        yield return $"bindaxis {look}_X VA_YAW MAP_LINEAR";
        yield return $"bindaxis {look}_Y VA_PITCH MAP_LINEAR";
    }
}

/// <summary>Reads Xbox-compatible controllers so a button can be assigned by pressing it.</summary>
internal static class XInputReader
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Gamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX, ThumbLY, ThumbRX, ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct State
    {
        public uint PacketNumber;
        public Gamepad Gamepad;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint GetState(uint index, out State state);

    private static readonly (ushort Mask, string Button)[] Masks =
    [
        (0x0001, "DPAD_UP"), (0x0002, "DPAD_DOWN"), (0x0004, "DPAD_LEFT"), (0x0008, "DPAD_RIGHT"),
        (0x0010, "BUTTON_START"), (0x0020, "BUTTON_BACK"), (0x0040, "BUTTON_LSTICK"), (0x0080, "BUTTON_RSTICK"),
        (0x0100, "BUTTON_LSHLDR"), (0x0200, "BUTTON_RSHLDR"),
        (0x1000, "BUTTON_A"), (0x2000, "BUTTON_B"), (0x4000, "BUTTON_X"), (0x8000, "BUTTON_Y")
    ];

    /// <summary>Every button currently held on any connected controller (Xbox-style pads and PS5 DualSense).</summary>
    public static HashSet<string> Pressed()
    {
        var pressed = DualSenseReader.Pressed();
        for (uint index = 0; index < 4; index++)
        {
            try
            {
                if (GetState(index, out var state) != 0) continue;
                foreach (var (mask, button) in Masks)
                    if ((state.Gamepad.Buttons & mask) != 0) pressed.Add(button);
                if (state.Gamepad.LeftTrigger > 120) pressed.Add("BUTTON_LTRIG");
                if (state.Gamepad.RightTrigger > 120) pressed.Add("BUTTON_RTRIG");
            }
            catch (DllNotFoundException) { return pressed; }
            catch (EntryPointNotFoundException) { return pressed; }
        }
        return pressed;
    }

    public static bool AnyConnected()
    {
        if (DualSenseReader.Connected) return true;
        for (uint index = 0; index < 4; index++)
        {
            try { if (GetState(index, out _) == 0) return true; }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
        }
        return false;
    }
}
