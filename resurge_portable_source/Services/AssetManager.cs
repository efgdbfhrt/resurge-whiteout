using System.Buffers.Binary;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace ResurgeLauncherCSharp.Services;

internal static class AssetManager
{
    private const string ResourcePrefix = "ResurgeLauncherCSharp.EmbeddedAssets.";
    private const string NativeDll = "T7InternalWS.dll";
    private const string NativeBackupSuffix = ".whiteout-original";

    public static bool HasNativeBackup(string path) => File.Exists(Path.Combine(path, NativeDll + NativeBackupSuffix));

    public static bool RestoreNative(string path)
    {
        string dll = Path.Combine(path, NativeDll);
        string backup = dll + NativeBackupSuffix;
        if (!File.Exists(backup)) return false;
        File.Move(backup, dll, overwrite: true);
        return true;
    }

    // The Whiteout runtime's offsets are built for this exact Resurge Beta client build.
    public const string SupportedClientHash = "A017E28DF1BDAAE79223A8A2F3A08173ADA750847F2FD89E41CC8E8859A76C49";

    /// <summary>
    /// Installs the Whiteout runtime (embedded in the launcher) as the game's T7InternalWS.dll.
    /// No original runtime is needed: a file staged this session is overwritten, a pre-existing one is
    /// kept aside and restored after the session.
    /// </summary>
    public static bool StageWhiteoutRuntime(string path, List<string> staged)
    {
        string dll = Path.Combine(path, NativeDll);
        string executable = Path.Combine(path, "Resurge - Beta.exe");
        using var payload = typeof(AssetManager).Assembly.GetManifestResourceStream("Whiteout.GdkRuntime");
        if (payload is null || !Matches(executable, SupportedClientHash)) return false;

        string backup = dll + NativeBackupSuffix;
        if (File.Exists(backup)) throw new IOException("Restore the original Resurge runtime before starting another session.");
        string temporary = dll + "." + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = File.Open(temporary, FileMode.CreateNew)) payload.CopyTo(output);
            bool stagedThisSession = staged.Contains(dll, StringComparer.OrdinalIgnoreCase);
            if (File.Exists(dll) && !stagedThisSession)
                File.Replace(temporary, dll, backup);
            else
            {
                File.Move(temporary, dll, overwrite: true);
                if (!stagedThisSession) staged.Add(dll);
            }
            return true;
        }
        finally { File.Delete(temporary); }
    }

    /// <summary>
    /// Looks for the original Resurge launcher bundle (Resurge.dll, which carries the game client)
    /// in the usual download places so players do not have to browse for it.
    /// </summary>
    public static string? FindClientBundle()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new[]
        {
            Path.GetDirectoryName(DefaultLegacyBundle)!,
            Path.Combine(profile, "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Path.Combine(profile, "Desktop"),
            Path.Combine(profile, "OneDrive", "Desktop"),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            AppContext.BaseDirectory
        };
        foreach (string root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "Resurge.dll", new EnumerationOptions
                {
                    RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (string file in files)
            {
                try
                {
                    if (new FileInfo(file).Length < 50_000_000) continue; // The bundle carries the 57 MB client.
                    ValidateLegacyBundle(file);
                    return file;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or BadImageFormatException or UnauthorizedAccessException) { }
            }
        }
        return null;
    }

    /// <summary>True when the game folder already has the client, or a bundle can provide it.</summary>
    public static bool HasClient(string gamePath, string? bundle) =>
        File.Exists(Path.Combine(gamePath, "Resurge - Beta.exe")) || (bundle is not null && File.Exists(bundle));

    private static bool Matches(string path, string expected)
    {
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)) == expected;
    }

    public static string DefaultLegacyBundle => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads",
        "Resurge Latest Build", "net8.0-windows", "Resurge.dll");

    public static void Stage(string group, string gamePath, string exeName, string? legacyBundle, List<string> staged)
    {
        if (group == "boiii" && !File.Exists(Path.Combine(gamePath, "BlackOps3.exe")))
            throw new FileNotFoundException("EZZ BOIII requires BlackOps3.exe from a compatible Steam installation.");

        if (group == "boiii" && exeName == "boiii.exe" && File.Exists(Path.Combine(gamePath, exeName)))
            return;

        string prefix = ResourcePrefix + group + ".";
        var assembly = typeof(AssetManager).Assembly;
        string[] names = assembly.GetManifestResourceNames();

        if (names.Contains(prefix + exeName, StringComparer.Ordinal))
        {
            foreach (var name in names)
            {
                if (ResourceTarget(name, prefix, gamePath) is not { } target) continue;
                using var input = assembly.GetManifestResourceStream(name) ?? throw new InvalidDataException(name);
                StageFile(target, staged, output => input.CopyTo(output));
            }
        }
        else if (legacyBundle is not null && File.Exists(legacyBundle))
        {
            StageLegacyBundle(legacyBundle, prefix, gamePath, staged);
        }

        if (!File.Exists(Path.Combine(gamePath, exeName)))
            throw new FileNotFoundException("The Resurge game client was not found. Open Settings › Game client and choose Resurge.dll from the original Resurge launcher.");
    }

    public static void ValidateLegacyBundle(string path)
    {
        using var file = File.OpenRead(path);
        using var pe = new PEReader(file);
        var metadata = pe.GetMetadataReader();
        bool containsClient = metadata.ManifestResources.Any(handle =>
        {
            var entry = metadata.GetManifestResource(handle);
            string name = metadata.GetString(entry.Name);
            return entry.Implementation.IsNil && name is ResourcePrefix + "ms.Resurge - Beta.exe" or ResourcePrefix + "boiii.EZZ boiii.exe";
        });
        if (!containsClient) throw new InvalidDataException("This DLL does not contain the original Resurge client assets.");
    }

    private static void StageLegacyBundle(string path, string prefix, string gamePath, List<string> staged)
    {
        // Read embedded assets directly without loading a second Resurge assembly into WPF.
        using var file = File.OpenRead(path);
        using var pe = new PEReader(file, PEStreamOptions.LeaveOpen);
        var directory = pe.PEHeaders.CorHeader?.ResourcesDirectory
            ?? throw new InvalidDataException("The original Resurge bundle must be a .NET assembly.");
        if (!pe.PEHeaders.TryGetDirectoryOffset(directory, out int directoryOffset)) return;

        var metadata = pe.GetMetadataReader();
        byte[] buffer = new byte[1024 * 1024];
        Span<byte> lengthBytes = stackalloc byte[4];
        foreach (var handle in metadata.ManifestResources)
        {
            var resource = metadata.GetManifestResource(handle);
            if (!resource.Implementation.IsNil) continue;
            string name = metadata.GetString(resource.Name);
            if (ResourceTarget(name, prefix, gamePath) is not { } target) continue;
            if (resource.Offset < 0 || resource.Offset + 4 > directory.Size)
                throw new InvalidDataException($"Invalid resource offset for {name}.");

            file.Position = directoryOffset + resource.Offset;
            file.ReadExactly(lengthBytes);
            int length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
            if (length < 0 || resource.Offset + 4 + length > directory.Size)
                throw new InvalidDataException($"Invalid resource length for {name}.");

            StageFile(target, staged, output =>
            {
                int remaining = length;
                while (remaining > 0)
                {
                    int chunk = Math.Min(remaining, buffer.Length);
                    file.ReadExactly(buffer.AsSpan(0, chunk));
                    output.Write(buffer, 0, chunk);
                    remaining -= chunk;
                }
            });
        }
    }

    private static string? ResourceTarget(string name, string prefix, string gamePath)
    {
        const string scripts = ResourcePrefix + "gsc.";
        string folder;
        string fileName;
        if (name.StartsWith(prefix, StringComparison.Ordinal))
        {
            folder = gamePath;
            fileName = name[prefix.Length..];
        }
        else if (name.StartsWith(scripts, StringComparison.Ordinal))
        {
            folder = Path.Combine(gamePath, "gsc");
            fileName = name[scripts.Length..];
        }
        else
        {
            return null;
        }

        if (fileName != Path.GetFileName(fileName) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            fileName.TrimEnd('.', ' ') != fileName || string.IsNullOrWhiteSpace(fileName))
            throw new InvalidDataException($"Invalid client resource name: {name}");

        return Path.Combine(folder, fileName);
    }

    private static void StageFile(string destination, List<string> staged, Action<Stream> copy)
    {
        if (File.Exists(destination)) return;

        string folder = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(folder);
        if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Client files cannot be staged through a linked folder: {folder}");

        string temporary = Path.Combine(folder, $".whiteout-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var output = File.Open(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                copy(output);

            File.Move(temporary, destination);
            staged.Add(destination);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public static int Cleanup(List<string> staged)
    {
        int failed = 0;
        foreach (var file in staged)
        {
            try { File.Delete(file); }
            catch (IOException) { failed++; }
            catch (UnauthorizedAccessException) { failed++; }
        }
        return failed;
    }
}
