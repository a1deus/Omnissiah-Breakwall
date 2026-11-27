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
    Zapret2Circular, Zapret2Exp, Zapret2DoubleFake,
    DbankShort, ActiveGameMinimal, TlsSmallOverlap, TlsMd5Disorder, Zapret2TlsClone, Zapret2TlsCircular, FlowsealLongTen, FlowsealLongFourteen, FlowsealDualFour
}
internal enum ServiceStrategy
{
    Standard, BadSequence, TimestampFake, HostSplit, TimestampOverlap, SimpleFake, SniSplit, None, AlternateTls,
    Zapret2DiscordExp, Zapret2DiscordCircular, Zapret2YoutubeCircular, Zapret2YoutubeExp
}
internal enum WarpStrategy
{
    Standard, QuicBurst, MasqueTls, None,
    Circular, CircularStun, DoubleFakeSequence, DoubleFakeSteam, DoubleFakeTimestamp
}

internal sealed class Engine : IDisposable
{
    internal const string DarktideUdpPorts = "35500-35650,35670";
    internal const string SpaceMarineTcpPorts = "11700-11720";
    internal const string SpaceMarineUdpPorts = "48800-55000";
    internal const string SpaceMarineSharedIps = "space-marine-2-ip.txt";
    internal const string SpaceMarineUdpIps = "space-marine-2-udp-ip.txt";
    internal const string DiscordVoiceOverlap = "50000-50100";
    internal const string DiscordVoicePorts = "19294-19344," + DiscordVoiceOverlap;
    internal const string DiscordTlsPorts = "2053,2083,2087,2096,8443";
    private static readonly string Root = AppDomain.CurrentDomain.BaseDirectory;
    private static readonly string DirectoryPath = Path.Combine(Root, "engine");
    private static readonly string RuntimeRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Omnissiah Breakwall", "engine");
    private static readonly string Zapret2Directory = Path.Combine(DirectoryPath, "zapret2");
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
        if (arguments == null) return false;

        EnsureNoOtherFilters();

        lock (output) output.Clear();
        try
        {
            cancellation.ThrowIfCancellationRequested();
            job = CreateKillJob();
            StartProcess(Info(arguments), cancellation);
            if (!Running) throw new InvalidOperationException("Процесс сетевого фильтра завершился при запуске.");
        }
        catch
        {
            Dispose();
            throw;
        }
        return true;
    }

    internal static void EnsureNoOtherFilters()
    {
        foreach (var name in new[] { "winws", "winws2" })
        {
            var others = Process.GetProcessesByName(name);
            foreach (var other in others) other.Dispose();
            if (others.Length > 0)
                throw new InvalidOperationException("Уже запущен другой zapret (" + name + "). Закройте его перед запуском этой сборки.");
        }
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
        return arguments == null ? 0 : ValidateProcess(arguments);
    }

    private static int ValidateProcess(string arguments)
    {
        using (var child = new Process { StartInfo = Info("--dry-run " + arguments) })
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
        var bundle = Zapret2Directory;
        var sources = new[] {
            Path.Combine(bundle, Zapret2.Executable), Path.Combine(DirectoryPath, "cygwin1.dll"),
            Path.Combine(bundle, "WinDivert.dll"), Path.Combine(DirectoryPath, "WinDivert64.sys")
        };
        var runtime = PrepareRuntime(sources);
        return new ProcessStartInfo(Path.Combine(runtime, Zapret2.Executable), arguments) {
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
        return Zapret2.Arguments(Path.Combine(Root, "profiles"), darktideSelection, warpSelection,
            discordSelection, youtubeSelection, spaceMarineSelection);
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
