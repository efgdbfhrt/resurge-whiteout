using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ResurgeLauncherCSharp.Services;

/// <summary>
/// In-app updates. The newest release is a row in the friends server's public `launcher_releases` table
/// (version, download part URLs, SHA-256 of the whole exe, size, notes). The exe is downloaded in parts
/// (free Supabase storage caps a file at 50 MB), joined, checked against the hash and its own file version,
/// then swapped in: Windows lets a running exe be renamed, so the old one becomes "*.old" and is deleted on
/// the next start.
/// </summary>
internal static class Updater
{
    internal sealed record Release(Version Version, string Label, string[] Urls, string Sha256, long Size, string Notes);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public static Version Current { get; } = ParseVersion(typeof(Updater).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "0") ?? new Version(0, 0);

    private static Version? ParseVersion(string text)
    {
        text = text.Split('+', '-')[0].Trim();
        if (!text.Contains('.')) text += ".0";
        return Version.TryParse(text, out var version) ? version : null;
    }

    /// <summary>Removes the previous exe left behind by an update.</summary>
    public static void CleanUpOldVersion()
    {
        if (Environment.ProcessPath is not { } exe) return;
        try { if (File.Exists(exe + ".old")) File.Delete(exe + ".old"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The newest published release if it is newer than this launcher, otherwise null.</summary>
    public static async Task<Release?> CheckAsync(CancellationToken cancel = default)
    {
        string? json = null;
        // For testing: a local manifest file (same JSON array the server returns).
        string? local = Environment.GetEnvironmentVariable("WHITEOUT_UPDATE_MANIFEST");
        if (!string.IsNullOrEmpty(local) && File.Exists(local)) json = await File.ReadAllTextAsync(local, cancel);
        else if (FriendsService.Endpoint is { } endpoint)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"{endpoint.Url}/rest/v1/launcher_releases?select=version,urls,sha256,size,notes&order=published_at.desc&limit=1");
            request.Headers.Add("apikey", endpoint.Key);
            using var response = await Http.SendAsync(request, cancel);
            if (!response.IsSuccessStatusCode) return null; // Table not set up yet, or offline.
            json = await response.Content.ReadAsStringAsync(cancel);
        }
        if (json is null) return null;
        try
        {
            if (JsonNode.Parse(json) is not JsonArray { Count: > 0 } rows || rows[0] is not { } row) return null;
            string label = row["version"]?.GetValue<string>() ?? "";
            if (ParseVersion(label) is not { } version || version <= Current) return null;
            string[] urls = (row["urls"] as JsonArray)?.Select(node => node?.GetValue<string>() ?? "").ToArray() ?? [];
            string hash = (row["sha256"]?.GetValue<string>() ?? "").ToLowerInvariant();
            long size = row["size"]?.GetValue<long>() ?? 0;
            if (urls.Length == 0 || urls.Any(url => !url.StartsWith("https://", StringComparison.Ordinal) && !File.Exists(url)) ||
                hash.Length != 64 || size <= 0 || size > 400_000_000)
                return null;
            return new Release(version, label, urls, hash, size, row["notes"]?.GetValue<string>() ?? "");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { return null; }
    }

    /// <summary>Downloads and verifies the release; returns the path of the verified new exe.</summary>
    public static async Task<string> DownloadAsync(Release release, IProgress<double> progress, CancellationToken cancel = default)
    {
        string folder = Path.Combine(GameLauncher.DataPath, "update");
        Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, $"Resurge Whiteout {release.Label}.exe");
        string partial = target + ".download";
        long received = 0;
        await using (var output = File.Create(partial))
        {
            foreach (string url in release.Urls)
            {
                await using Stream input = File.Exists(url) ? File.OpenRead(url) // local test parts
                    : await Http.GetStreamAsync(url, cancel);
                var buffer = new byte[1 << 16];
                int read;
                while ((read = await input.ReadAsync(buffer, cancel)) > 0)
                {
                    received += read;
                    if (received > release.Size) throw new InvalidDataException("The update is larger than expected.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancel);
                    progress.Report((double)received / release.Size);
                }
            }
        }
        if (received != release.Size) throw new InvalidDataException("The update download is incomplete.");
        await using (var check = File.OpenRead(partial))
        {
            string hash = Convert.ToHexString(await SHA256.HashDataAsync(check, cancel)).ToLowerInvariant();
            if (hash != release.Sha256) throw new InvalidDataException("The update failed its integrity check, so it was not installed.");
        }
        var info = FileVersionInfo.GetVersionInfo(partial);
        if (ParseVersion(info.ProductVersion ?? info.FileVersion ?? "") is not { } fileVersion ||
            fileVersion.Major != release.Version.Major || fileVersion.Minor != release.Version.Minor)
            throw new InvalidDataException("The downloaded file is not the expected launcher version.");
        File.Move(partial, target, overwrite: true);
        return target;
    }

    /// <summary>Swaps the running exe for the new one and starts it. The caller then closes the app.</summary>
    public static void Install(string newExe)
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("The launcher's own path is unknown.");
        string old = exe + ".old";
        if (File.Exists(old)) File.Delete(old);
        File.Move(exe, old);
        try { File.Move(newExe, exe); }
        catch
        {
            File.Move(old, exe); // Put the working launcher back.
            throw;
        }
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) });
    }
}
