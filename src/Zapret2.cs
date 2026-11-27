using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

// One process owns every selected profile. Specific rules precede broad lists.
internal static class Zapret2
{
    internal const string Executable = "winws2.exe";
    private const string MasqueIps = "warp-masque.txt";
    private const string ApiIps = "warp-api.txt";
    private const string MasquePorts = "443,500,1701,4500,4443,8443,8095";
    private const string WireguardPorts = "500,1701,2408,4500";
    private const string NextRule = " --new ";
    private const string StandardTls = "--payload=tls_client_hello --lua-desync=multisplit:pos=1:seqovl=568:seqovl_pattern=tls_4pda";
    private const string StandardHttp = "--payload=http_req --lua-desync=multisplit:pos=method+2";
    private const string GoogleSplit = "--payload=tls_client_hello --lua-desync=multisplit:pos=1:seqovl=681:seqovl_pattern=tls_google";
    private const string DefaultTlsFake = "fake:blob=fake_default_tls:tls_mod=rnd,dupsid,sni=www.google.com";
    private static readonly Dictionary<string, string> BlobFiles = new Dictionary<string, string>(StringComparer.Ordinal) {
        { "active_game", "ACTIVE_GAME_UDP.bin" }, { "active_discord", "ACTIVE_DISCORD_UDP.bin" },
        { "stun", "stun.bin" }, { "stun2", "stun2.bin" },
        { "quic_google", "quic_initial_www_google_com.bin" }, { "quic_4pda", "quic_initial_4pda_to.bin" },
        { "quic_dbank", "quic_initial_dbankcloud_ru.bin" }, { "quic_rutube", "zapret2/quic_initial_rutube_ru.bin" },
        { "tls_google", "tls_clienthello_www_google_com.bin" }, { "tls_sferum", "tls_clienthello_www_sferum_ru.bin" },
        { "tls_4pda", "tls_clienthello_4pda_to.bin" }, { "tls_mail", "tls_clienthello_sochi_park.bin" },
        { "tls_steamchat", "zapret2/tls_clienthello_steam_chat_com.bin" },
        { "tls_teamspeak", "zapret2/tls_clienthello_teamspeak_com.bin" }
    };

    internal static IEnumerable<string> RequiredFiles
    {
        get
        {
            return BlobFiles.Values.Select(file => Path.Combine("engine", file.Replace('/', Path.DirectorySeparatorChar)))
                .Concat(new[] { "zapret-lib.lua", "zapret-antidpi.lua", "zapret-auto.lua" }
                    .Select(file => Path.Combine("engine", "zapret2", file)))
                .Concat(new[] { "Zapret2Exp", "Zapret2DoubleFake" }.Select(name => Path.Combine("profiles", "game-zapret2", name + ".args")))
                .Concat(new[] { "Zapret2TlsClone", "Zapret2TlsCircular" }.Select(name => Path.Combine("profiles", "game-tls-zapret2", name + ".args")))
                .Concat(new[] { "Zapret2DiscordExp", "Zapret2DiscordCircular", "Zapret2YoutubeCircular", "Zapret2YoutubeExp" }
                    .Select(name => Path.Combine("profiles", "service-zapret2", name + ".args")))
                .Concat(new[] { "Circular", "CircularStun", "DoubleFakeSequence", "DoubleFakeSteam", "DoubleFakeTimestamp" }
                    .Select(name => Path.Combine("profiles", "warp-zapret2", name + ".args")));
        }
    }

    internal static string Arguments(string profiles, StrategySelection<Strategy> darktide,
        StrategySelection<WarpStrategy> warp, StrategySelection<ServiceStrategy> discord,
        StrategySelection<ServiceStrategy> youtube, StrategySelection<Strategy> spaceMarine)
    {
        var dt = darktide.BuiltIn != Strategy.None;
        var sm = spaceMarine.BuiltIn != Strategy.None;
        var ds = discord.Imported != null || discord.BuiltIn != ServiceStrategy.None;
        var yt = youtube.Imported != null || youtube.BuiltIn != ServiceStrategy.None;
        var wp = warp.BuiltIn != WarpStrategy.None;
        if (!dt && !sm && !ds && !yt && !wp) return null;
        var rules = new List<string>();
        var tcp = new List<string>();
        var udpOut = new List<string>();
        var udpIn = new List<string>();
        var scopes = new List<string>();
        if (dt || ds || yt || wp) tcp.Add("80");
        tcp.Add("443");
        if (sm) tcp.Add(Engine.SpaceMarineTcpPorts);
        if (ds) tcp.Add(Engine.DiscordTlsPorts);
        if (wp) tcp.Add("8443");
        udpOut.Add("443"); // QUIC SNI is only known after capturing the Initial.
        scopes.Add("(outbound and udp and udp.DstPort == 443)");
        if (wp)
        {
            var masque = ReadRanges(profiles, MasqueIps, ApiIps);
            var wireguard = ReadRanges(profiles, "warp-wireguard.txt");
            AddUdpCapture(scopes, udpOut, MasquePorts, masque, false);
            AddUdpCapture(scopes, udpIn, MasquePorts, masque, true);
            AddUdpCapture(scopes, udpOut, WireguardPorts, wireguard, false);
            AddWarpRules(rules, profiles, warp.BuiltIn);
        }
        var warpExclusions = wp ? HostExclude(profiles, "warp.txt") +
            IpExclude(profiles, MasqueIps) + IpExclude(profiles, ApiIps) : "";
        if (dt)
        {
            AddUdpCapture(scopes, udpOut, Engine.DarktideUdpPorts, null, false);
            if (darktide.BuiltIn == Strategy.CommunityAlt10)
                AddUdpCapture(scopes, udpIn, Engine.DarktideUdpPorts, null, true);
            AddGameWebRules(rules, profiles, "darktide", darktide.BuiltIn, warpExclusions, false);
        }
        if (sm)
        {
            var ranges = ReadRanges(profiles, Engine.SpaceMarineSharedIps, Engine.SpaceMarineUdpIps);
            AddUdpCapture(scopes, udpOut, Engine.SpaceMarineUdpPorts, ranges, false);
            if (spaceMarine.BuiltIn == Strategy.CommunityAlt10)
                AddUdpCapture(scopes, udpIn, Engine.SpaceMarineUdpPorts, ranges, true);
            AddGameWebRules(rules, profiles, "space-marine-2", spaceMarine.BuiltIn, warpExclusions, true);
        }
        var exclusions = warpExclusions + (dt ? HostExclude(profiles, "darktide.txt") : "") +
            (sm ? HostExclude(profiles, "space-marine-2.txt") : "");
        if (ds)
        {
            AddUdpCapture(scopes, udpOut, Engine.DiscordVoicePorts, null, false);
            var voice = discord.Imported == null ?
                "--filter-l7=discord,stun --out-range=-n5 --payload=discord_ip_discovery,stun " +
                    "--lua-desync=fake:blob=active_discord:repeats=6" : discord.Imported.Voice;
            if (sm)
            {
                // Unknown UDP in the overlap may belong to SM2. Remove its
                // profile match AND its Lua instances, preserving Discord/STUN.
                voice = voice.Replace("discord,stun,unknown", "discord,stun");
                voice = Regex.Replace(voice, @" --payload=unknown\b.*$", "");
            }
            rules.Add("--name=discord-voice --filter-udp=" + Engine.DiscordVoicePorts + " " + voice);
            AddDiscordWebRules(rules, profiles, discord, exclusions);
        }
        if (yt)
            AddYoutubeRules(rules, profiles, youtube, exclusions + (ds ? HostExclude(profiles, "discord.txt") : ""));
        if (dt)
            rules.Add(GameUdpRule(profiles, "darktide-udp", Engine.DarktideUdpPorts, darktide.BuiltIn, ""));
        if (sm)
            rules.Add(GameUdpRule(profiles, "space-marine-2-udp", Engine.SpaceMarineUdpPorts, spaceMarine.BuiltIn,
                IpSet(profiles, Engine.SpaceMarineSharedIps) + IpSet(profiles, Engine.SpaceMarineUdpIps)));
        var tcpPorts = Ports(tcp);
        scopes.Add("((outbound and tcp and " + MatchPorts(tcpPorts, "tcp.DstPort") +
            ") or (inbound and tcp and " + MatchPorts(tcpPorts, "tcp.SrcPort") + "))");
        var capture = "--wf-tcp-in=" + tcpPorts + " --wf-tcp-out=" + tcpPorts +
            " --wf-udp-out=" + Ports(udpOut) + (udpIn.Count == 0 ? "" : " --wf-udp-in=" + Ports(udpIn));
        var bundle = Path.Combine(Path.GetDirectoryName(profiles), "engine", "zapret2");
        var engine = Path.GetDirectoryName(bundle);
        var body = string.Join(NextRule, rules);
        // Import only referenced files; clone blobs and fake_default_* are
        // supplied by Lua or the engine. Blob variable names are case-sensitive.
        var blobs = BlobFiles.Where(pair => Regex.IsMatch(body,
                @"(?:blob|seqovl_pattern|pattern|fallback)=" + Regex.Escape(pair.Key) + @"(?=[:\s]|$)"))
            .Select(pair => "--blob=" + pair.Key + ":@" + QuotePath(engine, pair.Value));
        var lua = new[] { "zapret-lib.lua", "zapret-antidpi.lua", "zapret-auto.lua" }
            .Select(file => "--lua-init=@" + QuotePath(bundle, file));
        return capture + " --wf-tcp-empty=0 --ctrack-disable=0 --wf-priority=10 --wf-raw-filter=\"" +
            string.Join(" or ", scopes) + "\" " + string.Join(" ", lua) + " " + string.Join(" ", blobs) + " " + body;
    }

    private static void AddDiscordWebRules(List<string> rules, string profiles,
        StrategySelection<ServiceStrategy> selection, string exclusions)
    {
        var native = selection.BuiltIn == ServiceStrategy.Zapret2DiscordExp ||
            selection.BuiltIn == ServiceStrategy.Zapret2DiscordCircular;
        var imported = selection.Imported;
        var hosts = HostList(profiles, "discord.txt") + exclusions;
        rules.Add(WebRule("discord-quic", "udp", "443", "quic", hosts, imported == null ?
            Quic(ServiceQuicRepeats(selection.BuiltIn, false)) : imported.Quic));
        rules.Add(WebRule("discord-http", "tcp", "80", "http", hosts, imported == null ? StandardHttp : imported.Web));
        if (native)
        {
            rules.Add(ReadRules(profiles, "service-zapret2", selection.BuiltIn.ToString())
                .Replace("{media-ports}", Engine.DiscordTlsPorts).Replace("{exclude}", exclusions));
            return;
        }
        var tls = imported == null ? ServiceTls(selection.BuiltIn, true, false) : imported.Web;
        var media = imported == null ? ServiceTls(selection.BuiltIn, false, false) : imported.Media;
        rules.Add(WebRule("discord-tls", "tcp", "443", "tls", hosts, tls));
        rules.Add(WebRule("discord-media-tls", "tcp", Engine.DiscordTlsPorts, "tls",
            " --hostlist-domains=discord.media" + exclusions, media));
    }

    private static void AddYoutubeRules(List<string> rules, string profiles,
        StrategySelection<ServiceStrategy> selection, string exclusions)
    {
        var native = selection.BuiltIn == ServiceStrategy.Zapret2YoutubeCircular ||
            selection.BuiltIn == ServiceStrategy.Zapret2YoutubeExp;
        var imported = selection.Imported;
        var core = HostList(profiles, "youtube-core.txt") + exclusions;
        var other = HostList(profiles, "youtube.txt") + exclusions;
        var both = HostList(profiles, "youtube-core.txt") + HostList(profiles, "youtube.txt") + exclusions;
        if (native)
            rules.Add(ReadRules(profiles, "service-zapret2", selection.BuiltIn.ToString()).Replace("{exclude}", exclusions));
        else if (imported != null)
            rules.Add(WebRule("youtube-tls", "tcp", "443", "tls", both, imported.Google));
        else
        {
            rules.Add(WebRule("youtube-core-tls", "tcp", "443", "tls", core, ServiceTls(selection.BuiltIn, false, true)));
            rules.Add(WebRule("youtube-other-tls", "tcp", "443", "tls", other, WithIpId(GoogleSplit)));
        }
        if (imported != null)
            rules.Add(WebRule("youtube-quic", "udp", "443", "quic", both, imported.IpQuic));
        else
        {
            rules.Add(WebRule("youtube-core-quic", "udp", "443", "quic", core,
                Quic(ServiceQuicRepeats(native ? ServiceStrategy.Standard : selection.BuiltIn, true))));
            rules.Add(WebRule("youtube-other-quic", "udp", "443", "quic", other, Quic(6)));
        }
        rules.Add(WebRule("youtube-http", "tcp", "80", "http", imported == null ? other : both,
            imported == null ? StandardHttp : imported.Web));
    }

    private static int ServiceQuicRepeats(ServiceStrategy strategy, bool youtube)
    {
        return strategy == ServiceStrategy.TimestampOverlap || strategy == ServiceStrategy.BadSequence ||
            (youtube && strategy == ServiceStrategy.Standard) ? 11 : 6;
    }

    private static string ServiceTls(ServiceStrategy strategy, bool discord, bool zeroIpId)
    {
        string recipe;
        switch (strategy)
        {
            case ServiceStrategy.Standard:
                recipe = discord || !zeroIpId ? StandardTls : BadSequenceTls(); break;
            case ServiceStrategy.BadSequence: recipe = BadSequenceTls(); break;
            case ServiceStrategy.TimestampFake:
                recipe = "--payload=tls_client_hello --lua-desync=fake:blob=tls_google:tcp_ts=-600000:repeats=6"; break;
            case ServiceStrategy.HostSplit:
                // Upstream's native hostfakesplit replaces winws1 altorder=1.
                recipe = "--payload=tls_client_hello --lua-desync=" + DefaultTlsFake + ":tcp_ts=-600000 " +
                    "--lua-desync=hostfakesplit:host=www.google.com:tcp_ts=-600000"; break;
            case ServiceStrategy.TimestampOverlap:
                recipe = discord ? "--payload=tls_client_hello --lua-desync=fake:blob=stun2:tcp_ts=-600000:repeats=8 " +
                    "--lua-desync=fake:blob=tls_sferum:tcp_ts=-600000:repeats=8 " +
                    "--lua-desync=multisplit:pos=1:seqovl=661:seqovl_pattern=tls_sferum" :
                    GoogleSplit.Replace("--lua-desync=", "--lua-desync=fake:blob=tls_google:tcp_ts=-600000:repeats=8 --lua-desync="); break;
            case ServiceStrategy.SimpleFake:
                recipe = discord ? "--payload=tls_client_hello --lua-desync=fake:blob=stun2:tcp_ts=-600000:repeats=6 " +
                    "--lua-desync=fake:blob=tls_sferum:tcp_ts=-600000:repeats=6" :
                    "--payload=tls_client_hello --lua-desync=fake:blob=tls_google:tcp_ts=-600000:repeats=6"; break;
            case ServiceStrategy.SniSplit:
                recipe = "--payload=tls_client_hello --lua-desync=multisplit:pos=2,sniext+1:seqovl=679:seqovl_pattern=tls_google"; break;
            case ServiceStrategy.AlternateTls:
                recipe = GoogleSplit.Replace("--lua-desync=", "--lua-desync=" + DefaultTlsFake +
                    ":tcp_ts=-600000:repeats=8 --lua-desync="); break;
            default: throw new ArgumentOutOfRangeException("strategy");
        }
        return zeroIpId ? WithIpId(recipe) : recipe;
    }

    private static string BadSequenceTls()
    {
        return GoogleSplit.Replace("--lua-desync=", "--lua-desync=" + DefaultTlsFake +
            ":tcp_seq=10000000:tcp_ack=-66000:repeats=8 --lua-desync=");
    }
    private static string WithIpId(string recipe)
    {
        return Regex.Replace(recipe, @"--lua-desync=\S+", match => match.Value + ":ip_id=zero");
    }

    private static void AddGameWebRules(List<string> rules, string profiles, string game,
        Strategy strategy, string exclusions, bool spaceMarine)
    {
        var hosts = HostList(profiles, game + ".txt") + exclusions;
        rules.Add(WebRule(game + "-tls", "tcp", "443", "tls", hosts, GameTls(profiles, strategy).Replace("{key}", game + "-tls")));
        rules.Add(WebRule(game + "-quic", "udp", "443", "quic", hosts, Quic(6)));
        if (!spaceMarine)
            rules.Add(WebRule(game + "-http", "tcp", "80", "http", hosts, StandardHttp));
        else
            rules.Add(WebRule(game + "-extra-tls", "tcp", Engine.SpaceMarineTcpPorts, "tls",
                IpSet(profiles, Engine.SpaceMarineSharedIps) + IpSet(profiles, "space-marine-2-tcp-ip.txt") + exclusions,
                GameTls(profiles, strategy).Replace("{key}", game + "-extra-tls")));
    }
    private static string GameTls(string profiles, Strategy strategy)
    {
        switch (strategy)
        {
            case Strategy.TlsSmallOverlap:
                return "--payload=tls_client_hello --lua-desync=multisplit:pos=1,midsld:seqovl=1";
            case Strategy.TlsMd5Disorder:
                // winws1 chose the compatible TLS marker midsld from 1,midsld.
                return "--payload=tls_client_hello --lua-desync=fakeddisorder:pos=midsld:repeats=16:tcp_md5";
            case Strategy.Zapret2TlsClone:
            case Strategy.Zapret2TlsCircular: return ReadRules(profiles, "game-tls-zapret2", strategy.ToString());
            default: return StandardTls;
        }
    }

    private static string GameUdpRule(string profiles, string name, string ports, Strategy strategy, string ipsets)
    {
        string recipe;
        switch (strategy)
        {
            case Strategy.None: throw new ArgumentOutOfRangeException("strategy");
            case Strategy.Standard:
            case Strategy.TlsSmallOverlap:
            case Strategy.TlsMd5Disorder:
            case Strategy.Zapret2TlsClone:
            case Strategy.Zapret2TlsCircular: recipe = GameFake("stun", 1, 1); break;
            case Strategy.Alternate: recipe = GameFake("stun", 2, 3); break;
            case Strategy.FlowAlt2: recipe = GameFake("active_game", 12, 1); break;
            case Strategy.FlowAlt3: recipe = GameFake("active_game", 10, 3); break;
            case Strategy.FlowExp: recipe = GameFake("quic_4pda,active_game", 5, 3); break;
            case Strategy.FlowSimpleAlt2: recipe = GameFake("active_game", 12, 2); break;
            case Strategy.Combined: recipe = GameFake("stun,active_game", 4, 3); break;
            case Strategy.FlowAlt12: recipe = GameFake("quic_dbank", 10, 3); break;
            case Strategy.CommunityAlt10:
                // Lua adds the signed delta; winws1's unsigned 2 meant two hops before.
                recipe = GameFake("quic_google", 12, 1, ":ip_autottl=-2,3-20:ip6_autottl=-2,3-20"); break;
            case Strategy.ActiveGameShortTen: recipe = GameFake("active_game", 10, 1); break;
            case Strategy.ActiveGameMediumFourteen: recipe = GameFake("active_game", 14, 2); break;
            case Strategy.ActiveGameMediumTen: recipe = GameFake("active_game", 10, 2); break;
            case Strategy.ActiveGameSix: recipe = GameFake("active_game", 6, 4); break;
            case Strategy.Zapret2Circular: // Saved ID alias for ActiveGameEight.
            case Strategy.ActiveGameEight: recipe = GameFake("active_game", 8, 3); break;
            case Strategy.ActiveGameFour: recipe = GameFake("active_game", 4, 4); break;
            case Strategy.Zapret2Exp:
            case Strategy.Zapret2DoubleFake: recipe = ReadRules(profiles, "game-zapret2", strategy.ToString()); break;
            case Strategy.DbankShort: recipe = GameFake("quic_dbank", 10, 1); break;
            case Strategy.ActiveGameMinimal: recipe = GameFake("active_game", 2, 1); break;
            case Strategy.FlowsealLongTen: recipe = GameFake("active_game", 10, 4); break;
            case Strategy.FlowsealLongFourteen: recipe = GameFake("active_game", 14, 3); break;
            case Strategy.FlowsealDualFour: recipe = GameFake("quic_4pda,active_game", 5, 4); break;
            default: throw new ArgumentOutOfRangeException("strategy");
        }
        return "--name=" + name + " --filter-udp=" + ports + ipsets + " " + recipe;
    }
    private static string GameFake(string blobs, int repeats, int packets, string fooling = "")
    {
        return "--out-range=-n" + packets.ToString(CultureInfo.InvariantCulture) + " --payload=all " +
            string.Join(" ", blobs.Split(',').Select(blob => "--lua-desync=fake:blob=" + blob +
                ":repeats=" + repeats.ToString(CultureInfo.InvariantCulture) + ":payload=all" + fooling));
    }

    private static void AddWarpRules(List<string> rules, string profiles, WarpStrategy strategy)
    {
        if (strategy >= WarpStrategy.Circular && strategy <= WarpStrategy.DoubleFakeTimestamp)
            rules.Add(ReadRules(profiles, "warp-zapret2", strategy.ToString()));
        else
        {
            var hosts = HostList(profiles, "warp.txt");
            var endpoints = IpSet(profiles, MasqueIps) + IpSet(profiles, ApiIps);
            if (strategy == WarpStrategy.MasqueTls)
                rules.Add(WebRule("warp-masque-tls", "tcp", "443", "tls", IpSet(profiles, MasqueIps),
                    "--payload=tls_client_hello --lua-desync=fake:blob=tls_google:tcp_ts=-600000:repeats=6"));
            rules.Add(WebRule("warp-domain-tls", "tcp", "443", "tls", hosts, StandardTls));
            rules.Add(WebRule("warp-api-masque-tls", "tcp", "443", "tls", endpoints, StandardTls));
            rules.Add(WebRule("warp-masque-quic", "udp", MasquePorts, "quic", IpSet(profiles, MasqueIps),
                "--out-range=-n7 " + Quic(strategy == WarpStrategy.QuicBurst ? 11 : 6)));
            rules.Add(WebRule("warp-domain-quic", "udp", "443", "quic", hosts, "--out-range=-n7 " + Quic(6)));
        }
        rules.Add(WebRule("warp-wireguard", "udp", WireguardPorts, "wireguard", IpSet(profiles, "warp-wireguard.txt"),
            "--out-range=-n1 --payload=wireguard_initiation --lua-desync=fake:blob=fake_default_wireguard"));
    }
    private static string Quic(int repeats)
    {
        return "--payload=quic_initial --lua-desync=fake:blob=quic_google:repeats=" + repeats.ToString(CultureInfo.InvariantCulture);
    }
    private static string WebRule(string name, string transport, string ports, string l7, string filters, string recipe)
    {
        return "--name=" + name + " --filter-" + transport + "=" + ports + " --filter-l7=" + l7 + filters + " " + recipe;
    }
    private static string ReadRules(string profiles, string folder, string name)
    {
        var rules = Engine.ReadProfile(Path.Combine(profiles, folder, name + ".args"));
        if (string.IsNullOrWhiteSpace(rules)) throw new InvalidDataException("Пустой профиль zapret2: " + name);
        return rules.Replace("{profiles}", profiles.Replace(Path.DirectorySeparatorChar, '/'));
    }
    private static string QuotePath(string directory, string file)
    {
        return "\"" + Path.Combine(directory, file).Replace(Path.DirectorySeparatorChar, '/') + "\"";
    }
    private static string HostList(string profiles, string file) { return " --hostlist=" + QuotePath(profiles, file); }
    private static string HostExclude(string profiles, string file) { return " --hostlist-exclude=" + QuotePath(profiles, file); }
    private static string IpSet(string profiles, string file) { return " --ipset=" + QuotePath(profiles, file); }
    private static string IpExclude(string profiles, string file) { return " --ipset-exclude=" + QuotePath(profiles, file); }
    private static string Ports(IEnumerable<string> groups) { return string.Join(",", groups.SelectMany(p => p.Split(',')).Distinct()); }
    private static void AddUdpCapture(List<string> scopes, List<string> ports, string group, string[] ranges, bool incoming)
    {
        ports.Add(group);
        var field = incoming ? "Src" : "Dst";
        scopes.Add("(" + (incoming ? "inbound" : "outbound") + " and udp and " + MatchPorts(group, "udp." + field + "Port") +
            (ranges == null ? "" : " and (" + string.Join(" or ", ranges.Select(range => MatchRange(range, field + "Addr"))) + ")") + ")");
    }
    private static string MatchPorts(string ports, string field)
    {
        return "(" + string.Join(" or ", ports.Split(',').Select(port => {
            var bounds = port.Split('-');
            return bounds.Length == 1 ? field + " == " + port :
                "(" + field + " >= " + bounds[0] + " and " + field + " <= " + bounds[1] + ")";
        })) + ")";
    }
    private static string[] ReadRanges(string profiles, params string[] files)
    {
        var ranges = files.SelectMany(file => File.ReadLines(Path.Combine(profiles, file)))
            .Select(line => line.Split('#')[0].Trim()).Where(line => line.Length != 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (ranges.Length == 0) throw new InvalidDataException("В профиле zapret2 отсутствуют адреса: " + string.Join(", ", files));
        return ranges;
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
