using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace ResurgeLauncherCSharp.Services;

internal sealed record PublicEndpoint(string Host, int Port)
{
    public override string ToString() => $"{Host}:{Port}";
}

internal sealed record PublicMatch(PublicEndpoint Address, string Name, string Map, int Humans, int Bots, int Capacity, long Ping)
{
    public int Available => Capacity - Humans - Bots;
}

internal static class PublicServerSearch
{
    private const int MaximumServers = 16;
    private const int DirectoryLimit = 192;
    private const int Protocol = 8;
    private static readonly string[] Directories = ["master.ezz.lol", "m.ezz.lol"];

    public static PublicEndpoint[] ParseAddresses(string input)
    {
        var addresses = new List<PublicEndpoint>();
        foreach (string line in input.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith('#')) continue;
            int separator = line.LastIndexOf(':');
            if (separator < 1 || !int.TryParse(line.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int port) ||
                port is < 1 or > 65535)
                throw new FormatException($"Invalid server address: {line}");

            string host = line[..separator].ToLowerInvariant();
            if (host.Length > 253 || host.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-')) ||
                Uri.CheckHostName(host) is not (UriHostNameType.Dns or UriHostNameType.IPv4))
                throw new FormatException($"Invalid server host: {host}");

            var endpoint = new PublicEndpoint(host, port);
            if (!addresses.Contains(endpoint)) addresses.Add(endpoint);
            if (addresses.Count > MaximumServers) throw new FormatException($"Use up to {MaximumServers} server addresses.");
        }
        return addresses.ToArray();
    }

    public static async Task<PublicMatch?> FindAsync(PublicEndpoint[] addresses)
    {
        bool discover = addresses.Length == 0;
        if (discover)
        {
            var directories = await Task.WhenAll(Directories.Select(QueryDirectoryAsync));
            addresses = directories.SelectMany(result => result).Distinct().Take(DirectoryLimit).ToArray();
            if (addresses.Length == 0)
                throw new InvalidOperationException("The EZZ public directory did not return any servers.");
        }

        using var throttle = new SemaphoreSlim(28);
        async Task<PublicMatch?> Probe(PublicEndpoint address)
        {
            await throttle.WaitAsync();
            try { return await QueryAsync(address, discover ? 1300 : 3000); }
            finally { throttle.Release(); }
        }

        var matches = await Task.WhenAll(addresses.Select(Probe));
        return matches.OfType<PublicMatch>()
            .Where(match => match.Available > 0)
            .OrderByDescending(match => match.Humans)
            .ThenByDescending(match => match.Bots)
            .ThenBy(match => match.Ping)
            .FirstOrDefault();
    }

    private static async Task<PublicEndpoint[]> QueryDirectoryAsync(string host)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            var hosts = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, timeout.Token);
            if (hosts.Length == 0) return [];
            using var socket = new UdpClient(AddressFamily.InterNetwork);
            socket.Connect(hosts[0], 20810);
            byte[] request = Encoding.Latin1.GetBytes($"\xff\xff\xff\xffgetservers T7 {Protocol} full empty");
            await socket.SendAsync(request, timeout.Token);
            var endpoints = new HashSet<PublicEndpoint>();
            try
            {
                while (endpoints.Count < DirectoryLimit)
                    endpoints.UnionWith(ParseDirectory((await socket.ReceiveAsync(timeout.Token)).Buffer));
            }
            catch (OperationCanceledException) { }
            return endpoints.ToArray();
        }
        catch (SocketException) { return []; }
        catch (OperationCanceledException) { return []; }
    }

    private static PublicEndpoint[] ParseDirectory(byte[] packet)
    {
        byte[] header = Encoding.Latin1.GetBytes("\xff\xff\xff\xffgetserversResponse ");
        if (!packet.AsSpan().StartsWith(header)) return [];

        var endpoints = new List<PublicEndpoint>();
        for (int i = header.Length; i + 6 < packet.Length && packet[i] == '\\'; i += 7)
        {
            var address = packet.AsSpan(i + 1, 4);
            if (address.SequenceEqual("EOT\0"u8)) break;

            byte first = address[0];
            byte second = address[1];
            int port = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(i + 5, 2));
            if (port == 0 || first is 0 or 10 or 127 or >= 224 ||
                first == 169 && second == 254 || first == 172 && second is >= 16 and <= 31 ||
                first == 192 && second == 168 || first == 100 && second is >= 64 and <= 127)
                continue;

            endpoints.Add(new PublicEndpoint(new IPAddress(address).ToString(), port));
        }
        return endpoints.ToArray();
    }

    private static async Task<PublicMatch?> QueryAsync(PublicEndpoint endpoint, int timeoutMs)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));
        try
        {
            var hosts = await Dns.GetHostAddressesAsync(endpoint.Host, AddressFamily.InterNetwork, timeout.Token);
            if (hosts.Length == 0) return null;

            using var socket = new UdpClient(AddressFamily.InterNetwork);
            socket.Connect(hosts[0], endpoint.Port);
            string challenge = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
            byte[] request = Encoding.Latin1.GetBytes($"\xff\xff\xff\xffgetInfo {challenge}");
            var clock = Stopwatch.StartNew();
            await socket.SendAsync(request, timeout.Token);
            while (true)
            {
                var reply = await socket.ReceiveAsync(timeout.Token);
                if (ParseResponse(reply.Buffer, challenge, endpoint, clock.ElapsedMilliseconds) is { } match)
                    return match;
            }
        }
        catch (SocketException) { return null; }
        catch (OperationCanceledException) { return null; }
    }

    private static PublicMatch? ParseResponse(byte[] packet, string challenge, PublicEndpoint endpoint, long ping)
    {
        const string header = "\xff\xff\xff\xffinfoResponse\n";
        if (packet.Length is < 20 or > 8192) return null;

        string message = Encoding.Latin1.GetString(packet).TrimEnd('\0');
        if (!message.StartsWith(header, StringComparison.OrdinalIgnoreCase)) return null;
        string[] fields = message[header.Length..].TrimStart('\\').Split('\\');
        if (fields.Length % 2 != 0) return null;

        var info = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < fields.Length; i += 2)
            if (!info.TryAdd(fields[i], fields[i + 1])) return null;

        if (info.GetValueOrDefault("challenge") != challenge || info.GetValueOrDefault("gamename") != "T7" ||
            info.GetValueOrDefault("playmode") != "1" || info.GetValueOrDefault("dedicated") != "1" ||
            info.GetValueOrDefault("sv_running") != "1" || info.GetValueOrDefault("isPrivate") != "0" ||
            info.GetValueOrDefault("protocol") != Protocol.ToString(CultureInfo.InvariantCulture) ||
            info.GetValueOrDefault("sub_protocol") is not ("0" or "1") ||
            info.GetValueOrDefault("net_password_hash") is not (null or "" or "0"))
            return null;

        bool ReadCount(string key, out int count) => int.TryParse(info.GetValueOrDefault(key), NumberStyles.None,
            CultureInfo.InvariantCulture, out count);
        if (!ReadCount("clients", out int clients) || !ReadCount("bots", out int bots) ||
            !ReadCount("sv_maxclients", out int capacity) || capacity is < 1 or > 64 ||
            bots > clients || clients > capacity)
            return null;

        string name = DisplayValue(info.GetValueOrDefault("hostname"), endpoint.ToString());
        string map = DisplayValue(info.GetValueOrDefault("mapname"), "Unknown map");
        return new PublicMatch(endpoint, name, map, clients - bots, bots, capacity, ping);
    }

    private static string DisplayValue(string? input, string fallback)
    {
        string plain = System.Text.RegularExpressions.Regex.Replace(input ?? "", @"\^[0-9]", "");
        string clean = new(plain.Where(c => !char.IsControl(c)).Take(90).ToArray());
        return string.IsNullOrWhiteSpace(clean) ? fallback : clean;
    }
}
