using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace ResurgeLauncherCSharp.Services;

internal sealed record WorkshopItem(string Title, string Folder, string Target, string? PublisherId);

internal static class WorkshopService
{
    public const string PrivatePlayId = "2818689863";
    private const string AppId = "311210";
    private static readonly string SteamCmdHome = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ResurgeLauncher", "steamcmd");
    public static string CacheRoot => Path.Combine(SteamCmdHome, "steamapps", "workshop", "content", AppId);

    public static bool TryReadId(string input, out string id)
    {
        id = input.Trim();
        if (Uri.TryCreate(id, UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps &&
            uri.Host.Equals("steamcommunity.com", StringComparison.OrdinalIgnoreCase) &&
            uri.AbsolutePath.TrimEnd('/') is "/sharedfiles/filedetails" or "/workshop/filedetails")
        {
            id = uri.Query.TrimStart('?').Split('&')
                .FirstOrDefault(value => value.StartsWith("id=", StringComparison.Ordinal))?[3..] ?? "";
        }

        if (!ulong.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out ulong number) || number == 0)
            return false;
        id = number.ToString(CultureInfo.InvariantCulture);
        return true;
    }

    public static HashSet<string> InstalledIds(string gamePath)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (string root in new[] { "usermaps", "mods" })
        {
            string folder = Path.Combine(gamePath, root);
            if (!Directory.Exists(folder)) continue;
            foreach (string item in Directory.EnumerateDirectories(folder))
            {
                try { if (ReadItem(item).PublisherId is { Length: > 0 } id) ids.Add(id); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException) { }
            }
        }
        return ids;
    }

    // SteamCMD prints nothing while a Workshop item downloads, so progress comes from the staging folder size.
    private static async Task WatchDownloadAsync(string id, long expectedBytes, IProgress<double> percent, CancellationToken stop)
    {
        string[] folders =
        [
            Path.Combine(SteamCmdHome, "steamapps", "workshop", "downloads", AppId, id),
            Path.Combine(SteamCmdHome, "steamapps", "workshop", "temp", AppId, id),
            Path.Combine(CacheRoot, id)
        ];
        try
        {
            while (!stop.IsCancellationRequested)
            {
                long bytes = 0;
                foreach (string folder in folders)
                {
                    try
                    {
                        if (Directory.Exists(folder))
                            bytes = Math.Max(bytes, new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length));
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                percent.Report(Math.Clamp(bytes * 100.0 / expectedBytes, 0, 99));
                await Task.Delay(1000, stop);
            }
        }
        catch (OperationCanceledException) { }
    }

    public static async Task<WorkshopItem> DownloadAsync(string gamePath, string id, bool refresh,
        IProgress<string> status, CancellationToken cancellation, long expectedBytes = 0, IProgress<double>? percent = null)
    {
        if (!TryReadId(id, out var normalizedId) || normalizedId != id)
            throw new ArgumentException("A numeric Workshop item ID is required.", nameof(id));

        Directory.CreateDirectory(SteamCmdHome);
        using var steamLock = GameLauncher.LockFolder(SteamCmdHome);
        using var gameLock = GameLauncher.LockFolder(gamePath);
        string cached = Path.Combine(CacheRoot, id);

        if (refresh || !File.Exists(MetadataPath(cached)))
        {
            string steamCmd = Path.Combine(SteamCmdHome, "steamcmd.exe");
            if (!File.Exists(steamCmd))
                await BootstrapSteamCmdAsync(steamCmd, status, cancellation);

            status.Report($"Connecting to Steam Workshop for {id}…");
            var info = new ProcessStartInfo
            {
                FileName = steamCmd,
                WorkingDirectory = SteamCmdHome,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var arg in new[] { "+@ShutdownOnFailedCommand", "1", "+@NoPromptForPassword", "1",
                "+login", "anonymous", "+workshop_download_item", AppId, id, "+quit" })
                info.ArgumentList.Add(arg);

            using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start SteamCMD.");
            async Task<(string LastLine, string Error, bool Success)> DrainAsync(StreamReader stream)
            {
                string lastLine = "";
                string error = "";
                bool success = false;
                while (await stream.ReadLineAsync() is { } line)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (line.Contains($"Success. Downloaded item {id} ", StringComparison.OrdinalIgnoreCase))
                        success = true;
                    lastLine = line.Trim();
                    if (lastLine.Contains("ERROR!", StringComparison.OrdinalIgnoreCase)) error = lastLine;
                    status.Report(lastLine);
                }
                return (lastLine, error, success);
            }

            var stdout = DrainAsync(process.StandardOutput);
            var stderr = DrainAsync(process.StandardError);
            using var watchStop = new CancellationTokenSource();
            var watcher = expectedBytes > 0 && percent is not null
                ? WatchDownloadAsync(id, expectedBytes, percent, watchStop.Token) : Task.CompletedTask;
            try
            {
                await process.WaitForExitAsync(cancellation);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                await process.WaitForExitAsync();
                throw;
            }
            finally
            {
                watchStop.Cancel();
                await Task.WhenAll(stdout, stderr, watcher);
            }

            var output = await stdout;
            var errors = await stderr;
            if (process.ExitCode != 0 || !(output.Success || errors.Success) || !File.Exists(MetadataPath(cached)))
            {
                string detail = new[] { errors.Error, output.Error, errors.LastLine, output.LastLine }
                    .FirstOrDefault(line => line.Length > 0) ?? "";
                throw new IOException($"SteamCMD did not complete item {id} (exit {process.ExitCode}). {detail}");
            }
        }
        else
        {
            status.Report("Using the downloaded Workshop item from cache…");
        }

        return Install(gamePath, cached, id, status, cancellation);
    }

    private static async Task BootstrapSteamCmdAsync(string path, IProgress<string> status, CancellationToken cancellation)
    {
        status.Report("Downloading SteamCMD from Valve…");
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        using var response = await client.GetAsync(
            "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip", cancellation);
        response.EnsureSuccessStatusCode();
        using var archive = new ZipArchive(await response.Content.ReadAsStreamAsync(cancellation));
        var entry = archive.GetEntry("steamcmd.exe") ?? throw new InvalidDataException("The SteamCMD archive is missing its executable.");
        string temporary = path + ".download";
        try
        {
            using (var input = entry.Open())
            using (var output = File.Create(temporary))
                await input.CopyToAsync(output, cancellation);
            File.Move(temporary, path);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public static string MetadataPath(string itemPath)
    {
        string rootFile = Path.Combine(itemPath, "workshop.json");
        return File.Exists(rootFile) ? rootFile : Path.Combine(itemPath, "zone", "workshop.json");
    }

    public static WorkshopItem ReadItem(string itemPath)
    {
        RejectLink(itemPath);
        string metadata = MetadataPath(itemPath);
        RejectLink(Path.GetDirectoryName(metadata)!);
        RejectLink(metadata);
        if (new FileInfo(metadata).Length > 1024 * 1024)
            throw new InvalidDataException("Workshop metadata exceeds 1 MB.");
        using var json = JsonDocument.Parse(File.ReadAllBytes(metadata));
        var data = json.RootElement;
        if (data.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Workshop metadata must be an object.");
        string Required(string key) => data.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String
            ? field.GetString()!.Trim() : throw new InvalidDataException($"Workshop metadata is missing {key}.");
        string folder = Required("FolderName");
        if (!IsValidFolderName(folder))
            throw new InvalidDataException($"Workshop metadata contains an invalid folder name: {folder}");

        string target = Required("Type").ToLowerInvariant() switch
        {
            "map" => "usermaps",
            "mod" => "mods",
            _ => throw new InvalidDataException("Only BO3 maps and mods can be installed.")
        };
        string? id = data.TryGetProperty("PublisherID", out var publisher)
            ? publisher.ValueKind == JsonValueKind.String ? publisher.GetString() : publisher.GetRawText()
            : null;
        return new WorkshopItem(Required("Title"), folder, target, id);
    }

    public static WorkshopItem Install(string gamePath, string source, string id,
        IProgress<string> status, CancellationToken cancellation)
    {
        var item = ReadItem(source);
        if (!string.IsNullOrWhiteSpace(item.PublisherId) && item.PublisherId != "0" && item.PublisherId != id)
            throw new InvalidDataException($"Workshop metadata belongs to a different item ({item.PublisherId}).");

        string existingZone = Path.Combine(source, "zone");
        string content = Directory.Exists(existingZone) ? existingZone : source;
        RejectLink(content);
        if (!Directory.EnumerateFiles(content, "*.ff", SearchOption.TopDirectoryOnly).Any(file => new FileInfo(file).Length > 0))
            throw new InvalidDataException("No BO3 fastfiles (.ff) were found. The item may be incomplete.");
        if (item.Target == "usermaps")
        {
            var map = new FileInfo(Path.Combine(content, item.Folder + ".ff"));
            if (!map.Exists || map.Length == 0)
                throw new InvalidDataException($"Map {item.Folder} is missing its main fastfile. Refresh the Workshop download.");
            RejectLink(map.FullName);
        }

        string target = Path.Combine(gamePath, item.Target);
        Directory.CreateDirectory(target);
        RejectLink(target);
        string destination = Path.Combine(target, item.Folder);
        if (Directory.Exists(destination))
        {
            RejectLink(destination);
            if (ReadItem(destination).PublisherId != id)
                throw new IOException($"'{item.Folder}' already exists and belongs to another item.");
        }

        string temporary = Path.Combine(gamePath, $".whiteout-workshop-{Guid.NewGuid():N}");
        string payload = Path.Combine(temporary, "payload");
        string previous = Path.Combine(temporary, "previous");
        bool preserveBackup = false;
        try
        {
            status.Report($"Installing {item.Title}…");
            if (content == source)
                CopyTree(source, Path.Combine(payload, "zone"), cancellation);
            else
                CopyTree(source, payload, cancellation);

            // Keep metadata beside the fastfiles, where BO3 and the installed-content panel look for it.
            string metadata = Path.Combine(payload, "zone", "workshop.json");
            if (!File.Exists(metadata))
                File.Copy(MetadataPath(source), metadata);

            cancellation.ThrowIfCancellationRequested();
            if (Directory.Exists(destination))
                Directory.Move(destination, previous);
            try
            {
                Directory.Move(payload, destination);
            }
            catch
            {
                if (Directory.Exists(previous))
                {
                    preserveBackup = true;
                    Directory.Move(previous, destination);
                    preserveBackup = false;
                }
                throw;
            }
        }
        finally
        {
            if (!preserveBackup && Directory.Exists(temporary))
                Directory.Delete(temporary, recursive: true);
        }
        return item;
    }

    private static void CopyTree(string source, string destination, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        RejectLink(source);
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source))
        {
            cancellation.ThrowIfCancellationRequested();
            RejectLink(file);
            using var input = File.OpenRead(file);
            using var output = File.Create(Path.Combine(destination, Path.GetFileName(file)));
            input.CopyToAsync(output, cancellation).GetAwaiter().GetResult();
        }
        foreach (string directory in Directory.EnumerateDirectories(source))
            CopyTree(directory, Path.Combine(destination, Path.GetFileName(directory)), cancellation);
    }

    internal static bool IsValidFolderName(string folder)
    {
        if (folder.Length is < 1 or > 120 || !char.IsAsciiLetterOrDigit(folder[0]) ||
            folder.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or '.')) || folder.EndsWith('.'))
            return false;

        string stem = folder.Split('.')[0].ToUpperInvariant();
        return stem is not ("CON" or "PRN" or "AUX" or "NUL") &&
            !(stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsAsciiDigit(stem[3]));
    }

    internal static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Linked Workshop paths are unsupported: {path}");
    }
}
