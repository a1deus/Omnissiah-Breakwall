using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

internal interface ISpaceMarineTargets
{
    IList<IPEndPoint> Read(CancellationToken cancellation);
}

internal sealed class SpaceMarineEchoCache : ISpaceMarineTargets
{
    internal static readonly SpaceMarineEchoCache Default = new SpaceMarineEchoCache(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Omnissiah Breakwall", "sm2-echo.dpapi"), () => DateTime.UtcNow);
    private readonly string path;
    private readonly Func<DateTime> now;
    private readonly object gate = new object();
    private readonly Dictionary<string,DateTime> entries = new Dictionary<string,DateTime>();
    private bool loaded, dirty;
    internal SpaceMarineEchoCache(string path, Func<DateTime> now) { this.path = path; this.now = now; }
    internal static bool Valid(IPAddress ip) { return DarktideLogTargets.IsPublicIpv4(ip); }
    private void Load()
    {
        if (loaded) return; loaded = true;
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 32768) return;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
            foreach (var line in Encoding.UTF8.GetString(plain).Split('\n'))
            {
                var parts = line.Trim().Split('|'); IPAddress ip; long ticks;
                if (parts.Length == 2 && IPAddress.TryParse(parts[0], out ip) && Valid(ip) &&
                    long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out ticks) && ticks > 0 && ticks <= DateTime.MaxValue.Ticks)
                {
                    var time = new DateTime(ticks, DateTimeKind.Utc);
                    if (time >= now().AddHours(-24) && time <= now().AddMinutes(5)) entries[ip.ToString()] = time;
                    else dirty = true;
                }
                if (entries.Count == 64) break;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (CryptographicException) { }
    }
    internal void Remember(IPAddress ip)
    {
        if (!Valid(ip)) return;
        lock (gate)
        {
            Load(); entries[ip.ToString()] = now(); dirty = true;
            foreach (var old in entries.OrderByDescending(pair => pair.Value).Skip(64).Select(pair => pair.Key).ToArray()) entries.Remove(old);
        }
    }
    public IList<IPEndPoint> Read(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        lock (gate)
        {
            Load();
            foreach (var expired in entries.Where(pair => pair.Value < now().AddHours(-24) || pair.Value > now().AddMinutes(5)).Select(pair => pair.Key).ToArray())
            { entries.Remove(expired); dirty = true; }
            if (dirty) Flush();
            return entries.Where(pair => pair.Value >= now().AddHours(-24) && pair.Value <= now().AddMinutes(5))
                .OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new IPEndPoint(IPAddress.Parse(pair.Key), 50000)).ToArray();
        }
    }
    internal void Flush()
    {
        lock (gate)
        {
            if (!dirty) return;
            string temporary = null;
            try
            {
                if (entries.Count == 0) { File.Delete(path); dirty = false; return; }
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var plain = Encoding.UTF8.GetBytes(string.Join("\n", entries.Select(pair => pair.Key + "|" + pair.Value.Ticks.ToString(CultureInfo.InvariantCulture))));
                var encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
                temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(temporary, encrypted);
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
                dirty = false;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (CryptographicException) { }
            finally { if (temporary != null) { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } } }
        }
    }
}

internal static class SpaceMarineEchoPacket
{
    internal static byte[] Create() { return Encoding.ASCII.GetBytes(Guid.NewGuid().ToString("D")); }
    internal static bool IsRequest(byte[] packet)
    {
        Guid nonce;
        return packet != null && packet.Length == 36 && Guid.TryParseExact(Encoding.ASCII.GetString(packet), "D", out nonce);
    }
    internal static bool Accept(byte[] request, byte[] reply)
    {
        return IsRequest(request) && reply != null && reply.Length == 36 && request.SequenceEqual(reply);
    }
}

internal interface ISpaceMarineUdpProbe
{
    string[] Addresses { get; }
    Task<ProbeReport> Prepare(CancellationToken cancellation);
    Task<ProbeReport> Test(CancellationToken cancellation);
}

internal sealed class SpaceMarineUdpProbe : ISpaceMarineUdpProbe
{
    internal const string MissingTargets = "SM2: запустите игру с включённым обходом, закройте её и повторите подбор.";
    private readonly ISpaceMarineTargets source;
    private readonly IDarktideDatagrams transport;
    private IList<IPEndPoint> targets;
    internal SpaceMarineUdpProbe(ISpaceMarineTargets source, IDarktideDatagrams transport) { this.source = source; this.transport = transport; }
    public string[] Addresses { get { return targets == null ? new string[0] : targets.Select(endpoint => endpoint.Address.ToString()).ToArray(); } }
    public Task<ProbeReport> Prepare(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        targets = source.Read(cancellation).Where(endpoint => endpoint.Port == 50000 && SpaceMarineEchoCache.Valid(endpoint.Address))
            .GroupBy(endpoint => endpoint.Address.ToString()).Select(group => group.First()).Take(4).ToArray();
        return Task.FromResult(new ProbeReport { Passed = targets.Count != 0, Detail = targets.Count == 0 ? MissingTargets : "Адреса UDP-диагностики найдены локально." });
    }
    public async Task<ProbeReport> Test(CancellationToken cancellation)
    {
        if (targets == null) await Prepare(cancellation);
        if (targets.Count == 0) return new ProbeReport { Detail = MissingTargets };
        var responses = await Task.WhenAll(targets.Select(endpoint => TestEndpoint(endpoint, cancellation)));
        var passed = responses.Count(value => value);
        return new ProbeReport { Passed = passed != 0, Detail = passed == 0 ?
            "UDP-эхо не подтверждено. Узлы могли устареть; обновите адреса запуском игры с обходом." :
            "UDP-эхо: ответили " + passed + " из " + targets.Count + " узлов в двух попытках. Вход в матч не проверен." };
    }
    private async Task<bool> TestEndpoint(IPEndPoint endpoint, CancellationToken cancellation)
    {
        for (var round = 0; round < 2; round++)
        {
            cancellation.ThrowIfCancellationRequested();
            var request = SpaceMarineEchoPacket.Create();
            var matching = transport as IMatchingDatagrams;
            var reply = matching == null ? await transport.Request(endpoint, request, cancellation) :
                await matching.RequestMatching(endpoint, request, value => SpaceMarineEchoPacket.Accept(request, value), cancellation);
            if (!SpaceMarineEchoPacket.Accept(request, reply)) return false;
            if (round == 0) await Task.Delay(100, cancellation);
        }
        return true;
    }
}
