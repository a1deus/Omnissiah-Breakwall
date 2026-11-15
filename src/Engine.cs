using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

internal enum Strategy
{
    Standard, Alternate, FlowAlt2, FlowAlt3, FlowAlt7, FlowAlt10, FlowAlt11,
    FlowExp, FlowSimpleAlt2, FlowSimple, Combined, FlowAlt12, CommunityAlt10
}
internal enum ServiceStrategy { Standard, BadSequence, TimestampFake, HostSplit, TimestampOverlap, SimpleFake, SniSplit }

internal sealed class Engine : IDisposable
{
    private const string Standard = "--dpi-desync=multisplit --dpi-desync-split-pos=1 --dpi-desync-split-seqovl=568 --dpi-desync-split-seqovl-pattern=tls_clienthello_4pda_to.bin";
    private const string Alternate = "--dpi-desync=fake,multisplit --dpi-desync-split-pos=1 --dpi-desync-split-seqovl=681 --dpi-desync-split-seqovl-pattern=tls_clienthello_www_google_com.bin --dpi-desync-fooling=ts --dpi-desync-repeats=8 --dpi-desync-fake-tls-mod=rnd,dupsid,sni=www.google.com";
    private const string BadSequence = "--dpi-desync=fake,multisplit --dpi-desync-split-seqovl=681 --dpi-desync-split-pos=1 --dpi-desync-fooling=badseq --dpi-desync-badseq-increment=10000000 --dpi-desync-repeats=8 --dpi-desync-split-seqovl-pattern=tls_clienthello_www_google_com.bin --dpi-desync-fake-tls-mod=rnd,dupsid,sni=www.google.com";
    private const string TimestampFake = "--dpi-desync=fake --dpi-desync-repeats=6 --dpi-desync-fooling=ts --dpi-desync-fake-tls=tls_clienthello_www_google_com.bin --dpi-desync-fake-tls-mod=none";
    private const string HostSplit = "--dpi-desync=fake,hostfakesplit --dpi-desync-fake-tls-mod=rnd,dupsid,sni=www.google.com --dpi-desync-hostfakesplit-mod=host=www.google.com,altorder=1 --dpi-desync-fooling=ts";
    private const string TimestampOverlap = "--dpi-desync=fake,multisplit --dpi-desync-split-seqovl=681 --dpi-desync-split-pos=1 --dpi-desync-fooling=ts --dpi-desync-repeats=8 --dpi-desync-split-seqovl-pattern=tls_clienthello_www_google_com.bin --dpi-desync-fake-tls=tls_clienthello_www_google_com.bin";
    private const string DiscordTimestampOverlap = "--dpi-desync=fake,multisplit --dpi-desync-split-seqovl=664 --dpi-desync-split-pos=1 --dpi-desync-fooling=ts --dpi-desync-repeats=8 --dpi-desync-split-seqovl-pattern=tls_clienthello_www_sferum_ru.bin --dpi-desync-fake-tls=stun2.bin --dpi-desync-fake-tls=tls_clienthello_www_sferum_ru.bin";
    private const string SimpleFake = "--dpi-desync=fake --dpi-desync-repeats=6 --dpi-desync-fooling=ts --dpi-desync-fake-tls=tls_clienthello_www_google_com.bin";
    private const string DiscordSimpleFake = "--dpi-desync=fake --dpi-desync-repeats=6 --dpi-desync-fooling=ts --dpi-desync-fake-tls=stun2.bin --dpi-desync-fake-tls=tls_clienthello_www_sferum_ru.bin";
    private const string SniSplit = "--dpi-desync=multisplit --dpi-desync-split-pos=2,sniext+1 --dpi-desync-split-seqovl=679 --dpi-desync-split-seqovl-pattern=tls_clienthello_www_google_com.bin";
    private const string FlowAlt2Tls = "--dpi-desync=multisplit --dpi-desync-split-seqovl=652 --dpi-desync-split-pos=2 --dpi-desync-split-seqovl-pattern=tls_clienthello_www_google_com.bin";
    private const string FlowAlt12Tls = "--dpi-desync=fake,multisplit --dpi-desync-split-seqovl=664 --dpi-desync-split-pos=1 --dpi-desync-fooling=ts --dpi-desync-repeats=8 --dpi-desync-split-seqovl-pattern=tls_clienthello_max_ru.bin --dpi-desync-fake-tls=stun.bin --dpi-desync-fake-tls=tls_clienthello_max_ru.bin";
    private const string CommunityAlt10Tls = "--dpi-desync=fake --dpi-desync-repeats=6 --dpi-desync-fooling=ts --dpi-desync-fake-tls=tls_clienthello_4pda_to.bin --dpi-desync-fake-tls-mod=none";
    private const string HostOnlyTls = "--dpi-desync=hostfakesplit --dpi-desync-fooling=ts --dpi-desync-hostfakesplit-mod=host=www.google.com";
    private const string GamePayload = "--dpi-desync-fake-unknown-udp=ACTIVE_GAME_UDP.bin";
    private static readonly string Root = AppDomain.CurrentDomain.BaseDirectory;
    private static readonly string DirectoryPath = Path.Combine(Root, "engine");
    private static readonly string RuntimeRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Omnissiah Breakwall", "engine");
    private static readonly string[] RuntimeFiles = { "winws.exe", "cygwin1.dll", "WinDivert.dll", "WinDivert64.sys" };
    private readonly Queue<string> output = new Queue<string>();
    private Process process;
    private SafeFileHandle job;

    public bool Running { get { return process != null && !process.HasExited; } }

    public string Details
    {
        get { lock (output) return string.Join(Environment.NewLine, output.ToArray()); }
    }

    public void Start(Strategy strategy, bool warp, ServiceStrategy discord, ServiceStrategy youtube)
    {
        foreach (var name in new[] { "winws", "winws2" })
        {
            var others = Process.GetProcessesByName(name);
            foreach (var other in others) other.Dispose();
            if (others.Length > 0)
                throw new InvalidOperationException("Уже запущен другой zapret (" + name + "). Закройте его перед запуском этой сборки.");
        }

        lock (output) output.Clear();
        var child = new Process { StartInfo = Info(Arguments(strategy, warp, discord, youtube)) };
        child.OutputDataReceived += Receive;
        child.ErrorDataReceived += Receive;
        try
        {
            job = CreateKillJob();
            child.Start();
            process = child;
            if (!AssignProcessToJobObject(job, child.Handle))
                throw new System.ComponentModel.Win32Exception();
            child.BeginOutputReadLine();
            child.BeginErrorReadLine();
            if (child.WaitForExit(900))
            {
                child.WaitForExit();
                throw new InvalidOperationException("winws завершился при запуске. Код " + child.ExitCode + ".");
            }
        }
        catch
        {
            if (process == null) child.Dispose();
            Dispose();
            throw;
        }
    }

    public static int Validate(Strategy strategy, bool warp,
        ServiceStrategy discord = ServiceStrategy.Standard, ServiceStrategy youtube = ServiceStrategy.Standard)
    {
        using (var child = new Process { StartInfo = Info("--dry-run " + Arguments(strategy, warp, discord, youtube)) })
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

    private static ProcessStartInfo Info(string arguments)
    {
        var runtime = PrepareRuntime(DirectoryPath, RuntimeRoot);
        return new ProcessStartInfo(Path.Combine(runtime, "winws.exe"), arguments) {
            WorkingDirectory = DirectoryPath, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
    }

    private static string PrepareRuntime(string bundle, string runtimeRoot)
    {
        using (var sha = SHA256.Create())
        {
            var hashes = RuntimeFiles.ToDictionary(file => file, file => FileHash(sha, Path.Combine(bundle, file)));
            var manifest = Encoding.ASCII.GetBytes(string.Concat(RuntimeFiles.Select(file => hashes[file])));
            var directory = Path.Combine(runtimeRoot, BitConverter.ToString(sha.ComputeHash(manifest)).Replace("-", ""));
            Directory.CreateDirectory(directory);
            foreach (var file in RuntimeFiles)
            {
                var target = Path.Combine(directory, file);
                if (File.Exists(target) && FileHash(sha, target) == hashes[file]) continue;

                var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.Copy(Path.Combine(bundle, file), temporary);
                    if (FileHash(sha, temporary) != hashes[file])
                        throw new IOException("Файл сетевого фильтра изменился при копировании: " + file);
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

    private static string Arguments(Strategy strategy, bool warp, ServiceStrategy discord, ServiceStrategy youtube)
    {
        var profiles = Path.Combine(Root, "profiles");
        var alternate = strategy == Strategy.Alternate;
        var arguments = ReadProfile(Path.Combine(profiles, "base.args"));
        string gameUdp, gameTls;
        GameRules(strategy, out gameUdp, out gameTls);
        if (gameTls != null || discord != ServiceStrategy.Standard)
        {
            var extra = new List<string>();
            if (gameTls != null)
                extra.Add("--filter-tcp=443 --filter-l7=tls --hostlist=../profiles/darktide.txt " + gameTls);
            if (discord != ServiceStrategy.Standard)
                extra.Add(ServiceRules(Path.Combine(profiles, "discord-alt.args"), discord));
            var firstRule = arguments.IndexOf(" --filter-udp=", StringComparison.Ordinal);
            arguments = arguments.Insert(firstRule, " " + string.Join(" --new ", extra) + " --new");
        }
        if (warp)
        {
            arguments = arguments.Replace("--wf-udp=443,", "--wf-udp=443,500,1701,2408,4500,4443,8443,8095,");
            arguments += " " + ReadProfile(Path.Combine(profiles, "warp.args"));
        }
        arguments += " --new " + ServiceRules(Path.Combine(profiles, "youtube.args"), youtube);
        return arguments.Replace("{tls}", alternate ? Alternate : Standard)
            .Replace("{game-udp}", gameUdp);
    }

    private static void GameRules(Strategy strategy, out string udp, out string tls)
    {
        const string stun = "--dpi-desync-fake-unknown-udp=stun.bin";
        const string shortBurst = GamePayload + " --dpi-desync-repeats=12 --dpi-desync-cutoff=n2";
        const string mediumBurst = GamePayload + " --dpi-desync-repeats=12 --dpi-desync-cutoff=n3";
        const string extendedBurst = GamePayload + " --dpi-desync-repeats=10 --dpi-desync-cutoff=n4";
        tls = null;
        switch (strategy)
        {
            case Strategy.Standard:
                udp = stun + " --dpi-desync-cutoff=n2";
                break;
            case Strategy.Alternate:
                udp = stun + " --dpi-desync-repeats=2 --dpi-desync-cutoff=n4";
                break;
            case Strategy.FlowAlt2:
                udp = shortBurst;
                tls = FlowAlt2Tls;
                break;
            case Strategy.FlowAlt3:
                udp = extendedBurst;
                tls = HostSplit;
                break;
            case Strategy.FlowAlt7:
                udp = shortBurst;
                tls = SniSplit;
                break;
            case Strategy.FlowAlt10:
                udp = shortBurst;
                tls = TimestampFake;
                break;
            case Strategy.FlowAlt11:
                udp = extendedBurst;
                tls = TimestampOverlap;
                break;
            case Strategy.FlowExp:
                udp = "--dpi-desync-fake-unknown-udp=quic_initial_4pda_to.bin " +
                    GamePayload + " --dpi-desync-repeats=5 --dpi-desync-cutoff=n4";
                tls = HostOnlyTls;
                break;
            case Strategy.FlowSimpleAlt2:
                udp = mediumBurst;
                tls = SimpleFake;
                break;
            case Strategy.FlowSimple:
                udp = mediumBurst;
                tls = HostOnlyTls;
                break;
            case Strategy.Combined:
                udp = stun + " " + GamePayload + " --dpi-desync-repeats=4 --dpi-desync-cutoff=n4";
                tls = Alternate;
                break;
            case Strategy.FlowAlt12:
                udp = "--dpi-desync-fake-unknown-udp=quic_initial_dbankcloud_ru.bin --dpi-desync-repeats=10 --dpi-desync-cutoff=n4";
                tls = FlowAlt12Tls;
                break;
            case Strategy.CommunityAlt10:
                udp = "--dpi-desync-fake-unknown-udp=quic_initial_www_google_com.bin --dpi-desync-autottl=2 --dpi-desync-repeats=12 --dpi-desync-cutoff=n2";
                tls = CommunityAlt10Tls;
                break;
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

    private static string ReadProfile(string path)
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
        if (process == null) return;
        try
        {
            if (!process.WaitForExit(300))
            {
                try { process.Kill(); }
                catch (InvalidOperationException) { if (!process.HasExited) throw; }
                process.WaitForExit(2000);
            }
        }
        finally
        {
            process.Dispose();
            process = null;
        }
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
