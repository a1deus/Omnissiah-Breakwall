using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal sealed class ReleaseUpdate
{
    public const string ReleasesUrl = "https://github.com/a1deus/Omnissiah-Breakwall/releases";
    public const string ApplyArgument = "--apply-update";
    public const string ReadyArgument = "--update-ready";
    private const string ApiUrl = "https://api.github.com/repos/a1deus/Omnissiah-Breakwall/releases/latest";
    private const string ReleasesPath = "/a1deus/Omnissiah-Breakwall/releases";
    private const string Launcher = Program.Name + ".exe";
    private const string Gate = @"Local\OmnissiahBreakwall.Update";
    private const int DownloadLimit = 128 * 1024 * 1024;
    private static readonly Regex TagPattern = new Regex(@"\Av[0-9]+\.[0-9]+\.[0-9]+\z");
    private static readonly Regex DigestPattern = new Regex(@"\Asha256:([0-9a-fA-F]{64})\z");
    private static readonly string WorkRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Program.Name, "updates");
    private static readonly string[] PackageDirectories = { "engine", "profiles", "warp", "src", "licenses" };
    private static readonly string[] PackageRootFiles = { Launcher, "README.md", "Инструкция.txt", "LICENSE", "LICENSE.txt" };
    // Explicit retirement list: never delete arbitrary user files or IP/host lists.
    private static readonly string[] RetiredFiles = {
        @"engine\winws.exe", @"engine\WinDivert.dll", @"src\WarpZapret2.cs",
        @"profiles\base.args", @"profiles\youtube.args", @"profiles\discord-base.args",
        @"profiles\discord-alt.args", @"profiles\warp.args", @"profiles\game-zapret2\Zapret2Circular.args"
    };

    public Version Version { get; private set; }
    public Uri Download { get; private set; }
    private string digest;
    public static Version CurrentVersion { get { return typeof(Program).Assembly.GetName().Version; } }

    public static ReleaseUpdate Check()
    {
        ClearCompletedUpdates();
        try
        {
            using (var data = new MemoryStream())
            {
                Fetch(new Uri(ApiUrl), data, 1024 * 1024);
                return ReadRelease(Encoding.UTF8.GetString(data.ToArray()));
            }
        }
        catch (WebException error)
        {
            var response = error.Response as HttpWebResponse;
            if (response != null)
            {
                using (response)
                {
                    if ((int)response.StatusCode == 429 || response.StatusCode == HttpStatusCode.Forbidden)
                        return CheckReleasePage();
                }
            }
            throw;
        }
    }

    private static ReleaseUpdate ReadRelease(string json)
    {
        var release = new JavaScriptSerializer().Deserialize<ReleaseInfo>(json);
        Version version;
        if (release == null || release.draft || release.prerelease ||
            !TagPattern.IsMatch(release.tag_name ?? "") || !Version.TryParse(release.tag_name.Substring(1) + ".0", out version))
            throw new InvalidDataException("GitHub не вернул номер стабильного релиза.");

        return SelectAsset(release.tag_name, version, release.assets ?? new Asset[0]);
    }

    private static ReleaseUpdate CheckReleasePage()
    {
        using (var page = new MemoryStream())
        {
            var latest = Fetch(new Uri(ReleasesUrl + "/latest"), page, 2 * 1024 * 1024);
            var match = Regex.Match(latest.AbsolutePath, "^" + ReleasesPath + @"/tag/(v[0-9]+\.[0-9]+\.[0-9]+)$");
            Version version;
            if (latest.Host != "github.com" || !match.Success || !Version.TryParse(match.Groups[1].Value.Substring(1) + ".0", out version))
                throw new InvalidDataException("GitHub не вернул ссылку на стабильный релиз.");
            var tag = match.Groups[1].Value;
            if (version <= CurrentVersion) return new ReleaseUpdate { Version = version };

            page.SetLength(0);
            var assetsUrl = new Uri(ReleasesUrl + "/expanded_assets/" + tag);
            if (Fetch(assetsUrl, page, 1024 * 1024) != assetsUrl)
                throw new InvalidDataException("Не удалось получить список файлов релиза.");
            var html = Encoding.UTF8.GetString(page.ToArray());
            var assets = new List<Asset>();
            foreach (Match element in Regex.Matches(html, @"<clipboard-copy\b[^>]+>"))
            {
                const string label = "Copy to clipboard digest for ";
                var name = Attribute(element.Value, "aria-label");
                if (!name.StartsWith(label, StringComparison.Ordinal)) continue;
                name = name.Substring(label.Length);
                var path = ReleasesPath + "/download/" + tag + "/" + Uri.EscapeDataString(name);
                if (!html.Contains("href=\"" + path + "\"")) continue;
                assets.Add(new Asset { name = name, digest = Attribute(element.Value, "value"),
                    browser_download_url = "https://github.com" + path });
            }
            return SelectAsset(tag, version, assets);
        }
    }

    private static string Attribute(string element, string name)
    {
        return WebUtility.HtmlDecode(Regex.Match(element, "\\s" + name + "=\"([^\"]*)\"").Groups[1].Value);
    }

    private static ReleaseUpdate SelectAsset(string tag, Version version, IEnumerable<Asset> assets)
    {
        var result = new ReleaseUpdate { Version = version };
        foreach (var asset in assets)
        {
            var name = asset.name ?? "";
            if (!Regex.IsMatch(name, @"\AOmnissiah[ ._-]+Breakwall[ ._-]+" +
                Regex.Escape(tag) + @"[ ._-]+win[ ._-]+x64\.zip\z", RegexOptions.IgnoreCase))
                continue;
            var sum = DigestPattern.Match(asset.digest ?? "");
            Uri url;
            if (!sum.Success || !Uri.TryCreate(asset.browser_download_url, UriKind.Absolute, out url) ||
                !url.IsWellFormedOriginalString() || url.Scheme != Uri.UriSchemeHttps || url.Host != "github.com" ||
                !url.IsDefaultPort || url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0 ||
                Uri.UnescapeDataString(url.AbsolutePath) != ReleasesPath + "/download/" + tag + "/" + name)
                continue;
            if (result.Download != null)
                throw new InvalidDataException("В релизе несколько ZIP для Windows x64. Выберите архив на GitHub.");
            result.Download = url;
            result.digest = sum.Value;
        }
        return result;
    }

    private static Uri Fetch(Uri url, Stream destination, long limit)
    {
        // .NET Framework 4 compatibility on Windows 10/11.
        ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
        var request = (HttpWebRequest)WebRequest.Create(url);
        request.UserAgent = "Omnissiah-Breakwall/" + CurrentVersion.ToString(3);
        request.Timeout = 15000;
        request.ReadWriteTimeout = 15000;
        request.MaximumAutomaticRedirections = 4;
        using (var response = (HttpWebResponse)request.GetResponse())
        using (var source = response.GetResponseStream())
        {
            if (response.ResponseUri.Scheme != Uri.UriSchemeHttps || response.ContentLength > limit)
                throw new InvalidDataException("GitHub вернул недопустимый ответ.");
            CopyLimited(source, destination, limit);
            return response.ResponseUri;
        }
    }

    private static void CopyLimited(Stream source, Stream destination, long limit)
    {
        var buffer = new byte[65536];
        int count;
        while ((count = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            limit -= count;
            if (limit < 0) throw new InvalidDataException("Размер данных превышает допустимый.");
            destination.Write(buffer, 0, count);
        }
    }

    public string DownloadPackage()
    {
        if (Download == null || Version <= CurrentVersion)
            throw new InvalidOperationException("Подходящий архив обновления отсутствует.");
        var id = Guid.NewGuid().ToString("N");
        var work = WorkPath(id);
        Directory.CreateDirectory(work);
        try
        {
            var zip = Path.Combine(work, "release.zip");
            using (var destination = File.Create(zip)) Fetch(Download, destination, DownloadLimit);
            ValidateDigest(zip, digest);
            File.WriteAllLines(Path.Combine(work, "release.txt"), new[] { Version.ToString(), digest });
            return id;
        }
        catch
        {
            RemoveWork(work);
            throw;
        }
    }

    public static void CancelPackage(string id)
    {
        if (id != null) RemoveWork(WorkPath(id));
    }

    public static void StartInstaller(string id)
    {
        var work = WorkPath(id);
        var helper = Path.Combine(work, "updater.exe");
        File.Copy(Application.ExecutablePath, helper, false);
        using (var started = new EventWaitHandle(false, EventResetMode.ManualReset, EventName(id, "started")))
        using (var parent = Process.GetCurrentProcess())
        using (var installer = StartProcess(helper, ApplyArgument + " " + id + " " + parent.Id))
        {
            if (!WaitForSignal(installer, started, 8000))
                throw new InvalidOperationException("Обновлятор не запустился.");
        }
    }

    public static bool IsInstalling()
    {
        Mutex mutex;
        if (!Mutex.TryOpenExisting(Gate, out mutex)) return false;
        mutex.Dispose();
        return true;
    }

    public static bool TryOpenReadySignal(string[] args, out EventWaitHandle ready)
    {
        ready = null;
        Guid id;
        return args.Length == 2 && args[0] == ReadyArgument && Guid.TryParseExact(args[1], "N", out id) &&
            EventWaitHandle.TryOpenExisting(EventName(id.ToString("N"), "ready"), out ready);
    }

    public static void Install(string id, int parentId)
    {
        string executable = null;
        string work = null;
        UpdateFiles transaction = null;
        Exception failure = null;
        bool committed = false;
        Mutex gate = null;
        try
        {
            work = WorkPath(id);
            if (!SamePath(Application.ExecutablePath, Path.Combine(work, "updater.exe")))
                throw new InvalidOperationException("Недопустимый путь обновлятора.");
            bool first;
            gate = new Mutex(true, Gate, out first);
            if (!first) throw new InvalidOperationException("Обновление уже выполняется.");
            using (var parent = Process.GetProcessById(parentId))
            using (var started = EventWaitHandle.OpenExisting(EventName(id, "started")))
            {
                var parentExecutable = parent.MainModule.FileName;
                if (!string.Equals(Path.GetFileName(parentExecutable), Launcher, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Обновление запущено неизвестным приложением.");
                started.Set();
                if (!parent.WaitForExit(20000))
                    throw new InvalidOperationException("Лаунчер не завершился. Обновление отменено.");
                executable = parentExecutable;
            }

            var release = File.ReadAllLines(Path.Combine(work, "release.txt"));
            Version expected;
            if (release.Length != 2 || !Version.TryParse(release[0], out expected) || expected <= CurrentVersion)
                throw new InvalidDataException("Номер версии обновления недопустим.");
            var zip = Path.Combine(work, "release.zip");
            ValidateDigest(zip, release[1]);
            var payload = Path.Combine(work, "payload");
            ExtractPackage(zip, payload);
            var metadata = FileVersionInfo.GetVersionInfo(Path.Combine(payload, Launcher));
            Version actual;
            if (metadata.ProductName != Program.Name ||
                !Version.TryParse(metadata.ProductVersion, out actual) || actual != expected)
                throw new InvalidDataException("Версия EXE не совпадает с версией релиза.");
            transaction = new UpdateFiles(Path.GetDirectoryName(executable), payload, work);
            transaction.Apply();
            using (var ready = new EventWaitHandle(false, EventResetMode.ManualReset, EventName(id, "ready")))
            using (var updated = StartProcess(executable, ReadyArgument + " " + id))
            {
                if (!WaitForSignal(updated, ready, 15000))
                {
                    if (!updated.HasExited)
                    {
                        updated.Kill();
                        if (!updated.WaitForExit(5000))
                            throw new IOException("Новая версия не завершилась. Резервная копия сохранена.");
                    }
                    throw new InvalidOperationException("Новая версия не подтвердила запуск.");
                }
            }
            committed = true;
            // Keep the completion marker until a later check can remove the running helper.
            try { File.WriteAllText(Path.Combine(work, "completed"), ""); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            RemoveWork(work, true);
        }
        catch (Exception error)
        {
            failure = error;
            if (transaction != null && !committed)
            {
                try { transaction.Rollback(); }
                catch (Exception rollbackError)
                {
                    executable = null;
                    failure = new IOException(error.Message + " Восстановление не завершилось: " + rollbackError.Message, error);
                }
            }
        }
        finally { if (gate != null) gate.Dispose(); }
        if (failure != null)
        {
            var backup = work == null ? null : Path.Combine(work, "backup");
            var recovery = backup != null && Directory.Exists(backup) ? "\nРезервная копия: " + backup : "";
            if (executable != null)
            {
                try { using (StartProcess(executable, "")) { } }
                catch (Exception error) { recovery += "\nНе удалось открыть предыдущую версию: " + error.Message; }
            }
            MessageBox.Show("Обновление не установлено. " + failure.Message + recovery,
                Program.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static Process StartProcess(string executable, string arguments)
    {
        return Process.Start(new ProcessStartInfo(executable, arguments) {
            UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)
        });
    }

    private static bool WaitForSignal(Process process, EventWaitHandle signal, int milliseconds)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < milliseconds && !process.HasExited)
        {
            if (signal.WaitOne(100)) return !process.HasExited;
        }
        return false;
    }

    private static string WorkPath(string id)
    {
        Guid parsed;
        if (!Guid.TryParseExact(id, "N", out parsed)) throw new ArgumentException("Недопустимый идентификатор обновления.");
        return Path.Combine(WorkRoot, parsed.ToString("N"));
    }

    private static string EventName(string id, string kind) { return Gate + "." + id + "." + kind; }

    private static void ValidateDigest(string file, string digest)
    {
        var match = DigestPattern.Match(digest ?? "");
        if (!match.Success || !string.Equals(match.Groups[1].Value, Hash(file), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Контрольная сумма ZIP не совпадает с опубликованной на GitHub.");
    }

    private static string Hash(string path)
    {
        using (var file = File.OpenRead(path))
        using (var algorithm = SHA256.Create())
            return BitConverter.ToString(algorithm.ComputeHash(file)).Replace("-", "");
    }

    private static void ExtractPackage(string zip, string payload)
    {
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Read, Encoding.GetEncoding(866)))
        {
            var entries = archive.Entries.Where(entry => entry.Name.Length != 0).ToArray();
            if (entries.Length == 0 || entries.Length > 2048 || entries.Sum(entry => entry.Length) > DownloadLimit)
                throw new InvalidDataException("Недопустимый размер или состав ZIP.");
            const string prefix = "Omnissiah Breakwall/";
            var nested = entries.All(entry => entry.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            foreach (var entry in entries)
            {
                var name = nested ? entry.FullName.Substring(prefix.Length) : entry.FullName;
                var parts = name.Split('/');
                if (parts.Any(part => part.Length == 0 || part == "." || part == ".." ||
                    part.EndsWith(".") || part.EndsWith(" ") || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    Regex.IsMatch(part, @"\A(CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³])(\.|\z)", RegexOptions.IgnoreCase)))
                    throw new InvalidDataException("В ZIP обнаружен недопустимый путь.");
                if (parts.Length == 1 ? !PackageRootFiles.Contains(name, StringComparer.OrdinalIgnoreCase) :
                    !PackageDirectories.Contains(parts[0], StringComparer.OrdinalIgnoreCase))
                    throw new InvalidDataException("В ZIP есть файл вне структуры portable-сборки: " + name);
                var target = Path.Combine(payload, Path.Combine(parts));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                using (var source = entry.Open())
                using (var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
                    CopyLimited(source, destination, entry.Length);
            }
        }
        foreach (var required in new[] { Launcher, @"engine\zapret2\winws2.exe", @"engine\WinDivert64.sys",
            @"engine\zapret2\WinDivert.dll", @"engine\cygwin1.dll", @"engine\zapret2\zapret-lib.lua",
            @"engine\zapret2\zapret-antidpi.lua", @"engine\zapret2\zapret-auto.lua", @"profiles\ds-strategies.args",
            @"profiles\darktide.txt", @"profiles\space-marine-2.txt", @"profiles\discord.txt", @"profiles\youtube-core.txt",
            @"profiles\youtube.txt", @"profiles\warp.txt", @"profiles\warp-masque.txt", @"profiles\warp-api.txt",
            @"profiles\warp-wireguard.txt", @"profiles\space-marine-2-ip.txt", @"profiles\space-marine-2-tcp-ip.txt",
            @"profiles\space-marine-2-udp-ip.txt", @"warp\usque.exe", @"warp\wintun.dll" }.Concat(Zapret2.RequiredFiles))
        {
            if (!File.Exists(Path.Combine(payload, required)))
                throw new InvalidDataException("Из ZIP отсутствует обязательный файл: " + required);
        }
        if (RetiredFiles.Any(file => File.Exists(Path.Combine(payload, file))))
            throw new InvalidDataException("В ZIP остались файлы прежнего движка. Нужна полная сборка zapret2.");
    }

    private static bool SamePath(string first, string second)
    {
        return string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
    }

    private static void ClearCompletedUpdates()
    {
        if (!Directory.Exists(WorkRoot)) return;
        try
        {
            foreach (var work in Directory.EnumerateDirectories(WorkRoot))
            {
                Guid id;
                if (Guid.TryParseExact(Path.GetFileName(work), "N", out id) && File.Exists(Path.Combine(work, "completed")))
                    RemoveWork(work);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void RemoveWork(string work, bool keepHelper = false)
    {
        if (!Directory.Exists(work)) return;
        try
        {
            if (!keepHelper) Directory.Delete(work, true);
            else
            {
                foreach (var directory in Directory.EnumerateDirectories(work))
                    Directory.Delete(directory, true);
                foreach (var file in Directory.EnumerateFiles(work))
                {
                    var name = Path.GetFileName(file);
                    if (name != "updater.exe" && name != "completed") File.Delete(file);
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class ReleaseInfo
    {
        public string tag_name { get; set; }
        public bool draft { get; set; }
        public bool prerelease { get; set; }
        public Asset[] assets { get; set; }
    }

    private sealed class Asset
    {
        public string name { get; set; }
        public string browser_download_url { get; set; }
        public string digest { get; set; }
    }

    private sealed class UpdateFiles
    {
        private readonly string root, payload, backup;
        private readonly List<string> changes;
        private readonly List<string> removals;
        private readonly List<string> applied = new List<string>();

        public UpdateFiles(string root, string payload, string work)
        {
            this.root = root;
            this.payload = payload;
            backup = Path.Combine(work, "backup");
            changes = new List<string>();
            foreach (var source in Directory.EnumerateFiles(payload, "*", SearchOption.AllDirectories))
            {
                var relative = source.Substring(payload.Length + 1);
                var target = Path.Combine(root, relative);
                RefuseLinkedTarget(target);
                if (!File.Exists(target) || Hash(target) != Hash(source)) changes.Add(relative);
            }
            removals = RetiredFiles.Where(relative => File.Exists(Path.Combine(root, relative))).ToList();
            foreach (var relative in removals) RefuseLinkedTarget(Path.Combine(root, relative));
            const string driver = @"engine\WinDivert64.sys";
            if (changes.Contains(driver, StringComparer.OrdinalIgnoreCase))
                CheckDriverFile(Path.Combine(root, driver));
            File.WriteAllLines(Path.Combine(work, "changes.txt"), new[] { root }.Concat(changes).Concat(removals.Select(file => "DELETE " + file)));
            foreach (var relative in changes.Concat(removals))
            {
                var target = Path.Combine(root, relative);
                if (!File.Exists(target)) continue;
                var original = Path.Combine(backup, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(original));
                File.Copy(target, original);
            }
        }

        public void Apply()
        {
            foreach (var relative in changes)
            {
                Replace(Path.Combine(payload, relative), Path.Combine(root, relative));
                applied.Add(relative);
            }
            foreach (var relative in removals)
            {
                File.Delete(Path.Combine(root, relative));
                applied.Add(relative);
            }
        }

        public void Rollback()
        {
            var failures = new List<string>();
            for (var index = applied.Count - 1; index >= 0; index--)
            {
                var relative = applied[index];
                try
                {
                    var original = Path.Combine(backup, relative);
                    var target = Path.Combine(root, relative);
                    if (File.Exists(original)) Replace(original, target);
                    else File.Delete(target);
                }
                catch (Exception error) { failures.Add(relative + " — " + error.Message); }
            }
            if (failures.Count > 0) throw new IOException(string.Join("\n", failures));
        }

        private static void Replace(string source, string target)
        {
            RefuseLinkedTarget(target);
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.Copy(source, temporary);
                if (File.Exists(target)) File.Replace(temporary, target, null);
                else File.Move(temporary, target);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    try { File.Delete(temporary); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        private static void RefuseLinkedTarget(string path)
        {
            for (var item = path; item != null; item = Path.GetDirectoryName(item))
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(item); }
                catch (FileNotFoundException) { continue; }
                catch (DirectoryNotFoundException) { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0 &&
                    (attributes & FileAttributes.Offline) != 0)
                    throw new IOException("Файл обновления доступен только в облаке: " + item);
                // OneDrive's local placeholder reparse points do not redirect the path.
                if ((attributes & FileAttributes.ReparsePoint) != 0 && IsLink(item))
                    throw new IOException("Обновление через перенаправленный путь недоступно: " + item);
            }
        }

        private static bool IsLink(string path)
        {
            var data = new FindData();
            var handle = FindFirstFile(path, out data);
            if (handle == new IntPtr(-1)) throw new System.ComponentModel.Win32Exception();
            FindClose(handle);
            return (data.ReparseTag & 0x20000000u) != 0;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct FindData
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, AccessTime, WriteTime;
            public uint SizeHigh, SizeLow, ReparseTag, Reserved;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string FileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
            public string AlternateName;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindFirstFile(string path, out FindData data);

        [DllImport("kernel32.dll")]
        private static extern bool FindClose(IntPtr handle);

        private static void CheckDriverFile(string path)
        {
            if (!File.Exists(path)) return;
            try
            {
                using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            }
            catch (IOException error)
            {
                if ((error.HResult & 0xFFFF) != 32 && (error.HResult & 0xFFFF) != 33) throw;
                throw new IOException("Windows удерживает WinDivert64.sys в папке программы. Перезагрузите Windows и повторите обновление до включения фильтра.", error);
            }
        }
    }
}
