using System.IO;
using System.Runtime.InteropServices;

namespace ResurgeLauncherCSharp.Services;

internal static class ResurgeConfig
{
    private const string BackupSuffix = ".whiteout-backup";
    private static readonly string[] AutoexecNames = ["autoexec.cfg", "autoexec_mp.cfg", "autoexec_zm.cfg"];
    private static readonly string[] WorkshopFolders = ["usermaps", "mods"];

    [DllImport("kernel32", CharSet = CharSet.Unicode)]
    private static extern uint GetPrivateProfileInt(string section, string key, int defaultValue, string fileName);

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WritePrivateProfileString(string section, string key, string value, string fileName);

    public static int Read(string gamePath, string section, string key, int defaultValue) =>
        unchecked((int)GetPrivateProfileInt(section, key, defaultValue, IniPath(gamePath)));

    public static void Write(string gamePath, params (string Section, string Key, string Value)[] values)
    {
        string path = IniPath(gamePath);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (File.Exists(path))
                File.Copy(path, temporary);
            else
                File.WriteAllText(temporary, "");

            foreach (var (section, key, value) in values)
            {
                if (!WritePrivateProfileString(section, key, value, temporary))
                    throw new System.ComponentModel.Win32Exception();
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public static bool HasDiagnosticBackup(string gamePath) =>
        File.Exists(IniPath(gamePath) + BackupSuffix) || File.Exists(IniPath(gamePath) + BackupSuffix + ".new") ||
        AutoexecPaths(gamePath).Any(file => File.Exists(file + BackupSuffix)) ||
        WorkshopFolders.Any(folder => Directory.Exists(Path.Combine(gamePath, folder + BackupSuffix)));

    public static IDisposable BeginDiagnosticSession(string gamePath, bool vanilla = false, bool statsMode = false)
    {
        if (HasDiagnosticBackup(gamePath))
            throw new IOException("A previous diagnostic backup is still present. Use Restore files in Diagnostics.");

        string path = IniPath(gamePath);
        if (File.Exists(path))
            File.Copy(path, path + BackupSuffix);
        else
            using (File.Create(path + BackupSuffix + ".new")) { }

        try
        {
            if (vanilla)
            {
                foreach (string folder in WorkshopFolders)
                {
                    string pathToHide = Path.Combine(gamePath, folder);
                    if (Directory.Exists(pathToHide))
                        Directory.Move(pathToHide, pathToHide + BackupSuffix);
                }
            }
            // Avoid running third-party progression scripts during the diagnostic comparison.
            foreach (var autoexec in AutoexecPaths(gamePath))
            {
                if (File.Exists(autoexec))
                    File.Move(autoexec, autoexec + BackupSuffix);
            }

            var values = new List<(string Section, string Key, string Value)>
            {
                ("DLC", "enable_unlock_all", "0"),
                ("Hacks", "gobblegum_hack", "0"),
                ("Hacks", "cryptokeys_hack", "0"),
                ("Hacks", "liquid_divinium_hack", "0")
            };
            if (!statsMode) values.Add(("WhiteoutProgression", "enabled", "0"));
            if (Read(gamePath, "System", "force_disable_steam", 2) == 2)
                values.Add(("System", "force_disable_steam", "1"));
            if (vanilla)
            {
                values.Add(("System", "force_disable_steam", "1"));
                values.Add(("DLC", "spoof_dlc_mappacks", "0"));
            }

            Write(gamePath, values.ToArray());
            return new DiagnosticSession(gamePath);
        }
        catch
        {
            RestoreDiagnosticBackup(gamePath);
            throw;
        }
    }

    public static bool RestoreDiagnosticBackup(string gamePath)
    {
        bool restored = false;
        foreach (string folder in WorkshopFolders)
        {
            string destination = Path.Combine(gamePath, folder);
            string backup = destination + BackupSuffix;
            if (!Directory.Exists(backup)) continue;
            if (Directory.Exists(destination))
            {
                // Retain any directory recreated by the game separately from the user's library.
                string generated = destination + ".whiteout-session-" + Guid.NewGuid().ToString("N");
                Directory.Move(destination, generated);
            }
            Directory.Move(backup, destination);
            restored = true;
        }

        foreach (var autoexec in AutoexecPaths(gamePath))
        {
            if (!File.Exists(autoexec + BackupSuffix)) continue;
            File.Move(autoexec + BackupSuffix, autoexec, overwrite: true);
            restored = true;
        }

        string path = IniPath(gamePath);
        if (File.Exists(path + BackupSuffix))
        {
            File.Move(path + BackupSuffix, path, overwrite: true);
            restored = true;
        }
        if (File.Exists(path + BackupSuffix + ".new"))
        {
            File.Delete(path);
            File.Delete(path + BackupSuffix + ".new");
            restored = true;
        }
        return restored;
    }

    private static IEnumerable<string> AutoexecPaths(string gamePath) =>
        AutoexecNames.SelectMany(file => new[] { Path.Combine(gamePath, file), Path.Combine(gamePath, "players", file) });

    private static string IniPath(string gamePath) => Path.GetFullPath(Path.Combine(gamePath, "resurge.ini"));

    private sealed class DiagnosticSession(string gamePath) : IDisposable
    {
        public void Dispose() => RestoreDiagnosticBackup(gamePath);
    }
}
