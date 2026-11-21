using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

internal enum Strategy
{
    Standard, Alternate, FlowAlt2, FlowAlt3, FlowExp, FlowSimpleAlt2, Combined, FlowAlt12, CommunityAlt10,
    ActiveGameShortTen, ActiveGameMediumFourteen, ActiveGameMediumTen,
    ActiveGameSix, ActiveGameEight, ActiveGameFour, None,
    Zapret2Circular, Zapret2Exp, Zapret2DoubleFake
}
internal enum ServiceStrategy
{
    Standard, BadSequence, TimestampFake, HostSplit, TimestampOverlap, SimpleFake, SniSplit, None, LegacyAlternateTls,
    Zapret2DiscordExp, Zapret2DiscordCircular, Zapret2YoutubeCircular, Zapret2YoutubeExp
}
internal enum WarpStrategy
{
    Standard, QuicBurst, MasqueTls, None,
    Circular, CircularStun, DoubleFakeSequence, DoubleFakeSteam, DoubleFakeTimestamp
}

internal sealed class Engine : IDisposable
{
    private const string Standard = "--dpi-desync=multisplit --dpi-desync-split-pos=1 --dpi-desync-split-seqovl=568 --dpi-desync-split-seqovl-pattern=tls_clienthello_4pda_to.bin";
    private const string DiscordAlternateTls = "--dpi-desync=fake,multisplit --dpi-desync-split-pos=1 --dpi-desync-split-seqovl=681 --dpi-desync-split-seqovl-pattern=tls_clienthello_www_google_com.bin --dpi-desync-fooling=ts --dpi-desync-repeats=8 --dpi-desync-fake-tls-mod=rnd,dupsid,sni=www.google.com";
    private const string BadSequence = "--dpi-desync=fake,multisplit --dpi-desync-split-seqovl=681 --dpi-desync-split-pos=1 --dpi-desync-fooling=badseq --dpi-desync-badseq-increment=10000000 --dpi-desync-repeats=8 --dpi-desync-split-seqovl-pattern=tls_clienthello_www_google_com.bin --dpi-desync-fake-tls-mod=rnd,dupsid,sni=www.google.com";
    private const string TimestampFake = "--dpi-desync=fake --dpi-desync-repeats=6 --dpi-desync-fooling=ts --dpi-desync-fake-tls=tls_clienthello_www_google_com.bin --dpi-desync-fake-tls-mod=none";
    private const string HostSplit = "--dpi-desync=fake,hostfakesplit --dpi-desync-fake-tls-mod=rnd,dupsid,sni=www.google.com --dpi-desync-hostfakesplit-mod=host=www.google.com,altorder=1 --dpi-desync-fooling=ts";
    private const string TimestampOverlap = "--dpi-desync=fake,multisplit --dpi-desync-split-seqovl=681 --dpi-desync-split-pos=1 --dpi-desync-fooling=ts --dpi-desync-repeats=8 --dpi-desync-split-seqovl-pattern=tls_clienthello_www_google_com.bin --dpi-desync-fake-tls=tls_clienthello_www_google_com.bin";
    private const string DiscordTimestampOverlap = "--dpi-desync=fake,multisplit --dpi-desync-split-seqovl=664 --dpi-desync-split-pos=1 --dpi-desync-fooling=ts --dpi-desync-repeats=8 --dpi-desync-split-seqovl-pattern=tls_clienthello_www_sferum_ru.bin --dpi-desync-fake-tls=stun2.bin --dpi-desync-fake-tls=tls_clienthello_www_sferum_ru.bin";
    private const string SimpleFake = "--dpi-desync=fake --dpi-desync-repeats=6 --dpi-desync-fooling=ts --dpi-desync-fake-tls=tls_clienthello_www_google_com.bin";
    private const string DiscordSimpleFake = "--dpi-desync=fake --dpi-desync-repeats=6 --dpi-desync-fooling=ts --dpi-desync-fake-tls=stun2.bin --dpi-desync-fake-tls=tls_clienthello_www_sferum_ru.bin";
    private const string SniSplit = "--dpi-desync=multisplit --dpi-desync-split-pos=2,sniext+1 --dpi-desync-split-seqovl=679 --dpi-desync-split-seqovl-pattern=tls_clienthello_www_google_com.bin";
    private const string GamePayload = "--dpi-desync-fake-unknown-udp=ACTIVE_GAME_UDP.bin";
    internal const string DarktideUdpPorts = "35500-35650,35670";
    private const string SpaceMarineTcpPorts = "11700-11720";
    internal const string SpaceMarineUdpPorts = "48800-55000";
    private const string SpaceMarineHosts = "--hostlist=../profiles/space-marine-2.txt";
    internal const string SpaceMarineSharedIps = "space-marine-2-ip.txt";
    internal const string SpaceMarineUdpIps = "space-marine-2-udp-ip.txt";
    private const string SpaceMarineSharedNetworks = "--ipset=../profiles/" + SpaceMarineSharedIps;
    private const string SpaceMarineTcpNetworks = SpaceMarineSharedNetworks + " --ipset=../profiles/space-marine-2-tcp-ip.txt";
    private const string SpaceMarineUdpNetworks = SpaceMarineSharedNetworks + " --ipset=../profiles/" + SpaceMarineUdpIps;
    internal const string DiscordVoiceOverlap = "50000-50100";
    private const string DiscordVoicePorts = "19294-19344," + DiscordVoiceOverlap;
    internal const string DiscordTlsPorts = "2053,2083,2087,2096,8443";
    private const string WarpUdpPorts = "500,1701,2408,4500,4443,8443,8095";
    private static readonly string Root = AppDomain.CurrentDomain.BaseDirectory;
    private static readonly string DirectoryPath = Path.Combine(Root, "engine");
    private static readonly string RuntimeRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Omnissiah Breakwall", "engine");
    private static readonly string Zapret2Directory = Path.Combine(DirectoryPath, "zapret2");
    private const string LegacyExecutable = "winws.exe";
    private readonly Queue<string> output = new Queue<string>();
    private readonly List<Process> processes = new List<Process>();
    private SafeFileHandle job;

    public bool Running { get { return processes.Count != 0 && processes.All(process => !process.HasExited); } }

    public string Details
    {
        get { lock (output) return string.Join(Environment.NewLine, output.ToArray()); }
    }

    public bool Start(StrategySelection<Strategy> darktide, StrategySelection<WarpStrategy> warp,
        StrategySelection<ServiceStrategy> discord, StrategySelection<ServiceStrategy> youtube,
        StrategySelection<Strategy> spaceMarine, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var arguments = Arguments(darktide, warp, discord, youtube, spaceMarine);
        var zapret2 = Zapret2.Arguments(Path.Combine(Root, "profiles"), warp.BuiltIn, darktide.BuiltIn,
            spaceMarine.BuiltIn, DiscordEnabled(discord), discord.BuiltIn, youtube.BuiltIn);
        if (arguments.Length == 0 && zapret2 == null) return false;

        foreach (var name in new[] { "winws", "winws2" })
        {
            var others = Process.GetProcessesByName(name);
            foreach (var other in others) other.Dispose();
            if (others.Length > 0)
                throw new InvalidOperationException("Уже запущен другой zapret (" + name + "). Закройте его перед запуском этой сборки.");
        }

        lock (output) output.Clear();
        try
        {
            cancellation.ThrowIfCancellationRequested();
            job = CreateKillJob();
            if (arguments.Length != 0) StartProcess(Info(LegacyExecutable, arguments), cancellation);
            if (zapret2 != null)
            {
                StartProcess(Info(Zapret2.Executable, zapret2), cancellation);
                if (!Running) throw new InvalidOperationException("Процесс сетевого фильтра завершился при запуске.");
            }
        }
        catch
        {
            Dispose();
            throw;
        }
        return true;
    }

    private void StartProcess(ProcessStartInfo info, CancellationToken cancellation)
    {
        var child = new Process { StartInfo = info };
        child.OutputDataReceived += Receive;
        child.ErrorDataReceived += Receive;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            child.Start();
            processes.Add(child);
            if (!AssignProcessToJobObject(job, child.Handle))
                throw new System.ComponentModel.Win32Exception();
            child.BeginOutputReadLine();
            child.BeginErrorReadLine();
            if (WaitForExit(child, 900, cancellation))
            {
                child.WaitForExit();
                throw new InvalidOperationException(Path.GetFileName(info.FileName) +
                    " завершился при запуске. Код " + child.ExitCode + ".");
            }
        }
        catch
        {
            if (!processes.Contains(child)) child.Dispose();
            throw;
        }
    }

    internal static bool WaitForExit(Process process, int timeout, CancellationToken cancellation)
    {
        if (!cancellation.CanBeCanceled) return process.WaitForExit(timeout);

        var wait = Stopwatch.StartNew();
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var remaining = timeout - (int)wait.ElapsedMilliseconds;
            if (remaining <= 0) return process.HasExited;
            if (process.WaitForExit(Math.Min(remaining, 40))) return true;
        }
    }

    public static int Validate(Strategy strategy, bool warp,
        ServiceStrategy discord = ServiceStrategy.Standard, ServiceStrategy youtube = ServiceStrategy.Standard,
        Strategy spaceMarine = Strategy.None)
    {
        var warpStrategy = warp ? WarpStrategy.Standard : WarpStrategy.None;
        var discordSelection = new StrategySelection<ServiceStrategy>(discord);
        var arguments = Arguments(new StrategySelection<Strategy>(strategy),
            new StrategySelection<WarpStrategy>(warpStrategy), discordSelection,
            new StrategySelection<ServiceStrategy>(youtube), new StrategySelection<Strategy>(spaceMarine));
        var native = Zapret2.Arguments(Path.Combine(Root, "profiles"), warpStrategy, strategy,
            spaceMarine, DiscordEnabled(discordSelection), discord, youtube);
        var result = arguments.Length == 0 ? 0 : ValidateProcess(LegacyExecutable, arguments);
        return result == 0 && native != null ? ValidateProcess(Zapret2.Executable, native) : result;
    }

    private static int ValidateProcess(string executable, string arguments)
    {
        using (var child = new Process { StartInfo = Info(executable, "--dry-run " + arguments) })
        {
            child.OutputDataReceived += (sender, args) => { if (args.Data != null) Console.WriteLine(args.Data); };
            child.ErrorDataReceived += (sender, args) => { if (args.Data != null) Console.Error.WriteLine(args.Data); };
            child.Start();
            child.BeginOutputReadLine();
            child.BeginErrorReadLine();
            if (!child.WaitForExit(8000))
            {
                child.Kill();
                return 1;
            }
            child.WaitForExit();
            return child.ExitCode;
        }
    }

    private static ProcessStartInfo Info(string executable, string arguments)
    {
        var bundle = executable == Zapret2.Executable ? Zapret2Directory : DirectoryPath;
        var sources = new[] {
            Path.Combine(bundle, executable), Path.Combine(DirectoryPath, "cygwin1.dll"),
            Path.Combine(bundle, "WinDivert.dll"), Path.Combine(DirectoryPath, "WinDivert64.sys")
        };
        var runtime = PrepareRuntime(sources);
        return new ProcessStartInfo(Path.Combine(runtime, executable), arguments) {
            WorkingDirectory = bundle, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
    }

    private static string PrepareRuntime(string[] sources)
    {
        using (var sha = SHA256.Create())
        {
            var hashes = sources.ToDictionary(source => source, source => FileHash(sha, source));
            var manifest = Encoding.ASCII.GetBytes(string.Concat(sources.Select(source => hashes[source])));
            var directory = Path.Combine(RuntimeRoot, BitConverter.ToString(sha.ComputeHash(manifest)).Replace("-", ""));
            Directory.CreateDirectory(directory);
            foreach (var source in sources)
            {
                var target = Path.Combine(directory, Path.GetFileName(source));
                if (File.Exists(target) && FileHash(sha, target) == hashes[source]) continue;

                var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.Copy(source, temporary);
                    if (FileHash(sha, temporary) != hashes[source])
                        throw new IOException("Файл сетевого фильтра изменился при копировании: " + Path.GetFileName(source));
                    if (File.Exists(target)) File.Replace(temporary, target, null);
                    else File.Move(temporary, target);
                }
                finally { File.Delete(temporary); }
            }
            return directory;
        }
    }

    private static string FileHash(SHA256 sha, string path)
    {
        using (var stream = File.OpenRead(path))
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
    }

    private static string Arguments(StrategySelection<Strategy> darktideSelection, StrategySelection<WarpStrategy> warpSelection,
        StrategySelection<ServiceStrategy> discordSelection, StrategySelection<ServiceStrategy> youtubeSelection,
        StrategySelection<Strategy> spaceMarineSelection)
    {
        var profiles = Path.Combine(Root, "profiles");
        var rules = new List<string>();
        var tcp = new List<string>();
        var udp = new List<string>();
        var darktide = darktideSelection.BuiltIn;
        var spaceMarine = spaceMarineSelection.BuiltIn;
        var discord = discordSelection.BuiltIn;
        var youtube = youtubeSelection.BuiltIn;
        var warp = warpSelection.BuiltIn;
        var discordDs = discordSelection.Imported;
        var youtubeDs = youtubeSelection.Imported;
        var darktideEnabled = darktide != Strategy.None;
        var spaceMarineEnabled = spaceMarine != Strategy.None;
        var discordEnabled = DiscordEnabled(discordSelection);
        var useDiscordAlternateTls = discord == ServiceStrategy.LegacyAlternateTls;
        var youtubeEnabled = youtubeDs != null || youtube != ServiceStrategy.None;
        var warpEnabled = warp != WarpStrategy.None;
        var warpZapret2 = Zapret2.UsesWarp(warp);
        var spaceMarineZapret2 = Zapret2.UsesGame(spaceMarine);
        var discordZapret2 = Zapret2.UsesDiscord(discord);
        var youtubeZapret2 = Zapret2.UsesYoutube(youtube);

        if (darktideEnabled || discordEnabled || youtubeEnabled) tcp.Add("80");
        if (darktideEnabled || spaceMarineEnabled || warpEnabled ||
            (discordEnabled && !discordZapret2) || (youtubeEnabled && !youtubeZapret2))
            tcp.Add("443");
        if (darktideEnabled || spaceMarineEnabled || discordEnabled || youtubeEnabled || warpEnabled)
            udp.Add("443");
        if (warpEnabled) udp.Add(WarpUdpPorts);

        var hosts = new List<string>();
        const string darktideHosts = "--hostlist=../profiles/darktide.txt";
        const string discordHosts = "--hostlist=../profiles/discord.txt";
        if (discordEnabled && discordDs == null && !useDiscordAlternateTls) hosts.Add(discordHosts);
        if (darktideEnabled)
        {
            hosts.Add(darktideHosts);
            if (!Zapret2.UsesGame(darktide))
            {
                udp.Add(DarktideUdpPorts);
                rules.Add(GameUdpRule(DarktideUdpPorts, GameUdpOptions(darktide)));
            }
        }

        if (discordEnabled)
        {
            if (!discordZapret2) tcp.Add(DiscordTlsPorts);
            udp.Add(spaceMarineEnabled && !spaceMarineZapret2 ? "19294-19344" : DiscordVoicePorts);
            var exclusion = darktideEnabled ? " --hostlist-exclude=../profiles/darktide.txt" : "";
            if (discordDs != null)
            {
                var voice = spaceMarineEnabled ? discordDs.Voice.Replace("discord,stun,unknown", "discord,stun") : discordDs.Voice;
                rules.Add("--filter-udp=" + DiscordVoicePorts + " " + voice);
                rules.Add("--filter-tcp=" + DiscordTlsPorts + " --filter-l7=tls --hostlist-domains=discord.media " + discordDs.Media);
                AddDsWebRules(rules, discordHosts + exclusion, discordDs);
            }
            else if (discordZapret2)
            {
                rules.Add(WithoutTls(ReadProfile(Path.Combine(profiles, "discord-base.args"))));
            }
            else
            {
                if (discord != ServiceStrategy.Standard && !useDiscordAlternateTls)
                    rules.Add(ServiceRules(Path.Combine(profiles, "discord-alt.args"), discord)
                        .Replace("{darktide-exclusion}", exclusion));
                var discordTls = useDiscordAlternateTls ? DiscordAlternateTls : Standard;
                rules.Add(ReadProfile(Path.Combine(profiles, "discord-base.args")).Replace("{tls}", discordTls));
            }
        }

        if (hosts.Count != 0 || useDiscordAlternateTls)
        {
            var webRules = ReadProfile(Path.Combine(profiles, "base.args"));
            if (useDiscordAlternateTls)
                rules.Add(webRules.Replace("{hosts}", discordHosts).Replace("{tls}", DiscordAlternateTls));
            if (hosts.Count != 0)
            {
                var sharedHosts = string.Join(" ", hosts);
                var sharedRules = webRules.Replace("{hosts}", sharedHosts).Replace("{tls}", Standard);
                if (discordZapret2)
                    sharedRules = MapTls(sharedRules, rule => darktideEnabled ? rule.Replace(sharedHosts, darktideHosts) : null);
                rules.Add(sharedRules);
            }
        }
        if (warpEnabled)
        {
            if (warp == WarpStrategy.MasqueTls)
                rules.Add("--filter-tcp=443 --filter-l7=tls --ipset=../profiles/warp-masque.txt " + TimestampFake);
            var warpRules = ReadProfile(Path.Combine(profiles, "warp.args")).Replace("{tls}", Standard)
                .Replace("{masque-quic-repeats}", warp == WarpStrategy.QuicBurst ? "11" : "6");
            rules.Add(warpZapret2 ? Zapret2.LegacyFallback(warpRules) : warpRules);
        }
        if (youtubeEnabled)
        {
            var youtubeRules = new List<string>();
            if (youtubeDs != null)
                AddDsYouTubeRules(youtubeRules, youtubeDs);
            else
                youtubeRules.Add(ServiceRules(Path.Combine(profiles, "youtube.args"),
                    youtubeZapret2 ? ServiceStrategy.Standard : youtube));
            var selectedRules = string.Join(" --new ", youtubeRules);
            if (youtubeZapret2)
                selectedRules = WithoutTls(selectedRules);
            else if (discordZapret2)
                selectedRules = MapTls(selectedRules, rule => rule + " --hostlist-exclude=../profiles/discord.txt");
            rules.Add(selectedRules);
        }
        if (spaceMarineEnabled)
        {
            tcp.Add(SpaceMarineTcpPorts);
            if (!spaceMarineZapret2)
            {
                udp.Add(SpaceMarineUdpPorts);
                // Existing service rules take priority, including Discord voice on overlapping UDP ports.
                rules.Add(GameUdpRule(SpaceMarineUdpPorts, SpaceMarineUdpNetworks + " " + GameUdpOptions(spaceMarine)));
            }
            rules.Add("--filter-tcp=" + SpaceMarineTcpPorts + " --filter-l7=tls " + SpaceMarineTcpNetworks + " " + Standard);
            rules.Add("--filter-tcp=443 --filter-l7=tls " + SpaceMarineHosts + " " + Standard);
            rules.Add("--filter-udp=443 --filter-l7=quic " + SpaceMarineHosts +
                " --dpi-desync=fake --dpi-desync-repeats=6 --dpi-desync-fake-quic=quic_initial_www_google_com.bin");
        }

        if (rules.Count == 0) return "";
        var profilesArgument = string.Join(" --new ", rules);
        return "--wf-tcp=" + string.Join(",", tcp) + " --wf-udp=" + string.Join(",", udp) +
            " " + (warpZapret2 ? Zapret2.ExcludeWarpEndpoints(profilesArgument) : profilesArgument);
    }

    private static string WithoutTls(string rules)
    {
        return MapTls(rules, rule => null);
    }

    private static string MapTls(string rules, Func<string, string> transform)
    {
        const string next = " --new ";
        return string.Join(next, rules.Split(new[] { next }, StringSplitOptions.None)
            .Select(rule => rule.Contains(" --filter-l7=tls ") ? transform(rule) : rule)
            .Where(rule => rule != null));
    }

    private static bool DiscordEnabled(StrategySelection<ServiceStrategy> discord)
    {
        return discord.Imported != null || discord.BuiltIn != ServiceStrategy.None;
    }

    private static void AddDsWebRules(List<string> rules, string hosts, DsStrategy profile)
    {
        rules.Add("--filter-udp=443 --filter-l7=quic " + hosts + " " + profile.Quic);
        rules.Add("--filter-tcp=443 --filter-l7=tls " + hosts + " " + profile.Web);
        rules.Add("--filter-tcp=80 --filter-l7=http " + hosts + " " + profile.Web);
    }

    private static void AddDsYouTubeRules(List<string> rules, DsStrategy profile)
    {
        const string hosts = "--hostlist=../profiles/youtube-core.txt --hostlist=../profiles/youtube.txt";
        rules.Add("--filter-tcp=443 --filter-l7=tls " + hosts + " " + profile.Google);
        rules.Add("--filter-udp=443 --filter-l7=quic " + hosts + " " + profile.IpQuic);
        rules.Add("--filter-tcp=80 --filter-l7=http " + hosts + " " + profile.Web);
    }

    private static string GameUdpRule(string ports, string options)
    {
        return "--filter-udp=" + ports + " --dpi-desync=fake --dpi-desync-any-protocol=1 " + options;
    }

    private static string GameUdpOptions(Strategy strategy)
    {
        const string stun = "--dpi-desync-fake-unknown-udp=stun.bin";
        switch (strategy)
        {
            case Strategy.Standard:
                return stun + " --dpi-desync-cutoff=n2";
            case Strategy.Alternate:
                return stun + " --dpi-desync-repeats=2 --dpi-desync-cutoff=n4";
            case Strategy.FlowAlt2:
                return GamePayload + " --dpi-desync-repeats=12 --dpi-desync-cutoff=n2";
            case Strategy.FlowAlt3:
                return GamePayload + " --dpi-desync-repeats=10 --dpi-desync-cutoff=n4";
            case Strategy.FlowExp:
                return "--dpi-desync-fake-unknown-udp=quic_initial_4pda_to.bin " +
                    GamePayload + " --dpi-desync-repeats=5 --dpi-desync-cutoff=n4";
            case Strategy.FlowSimpleAlt2:
                return GamePayload + " --dpi-desync-repeats=12 --dpi-desync-cutoff=n3";
            case Strategy.Combined:
                return stun + " " + GamePayload + " --dpi-desync-repeats=4 --dpi-desync-cutoff=n4";
            case Strategy.FlowAlt12:
                return "--dpi-desync-fake-unknown-udp=quic_initial_dbankcloud_ru.bin --dpi-desync-repeats=10 --dpi-desync-cutoff=n4";
            case Strategy.CommunityAlt10:
                return "--dpi-desync-fake-unknown-udp=quic_initial_www_google_com.bin --dpi-desync-autottl=2 --dpi-desync-repeats=12 --dpi-desync-cutoff=n2";
            case Strategy.ActiveGameShortTen:
                return GamePayload + " --dpi-desync-repeats=10 --dpi-desync-cutoff=n2";
            case Strategy.ActiveGameMediumFourteen:
                return GamePayload + " --dpi-desync-repeats=14 --dpi-desync-cutoff=n3";
            case Strategy.ActiveGameMediumTen:
                return GamePayload + " --dpi-desync-repeats=10 --dpi-desync-cutoff=n3";
            // winws cutoff is exclusive, while the donor winws2 out-range is inclusive.
            case Strategy.ActiveGameSix:
                return GamePayload + " --dpi-desync-repeats=6 --dpi-desync-cutoff=n5";
            case Strategy.ActiveGameEight:
                return GamePayload + " --dpi-desync-repeats=8 --dpi-desync-cutoff=n4";
            case Strategy.ActiveGameFour:
                return GamePayload + " --dpi-desync-repeats=4 --dpi-desync-cutoff=n5";
            default:
                throw new ArgumentOutOfRangeException("strategy");
        }
    }

    private static string ServiceRules(string path, ServiceStrategy strategy)
    {
        string tls;
        string discordTls = null;
        var quicRepeats = "6";
        switch (strategy)
        {
            case ServiceStrategy.Standard:
            case ServiceStrategy.BadSequence:
                tls = BadSequence;
                quicRepeats = "11";
                break;
            case ServiceStrategy.TimestampFake:
                tls = TimestampFake;
                break;
            case ServiceStrategy.HostSplit:
                tls = HostSplit;
                break;
            case ServiceStrategy.TimestampOverlap:
                tls = TimestampOverlap;
                discordTls = DiscordTimestampOverlap;
                quicRepeats = "11";
                break;
            case ServiceStrategy.SimpleFake:
                tls = SimpleFake;
                discordTls = DiscordSimpleFake;
                break;
            case ServiceStrategy.SniSplit:
                tls = SniSplit;
                break;
            default:
                throw new ArgumentOutOfRangeException("strategy");
        }

        return ReadProfile(path).Replace("{service-tls}", tls)
            .Replace("{discord-tls}", discordTls ?? tls)
            .Replace("{quic-repeats}", quicRepeats);
    }

    internal static string ReadProfile(string path)
    {
        return string.Join(" ", File.ReadAllLines(path).Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith("#", StringComparison.Ordinal)));
    }

    private void Receive(object sender, DataReceivedEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.Data)) return;
        lock (output)
        {
            if (output.Count == 30) output.Dequeue();
            output.Enqueue(args.Data);
        }
    }

    public void Dispose()
    {
        if (job != null)
        {
            job.Dispose();
            job = null;
        }
        Exception failure = null;
        foreach (var process in processes)
        {
            try
            {
                if (process.WaitForExit(300)) continue;
                try { process.Kill(); }
                catch (InvalidOperationException) { if (!process.HasExited) throw; }
                if (!process.WaitForExit(2000))
                    throw new InvalidOperationException("Не удалось остановить процесс сетевого фильтра.");
            }
            catch (Exception error) { if (failure == null) failure = error; }
            finally { process.Dispose(); }
        }
        processes.Clear();
        if (failure != null) throw new InvalidOperationException(failure.Message, failure);
    }

    internal static SafeFileHandle CreateKillJob()
    {
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new System.ComponentModel.Win32Exception();
        }
        var limits = new ExtendedLimits();
        limits.Basic.Flags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        if (!SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf(typeof(ExtendedLimits))))
        {
            var error = new System.ComponentModel.Win32Exception();
            handle.Dispose();
            throw error;
        }
        return handle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcesses;
        public UIntPtr Affinity;
        public uint Priority, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int type, ref ExtendedLimits limits, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
}
