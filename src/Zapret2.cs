using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;

internal static class Zapret2
{
    internal const string Executable = "winws2.exe";
    private const string MasqueIps = "warp-masque.txt";
    private const string ApiIps = "warp-api.txt";
    private const string MasquePorts = "443,500,1701,4500,4443,8443,8095";
    private const string WarpTcpPorts = "80,443,8443";
    private const string GoogleBlob = "tls_google:@tls_clienthello_www_google_com.bin";
    private const string SferumBlob = "tls_sferum:@tls_clienthello_www_sferum_ru.bin";
    private const string SteamChatBlob = "tls_steamchat:@tls_clienthello_steam_chat_com.bin";
    private const string StunBlob = "stun:@stun.bin";
    private const string NextRule = " --new ";
    private static readonly string[] WarpBlobs = {
        "quic_rutube:@quic_initial_rutube_ru.bin",
        "quic_google:@quic_initial_www_google_com.bin",
        StunBlob,
        GoogleBlob,
        SferumBlob,
        SteamChatBlob,
        "tls_teamspeak:@tls_clienthello_teamspeak_com.bin",
        "tls_mail:@tls_clienthello_sochi_park.bin"
    };

    internal static bool UsesWarp(WarpStrategy strategy)
    {
        return strategy >= WarpStrategy.Circular && strategy <= WarpStrategy.DoubleFakeTimestamp;
    }

    internal static bool UsesGame(Strategy strategy)
    {
        return strategy >= Strategy.Zapret2Circular && strategy <= Strategy.Zapret2DoubleFake;
    }

    internal static bool UsesDiscord(ServiceStrategy strategy)
    {
        return strategy == ServiceStrategy.Zapret2DiscordExp || strategy == ServiceStrategy.Zapret2DiscordCircular;
    }

    internal static bool UsesYoutube(ServiceStrategy strategy)
    {
        return strategy == ServiceStrategy.Zapret2YoutubeCircular || strategy == ServiceStrategy.Zapret2YoutubeExp;
    }

    internal static string Arguments(string profiles, WarpStrategy warp, Strategy darktide, Strategy spaceMarine,
        bool discordEnabled, ServiceStrategy discord, ServiceStrategy youtube)
    {
        var withWarp = UsesWarp(warp);
        var withDarktide = UsesGame(darktide);
        var withSpaceMarine = UsesGame(spaceMarine);
        var withGame = withDarktide || withSpaceMarine;
        var withDiscord = UsesDiscord(discord);
        var withYoutube = UsesYoutube(youtube);
        var withServices = withDiscord || withYoutube;
        if (!withWarp && !withGame && !withServices) return null;

        var rules = new List<string>();
        var scopes = new List<string>();
        var udpOut = new List<string>();
        var tcp = new List<string>();
        if (withWarp)
        {
            var ranges = ReadRanges(profiles, MasqueIps, ApiIps);
            var outbound = MatchAddresses(ranges, "DstAddr");
            var inbound = MatchAddresses(ranges, "SrcAddr");
            if (withGame || withServices)
            {
                // Keep WARP addresses paired with WARP ports when sharing the kernel filter.
                outbound = WarpDirection(outbound, "DstPort");
                inbound = WarpDirection(inbound, "SrcPort");
            }
            scopes.Add("((outbound and (" + outbound + ")) or (inbound and (" + inbound + ")))");
            var warpRules = ReadRules(profiles, "warp-zapret2", warp.ToString());
            if (withServices)
            {
                // Service TCP capture also sees these ports on hosts outside WARP's normal address filter.
                var ipsets = " --ipset=" + QuotePath(profiles, MasqueIps) + " --ipset=" + QuotePath(profiles, ApiIps);
                warpRules = string.Join(NextRule, warpRules.Split(new[] { NextRule }, StringSplitOptions.None)
                    .Select(rule => rule.Contains(" --hostlist=") ? rule + ipsets : rule));
            }
            rules.Add(warpRules);
            tcp.Add(WarpTcpPorts);
            udpOut.Add(MasquePorts);
        }
        if (withDarktide)
        {
            udpOut.Add(Engine.DarktideUdpPorts);
            scopes.Add("(outbound and udp and " + MatchPorts(Engine.DarktideUdpPorts, "udp.DstPort") + ")");
            rules.Add(GameRule(profiles, "darktide-udp", Engine.DarktideUdpPorts, darktide));
        }
        if (withSpaceMarine)
        {
            var ranges = ReadRanges(profiles, Engine.SpaceMarineSharedIps, Engine.SpaceMarineUdpIps);
            udpOut.Add(Engine.SpaceMarineUdpPorts);
            scopes.Add("(outbound and udp and " + MatchPorts(Engine.SpaceMarineUdpPorts, "udp.DstPort") +
                " and (" + MatchAddresses(ranges, "DstAddr") + "))");
            if (discordEnabled)
            {
                // The first matching profile passes Discord/STUN through to the legacy voice rules.
                rules.Add("--name=discord-voice-pass --filter-udp=" + Engine.DiscordVoiceOverlap +
                    " --filter-l7=discord,stun");
            }
            var ipsets = " --ipset=" + QuotePath(profiles, Engine.SpaceMarineSharedIps) +
                " --ipset=" + QuotePath(profiles, Engine.SpaceMarineUdpIps);
            rules.Add(GameRule(profiles, "space-marine-2-udp", Engine.SpaceMarineUdpPorts, spaceMarine, ipsets));
        }

        if (withServices)
        {
            var servicePorts = withDiscord ? "443," + Engine.DiscordTlsPorts : "443";
            tcp.Add(servicePorts);
            scopes.Add("((outbound and tcp and " + MatchPorts(servicePorts, "tcp.DstPort") +
                ") or (inbound and tcp and " + MatchPorts(servicePorts, "tcp.SrcPort") + "))");

            var exclusions = " --hostlist-exclude=" + QuotePath(profiles, "darktide.txt") +
                " --hostlist-exclude=" + QuotePath(profiles, "space-marine-2.txt");
            if (warp != WarpStrategy.None)
                exclusions += " --hostlist-exclude=" + QuotePath(profiles, "warp.txt") +
                    " --ipset-exclude=" + QuotePath(profiles, MasqueIps) + " --ipset-exclude=" + QuotePath(profiles, ApiIps);
            if (withDiscord)
                rules.Add(ServiceRules(profiles, discord, exclusions));
            if (withYoutube)
                rules.Add(ServiceRules(profiles, youtube, exclusions +
                    " --hostlist-exclude=" + QuotePath(profiles, "discord.txt")));
        }

        var engineDirectory = Path.Combine(Path.GetDirectoryName(profiles), "engine");
        var bundle = Path.Combine(engineDirectory, "zapret2");
        var blobs = withWarp ? WarpBlobs.Select(blob => BlobArgument(bundle, blob)).ToList() : new List<string>();
        if (withGame) blobs.Add(BlobArgument(engineDirectory, "active_game:@ACTIVE_GAME_UDP.bin"));
        if (!withWarp)
        {
            if (withServices) blobs.Add(BlobArgument(bundle, GoogleBlob));
            if (discord == ServiceStrategy.Zapret2DiscordCircular) blobs.Add(BlobArgument(bundle, StunBlob));
            if (discord == ServiceStrategy.Zapret2DiscordExp || youtube == ServiceStrategy.Zapret2YoutubeCircular)
                blobs.Add(BlobArgument(bundle, SteamChatBlob));
            if (darktide == Strategy.Zapret2Exp || spaceMarine == Strategy.Zapret2Exp ||
                youtube == ServiceStrategy.Zapret2YoutubeCircular)
                blobs.Add(BlobArgument(bundle, SferumBlob));
        }
        var lua = new[] { "zapret-lib.lua", "zapret-antidpi.lua", "zapret-auto.lua" }
            .Select(file => "--lua-init=@" + QuotePath(bundle, file));

        var tcpPorts = string.Join(",", tcp.SelectMany(ports => ports.Split(',')).Distinct());
        var capture = tcpPorts.Length == 0 ? "" : "--wf-tcp-in=" + tcpPorts + " --wf-tcp-out=" + tcpPorts + " ";
        if (withWarp) capture += "--wf-udp-in=" + MasquePorts + " ";
        if (udpOut.Count != 0) capture += "--wf-udp-out=" + string.Join(",", udpOut) + " ";
        return capture + "--wf-tcp-empty=0 --ctrack-disable=0 --wf-priority=10 --wf-raw-filter=\"" + string.Join(" or ", scopes) + "\"" +
            " " + string.Join(" ", lua) + " " + string.Join(" ", blobs) + " " + string.Join(NextRule, rules);
    }

    private static string ServiceRules(string profiles, ServiceStrategy strategy, string exclusions)
    {
        return ReadRules(profiles, "service-zapret2", strategy.ToString())
            .Replace("{media-ports}", Engine.DiscordTlsPorts)
            .Replace("{exclude}", exclusions);
    }

    private static string GameRule(string profiles, string name, string ports, Strategy strategy, string ipsets = "")
    {
        return "--name=" + name + " --filter-udp=" + ports + ipsets + " " +
            ReadRules(profiles, "game-zapret2", strategy.ToString());
    }

    private static string ReadRules(string profiles, string folder, string name)
    {
        var rules = Engine.ReadProfile(Path.Combine(profiles, folder, name + ".args"));
        if (string.IsNullOrWhiteSpace(rules))
            throw new InvalidDataException("Пустой профиль zapret2: " + name);
        return rules.Replace("{profiles}", profiles.Replace(Path.DirectorySeparatorChar, '/'));
    }

    private static string BlobArgument(string directory, string blob)
    {
        var separator = blob.IndexOf('@');
        return "--blob=" + blob.Substring(0, separator + 1) + QuotePath(directory, blob.Substring(separator + 1));
    }

    private static string QuotePath(string directory, string file)
    {
        return "\"" + Path.Combine(directory, file).Replace(Path.DirectorySeparatorChar, '/') + "\"";
    }

    private static string WarpDirection(string addresses, string field)
    {
        return "((" + addresses + ") and ((tcp and " + MatchPorts(WarpTcpPorts, "tcp." + field) +
            ") or (udp and " + MatchPorts(MasquePorts, "udp." + field) + ")))";
    }

    private static string MatchPorts(string ports, string field)
    {
        return "(" + string.Join(" or ", ports.Split(',').Select(port => {
            var bounds = port.Split('-');
            return bounds.Length == 1 ? field + " == " + port :
                "(" + field + " >= " + bounds[0] + " and " + field + " <= " + bounds[1] + ")";
        })) + ")";
    }

    internal static string LegacyFallback(string rules)
    {
        return string.Join(NextRule, rules.Split(new[] { NextRule }, StringSplitOptions.None)
            .Where(rule => !rule.Contains("--ipset=../profiles/" + MasqueIps)));
    }

    internal static string ExcludeWarpEndpoints(string rules)
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

    private static string[] ReadRanges(string profiles, params string[] files)
    {
        var ranges = files.SelectMany(file => File.ReadLines(Path.Combine(profiles, file)))
            .Select(line => line.Split('#')[0].Trim()).Where(line => line.Length != 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (ranges.Length == 0)
            throw new InvalidDataException("В профиле zapret2 отсутствуют адреса: " + string.Join(", ", files));
        return ranges;
    }

    private static string MatchAddresses(string[] ranges, string field)
    {
        return string.Join(" or ", ranges.Select(range => MatchRange(range, field)));
    }

    private static string MatchRange(string range, string field)
    {
        var parts = range.Split('/');
        IPAddress address;
        if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out address) ||
            (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0))
            throw new InvalidDataException("Недопустимый адрес в профиле zapret2: " + range);

        var min = address.GetAddressBytes();
        var prefix = min.Length * 8;
        if (parts.Length == 2 && (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out prefix) ||
            prefix <= 0 || prefix > min.Length * 8))
            throw new InvalidDataException("Недопустимая подсеть в профиле zapret2: " + range);

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
