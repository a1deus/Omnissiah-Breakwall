using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;

internal sealed class Warp : IDisposable
{
    internal const string ConnectedMessage = "Фильтр + WARP подключен";
    private static readonly string Bundle = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "warp");
    private static readonly string Executable = Path.Combine(Bundle, "usque.exe");
    private static readonly string AppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    // Keep the existing registration available to both versions of the launcher.
    private static readonly string LocalDirectory = Path.Combine(AppData, "Omnissiah Darktide Local");
    private static readonly string LegacyDirectory = Path.Combine(AppData, "Omnissiah Darktide");
    private static readonly string LegacyConfig = Path.Combine(LegacyDirectory, "warp-masque.json");
    private static readonly string LegacyProtectedConfig = Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.MyDocuments), "Omnissiah Darktide Local", "warp-masque.dpapi");
    private static readonly string Config = Path.Combine(LocalDirectory, "warp-masque.dpapi");
    private static readonly string RuntimeDirectory = Path.Combine(LocalDirectory, "warp-runtime");
    private readonly string adapterName = "ODWarp" + Guid.NewGuid().ToString("N").Substring(0, 8);
    private readonly OfficialWarp official = new OfficialWarp();
    private readonly List<string> routes = new List<string>();
    private Process tunnel;
    private SafeFileHandle job;
    private volatile bool connected;
    private int ipv4Index, ipv6Index;

    public bool Installed { get { return official.Installed || (File.Exists(Executable) && File.Exists(Path.Combine(Bundle, "wintun.dll"))); } }
    public bool UsesOfficialClient { get { return official.Installed; } }
    public bool Registered { get { return File.Exists(Config) || File.Exists(LegacyProtectedConfig) || File.Exists(LegacyConfig); } }
    public bool OwnsConnection { get { return official.OwnsConnection || tunnel != null; } }
    public bool BuiltInNeedsStop { get { return tunnel != null && (tunnel.HasExited || OtherVpnActive()); } }
    private bool ProcessRunning { get { return tunnel != null && !tunnel.HasExited; } }
    private bool Running { get { return ProcessRunning && connected; } }

    public bool OtherVpnActive(bool allowOfficialAdapter = false)
    {
        return NetworkInterface.GetAllNetworkInterfaces().Any(nic =>
            nic.Name != adapterName && nic.OperationalStatus == OperationalStatus.Up &&
            !(allowOfficialAdapter && Regex.IsMatch(nic.Name + " " + nic.Description, @"Cloudflare\s*WARP", RegexOptions.IgnoreCase)) &&
            !Regex.IsMatch(nic.Name + " " + nic.Description, "6to4|Teredo|ISATAP", RegexOptions.IgnoreCase) &&
            (nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel || (int)nic.NetworkInterfaceType == 53 ||
             Regex.IsMatch(nic.Name + " " + nic.Description,
                 "Cloudflare|WireGuard|Wintun|Amnezia|Tailscale|OpenVPN|TAP-Windows|NordLynx|VPN",
                 RegexOptions.IgnoreCase)));
    }

    public Task<string> ConnectOfficial()
    {
        return official.Connect(() => OtherVpnActive(true));
    }

    public async Task<string> Connect()
    {
        if (OtherVpnActive()) return "WARP пропущен: обнаружен другой активный VPN.";
        Directory.CreateDirectory(RuntimeDirectory);
        ClearRuntimeConfigs(RuntimeDirectory);
        ClearRuntimeConfigs(Path.Combine(LegacyDirectory, "warp-runtime"));
        var runtimeConfig = Path.Combine(RuntimeDirectory, "session-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await PrepareConfig(runtimeConfig);
            return await ConnectTunnel(runtimeConfig, ReadConfig(File.ReadAllBytes(runtimeConfig)));
        }
        finally { File.Delete(runtimeConfig); }
    }

    private static void ClearRuntimeConfigs(string directory)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var leftover in Directory.EnumerateFiles(directory, "session-*.json"))
            File.Delete(leftover);
    }

    private async Task<string> ConnectTunnel(string runtimeConfig, Dictionary<string, string> config)
    {
        var h3 = Address(config, "endpoint_v4", AddressFamily.InterNetwork);
        string h2Value;
        var h2 = config.TryGetValue("endpoint_h2_v4", out h2Value) && !string.IsNullOrWhiteSpace(h2Value)
            ? Address(config, "endpoint_h2_v4", AddressFamily.InterNetwork) : IPAddress.Parse("162.159.198.2");
        Address(config, "ipv4", AddressFamily.InterNetwork);
        Address(config, "ipv6", AddressFamily.InterNetworkV6);

        Exception failure = null;
        try
        {
            foreach (var useHttp2 in new[] { false, true })
            {
                if (OtherVpnActive()) return "WARP пропущен: обнаружен другой активный VPN.";
                StartTunnel(runtimeConfig, useHttp2);
                var wait = Stopwatch.StartNew();
                while (!tunnel.HasExited && !connected && wait.Elapsed < TimeSpan.FromSeconds(11))
                    await Task.Delay(200);
                if (Running)
                {
                    if (OtherVpnActive())
                    {
                        await Disconnect();
                        return "WARP пропущен: обнаружен другой активный VPN.";
                    }
                    await AddRoutes(useHttp2 ? h2 : h3);
                    if (!Running) throw new InvalidOperationException("Соединение MASQUE прервалось при настройке маршрутов.");
                    return ConnectedMessage;
                }
                await Disconnect();
            }
        }
        catch (Exception error) { failure = error; }
        await Disconnect();
        if (failure != null) throw new InvalidOperationException(failure.Message, failure);
        return "WARP не подключился. Фильтр zapret продолжает работать.";
    }

    private static async Task PrepareConfig(string runtimeConfig)
    {
        var hasLocalConfig = File.Exists(Config);
        var migrateProtected = !hasLocalConfig && File.Exists(LegacyProtectedConfig);
        var migrateJson = !hasLocalConfig && !migrateProtected && File.Exists(LegacyConfig);
        var register = !hasLocalConfig && !migrateProtected && !migrateJson;
        byte[] data;
        if (hasLocalConfig || migrateProtected)
        {
            var source = hasLocalConfig ? Config : LegacyProtectedConfig;
            data = ProtectedData.Unprotect(File.ReadAllBytes(source), null, DataProtectionScope.CurrentUser);
        }
        else if (migrateJson)
            data = File.ReadAllBytes(LegacyConfig);
        else
        {
            await Run(Executable, "-c \"" + runtimeConfig + "\" register --accept-tos --name OmnissiahBreakwall", 35000, false);
            data = File.ReadAllBytes(runtimeConfig);
        }

        try
        {
            ReadConfig(data);
            if (!hasLocalConfig) SaveProtectedConfig(data);
            if (migrateProtected) File.Delete(LegacyProtectedConfig);
            if (migrateJson) File.Delete(LegacyConfig);
            if (!register) File.WriteAllBytes(runtimeConfig, data);
        }
        finally { Array.Clear(data, 0, data.Length); }
    }

    private static void SaveProtectedConfig(byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Config));
        var temporary = Config + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser));
            File.Move(temporary, Config);
        }
        finally { File.Delete(temporary); }
    }

    private static Dictionary<string, string> ReadConfig(byte[] data)
    {
        var config = new JavaScriptSerializer().Deserialize<Dictionary<string, string>>(
            Encoding.UTF8.GetString(data).TrimStart('\uFEFF'));
        string key, peer;
        if (config == null || !config.TryGetValue("private_key", out key) || string.IsNullOrWhiteSpace(key) ||
            !config.TryGetValue("endpoint_pub_key", out peer) || string.IsNullOrWhiteSpace(peer))
            throw new InvalidDataException("Конфигурация WARP повреждена.");
        return config;
    }

    private static IPAddress Address(Dictionary<string, string> config, string field, AddressFamily family)
    {
        string value;
        IPAddress address;
        if (!config.TryGetValue(field, out value) || !IPAddress.TryParse(value, out address) || address.AddressFamily != family)
            throw new InvalidDataException("Некорректный адрес в конфигурации WARP: " + field);
        return address;
    }

    private void StartTunnel(string runtimeConfig, bool useHttp2)
    {
        connected = false;
        var child = new Process { StartInfo = Info(Executable, "-c \"" + runtimeConfig +
            "\" nativetun --interface-name " + adapterName + " --always-reconnect" +
            (useHttp2 ? " --http2" : "")) };
        child.ErrorDataReceived += (sender, args) =>
        {
            if (args.Data == null) return;
            if (args.Data.EndsWith("Connected to MASQUE server", StringComparison.Ordinal)) connected = true;
            if (args.Data.Contains("Tunnel connection lost:")) connected = false;
        };
        try
        {
            job = Engine.CreateKillJob();
            child.Start();
            tunnel = child;
            if (!Engine.AssignProcessToJobObject(job, child.Handle))
                throw new System.ComponentModel.Win32Exception();
            child.BeginErrorReadLine();
            child.BeginOutputReadLine();
        }
        catch
        {
            if (tunnel == null) child.Dispose();
            Dispose();
            throw;
        }
    }

    // The sibling prefixes along the endpoint's /32 path cover every other IPv4 address.
    // Its existing route therefore continues to carry the outer MASQUE connection.
    internal static IEnumerable<string> TunnelPrefixes(IPAddress endpoint)
    {
        var bytes = endpoint.GetAddressBytes();
        for (var bit = 0; bit < 32; bit++)
        {
            var network = new byte[4];
            var octet = bit / 8;
            var mask = 128 >> (bit % 8);
            Array.Copy(bytes, network, octet);
            network[octet] = (byte)((bytes[octet] ^ mask) & (256 - mask));
            yield return new IPAddress(network) + "/" + (bit + 1);
        }
    }

    private async Task AddRoutes(IPAddress endpoint)
    {
        var adapter = NetworkInterface.GetAllNetworkInterfaces().Single(nic => nic.Name == adapterName);
        var properties = adapter.GetIPProperties();
        ipv4Index = properties.GetIPv4Properties().Index;
        ipv6Index = properties.GetIPv6Properties().Index;
        foreach (var prefix in TunnelPrefixes(endpoint).Concat(new[] { "2000::/3" }))
        {
            if (!Running) throw new InvalidOperationException("Соединение WARP прервалось.");
            await Route("add", prefix);
            routes.Add(prefix);
        }
    }

    public async Task Disconnect()
    {
        await official.Disconnect();
        if (tunnel == null) return;
        Exception failure = null;
        try
        {
            for (var index = routes.Count - 1; index >= 0; index--)
            {
                if (!NetworkInterface.GetAllNetworkInterfaces().Any(nic => nic.Name == adapterName)) break;
                try { await Route("delete", routes[index]); }
                catch (Exception error) { failure = error; }
            }
        }
        finally { Dispose(); }
        if (failure != null && NetworkInterface.GetAllNetworkInterfaces().Any(nic => nic.Name == adapterName))
            throw new InvalidOperationException("Не удалось удалить маршрут адаптера WARP.", failure);
    }

    private Task Route(string verb, string prefix)
    {
        var ipv6 = prefix.Contains(":");
        return Run(Path.Combine(Environment.SystemDirectory, "netsh.exe"),
            "interface " + (ipv6 ? "ipv6" : "ipv4") + " " + verb + " route prefix=" + prefix +
            " interface=" + (ipv6 ? ipv6Index : ipv4Index) + " nexthop=" + (ipv6 ? "::" : "0.0.0.0") +
            " store=active" + (verb == "add" ? " metric=1" : ""), 4000, true);
    }

    private static ProcessStartInfo Info(string path, string arguments)
    {
        return new ProcessStartInfo(path, arguments) {
            WorkingDirectory = RuntimeDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
    }

    private static async Task Run(string path, string arguments, int timeout, bool includeOutput)
    {
        using (var command = Process.Start(Info(path, arguments)))
        {
            var output = command.StandardOutput.ReadToEndAsync();
            var errors = command.StandardError.ReadToEndAsync();
            if (!await Task.Run(() => command.WaitForExit(timeout)))
            {
                if (!command.HasExited) command.Kill();
                throw new TimeoutException("Компонент WARP не ответил вовремя.");
            }
            var result = (await output) + (await errors);
            if (command.ExitCode != 0)
                throw new InvalidOperationException(includeOutput ? result.Trim() :
                    "Не удалось зарегистрировать устройство WARP. Код " + command.ExitCode + ".");
        }
    }

    public void Dispose()
    {
        if (job != null) { job.Dispose(); job = null; }
        if (tunnel == null) return;
        if (!tunnel.WaitForExit(500))
        {
            if (!tunnel.HasExited) tunnel.Kill();
            if (!tunnel.WaitForExit(2000))
                throw new InvalidOperationException("Процесс usque не завершился.");
        }
        tunnel.Dispose();
        tunnel = null;
        connected = false;
        routes.Clear();
    }
}

internal sealed class OfficialWarp
{
    private readonly string executable;
    private readonly Func<string, Task<string>> command;

    public OfficialWarp()
    {
        executable = new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
            .Select(folder => Path.Combine(Environment.GetFolderPath(folder), @"Cloudflare\Cloudflare WARP\warp-cli.exe"))
            .FirstOrDefault(File.Exists);
        command = Run;
    }

    internal OfficialWarp(Func<string, Task<string>> command) { this.command = command; }

    public bool Installed { get { return executable != null && File.Exists(executable); } }
    public bool OwnsConnection { get; private set; }

    public async Task<string> Connect(Func<bool> otherVpnActive)
    {
        var status = await Status();
        if (status != "Connected" && otherVpnActive()) return "WARP пропущен: обнаружен другой активный VPN.";
        if (status != "Disconnected" && status != "Connected")
            return "Cloudflare WARP занят. Дождитесь завершения подключения.";

        var settings = await command("settings list");
        if (!Regex.IsMatch(settings, @"(?im)\bMode:\s*(Warp|WarpWithDnsOverHttps)\s*$"))
            return "В клиенте Cloudflare выберите режим WARP.";
        if (status == "Connected") return Warp.ConnectedMessage;
        if (otherVpnActive()) return "WARP пропущен: обнаружен другой активный VPN.";

        status = await Status();
        if (status == "Connected") return Warp.ConnectedMessage;
        if (status != "Disconnected") return "Cloudflare WARP уже подключается.";

        await command("connect");
        OwnsConnection = true;
        Exception failure = null;
        try
        {
            var wait = Stopwatch.StartNew();
            while (wait.Elapsed < TimeSpan.FromSeconds(18))
            {
                if (otherVpnActive()) break;
                if (await Status() == "Connected") return Warp.ConnectedMessage;
                await Task.Delay(500);
            }
        }
        catch (Exception error) { failure = error; }
        await Disconnect();
        if (failure != null) throw new InvalidOperationException(failure.Message, failure);
        return otherVpnActive() ? "WARP пропущен: обнаружен другой активный VPN." :
            "Cloudflare WARP не подключился вовремя.";
    }

    public async Task Disconnect()
    {
        if (!OwnsConnection) return;
        await command("disconnect");
        OwnsConnection = false;
    }

    private async Task<string> Status()
    {
        var output = await command("--json status");
        var json = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(output);
        object status;
        if (json == null || !json.TryGetValue("status", out status) || !(status is string))
            throw new InvalidOperationException("Не удалось прочитать статус Cloudflare WARP.");
        return (string)status;
    }

    private async Task<string> Run(string arguments)
    {
        var info = new ProcessStartInfo(executable, "--no-ansi --no-paginate " + arguments) {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        using (var process = Process.Start(info))
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!await Task.Run(() => process.WaitForExit(5000)))
            {
                try { process.Kill(); }
                catch (InvalidOperationException) { }
                throw new TimeoutException("Официальный клиент Cloudflare WARP не ответил.");
            }
            var output = (await stdout).Trim();
            var error = (await stderr).Trim();
            if (process.ExitCode != 0)
                throw new InvalidOperationException("Cloudflare WARP: " + (error.Length > 0 ? error : output));
            return output;
        }
    }
}
