using System.IO;

namespace ResurgeLauncherCSharp.Services;

internal static class InstallationDiagnostics
{
    public static string CreateReport(string path, string bundle)
    {
        bool hasFolder = Directory.Exists(path);
        var lines = new List<string>
        {
            $"Game folder       {(hasFolder ? path : "Select your Black Ops III folder in Settings")}",
            $"Game data         {(hasFolder && Directory.Exists(Path.Combine(path, "zone")) ? "Found" : "Missing zone")}",
            $"Resurge client    {(hasFolder && File.Exists(Path.Combine(path, "Resurge - Beta.exe")) ? "Installed" : "Uses original launcher bundle")}",
            $"Steam base EXE    {(hasFolder && File.Exists(Path.Combine(path, "BlackOps3.exe")) ? "Found" : "Required for EZZ BOIII")}",
            $"Client bundle     {(File.Exists(bundle) ? bundle : "Select the original Resurge.dll in Settings")}",
            $"Session log       {GameLauncher.LogPath}"
        };

        if (hasFolder)
        {
            string crash = Path.Combine(path, "crashes.log");
            if (File.Exists(crash))
            {
                lines.Add("");
                lines.Add("RECENT NATIVE CRASHES");
                lines.AddRange(Tail(crash, 4, line => line.Contains("Exception at (", StringComparison.Ordinal)));
            }
        }

        if (File.Exists(GameLauncher.LogPath))
        {
            lines.Add("");
            lines.Add("RECENT LAUNCHER SESSIONS");
            lines.AddRange(Tail(GameLauncher.LogPath, 8));
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static string[] Tail(string path, int count, Func<string, bool>? predicate = null)
    {
        try
        {
            using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(file);
            var buffer = new Queue<string>(count);
            while (reader.ReadLine() is { } line)
            {
                if (predicate is not null && !predicate(line)) continue;
                if (buffer.Count == count) buffer.Dequeue();
                buffer.Enqueue(line);
            }
            return buffer.ToArray();
        }
        catch (IOException ex) { return [ex.Message]; }
        catch (UnauthorizedAccessException ex) { return [ex.Message]; }
    }
}
