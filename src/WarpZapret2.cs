using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;

internal static class WarpZapret2
{
    internal const string Executable = "winws2.exe";
    private const string MasqueIps = "warp-masque.txt";
    private const string ApiIps = "warp-api.txt";
    private const string MasquePorts = "443,500,1701,4500,4443,8443,8095";
    private const string TcpPorts = "80,443,8443";
    private const string NextRule = " --new ";
    private static readonly string[] Blobs = {
        "quic_rutube:@quic_initial_rutube_ru.bin",
        "quic_google:@quic_initial_www_google_com.bin",
        "stun:@stun.bin",
        "tls_google:@tls_clienthello_www_google_com.bin",
        "tls_sferum:@tls_clienthello_www_sferum_ru.bin",
        "tls_steamchat:@tls_clienthello_steam_chat_com.bin",
        "tls_teamspeak:@tls_clienthello_teamspeak_com.bin",
        "tls_mail:@tls_clienthello_sochi_park.bin"
    };

    internal static bool IsSelected(WarpStrategy strategy)
    {
        return strategy >= WarpStrategy.Circular && strategy <= WarpStrategy.DoubleFakeTimestamp;
    }

    internal static string Arguments(string profiles, WarpStrategy strategy)
    {
        var scope = AddressFilter(profiles);
        var rules = Engine.ReadProfile(Path.Combine(profiles, "warp-zapret2", strategy + ".args"));
        if (string.IsNullOrWhiteSpace(rules))
            throw new InvalidDataException("Пустой профиль zapret2 для WARP: " + strategy);

        var bundle = Path.Combine(Path.GetDirectoryName(profiles), "engine", "zapret2");
        rules = rules.Replace("{profiles}", profiles.Replace(Path.DirectorySeparatorChar, '/'));
        return "--wf-tcp-in=" + TcpPorts + " --wf-tcp-out=" + TcpPorts +
            " --wf-udp-in=" + MasquePorts + " --wf-udp-out=" + MasquePorts +
            " --wf-tcp-empty=0 --ctrack-disable=0 --wf-priority=10 --wf-raw-filter=\"" + scope + "\"" +
            " " + string.Join(" ", new[] { "zapret-lib.lua", "zapret-antidpi.lua", "zapret-auto.lua" }
                .Select(file => "--lua-init=@" + QuotePath(bundle, file))) +
            " " + string.Join(" ", Blobs.Select(blob => {
                var name = blob.Split(':')[0];
                return "--blob=" + name + ":@" + QuotePath(bundle, blob.Substring(name.Length + 2));
            })) + " " + rules;
    }

    private static string QuotePath(string directory, string file)
    {
        return "\"" + Path.Combine(directory, file).Replace(Path.DirectorySeparatorChar, '/') + "\"";
    }

    internal static string LegacyFallback(string rules)
    {
        return string.Join(NextRule, rules.Split(new[] { NextRule }, StringSplitOptions.None)
            .Where(rule => !rule.Contains("--ipset=../profiles/" + MasqueIps)));
    }

    internal static string ExcludeEndpoints(string rules)
    {
        var exclusions = " --ipset-exclude=../profiles/" + MasqueIps +
                         " --ipset-exclude=../profiles/" + ApiIps;
        // The higher-priority winws2 handle can reinject packets into winws.
        // Keep the TLS and QUIC rules from processing those packets a second time.
        return string.Join(NextRule, rules.Split(new[] { NextRule }, StringSplitOptions.None)
            .Select(rule => rule.StartsWith("--filter-tcp=", StringComparison.Ordinal) ||
                            rule.StartsWith("--filter-udp=443 ", StringComparison.Ordinal)
                ? rule + exclusions : rule));
    }

    private static string AddressFilter(string profiles)
    {
        var ranges = new[] { MasqueIps, ApiIps }
            .SelectMany(file => File.ReadLines(Path.Combine(profiles, file)))
            .Select(line => line.Split('#')[0].Trim()).Where(line => line.Length != 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (ranges.Length == 0)
            throw new InvalidDataException("В списках WARP отсутствуют адреса MASQUE и API.");

        return "((outbound and (" + string.Join(" or ", ranges.Select(range => MatchRange(range, "DstAddr"))) +
            ")) or (inbound and (" + string.Join(" or ", ranges.Select(range => MatchRange(range, "SrcAddr"))) + ")))";
    }

    private static string MatchRange(string range, string field)
    {
        var parts = range.Split('/');
        IPAddress address;
        if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out address) ||
            (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0))
            throw new InvalidDataException("Недопустимый адрес WARP: " + range);

        var min = address.GetAddressBytes();
        var prefix = min.Length * 8;
        if (parts.Length == 2 && (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out prefix) ||
            prefix <= 0 || prefix > min.Length * 8))
            throw new InvalidDataException("Недопустимая подсеть WARP: " + range);

        var ip = address.AddressFamily == AddressFamily.InterNetwork ? "ip" : "ipv6";
        var addressField = ip + "." + field;
        if (prefix == min.Length * 8) return "(" + ip + " and " + addressField + " == " + address + ")";

        var max = new byte[min.Length];
        for (var i = 0; i < min.Length; i++)
        {
            var bits = Math.Max(0, Math.Min(8, prefix - i * 8));
            var mask = (byte)(0xff << (8 - bits));
            min[i] = (byte)(min[i] & mask);
            max[i] = (byte)(min[i] | (mask ^ 0xff));
        }
        return "(" + ip + " and " + addressField + " >= " + new IPAddress(min) +
            " and " + addressField + " <= " + new IPAddress(max) + ")";
    }
}
