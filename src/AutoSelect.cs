using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal enum AutoGroup { Darktide, SpaceMarine2, Youtube, Discord, Warp }

internal sealed class AutoCandidate
{
    public AutoGroup Group;
    public string Id, Code, Label;
    public StrategySelection<Strategy> Darktide = new StrategySelection<Strategy>(Strategy.None);
    public StrategySelection<Strategy> SpaceMarine = new StrategySelection<Strategy>(Strategy.None);
    public StrategySelection<ServiceStrategy> Discord = new StrategySelection<ServiceStrategy>(ServiceStrategy.None);
    public StrategySelection<ServiceStrategy> Youtube = new StrategySelection<ServiceStrategy>(ServiceStrategy.None);
    public StrategySelection<WarpStrategy> Warp = new StrategySelection<WarpStrategy>(WarpStrategy.None);
    public bool Adaptive { get { return (Id ?? "").IndexOf("Circular", StringComparison.OrdinalIgnoreCase) >= 0; } }
}

internal sealed class AutoResult
{
    public AutoCandidate Candidate;
    public bool Passed;
    public bool Skipped;
    public string Detail;
}

internal sealed class AutoProgress
{
    public int Completed, Total;
    public string Message;
    public int Percent { get { return Total == 0 ? 0 : Completed * 100 / Total; } }
}

internal interface IAutoSession
{
    Task Prepare(CancellationToken cancellation);
    Task Start(AutoCandidate candidate, CancellationToken cancellation);
    Task Verify(AutoCandidate candidate, CancellationToken cancellation);
    Task Stop();
}

internal interface IAutoProbe
{
    Task<ProbeReport> Test(AutoGroup group, CancellationToken cancellation);
}

internal interface IAutoProbePreparation
{
    Task<ProbeReport> PrepareGroup(AutoGroup group, CancellationToken cancellation);
}

internal sealed class ProbeReport
{
    public bool Passed;
    public string Detail;
}

// No BAT imports, ipset rewrites or global process termination. The exact same
// Engine.Start path as the power button preserves each group's real rules.
internal sealed class AutoSession : IAutoSession
{
    private readonly Engine engine;
    private readonly Warp warp;
    public AutoSession(Engine engine, Warp warp) { this.engine = engine; this.warp = warp; }

    public Task Prepare(CancellationToken cancellation)
    {
        return Task.Run(() => {
            cancellation.ThrowIfCancellationRequested();
            Engine.EnsureNoOtherFilters();
            if (warp.OtherVpnActive())
                throw new InvalidOperationException("Перед автоподбором отключите другой VPN или WARP: иначе он повлияет на результат проверки.");
        }, cancellation);
    }

    public async Task Start(AutoCandidate candidate, CancellationToken cancellation)
    {
        // A VPN enabled halfway through a run must not produce false positives.
        if (warp.OtherVpnActive())
            throw new AutoEnvironmentException("Обнаружен другой VPN или WARP. Подбор остановлен, чтобы не смешивать результаты.");
        var started = await Task.Run(() => engine.Start(candidate.Darktide, candidate.Warp,
            candidate.Discord, candidate.Youtube, candidate.SpaceMarine, cancellation), cancellation);
        if (!started) throw new InvalidOperationException("Для проверки не выбрана стратегия.");
        if (candidate.Group == AutoGroup.Warp)
        {
            var attempts = candidate.Adaptive ? 3 : 1;
            for (var attempt = 0; attempt < attempts; attempt++)
            {
                string result;
                try { result = warp.UsesOfficialClient ? await warp.ConnectOfficial(cancellation) : await warp.Connect(cancellation); }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) { result = error.Message; }
                if (result == Warp.ConnectedMessage)
                {
                    if (!warp.OwnsConnection) throw new AutoEnvironmentException("WARP уже подключён другим приложением. Отключите его перед подбором.");
                    break;
                }
                if (attempt == attempts - 1) throw new InvalidOperationException(result);
                // Keep winws2 alive: circular failure counters must survive
                // while retrying the WARP connection under this same strategy.
                await warp.Disconnect();
                await Task.Delay(150, cancellation);
            }
        }
    }

    public async Task Stop()
    {
        Exception failure = null;
        try { await warp.Disconnect(); }
        catch (Exception error) { failure = error; }
        try { await Task.Run(() => engine.Dispose()); }
        catch (Exception error) { if (failure == null) failure = error; }
        if (failure != null) throw new InvalidOperationException("Не удалось остановить проверочное подключение: " + failure.Message, failure);
    }

    public Task Verify(AutoCandidate candidate, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!engine.Running) throw new InvalidOperationException("Проверочный фильтр завершился до окончания теста.");
        if (candidate.Group != AutoGroup.Warp && warp.OtherVpnActive())
            throw new AutoEnvironmentException("Во время проверки включился другой VPN или WARP. Результаты сброшены.");
        return Task.FromResult(0);
    }
}

internal sealed class AutoEnvironmentException : Exception
{
    public AutoEnvironmentException(string message) : base(message) { }
}

internal sealed class AutoSelector
{
    private readonly IAutoSession session;
    private readonly IAutoProbe probe;
    internal readonly List<AutoResult> Results = new List<AutoResult>();
    internal readonly Dictionary<AutoGroup, ProbeReport> Baselines = new Dictionary<AutoGroup, ProbeReport>();
    internal readonly Dictionary<AutoGroup, string> SkippedGroups = new Dictionary<AutoGroup, string>();
    public AutoSelector(IAutoSession session, IAutoProbe probe) { this.session = session; this.probe = probe; }

    public async Task Run(IList<AutoCandidate> candidates, IProgress<AutoProgress> progress, CancellationToken cancellation)
    {
        if (candidates.Count == 0) throw new InvalidOperationException("Выберите хотя бы одну группу.");
        Results.Clear(); Baselines.Clear(); SkippedGroups.Clear();
        await session.Prepare(cancellation);
        var completed = 0;
        foreach (var group in candidates.GroupBy(candidate => candidate.Group))
        {
            cancellation.ThrowIfCancellationRequested();
            var preparation = probe as IAutoProbePreparation;
            var prepared = preparation == null ? null : await preparation.PrepareGroup(group.Key, cancellation);
            if (prepared != null && !prepared.Passed)
            {
                SkippedGroups[group.Key] = prepared.Detail;
                foreach (var candidate in group)
                {
                    cancellation.ThrowIfCancellationRequested();
                    Results.Add(new AutoResult { Candidate = candidate, Skipped = true, Detail = prepared.Detail });
                    Report(progress, ++completed, candidates.Count, prepared.Detail);
                }
                continue;
            }
            Report(progress, completed, candidates.Count, "Проверяем доступность без стратегии: " + GroupName(group.Key));
            // WARP is tested by actually connecting its tunnel. No registration
            // or tunnel connection is performed for the other groups.
            if (group.Key != AutoGroup.Warp) Baselines[group.Key] = await probe.Test(group.Key, cancellation);
            foreach (var candidate in group)
            {
                cancellation.ThrowIfCancellationRequested();
                Report(progress, completed, candidates.Count, "Проверяем " + candidate.Code + " · " + GroupName(candidate.Group));
                var result = new AutoResult { Candidate = candidate };
                Exception interrupted = null;
                try
                {
                    await session.Start(candidate, cancellation);
                    if (candidate.Adaptive)
                    {
                        Report(progress, completed, candidates.Count, "Проверяем " + candidate.Code + " · повторные попытки для адаптивной схемы");
                        await probe.Test(candidate.Group, cancellation);
                        await probe.Test(candidate.Group, cancellation);
                    }
                    var tested = await probe.Test(candidate.Group, cancellation);
                    await session.Verify(candidate, cancellation);
                    result.Passed = tested.Passed; result.Detail = tested.Detail;
                }
                catch (OperationCanceledException error) { interrupted = error; }
                catch (AutoEnvironmentException error) { interrupted = error; }
                catch (Exception error) { result.Detail = error.Message; }
                // C# 5 has no await in finally. All ordinary outcomes, including
                // cancellation, reach this non-cancellable cleanup before throw.
                await session.Stop();
                if (interrupted != null) ExceptionDispatchInfo.Capture(interrupted).Throw();
                cancellation.ThrowIfCancellationRequested();
                Results.Add(result);
                Report(progress, ++completed, candidates.Count, "Проверено " + candidate.Code);
            }
        }
    }

    private static void Report(IProgress<AutoProgress> progress, int completed, int total, string message)
    {
        if (progress != null) progress.Report(new AutoProgress { Completed = completed, Total = total, Message = message });
    }
    internal static string GroupName(AutoGroup group)
    {
        switch (group) {
            case AutoGroup.Darktide: return "Darktide";
            case AutoGroup.SpaceMarine2: return "Space Marine 2";
            case AutoGroup.Youtube: return "YouTube";
            case AutoGroup.Discord: return "Discord";
            default: return "WARP";
        }
    }
    internal static string Scope(AutoGroup group)
    {
        switch (group) {
            case AutoGroup.Darktide:
                return "TLS + UDP-ответ игровых узлов · матч не проверен";
            case AutoGroup.SpaceMarine2: return "HTTPS/TLS · игровой UDP и матч не проверены";
            case AutoGroup.Discord: return "API/CDN/TLS · голос не проверен";
            case AutoGroup.Youtube: return "HTTPS/CDN · видео и QUIC не проверены";
            default: return "Туннель WARP и передача данных";
        }
    }
}

internal enum AutoProbeKind { Https, Tls, Image, DiscordApi, WarpTrace }
internal sealed class AutoProbeTarget
{
    public string Name;
    public Uri Url;
    public AutoProbeKind Kind;
    public AutoProbeTarget(string name, string url, AutoProbeKind kind) { Name = name; Url = new Uri(url); Kind = kind; }
}

internal sealed class CurlResponse
{
    public int ExitCode, Status;
    public long Bytes;
    public string Body, ContentType;
}

internal interface IAutoHttp
{
    Task<CurlResponse> Request(AutoProbeTarget target, CancellationToken cancellation);
}

internal sealed class AutoProbe : IAutoProbe, IAutoProbePreparation
{
    private readonly IAutoHttp http;
    private readonly string profiles;
    private readonly IDarktideUdpProbe darktideUdp;
    public AutoProbe(string profiles, IAutoHttp http) : this(profiles, http,
        new DarktideUdpProbe(new DarktideLogTargets(), new DarktideDatagrams())) { }
    internal AutoProbe(string profiles, IAutoHttp http, IDarktideUdpProbe darktideUdp)
    { this.profiles = profiles; this.http = http; this.darktideUdp = darktideUdp; }

    public Task<ProbeReport> PrepareGroup(AutoGroup group, CancellationToken cancellation)
    {
        return group == AutoGroup.Darktide ? darktideUdp.Prepare(cancellation) : Task.FromResult<ProbeReport>(null);
    }

    internal static AutoProbeTarget[] Targets(AutoGroup group)
    {
        switch (group)
        {
            case AutoGroup.Darktide: return new[] {
                new AutoProbeTarget("Atoma backend TLS", "https://bsp-td-prod.atoma.cloud/", AutoProbeKind.Tls),
                new AutoProbeTarget("Fatshark identity TLS", "https://login.shr-prod-identity.fatshark.services/", AutoProbeKind.Tls)
            };
            case AutoGroup.SpaceMarine2: return new[] {
                new AutoProbeTarget("Epic services TLS", "https://api.epicgames.dev/epic/oauth/v2/token", AutoProbeKind.Tls),
                new AutoProbeTarget("EAC CDN TLS", "https://download.eac-cdn.com/", AutoProbeKind.Tls)
            };
            case AutoGroup.Discord: return new[] {
                new AutoProbeTarget("Discord API", "https://discord.com/api/v10/gateway", AutoProbeKind.DiscordApi),
                new AutoProbeTarget("Discord CDN", "https://cdn.discordapp.com/embed/avatars/0.png", AutoProbeKind.Image),
                new AutoProbeTarget("Discord Gateway TLS", "https://gateway.discord.gg/", AutoProbeKind.Tls)
            };
            case AutoGroup.Youtube: return new[] {
                new AutoProbeTarget("YouTube HTTPS", "https://www.youtube.com/", AutoProbeKind.Https),
                new AutoProbeTarget("YouTube images", "https://i.ytimg.com/vi/jNQXAC9IVRw/default.jpg", AutoProbeKind.Image),
                new AutoProbeTarget("Googlevideo TLS", "https://redirector.googlevideo.com/", AutoProbeKind.Tls)
            };
            default: return new[] {
                new AutoProbeTarget("WARP tunnel trace", "https://www.cloudflare.com/cdn-cgi/trace", AutoProbeKind.WarpTrace)
            };
        }
    }

    public async Task<ProbeReport> Test(AutoGroup group, CancellationToken cancellation)
    {
        var targets = Targets(group);
        ValidateTargets(profiles, group, targets);
        var successful = 0;
        // Separate curl processes/connections on both rounds; no pooled TLS
        // session from a previous strategy may satisfy the next one's probe.
        for (var round = 0; round < 2; round++)
        {
            cancellation.ThrowIfCancellationRequested();
            var outcomes = await Task.WhenAll(targets.Select(target => TestTarget(target, cancellation)));
            successful += outcomes.Count(passed => passed);
            if (outcomes.Any(passed => !passed))
                return new ProbeReport { Detail = "Ответили " + outcomes.Count(passed => passed) + " из " + targets.Length + " проверок" };
            if (round == 0) await Task.Delay(150, cancellation);
        }
        if (group == AutoGroup.Darktide)
        {
            var udp = await darktideUdp.Test(cancellation);
            return new ProbeReport { Passed = udp.Passed, Detail = "TLS " + successful + "/" + (targets.Length * 2) + ". " + udp.Detail };
        }
        return new ProbeReport { Passed = true, Detail = successful + "/" + (targets.Length * 2) + " · " + AutoSelector.Scope(group) };
    }

    private async Task<bool> TestTarget(AutoProbeTarget target, CancellationToken cancellation)
    {
        try { return Accept(target.Kind, await http.Request(target, cancellation)); }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    internal static bool Accept(AutoProbeKind kind, CurlResponse response)
    {
        if (response.ExitCode != 0 || response.Status < 200 || response.Status >= 500 || response.Status == 408 || response.Status == 429)
            return false;
        // Authentication errors and missing routes still prove a TLS connection,
        // not a usable game session. These checks are labelled TLS throughout UI.
        if (kind == AutoProbeKind.Tls) return true;
        if (response.Status >= 400) return false;
        if (kind == AutoProbeKind.Https) return response.Status < 400;
        if (response.Status >= 300) return false;
        if (kind == AutoProbeKind.Image)
            return response.Bytes >= 100 && (response.ContentType ?? "").StartsWith("image/", StringComparison.OrdinalIgnoreCase);
        if (kind == AutoProbeKind.DiscordApi)
        {
            try
            {
                var data = new JavaScriptSerializer().DeserializeObject(response.Body ?? "") as Dictionary<string, object>;
                object value; Uri gateway;
                return data != null && data.TryGetValue("url", out value) && value is string &&
                    Uri.TryCreate((string)value, UriKind.Absolute, out gateway) && gateway.Scheme == "wss" &&
                    gateway.Host.Equals("gateway.discord.gg", StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }
        return Regex.IsMatch(response.Body ?? "", @"(?m)^warp=(on|plus)\r?$");
    }

    internal static void ValidateTargets(string profiles, AutoGroup group, IEnumerable<AutoProbeTarget> targets)
    {
        // Trace deliberately tests traffic THROUGH the real WARP tunnel, not a
        // domain DPI recipe. All other probes must be inside our actual lists.
        if (group == AutoGroup.Warp) return;
        var files = group == AutoGroup.Darktide ? new[] { "darktide.txt" } :
            group == AutoGroup.SpaceMarine2 ? new[] { "space-marine-2.txt" } :
            group == AutoGroup.Discord ? new[] { "discord.txt" } : new[] { "youtube-core.txt", "youtube.txt" };
        var entries = files.SelectMany(file => File.ReadAllLines(Path.Combine(profiles, file)))
            .Select(line => line.Split('#')[0].Trim()).Where(line => line.Length != 0).ToArray();
        foreach (var target in targets)
            if (target.Url.Scheme != "https" || target.Url.Port != 443 || !entries.Any(entry => Covers(entry, target.Url.Host)))
                throw new InvalidOperationException("Тестовый адрес не входит в профиль " + AutoSelector.GroupName(group) + ": " + target.Url.Host);
    }
    private static bool Covers(string entry, string host)
    {
        var exact = entry.StartsWith("^", StringComparison.Ordinal);
        var domain = exact ? entry.Substring(1) : entry;
        return host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
            (!exact && host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));
    }
}

internal sealed class CurlAutoHttp : IAutoHttp
{
    private readonly string executable;
    internal CurlAutoHttp(string executable) { this.executable = executable; }
    internal static string SystemCurl { get { return Path.Combine(Environment.SystemDirectory, "curl.exe"); } }

    internal static string Arguments(AutoProbeTarget target)
    {
        var head = target.Kind == AutoProbeKind.Https || target.Kind == AutoProbeKind.Tls;
        var url = target.Url.AbsoluteUri;
        // -q must be the first option: ignore curlrc. Never use a proxy, bypass
        // certificate checks, follow an off-profile redirect or reuse a socket.
        return "-q --noproxy \"*\" --silent --show-error --http1.1 --connect-timeout 3 --max-time 6 --retry 0 " +
            "--max-filesize 1048576 --header \"Cache-Control: no-cache\" --header \"Connection: close\" " +
            (head ? "--head " : "") + "--write-out \"\nBREAKWALL_PROBE:%{http_code}:%{size_download}:%{content_type}\" \"" + url + "\"";
    }

    public Task<CurlResponse> Request(AutoProbeTarget target, CancellationToken cancellation)
    {
        return Task.Run(() => {
            cancellation.ThrowIfCancellationRequested();
            using (var job = Engine.CreateKillJob())
            using (var child = new Process { StartInfo = new ProcessStartInfo(executable, Arguments(target)) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            } })
            {
                var started = false;
                try
                {
                    child.Start(); started = true;
                    if (!Engine.AssignProcessToJobObject(job, child.Handle)) throw new System.ComponentModel.Win32Exception();
                    var output = child.StandardOutput.ReadToEndAsync();
                    var error = child.StandardError.ReadToEndAsync();
                    if (!Engine.WaitForExit(child, 8500, cancellation))
                    {
                        child.Kill(); child.WaitForExit(2000);
                        return new CurlResponse { ExitCode = -1 };
                    }
                    child.WaitForExit();
                    // Do not retain stderr, trace addresses or any response body
                    // in diagnostics/results. They only exist for acceptance.
                    var text = output.GetAwaiter().GetResult(); error.GetAwaiter().GetResult();
                    return Parse(text, child.ExitCode);
                }
                finally
                {
                    if (started)
                    {
                        try { if (!child.HasExited) child.Kill(); }
                        catch (InvalidOperationException) { if (!child.HasExited) throw; }
                        child.WaitForExit(2000);
                    }
                }
            }
        }, cancellation);
    }

    internal static CurlResponse Parse(string text, int exitCode)
    {
        var match = Regex.Match(text ?? "", @"\nBREAKWALL_PROBE:(\d{3}):([0-9]+):([^\r\n]*)\r?\n?$");
        var response = new CurlResponse { ExitCode = exitCode };
        if (!match.Success) return response;
        response.Status = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        response.Bytes = long.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        response.ContentType = match.Groups[3].Value.Trim();
        response.Body = text.Substring(0, match.Index);
        return response;
    }
}
