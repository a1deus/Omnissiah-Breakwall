using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

internal interface IDarktideTargets
{
    IList<IPEndPoint> Read(CancellationToken cancellation);
}

internal sealed class DarktideLogTargets : IDarktideTargets
{
    private readonly string directory;
    private readonly Func<DateTime> now;
    private static readonly Regex Address = new Regex(
        @"\[PartyImmaterium(?:Hub|Mission)SessionBoot\].*?Searching for WAN lobby on (?<ip>[0-9]{1,3}(?:\.[0-9]{1,3}){3}):(?<port>[0-9]{1,5})(?:\s|$)",
        RegexOptions.CultureInvariant);
    internal DarktideLogTargets() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Fatshark", "Darktide", "console_logs"), () => DateTime.UtcNow) { }
    internal DarktideLogTargets(string directory, Func<DateTime> now) { this.directory = directory; this.now = now; }

    public IList<IPEndPoint> Read(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        // Only local discovery; no static server IPs, tokens, account IDs, log
        // copies, telemetry or endpoint files. The snapshot lives in memory.
        var result = new List<IPEndPoint>();
        try
        {
            if (!Directory.Exists(directory)) return result;
            var files = new DirectoryInfo(directory).GetFiles("console-*.log")
                .Where(file => file.LastWriteTimeUtc >= now().AddDays(-7) && file.Length <= 16 * 1024 * 1024)
                .OrderByDescending(file => file.LastWriteTimeUtc).Take(6);
            foreach (var file in files)
            {
                cancellation.ThrowIfCancellationRequested();
                var found = new List<IPEndPoint>();
                try
                {
                    using (var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                    {
                        string line; long processed = 0;
                        while ((line = reader.ReadLine()) != null && processed < 16 * 1024 * 1024)
                        {
                            cancellation.ThrowIfCancellationRequested(); processed += line.Length + 1;
                            if (line.Length > 4096) continue;
                            var match = Address.Match(line); IPAddress ip; int port;
                            if (match.Success && IPAddress.TryParse(match.Groups["ip"].Value, out ip) &&
                                int.TryParse(match.Groups["port"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out port) &&
                                IsPublicGameEndpoint(ip, port))
                                found.Add(new IPEndPoint(ip, port));
                        }
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                foreach (var endpoint in found.AsEnumerable().Reverse())
                {
                    if (!result.Any(existing => existing.Equals(endpoint))) result.Add(endpoint);
                    if (result.Count == 4) return result;
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return result;
    }

    internal static bool IsPublicGameEndpoint(IPAddress ip, int port)
    {
        if (ip == null || ip.AddressFamily != AddressFamily.InterNetwork ||
            !((port >= 35500 && port <= 35650) || port == 35670)) return false;
        var b = ip.GetAddressBytes();
        if (b[0] == 0 || b[0] == 10 || b[0] == 127 || b[0] >= 224 ||
            (b[0] == 100 && b[1] >= 64 && b[1] <= 127) || (b[0] == 169 && b[1] == 254) ||
            (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) ||
            (b[0] == 192 && b[1] == 0 && (b[2] == 0 || b[2] == 2)) ||
            (b[0] == 198 && (b[1] == 18 || b[1] == 19 || (b[1] == 51 && b[2] == 100))) ||
            (b[0] == 203 && b[1] == 0 && b[2] == 113)) return false;
        return true;
    }
}

internal static class DarktideConnectPacket
{
    // Connectionless request for the supported Windows protocol. Identifiers
    // are generated per attempt, never copied from a game account or capture.
    internal static byte[] Create(Guid peer)
    {
        var uuid = Encoding.ASCII.GetBytes(peer.ToString("D") + "\0");
        var packet = new byte[55]; packet[0] = 0x36; packet[1] = 0xe5; packet[2] = 0x3e;
        var hash = Hash(uuid);
        for (var i = 0; i < 8; i++) packet[6 + i] = (byte)(hash >> (8 * i));
        Array.Copy(uuid, 0, packet, 14, 37);
        packet[52] = 0x10; packet[53] = 1; packet[54] = 0x21;
        return packet;
    }
    internal static ulong Hash(byte[] value)
    {
        unchecked
        {
            const ulong m = 0xc6a4a7935bd1e995UL;
            ulong hash = (ulong)value.Length * m; int offset = 0;
            while (offset + 8 <= value.Length)
            {
                ulong block = 0;
                for (var i = 0; i < 8; i++) block |= (ulong)value[offset + i] << (8 * i);
                block *= m; block ^= block >> 47; block *= m; hash ^= block; hash *= m; offset += 8;
            }
            if (offset < value.Length)
            {
                for (var i = offset; i < value.Length; i++) hash ^= (ulong)value[i] << (8 * (i - offset));
                hash *= m;
            }
            hash ^= hash >> 47; hash *= m; hash ^= hash >> 47; return hash;
        }
    }
    internal static bool Accept(byte[] reply)
    {
        // Conservative initial reply family. This confirms a UDP response,
        // not authentication, a complete handshake, or a stable match.
        return reply != null && reply.Length >= 31 && reply.Length <= 33 && reply[0] == 0x3e;
    }
}

internal interface IDarktideDatagrams
{
    Task<byte[]> Request(IPEndPoint endpoint, byte[] request, CancellationToken cancellation);
}

internal sealed class DarktideDatagrams : IDarktideDatagrams
{
    public async Task<byte[]> Request(IPEndPoint endpoint, byte[] request, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        using (var client = new UdpClient(endpoint.AddressFamily))
        using (cancellation.Register(() => client.Close()))
        {
            try
            {
                // A fresh connected socket only receives from this exact
                // server IP/port. No stale socket crosses strategy boundaries.
                client.Connect(endpoint);
                await client.SendAsync(request, request.Length);
                var receiving = client.ReceiveAsync();
                if (await Task.WhenAny(receiving, Task.Delay(1800, cancellation)) == receiving)
                {
                    var reply = await receiving;
                    cancellation.ThrowIfCancellationRequested();
                    return reply.RemoteEndPoint.Equals(endpoint) ? reply.Buffer : null;
                }
                client.Close();
                try { await receiving; }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
                cancellation.ThrowIfCancellationRequested(); return null;
            }
            catch (SocketException) { cancellation.ThrowIfCancellationRequested(); return null; }
            catch (ObjectDisposedException) { cancellation.ThrowIfCancellationRequested(); return null; }
        }
    }
}

internal interface IDarktideUdpProbe
{
    Task<ProbeReport> Prepare(CancellationToken cancellation);
    Task<ProbeReport> Test(CancellationToken cancellation);
}

internal sealed class DarktideUdpProbe : IDarktideUdpProbe
{
    internal const string MissingTargets = "Darktide пропущен: попробуйте войти в хаб, закройте игру и повторите подбор.";
    private readonly IDarktideTargets source;
    private readonly IDarktideDatagrams transport;
    private IList<IPEndPoint> targets;
    internal DarktideUdpProbe(IDarktideTargets source, IDarktideDatagrams transport) { this.source = source; this.transport = transport; }
    public async Task<ProbeReport> Prepare(CancellationToken cancellation)
    {
        targets = await Task.Run(() => source.Read(cancellation), cancellation);
        return new ProbeReport { Passed = targets.Count != 0, Detail = targets.Count == 0 ? MissingTargets : "Адреса для UDP-проверки найдены локально." };
    }
    public async Task<ProbeReport> Test(CancellationToken cancellation)
    {
        if (targets == null) await Prepare(cancellation);
        if (targets.Count == 0) return new ProbeReport { Detail = MissingTargets };
        var responses = await Task.WhenAll(targets.Select(endpoint => TestEndpoint(endpoint, cancellation)));
        var passed = responses.Count(value => value);
        return new ProbeReport { Passed = passed != 0, Detail = passed == 0 ?
            "UDP-ответ не подтверждён: возможны фильтрация, недоступный сервер или изменение протокола. Обновите адреса попыткой входа в игру." :
            "UDP: ответили " + passed + " из " + targets.Count + " игровых узлов в двух попытках. Вход в матч не проверен." };
    }
    private async Task<bool> TestEndpoint(IPEndPoint endpoint, CancellationToken cancellation)
    {
        for (var round = 0; round < 2; round++)
        {
            cancellation.ThrowIfCancellationRequested();
            var reply = await transport.Request(endpoint, DarktideConnectPacket.Create(Guid.NewGuid()), cancellation);
            if (!DarktideConnectPacket.Accept(reply)) return false;
            if (round == 0) await Task.Delay(100, cancellation);
        }
        return true;
    }
}
