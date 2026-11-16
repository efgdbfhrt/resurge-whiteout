using System.Diagnostics;
using System.IO;

namespace ResurgeLauncherCSharp.Services;

internal sealed record GameExit(int ExitCode, bool NewCrashLog, int CleanupFailures, TimeSpan Duration, int? BestRound);

internal static class GameLauncher
{
    internal static readonly string DataPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ResurgeWhiteout");
    internal static string LogPath => Path.Combine(DataPath, "sessions.log");
    internal static string StatusPath => Path.Combine(DataPath, "live-status.txt");

    public static FileStream LockFolder(string gamePath) => new(Path.Combine(gamePath, ".whiteout.lock"),
        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);

    public static void RequireStopped(string gamePath)
    {
        foreach (string name in new[] { "Resurge - Beta", "boiii", "EZZ boiii", "BlackOps3" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    string? executable;
                    try { executable = process.MainModule?.FileName; }
                    catch (System.ComponentModel.Win32Exception) { continue; }
                    catch (InvalidOperationException) { continue; }
                    if (executable is not null && Path.GetDirectoryName(executable)!.Equals(gamePath, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Close the running game first.");
                }
            }
        }
    }

    /// <summary>
    /// A session that ended without the launcher (launcher closed, PC restarted) leaves its backups
    /// behind. Callers have checked the game is not running, so the backups are restored now.
    /// </summary>
    public static void RecoverInterruptedSession(string gamePath)
    {
        if (AssetManager.HasNativeBackup(gamePath))
        {
            AssetManager.RestoreNative(gamePath);
            WriteLog("RECOVER restored the original runtime left by an interrupted session");
        }
        if (ResurgeConfig.HasDiagnosticBackup(gamePath))
        {
            ResurgeConfig.RestoreDiagnosticBackup(gamePath);
            WriteLog("RECOVER restored settings left by an interrupted safe session");
        }
    }

    public static string LocalSavesFolder(string gamePath) => Path.Combine(gamePath, ".whiteout-local-saves");

    public static async Task<GameExit> LaunchAsync(string group, string gamePath, string exeName,
        string? legacyBundle, bool autoCleanup, bool safeMode, IProgress<string> status,
        bool vanilla = false, PublicEndpoint? server = null)
    {
        var staged = new List<string>();
        string crashPath = Path.Combine(gamePath, "crashes.log");
        var previousCrash = CrashStamp(crashPath);
        bool started = false;
        bool patched = false;
        var playClock = new Stopwatch();
        string? roundFile = null;
        int? bestRound = null;
        int exitCode = 0;
        int cleanupFailures = 0;
        using var folderLock = LockFolder(gamePath);
        RequireStopped(gamePath);
        RecoverInterruptedSession(gamePath);
        using var profile = safeMode ? ResurgeConfig.BeginDiagnosticSession(gamePath, vanilla) : null;

        try
        {
            status.Report("Preparing client files…");
            AssetManager.Stage(group, gamePath, exeName, legacyBundle, staged);
            string executable = Path.Combine(gamePath, exeName);
            if (!File.Exists(executable)) throw new FileNotFoundException($"Client executable missing: {executable}");
            if (group == "ms") patched = AssetManager.StageWhiteoutRuntime(gamePath, staged);
            if (vanilla && !patched)
                throw new InvalidOperationException("Official maps mode needs the Whiteout client runtime.");

            var info = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = gamePath,
                UseShellExecute = false
            };
            if (server is not null && group == "boiii")
            {
                info.ArgumentList.Add("+connect");
                info.ArgumentList.Add(server.ToString());
            }
            if (patched)
            {
                Directory.CreateDirectory(DataPath);
                roundFile = Path.Combine(DataPath, Guid.NewGuid().ToString("N") + ".rounds");
                info.Environment["WHITEOUT_ROUND_FILE"] = roundFile;
                info.Environment["WHITEOUT_OFFICIAL_MAPS"] = vanilla ? "1" : "0";
                info.Environment["WHITEOUT_STATS_SESSION"] = "0";
                info.Environment["WHITEOUT_STATUS_FILE"] = StatusPath;
                // The GDK build stores settings through XGameSave. Without a signed-in Xbox user that
                // call fails (0x80830004) and the game crashes, so every session saves locally.
                string localSaves = LocalSavesFolder(gamePath);
                Directory.CreateDirectory(localSaves);
                info.Environment["WHITEOUT_LOCAL_SAVES"] = localSaves;
                ControllerBindings.ApplyPending(gamePath);
                GraphicsSettings.ApplyPending(gamePath);
            }
            ClearUncleanShutdownMarker(gamePath, exeName);
            TryDelete(StatusPath);

            WriteLog($"START {exeName} | diagnostic={safeMode} | official_maps={vanilla} | whiteout_runtime={patched} | {gamePath}");
            using var process = Process.Start(info) ?? throw new InvalidOperationException("Windows did not start the game.");
            started = true;
            playClock.Start();
            status.Report("Game running");
            await process.WaitForExitAsync();
            playClock.Stop();
            exitCode = process.ExitCode;
            if (roundFile is not null)
            {
                try
                {
                    if (File.Exists(roundFile))
                        foreach (string line in File.ReadLines(roundFile))
                            if (int.TryParse(line, out int round) && round is > 0 and <= 10_000)
                                bestRound = Math.Max(bestRound ?? 0, round);
                }
                catch (IOException ex) { WriteLog($"WARN round data could not be read | {ex.Message}"); }
                catch (UnauthorizedAccessException ex) { WriteLog($"WARN round data could not be read | {ex.Message}"); }
            }
        }
        catch (Exception ex)
        {
            WriteLog($"ERROR {exeName} | {ex.GetType().Name}: {ex.Message}");
            throw;
        }
        finally
        {
            bool restored = true;
            if (patched)
            {
                try { AssetManager.RestoreNative(gamePath); }
                catch (IOException) { restored = false; cleanupFailures++; }
                catch (UnauthorizedAccessException) { restored = false; cleanupFailures++; }
            }
            if (restored && (autoCleanup || !started))
                cleanupFailures += AssetManager.Cleanup(staged);
            if (roundFile is not null && !TryDelete(roundFile)) cleanupFailures++;
            TryDelete(StatusPath);
            if (cleanupFailures > 0)
                WriteLog($"WARN {cleanupFailures} staged files could not be cleaned up");
        }

        bool crashed = CrashStamp(crashPath) != previousCrash && File.Exists(crashPath);
        WriteLog($"EXIT {exeName} | 0x{unchecked((uint)exitCode):X8} | crash log updated={crashed}");
        return new GameExit(exitCode, crashed, cleanupFailures, playClock.Elapsed, bestRound);
    }

    // BO3 writes "<client>.start" on boot and removes it on a clean exit. A leftover marker makes the
    // next boot ask for Safe Mode; answering No then reuses the state that crashed.
    private static void ClearUncleanShutdownMarker(string gamePath, string exeName)
    {
        string marker = Path.Combine(gamePath, Path.GetFileNameWithoutExtension(exeName) + ".start");
        if (File.Exists(marker) && !TryDelete(marker))
            WriteLog("WARN safe-mode marker could not be cleared");
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static (long Length, DateTime LastWrite) CrashStamp(string path)
    {
        var file = new FileInfo(path);
        return file.Exists ? (file.Length, file.LastWriteTimeUtc) : (0, DateTime.MinValue);
    }

    internal static void WriteLog(string entry)
    {
        try
        {
            Directory.CreateDirectory(DataPath);
            var log = new FileInfo(LogPath);
            if (log.Exists && log.Length > 2_000_000) File.Move(LogPath, LogPath + ".old", overwrite: true);
            File.AppendAllText(LogPath, $"{DateTimeOffset.Now:O}  {entry}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
