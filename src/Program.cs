using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: AssemblyTitle(Program.Name)]
[assembly: AssemblyProduct(Program.Name)]
[assembly: AssemblyDescription(Program.Name)]

internal static class Program
{
    public const string Name = "Omnissiah Breakwall";

    [STAThread]
    private static void Main(string[] args)
    {
        int parent;
        if (args.Length == 3 && args[0] == ReleaseUpdate.ApplyArgument && int.TryParse(args[2], out parent))
        {
            ReleaseUpdate.Install(args[1], parent);
            return;
        }
        EventWaitHandle ready;
        var afterUpdate = ReleaseUpdate.TryOpenReadySignal(args, out ready);
        var fromWindows = Array.IndexOf(args, WindowsStartup.Argument) >= 0;
        using (ready)
        {
            if (!afterUpdate && ReleaseUpdate.IsInstalling())
            {
                if (!fromWindows) MessageBox.Show("Выполняется обновление. Подождите, пока программа откроется.", Name);
                return;
            }
            Run(fromWindows, ready);
        }
    }

    private static void Run(bool fromWindows, EventWaitHandle ready)
    {
        bool first;
        // Share the mutex with copies released under the former name.
        using (var instance = new Mutex(true, @"Local\DarktideDiscordPortable", out first))
        {
            if (!first)
            {
                if (!fromWindows)
                    MessageBox.Show("Приложение уже открыто. Если окно скрыто, откройте его через значок рядом с часами.", Name);
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var preferencesPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Name, "strategies.txt");
            using (var window = new MainWindow(preferencesPath, fromWindows))
            {
                if (ready != null)
                    window.Shown += (sender, args) => window.BeginInvoke(new Action(() => ready.Set()));
                Application.Run(window);
            }
        }
    }
}

internal sealed partial class MainWindow : Form
{
    private static readonly Color Background = Color.FromArgb(17, 17, 17);
    private static readonly Color Surface = Color.FromArgb(25, 25, 25);
    private static readonly Color Field = Color.FromArgb(36, 36, 36);
    private static readonly Color Border = Color.FromArgb(57, 57, 57);
    private static readonly Color Primary = Color.FromArgb(242, 242, 242);
    private static readonly Color Secondary = Color.FromArgb(170, 170, 170);
    private readonly Engine engine = new Engine();
    private readonly Warp warp = new Warp();
    private readonly PowerButton toggle = new PowerButton();
    private readonly PictureBox logo = new PictureBox();
    private readonly CheckBox useWarp = new AccentCheckBox { DimWhenDisabled = true };
    private readonly Button openStrategies = new DimmedSettingsButton();
    private readonly StrategyPicker<Strategy> darktideStrategy = new StrategyPicker<Strategy>("Darktide", Strategy.None);
    private readonly StrategyPicker<Strategy> spaceMarineStrategy = new StrategyPicker<Strategy>("Space Marine 2", Strategy.None);
    private readonly StrategyPicker<ServiceStrategy> discordStrategy = new StrategyPicker<ServiceStrategy>("Discord", ServiceStrategy.None);
    private readonly StrategyPicker<ServiceStrategy> youtubeStrategy = new StrategyPicker<ServiceStrategy>("YouTube", ServiceStrategy.None);
    private readonly StrategyPicker<WarpStrategy> warpStrategy = new StrategyPicker<WarpStrategy>("WARP", WarpStrategy.None);
    private readonly Label status = new Label();
    private readonly Label message = new Label();
    private readonly LinkLabel details = new LinkLabel();
    private readonly System.Windows.Forms.Timer health = new System.Windows.Forms.Timer();
    private readonly Panel mainPage = new Panel { Dock = DockStyle.Fill };
    private readonly Panel strategiesPage = new Panel { Dock = DockStyle.Fill, Visible = false };
    private readonly Panel settingsPage = new Panel { Dock = DockStyle.Fill, Visible = false };
    private readonly Panel privacyPage = new Panel { Dock = DockStyle.Fill, Visible = false };
    private readonly Panel instructionsPage = new Panel { Dock = DockStyle.Fill, Visible = false };
    private readonly Panel aboutPage = new Panel { Dock = DockStyle.Fill, Visible = false };
    private readonly Button navigation = new Button();
    private readonly CheckBox autoStart = new AccentCheckBox();
    private readonly CheckBox trayMode = new AccentCheckBox();
    private readonly CheckBox startFilter = new AccentCheckBox();
    private readonly Label settingsMessage = new Label();
    private readonly Label updateMessage = new Label();
    private readonly Button updateButton = new Button();
    private readonly NotifyIcon tray = new NotifyIcon();
    private readonly ContextMenuStrip trayMenu = new ContextMenuStrip();
    private readonly ToolStripMenuItem traySwitch = new ToolStripMenuItem();
    private readonly ToolStripMenuItem trayExit = new ToolStripMenuItem("Выход");
    private readonly ToolTip help = new ToolTip();
    private readonly string preferencesPath;
    private readonly bool fromWindows;
    private bool active;
    private bool busy;
    private CancellationTokenSource connectionAttempt;
    private bool allowClose;
    private bool exitRequested;
    private bool showingWindow;
    private bool checkingUpdate;
    private bool installingUpdate;
    private ReleaseUpdate availableUpdate;
    private string diagnostics;

    public MainWindow(string preferencesPath, bool fromWindows = false)
    {
        this.preferencesPath = preferencesPath;
        this.fromWindows = fromWindows;
        Text = Program.Name;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        ClientSize = new Size(448, 671);
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Background;
        ForeColor = Primary;
        Font = new Font("Segoe UI", 10);
        Controls.Add(mainPage);
        Controls.Add(strategiesPage);
        Controls.Add(settingsPage);
        Controls.Add(privacyPage);
        Controls.Add(instructionsPage);
        Controls.Add(aboutPage);

        logo.SetBounds(178, 23, 92, 92);
        logo.SizeMode = PictureBoxSizeMode.Zoom;
        using (var stream = typeof(Program).Assembly.GetManifestResourceStream("omnissiah.png"))
        using (var artwork = Image.FromStream(stream))
            logo.Image = new Bitmap(artwork);
        mainPage.Controls.Add(logo);

        var title = new Label {
            Text = Program.Name, TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI Semibold", 23)
        };
        title.SetBounds(24, 121, 400, 45);
        mainPage.Controls.Add(title);

        status.SetBounds(24, 306, 400, 25);
        status.TextAlign = ContentAlignment.MiddleCenter;
        status.Font = new Font("Segoe UI Semibold", 9);
        mainPage.Controls.Add(status);

        toggle.SetBounds(161, 176, 126, 126);
        toggle.FlatStyle = FlatStyle.Flat;
        toggle.FlatAppearance.BorderSize = 0;
        toggle.Cursor = Cursors.Hand;
        toggle.Font = new Font("Segoe UI Semibold", 10);
        toggle.Click += async (sender, args) => await Switch();
        mainPage.Controls.Add(toggle);

        var settings = new Panel { BackColor = Surface };
        settings.SetBounds(38, 343, 372, 141);
        mainPage.Controls.Add(settings);

        openStrategies.Text = "Настройка стратегий";
        openStrategies.FlatStyle = FlatStyle.Flat;
        openStrategies.Cursor = Cursors.Hand;
        openStrategies.BackColor = Field;
        openStrategies.ForeColor = Primary;
        openStrategies.FlatAppearance.BorderColor = Border;
        openStrategies.FlatAppearance.MouseOverBackColor = Border;
        openStrategies.SetBounds(12, 12, 348, 34);
        openStrategies.Click += (sender, args) => ShowPage(strategiesPage);
        settings.Controls.Add(openStrategies);

        StyleAutoButton(openAuto);
        openAuto.Text = "Автоподбор";
        openAuto.SetBounds(12, 54, 348, 34);
        openAuto.Click += (sender, args) => ShowPage(autoPage);
        settings.Controls.Add(openAuto);

        useWarp.Text = "Подключить WARP";
        useWarp.AutoSize = true;
        useWarp.Location = new Point(0, 100);
        settings.Controls.Add(useWarp);
        settings.Layout += (sender, args) =>
            useWarp.Left = (settings.ClientSize.Width - useWarp.Width) / 2;
        settings.PerformLayout();

        message.SetBounds(24, 494, 400, 48);
        message.TextAlign = ContentAlignment.MiddleCenter;
        message.ForeColor = Secondary;
        message.Font = new Font("Segoe UI", 9);
        mainPage.Controls.Add(message);

        details.Text = "Подробности";
        details.SetBounds(24, 549, 400, 18);
        details.TextAlign = ContentAlignment.MiddleCenter;
        details.Font = new Font("Segoe UI", 8);
        details.LinkColor = Secondary;
        details.ActiveLinkColor = Primary;
        details.LinkClicked += (sender, args) => MessageBox.Show(this,
            diagnostics + Environment.NewLine + Environment.NewLine + engine.Details,
            "Подробности", MessageBoxButtons.OK, MessageBoxIcon.Information);
        mainPage.Controls.Add(details);
        details.BringToFront();

        var motto = new Label {
            Text = "Слава Омниссии!", TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Secondary, Font = new Font("Segoe UI Semibold", 10)
        };
        motto.SetBounds(24, 628, 400, 24);
        mainPage.Controls.Add(motto);

        ConfigureNavigation();
        BuildStrategiesPage();
        BuildAutoPage();
        BuildSettingsPage();
        BuildPrivacyPage();
        BuildInstructionsPage();
        BuildAboutPage();
        ConfigureTray();

        health.Interval = 1200;
        health.Tick += async (sender, args) =>
        {
            if (busy || installingUpdate || (!active && !warp.OwnsConnection)) return;
            if (!active || engine.Running)
            {
                if (warp.BuiltInNeedsStop)
                {
                    busy = true;
                    RefreshControls();
                    try
                    {
                        await warp.Disconnect();
                        message.Text = active ? "Туннель WARP остановлен. Фильтр работает." : "Туннель WARP остановлен.";
                    }
                    catch (Exception error)
                    {
                        diagnostics = error.ToString();
                        message.Text = "Ошибка остановки туннеля WARP.";
                    }
                    finally { busy = false; RefreshControls(); }
                }
                return;
            }
            diagnostics = "Процесс сетевого фильтра завершился.";
            await StopSession();
            if (!warp.OwnsConnection)
                message.Text = "Фильтр остановился. Откройте подробности.";
        };
        health.Start();
        message.Text = IdleMessage;
        RefreshControls();
        RestorePreferences();
        darktideStrategy.SelectionChanged += SavePreferences;
        spaceMarineStrategy.SelectionChanged += SavePreferences;
        discordStrategy.SelectionChanged += SavePreferences;
        youtubeStrategy.SelectionChanged += SavePreferences;
        warpStrategy.SelectionChanged += SavePreferences;
        useWarp.CheckedChanged += SavePreferences;
        trayMode.CheckedChanged += (sender, args) =>
        {
            tray.Visible = trayMode.Checked;
            SavePreferences(sender, args);
        };
        startFilter.CheckedChanged += SavePreferences;
        tray.Visible = trayMode.Checked;
        if (fromWindows && trayMode.Checked)
        {
            ShowInTaskbar = false;
            Opacity = 0;
        }
    }

    private void BuildStrategiesPage()
    {
        AddInformationHeading(strategiesPage, "Настройка стратегий");

        AddGameStrategies(darktideStrategy, "DT", "Darktide");
        darktideStrategy.Restore(Strategy.Standard.ToString());
        AddGameStrategies(spaceMarineStrategy, "SM", "Space Marine 2");
        AddStrategyGroup("Игры", 99, darktideStrategy, spaceMarineStrategy);

        discordStrategy.Add(ServiceStrategy.Standard, "DS01 - Discord · основной");
        discordStrategy.Add(ServiceStrategy.BadSequence, "DS02 - Discord · TLS со смещением");
        discordStrategy.Add(ServiceStrategy.TimestampFake, "DS03 - Discord · TLS с метками времени");
        discordStrategy.Add(ServiceStrategy.HostSplit, "DS04 - Discord · подмена имени хоста");
        discordStrategy.Add(ServiceStrategy.TimestampOverlap, "DS05 - Discord · TLS с перекрытием");
        discordStrategy.Add(ServiceStrategy.SimpleFake, "DS06 - Discord · простой TLS");
        discordStrategy.Add(ServiceStrategy.SniSplit, "DS07 - Discord · разбиение по SNI");
        discordStrategy.AddImportedStrategy("DS", "Discord", "ALT12");
        discordStrategy.AddImportedStrategy("DS", "Discord", "ALT13");
        discordStrategy.AddImportedStrategy("DS", "Discord", "EXP");
        discordStrategy.AddNumbered("DS", "Discord", ServiceStrategy.AlternateTls, "альтернативный TLS", "LegacyAlternateTls");
        discordStrategy.AddNumbered("DS", "Discord", ServiceStrategy.Zapret2DiscordExp, "копия ClientHello");
        discordStrategy.AddNumbered("DS", "Discord", ServiceStrategy.Zapret2DiscordCircular, "смена TLS-схемы");
        discordStrategy.Restore(ServiceStrategy.Standard.ToString());

        youtubeStrategy.Add(ServiceStrategy.Standard, "YT01 - YouTube · основной");
        youtubeStrategy.Add(ServiceStrategy.TimestampFake, "YT02 - YouTube · TLS с метками времени");
        youtubeStrategy.Add(ServiceStrategy.HostSplit, "YT03 - YouTube · подмена имени хоста");
        youtubeStrategy.Add(ServiceStrategy.TimestampOverlap, "YT04 - YouTube · TLS с перекрытием");
        youtubeStrategy.Add(ServiceStrategy.SimpleFake, "YT05 - YouTube · простой TLS");
        youtubeStrategy.Add(ServiceStrategy.SniSplit, "YT06 - YouTube · разбиение по SNI");
        youtubeStrategy.AddImportedStrategy("YT", "YouTube", "GENERAL", true, "DS:ALT6");
        youtubeStrategy.AddImportedStrategy("YT", "YouTube", "ALT11", true);
        youtubeStrategy.AddImportedStrategy("YT", "YouTube", "ALT12", true, "DS:EXP");
        youtubeStrategy.AddImportedStrategy("YT", "YouTube", "SIMPLE FAKE ALT2", true, "DS:ALT10");
        youtubeStrategy.AddNumbered("YT", "YouTube", ServiceStrategy.Zapret2YoutubeCircular, "смена TLS-схемы");
        youtubeStrategy.AddNumbered("YT", "YouTube", ServiceStrategy.Zapret2YoutubeExp, "перестановка частей TLS");
        youtubeStrategy.Restore(ServiceStrategy.Standard.ToString());

        warpStrategy.Add(WarpStrategy.Standard, "W01 - WARP · основной");
        warpStrategy.Add(WarpStrategy.QuicBurst, "W02 - WARP · усиленный QUIC");
        warpStrategy.Add(WarpStrategy.MasqueTls, "W03 - WARP · подмена TLS MASQUE");
        warpStrategy.AddNumbered("W", "WARP", WarpStrategy.Circular, "смена TLS-схемы");
        warpStrategy.AddNumbered("W", "WARP", WarpStrategy.CircularStun, "смена TLS-схемы и STUN");
        warpStrategy.AddNumbered("W", "WARP", WarpStrategy.DoubleFakeSequence, "двойной шаблон и TCP Seq/ACK");
        warpStrategy.AddNumbered("W", "WARP", WarpStrategy.DoubleFakeSteam, "двойной шаблон Steam Chat");
        warpStrategy.AddNumbered("W", "WARP", WarpStrategy.DoubleFakeTimestamp, "двойной шаблон с метками времени");
        warpStrategy.Restore(WarpStrategy.Standard.ToString());
        AddStrategyGroup("Сервисы", 231, discordStrategy, youtubeStrategy, warpStrategy);

        var warpHint = new Label {
            Text = "Стратегия WARP применяется, если на главном экране отмечено «Подключить WARP».",
            ForeColor = Secondary, Font = new Font("Segoe UI", 9)
        };
        warpHint.SetBounds(34, 407, 380, 45);
        strategiesPage.Controls.Add(warpHint);
    }

    private void AddStrategyGroup(string title, int top, params Button[] pickers)
    {
        var group = new Panel { BackColor = Surface, AccessibleName = title };
        group.SetBounds(34, top, 380, 41 + 38 * pickers.Length);
        var heading = new Label { Text = title, Font = new Font("Segoe UI Semibold", 11) };
        heading.SetBounds(12, 9, 356, 24);
        group.Controls.Add(heading);
        for (var index = 0; index < pickers.Length; index++)
        {
            pickers[index].SetBounds(12, 36 + 38 * index, 356, 32);
            group.Controls.Add(pickers[index]);
        }
        strategiesPage.Controls.Add(group);
    }

    private static void AddGameStrategies(StrategyPicker<Strategy> picker, string prefix, string game)
    {
        picker.AddNumbered(prefix, game, Strategy.Standard, "основной STUN");
        picker.AddNumbered(prefix, game, Strategy.Alternate, "усиленный STUN");
        picker.AddNumbered(prefix, game, Strategy.FlowAlt2, "короткий игровой UDP",
            "FlowAlt7", "FlowAlt10", "DS:GENERAL", "DS:ALT2", "DS:ALT6", "DS:ALT7", "DS:ALT8", "DS:ALT9", "DS:ALT10");
        picker.AddNumbered(prefix, game, Strategy.FlowAlt3, "длинный игровой UDP",
            "FlowAlt11", "DS:ALT3", "DS:ALT11", "DS:ALT12", "DS:ALT13");
        picker.AddNumbered(prefix, game, Strategy.FlowExp, "QUIC + игровой UDP", "DS:EXP");
        picker.AddNumbered(prefix, game, Strategy.FlowSimpleAlt2, "UDP · 12 повторов",
            "FlowSimple", "DS:ALT", "DS:SIMPLE FAKE", "DS:SIMPLE FAKE ALT2");
        picker.AddNumbered(prefix, game, Strategy.Combined, "STUN + игровой UDP");
        picker.AddNumbered(prefix, game, Strategy.FlowAlt12, "QUIC dbankcloud");
        picker.AddNumbered(prefix, game, Strategy.CommunityAlt10, "QUIC с AutoTTL");
        picker.AddNumbered(prefix, game, Strategy.ActiveGameShortTen, "короткий UDP · 10 повторов",
            "DS:ALT4", "DS:FAKE TLS AUTO", "DS:FAKE TLS AUTO ALT", "DS:FAKE TLS AUTO ALT2", "DS:FOR ALL", "DS:SIMPLE FAKE ALT");
        picker.AddNumbered(prefix, game, Strategy.ActiveGameMediumFourteen, "UDP · 14 повторов", "DS:ALT5");
        picker.AddNumbered(prefix, game, Strategy.ActiveGameMediumTen, "UDP · 10 повторов", "DS:FAKE TLS AUTO ALT3");
        picker.AddNumbered(prefix, game, Strategy.ActiveGameSix, "UDP · 6 повторов");
        picker.AddNumbered(prefix, game, Strategy.ActiveGameEight, "UDP · 8 повторов, 3 пакета", "Zapret2Circular");
        picker.AddNumbered(prefix, game, Strategy.ActiveGameFour, "UDP · 4 повтора");
        picker.AddNumbered(prefix, game, Strategy.Zapret2Exp, "UDP · два шаблона");
        picker.AddNumbered(prefix, game, Strategy.Zapret2DoubleFake, "UDP · 12 повторов, 3 пакета");
        picker.AddNumbered(prefix, game, Strategy.DbankShort, "короткий dbankcloud");
        picker.AddNumbered(prefix, game, Strategy.ActiveGameMinimal, "минимальный игровой UDP");
        picker.AddNumbered(prefix, game, Strategy.TlsSmallOverlap, "TLS с малым перекрытием");
        picker.AddNumbered(prefix, game, Strategy.TlsMd5Disorder, "TLS fakeddisorder + MD5");
        picker.AddNumbered(prefix, game, Strategy.Zapret2TlsClone, "копия ClientHello");
        picker.AddNumbered(prefix, game, Strategy.Zapret2TlsCircular, "смена TLS-схемы");
        picker.AddNumbered(prefix, game, Strategy.FlowsealLongTen, "UDP · 10 повторов, 4 пакета");
        picker.AddNumbered(prefix, game, Strategy.FlowsealLongFourteen, "UDP · 14 повторов, 3 пакета");
        picker.AddNumbered(prefix, game, Strategy.FlowsealDualFour, "QUIC + игровой UDP, 4 пакета");
    }

    private void ConfigureNavigation()
    {
        navigation.FlatStyle = FlatStyle.Flat;
        navigation.ForeColor = Primary;
        navigation.BackColor = Surface;
        navigation.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        navigation.Cursor = Cursors.Hand;
        navigation.Font = new Font("Segoe MDL2 Assets", 13);
        navigation.FlatAppearance.BorderColor = Border;
        navigation.FlatAppearance.MouseOverBackColor = Border;
        navigation.SetBounds(390, 20, 38, 38);
        navigation.Click += (sender, args) =>
        {
            if (mainPage.Visible) ReadStartupSetting();
            ShowPage(settingsPage.Visible || strategiesPage.Visible || autoPage.Visible ? mainPage : settingsPage);
        };
        Controls.Add(navigation);
        navigation.BringToFront();
        ShowPage(mainPage);
    }

    private void ShowPage(Panel page)
    {
        mainPage.Visible = page == mainPage;
        strategiesPage.Visible = page == strategiesPage;
        settingsPage.Visible = page == settingsPage;
        privacyPage.Visible = page == privacyPage;
        instructionsPage.Visible = page == instructionsPage;
        aboutPage.Visible = page == aboutPage;
        autoPage.Visible = page == autoPage;
        navigation.Text = page == mainPage ? "\uE713" : "\uE72B";
        navigation.AccessibleName = page == mainPage ? "Настройки" :
            page == privacyPage || page == instructionsPage || page == aboutPage ? "Назад в настройки" : "Назад";
        help.SetToolTip(navigation, navigation.AccessibleName);
    }

    private void BuildSettingsPage()
    {
        var heading = new Label {
            Text = "Настройки", Font = new Font("Segoe UI Semibold", 23),
            TextAlign = ContentAlignment.MiddleLeft
        };
        heading.SetBounds(34, 27, 320, 43);
        settingsPage.Controls.Add(heading);

        var card = new Panel { BackColor = Surface };
        const int rowHeight = 60;
        card.SetBounds(34, 102, 380, rowHeight * 3);
        settingsPage.Controls.Add(card);
        AddSetting(card, autoStart, 0, "Автозапуск с Windows",
            "Запускает приложение при входе в вашу учётную запись Windows.");
        AddSetting(card, trayMode, rowHeight, "Фоновый режим",
            "Скрывает окно рядом с часами при сворачивании или закрытии. Фильтр продолжает работать.");
        AddSetting(card, startFilter, rowHeight * 2, "Включать фильтр при запуске с Windows",
            "Включает фильтр с сохранёнными стратегиями и настройкой WARP при входе в Windows. Требует автозапуска.");
        settingsMessage.SetBounds(34, 293, 380, 90);
        settingsMessage.ForeColor = Secondary;
        settingsPage.Controls.Add(settingsMessage);
        BuildUpdateControls();

        var footer = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 154, ColumnCount = 1, RowCount = 5 };
        for (var index = 0; index < 4; index++)
            footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
        footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 23));
        AddSettingsPageLink(footer, 0, "Инструкция", instructionsPage);
        AddSettingsPageLink(footer, 1, "О приложении", aboutPage);
        AddSettingsPageLink(footer, 2, "Политика конфиденциальности", privacyPage);
        var source = PageLink("Исходный код");
        source.Dock = DockStyle.Fill;
        source.LinkClicked += (sender, args) => OpenLink("https://github.com/a1deus/Omnissiah-Breakwall");
        footer.Controls.Add(source, 0, 3);
        footer.Controls.Add(new Label {
            Text = "v" + typeof(Program).Assembly.GetName().Version.ToString(3),
            TextAlign = ContentAlignment.MiddleCenter, ForeColor = Secondary, Dock = DockStyle.Fill
        }, 0, 4);
        settingsPage.Controls.Add(footer);

        startFilter.Enabled = false;
        autoStart.Click += (sender, args) =>
        {
            try
            {
                WindowsStartup.SetEnabled(autoStart.Checked, Application.ExecutablePath);
                startFilter.Enabled = autoStart.Checked;
                settingsMessage.Text = "";
            }
            catch (Exception error)
            {
                ReadStartupSetting();
                settingsMessage.Text = "Не удалось изменить автозапуск. " + error.Message;
            }
        };
    }

    private void BuildUpdateControls()
    {
        updateButton.Text = "Проверить обновления";
        updateButton.FlatStyle = FlatStyle.Flat;
        updateButton.FlatAppearance.BorderColor = Border;
        updateButton.FlatAppearance.MouseOverBackColor = Border;
        updateButton.BackColor = Field;
        updateButton.SetBounds(34, 383, 222, 34);
        updateButton.Click += async (sender, args) => await CheckOrInstallUpdate();
        settingsPage.Controls.Add(updateButton);

        var releases = PageLink("Релизы на GitHub");
        releases.TextAlign = ContentAlignment.MiddleRight;
        releases.SetBounds(257, 383, 157, 34);
        releases.LinkClicked += (sender, args) => OpenLink(ReleaseUpdate.ReleasesUrl);
        settingsPage.Controls.Add(releases);

        updateMessage.ForeColor = Secondary;
        updateMessage.SetBounds(34, 428, 380, 88);
        settingsPage.Controls.Add(updateMessage);
    }

    private async Task CheckOrInstallUpdate()
    {
        if (checkingUpdate || busy || installingUpdate) return;
        if (availableUpdate == null)
        {
            checkingUpdate = true;
            updateMessage.Text = "Проверяем GitHub Releases…";
            RefreshControls();
            try
            {
                var release = await Task.Run(() => ReleaseUpdate.Check());
                if (release.Version <= ReleaseUpdate.CurrentVersion)
                    updateMessage.Text = "У вас актуальная версия.";
                else if (release.Download == null)
                    updateMessage.Text = "Доступна v" + release.Version.ToString(3) +
                        ". Архив с контрольной суммой можно проверить на странице релизов.";
                else
                {
                    availableUpdate = release;
                    updateMessage.Text = "Доступна версия v" + release.Version.ToString(3) + ".";
                }
            }
            catch (Exception error) { updateMessage.Text = "Проверка недоступна. " + error.Message; }
            finally
            {
                checkingUpdate = false;
                RefreshControls();
            }
            return;
        }

        if (MessageBox.Show(this, "Установить v" + availableUpdate.Version.ToString(3) +
            "? Приложение остановит фильтр и запустится заново после обновления.",
            Program.Name, MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.OK)
            return;

        installingUpdate = true;
        RefreshControls();
        string package = null;
        try
        {
            updateMessage.Text = "Скачиваем и проверяем архив…";
            var release = availableUpdate;
            package = await Task.Run(() => release.DownloadPackage());
            updateMessage.Text = "Подготавливаем замену файлов…";
            if ((active || warp.OwnsConnection) && !await StopSession())
                throw new InvalidOperationException("Не удалось остановить WARP.");
            await Task.Run(() => ReleaseUpdate.StartInstaller(package));
            allowClose = exitRequested = true;
            Close();
        }
        catch (Exception error)
        {
            ReleaseUpdate.CancelPackage(package);
            updateMessage.Text = "Обновление не началось. " + error.Message;
        }
        finally
        {
            installingUpdate = false;
            if (!IsDisposed) RefreshControls();
        }
    }

    private void AddSettingsPageLink(TableLayoutPanel footer, int row, string text, Panel page)
    {
        var link = PageLink(text);
        link.Dock = DockStyle.Fill;
        link.LinkClicked += (sender, args) =>
        {
            ShowPage(page);
            if (!page.SelectNextControl(null, true, true, true, false)) navigation.Focus();
        };
        footer.Controls.Add(link, 0, row);
    }

    private void BuildPrivacyPage()
    {
        AddInformationHeading(privacyPage, "Политика\nконфиденциальности");
        var sections = AddInformationScroll(privacyPage, "Политика конфиденциальности");
        var content = new RichTextBox {
            BackColor = Background, ForeColor = Primary, BorderStyle = BorderStyle.None,
            ReadOnly = true, WordWrap = true, DetectUrls = false, ScrollBars = RichTextBoxScrollBars.None,
            AccessibleName = "Текст политики конфиденциальности", Size = new Size(380, 340), Margin = Padding.Empty
        };
        content.ContentsResized += (sender, args) => content.Height = args.NewRectangle.Height + 4;
        content.Text =
            "Лаунчер хранит выбранные стратегии и состояние галочки WARP локально в профиле Windows. " +
            "Лаунчер не отправляет эти настройки или диагностические отчёты разработчику.\n\n" +
            "При нажатии «Проверить обновления» лаунчер запрашивает сведения о последнем релизе у GitHub. " +
            "Если пользователь подтверждает установку, он скачивает архив из GitHub Releases. " +
            "GitHub получает IP соединения и обычные данные HTTP-запроса.\n\n" +
            "При запуске автоподбора лаунчер делает тестовые запросы к сервисам выбранных групп. " +
            "Эти сервисы получают IP соединения. Список результатов остаётся в памяти приложения и не отправляется разработчику.\n\n" +
            "Для UDP-проверки Darktide лаунчер читает адреса серверов только из строк подключения в недавних локальных логах игры. " +
            "Логи не копируются. Найденные адреса используются в памяти для коротких UDP-запросов со случайным идентификатором; " +
            "игровые билеты и идентификатор аккаунта не используются. Адреса и ответы не сохраняются и не отправляются разработчику.\n\n" +
            "При включённом обходе Space Marine 2 лаунчер локально распознаёт её диагностические UDP-запросы и запоминает адреса узлов. " +
            "Кэш адресов со сроком годности сутки шифруется Windows в профиле пользователя. Для работающего фильтра создаётся временный список адресов " +
            "в том же профиле; он удаляется при штатной остановке. Эти данные не включаются в папку программы и не отправляются разработчику. " +
            "Содержимое пакетов, случайные идентификаторы запросов и данные аккаунта не сохраняются.\n\n" +
            "Если пользователь включает WARP, клиент связывается с Cloudflare для подключения, " +
            "а трафик, направленный в туннель, проходит через сеть Cloudflare. При первой регистрации " +
            "встроенного клиента Cloudflare получает IP соединения и данные для регистрации устройства. " +
            "Регистрационные данные встроенного WARP хранятся в профиле Windows в зашифрованном виде. " +
            "При использовании официального клиента данные хранятся у него.";
        sections.Controls.Add(content);

        var cloudflare = PageLink("Политика конфиденциальности Cloudflare WARP");
        cloudflare.TextAlign = ContentAlignment.MiddleLeft;
        cloudflare.Size = new Size(380, 30);
        cloudflare.Margin = new Padding(0, 10, 0, 0);
        cloudflare.LinkClicked += (sender, args) => OpenLink("https://developers.cloudflare.com/warp-client/privacy/");
        sections.Controls.Add(cloudflare);

        var github = PageLink("Политика конфиденциальности GitHub");
        github.TextAlign = ContentAlignment.MiddleLeft;
        github.Size = new Size(380, 30);
        github.Margin = Padding.Empty;
        github.LinkClicked += (sender, args) => OpenLink("https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement");
        sections.Controls.Add(github);
    }

    private void BuildInstructionsPage()
    {
        AddInformationHeading(instructionsPage, "Инструкция");
        var sections = AddInformationScroll(instructionsPage, "Разделы инструкции");

        AddInstructionSection(sections, "Установка и первый запуск",
            "Распакуйте весь архив в отдельную папку. Не запускайте EXE прямо из архива и не переносите его отдельно от остальных файлов. " +
            "Откройте Omnissiah Breakwall.exe и подтвердите запрос Windows на запуск от имени администратора. " +
            "Нажмите «Настройка стратегий», выберите нужные игры и сервисы, затем вернитесь на главный экран и нажмите «Включить». " +
            "Дождитесь сообщения о запуске и откройте игру или сервис. Пока пользуетесь ими, оставляйте фильтр включённым.");
        AddInstructionSection(sections, "Выбор стратегии",
            "Начните с основного варианта для нужной игры или сервиса. Ненужные группы можно перевести в «Выключено». " +
            "Если подключение не работает, остановите фильтр, выберите другую стратегию для проблемной группы и включите его снова. " +
            "Проверяйте результат в самой игре или приложении. Для Discord отдельно проверьте сообщения, изображения и голос. " +
            "Приложение запоминает выбранные варианты. Во время подключения повторное нажатие на «Подождите» отменяет попытку.");
        AddInstructionSection(sections, "Автоподбор",
            "Остановите фильтр и откройте «Автоподбор» на главном экране. Отметьте нужные группы и нажмите «Запустить подбор». " +
            "Ход проверки показан в процентах; той же кнопкой можно остановить подбор. После завершения появятся прошедшие проверку варианты. " +
            "Кнопка «Применить» сохраняет вариант в настройках. Чтобы запустить его, вернитесь на главный экран и нажмите «Включить». " +
            "Для Darktide проверяются TLS-сервисы и UDP-ответ игровых серверов. Адреса автоматически берутся из недавних локальных логов: " +
            "если адресов нет, запустите игру до попытки входа в хаб, закройте её и повторите подбор. Загрузка логов и ввод IP не нужны. " +
            "Для Space Marine 2 проверяются TLS и UDP-эхо диагностических узлов. Перед первым подбором включите обход для игры, " +
            "запустите её до меню, затем закройте игру и остановите обход: адреса сохранятся автоматически на сутки. " +
            "Вход в матч, стабильность игры, голос Discord, видео и QUIC тестом не подтверждаются. " +
            "При выборе WARP проверяется реальное подключение туннеля; применение результата само по себе туннель не включает.");
        AddInstructionSection(sections, "Cloudflare WARP",
            "Если смена стратегий не помогает, остановите фильтр и отметьте «Подключить WARP» на главном экране. " +
            "В настройке стратегий начните с основного варианта WARP, затем нажмите «Включить». При первом подключении встроенного клиента подтвердите условия Cloudflare. " +
            "Официальный клиент используется, если он установлен; иначе запускается встроенный. WARP может увеличить задержку в игре. " +
            "Пункт «WARP · Выключено» отключает только правила обхода для WARP: запуск самого туннеля управляется галочкой на главном экране.");
        AddInstructionSection(sections, "Фоновый режим и выход",
            "Шестерёнка открывает настройки. В фоновом режиме сворачивание или закрытие окна скрывает приложение рядом с часами, а фильтр продолжает работать. " +
            "Чтобы полностью завершить приложение, выберите «Выход» в меню его значка. " +
            "Кнопка «Выключить» останавливает фильтр и подключение WARP, запущенное лаунчером. Уже активное подключение официального WARP остаётся работать.");
        AddInstructionSection(sections, "Автозапуск и обновления",
            "Автозапуск с Windows включается в настройках. При желании можно также включать фильтр с сохранёнными настройками при входе в Windows. " +
            "Обновления проверяются там же. Если автоматическая установка недоступна, завершите приложение и распакуйте новый архив в отдельную папку. " +
            "Выбранные стратегии и регистрация WARP сохранятся: удалять данные из AppData не нужно. " +
            "Перед переносом или удалением старой папки отключите автозапуск, а после переноса включите его в новой папке, чтобы обновить путь к программе.");
        AddInstructionSection(sections, "Если возникла ошибка",
            "Если приложение сообщит об ошибке, откройте «Подробности» на главном экране. " +
            "Если обновлению мешает занятый файл, завершите приложение, перезагрузите Windows и повторите установку.");
    }

    private static void AddInstructionSection(FlowLayoutPanel sections, string title, string text)
    {
        const int width = 380;
        if (sections.Controls.Count != 0)
            sections.Controls.Add(new Panel {
                BackColor = Border, Size = new Size(width, 1), Margin = new Padding(0, 9, 0, 9)
            });
        sections.Controls.Add(new Label {
            Text = title, AutoSize = true, Font = new Font("Segoe UI", 11, FontStyle.Bold),
            MaximumSize = new Size(width, 0), Margin = new Padding(0, 0, 0, 15)
        });
        sections.Controls.Add(new Label {
            Text = text, AutoSize = true, MaximumSize = new Size(width, 0), Margin = Padding.Empty,
            UseMnemonic = false
        });
    }

    private static void AddInformationHeading(Panel page, string title)
    {
        var heading = new Label {
            Text = title, Font = new Font("Segoe UI Semibold", 20),
            TextAlign = ContentAlignment.MiddleLeft, AccessibleName = title.Replace('\n', ' ')
        };
        heading.SetBounds(34, 20, 342, 72);
        page.Controls.Add(heading);
    }

    private static FlowLayoutPanel AddInformationScroll(Panel page, string accessibleName)
    {
        var content = new FlowLayoutPanel {
            FlowDirection = FlowDirection.TopDown, WrapContents = false,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink
        };
        var scrollView = new InformationScrollView(content) { AccessibleName = accessibleName };
        scrollView.SetBounds(34, 102, 380 + SystemInformation.VerticalScrollBarWidth, 549);
        page.Controls.Add(scrollView);
        return content;
    }

    private void BuildAboutPage()
    {
        AddInformationHeading(aboutPage, "О приложении");
        var description = new Label {
            Text = "Omnissiah Breakwall — лаунчер для Windows с сетевыми профилями для Darktide, " +
                "Space Marine 2, Discord и YouTube и поддержкой Cloudflare WARP.",
            UseMnemonic = false, ForeColor = Secondary
        };
        description.SetBounds(34, 102, 380, 69);
        aboutPage.Controls.Add(description);

        var author = new Panel { BackColor = Surface, AccessibleName = "Автор" };
        author.SetBounds(34, 186, 380, 118);
        aboutPage.Controls.Add(author);
        var authorHeading = new Label { Text = "Автор", ForeColor = Secondary };
        authorHeading.SetBounds(12, 12, 356, 21);
        author.Controls.Add(authorHeading);
        var authorName = new Label { Text = "a1deus", Font = new Font("Segoe UI Semibold", 13) };
        authorName.SetBounds(12, 33, 356, 25);
        author.Controls.Add(authorName);
        AddAboutButton(author, "GitHub", "https://github.com/a1deus/Omnissiah-Breakwall", 12);
        AddAboutButton(author, "Steam", "https://steamcommunity.com/sharedfiles/filedetails/?id=3839586340", 195);

        var sources = new Panel { BackColor = Surface, AccessibleName = "Авторы источников" };
        sources.SetBounds(34, 320, 380, 140);
        aboutPage.Controls.Add(sources);
        var sourcesHeading = new Label { Text = "Авторы источников", Font = new Font("Segoe UI Semibold", 11) };
        sourcesHeading.SetBounds(12, 12, 356, 25);
        sources.Controls.Add(sourcesHeading);
        var names = new[] { "Flowseal", "bol-van", "kinlay0" };
        for (var index = 0; index < names.Length; index++)
        {
            var name = names[index];
            var link = PageLink(name);
            link.TextAlign = ContentAlignment.MiddleLeft;
            link.SetBounds(12, 42 + 27 * index, 356, 27);
            link.LinkClicked += (sender, args) => OpenLink("https://github.com/" + name);
            sources.Controls.Add(link);
        }
    }

    private void AddAboutButton(Panel card, string text, string url, int left)
    {
        var button = new Button { Text = text, FlatStyle = FlatStyle.Flat, BackColor = Field, Cursor = Cursors.Hand };
        button.FlatAppearance.BorderColor = Border;
        button.FlatAppearance.MouseOverBackColor = Border;
        button.SetBounds(left, 69, 173, 34);
        button.Click += (sender, args) => OpenLink(url);
        card.Controls.Add(button);
    }

    private static LinkLabel PageLink(string label)
    {
        return new LinkLabel {
            Text = label, TextAlign = ContentAlignment.MiddleCenter,
            LinkColor = Color.FromArgb(255, 183, 111), ActiveLinkColor = Primary
        };
    }

    private void OpenLink(string url)
    {
        try
        {
            using (Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })) { }
        }
        catch (Win32Exception error)
        {
            MessageBox.Show(this, "Не удалось открыть ссылку. " + error.Message, Program.Name);
        }
    }

    private void AddSetting(Panel card, CheckBox option, int top, string title, string description)
    {
        option.Text = title;
        option.SetBounds(16, top + 10, 314, 40);
        card.Controls.Add(option);
        if (top != 0)
        {
            var divider = new Label { BackColor = Border };
            divider.SetBounds(16, top, 348, 1);
            card.Controls.Add(divider);
        }
        var hint = new Button {
            AccessibleName = "Подсказка: " + title, AccessibleDescription = description,
            FlatStyle = FlatStyle.Flat, ForeColor = Secondary, BackColor = Field, Cursor = Cursors.Hand
        };
        hint.FlatAppearance.BorderColor = Border;
        hint.SetBounds(340, option.Top + (option.Height - 24) / 2, 24, 24);
        hint.Paint += (sender, args) =>
        {
            using (var glyph = new GraphicsPath())
            using (var center = new Matrix())
            using (var brush = new SolidBrush(hint.ForeColor))
            {
                glyph.AddString("?", hint.Font.FontFamily, (int)hint.Font.Style,
                    hint.Font.SizeInPoints * args.Graphics.DpiY / 72, PointF.Empty, StringFormat.GenericDefault);
                var ink = glyph.GetBounds();
                center.Translate((hint.ClientSize.Width - ink.Width) / 2 - ink.X,
                    (hint.ClientSize.Height - ink.Height) / 2 - ink.Y);
                glyph.Transform(center);
                args.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                args.Graphics.FillPath(brush, glyph);
            }
        };
        help.SetToolTip(hint, description);
        hint.Click += (sender, args) => help.Show(description, hint, 0, hint.Height, 5000);
        card.Controls.Add(hint);
    }

    private void ReadStartupSetting()
    {
        try
        {
            autoStart.Checked = WindowsStartup.IsEnabled(Application.ExecutablePath);
            autoStart.Enabled = true;
            settingsMessage.Text = "";
        }
        catch (Exception error)
        {
            autoStart.Checked = false;
            autoStart.Enabled = false;
            settingsMessage.Text = "Не удалось проверить автозапуск. " + error.Message;
        }
        startFilter.Enabled = autoStart.Checked;
    }

    private void ConfigureTray()
    {
        tray.Icon = Icon;
        tray.ContextMenuStrip = trayMenu;
        trayMenu.Items.Add("Открыть", null, (sender, args) => ShowWindow());
        trayMenu.Items.Add(traySwitch);
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add(trayExit);
        traySwitch.Click += async (sender, args) => await Switch();
        trayExit.Click += (sender, args) =>
        {
            exitRequested = true;
            Close();
        };
        tray.DoubleClick += (sender, args) => ShowWindow();
    }

    private void ShowWindow()
    {
        showingWindow = true;
        try
        {
            ShowInTaskbar = true;
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }
        finally { showingWindow = false; }
    }

    protected override async void OnShown(EventArgs args)
    {
        base.OnShown(args);
        if (!fromWindows) return;
        if (trayMode.Checked)
        {
            Hide();
            Opacity = 1;
        }
        if (startFilter.Checked) await Switch();
    }

    protected override void OnResize(EventArgs args)
    {
        base.OnResize(args);
        if (!showingWindow && trayMode.Checked && Visible && WindowState == FormWindowState.Minimized) Hide();
    }

    private void RestorePreferences()
    {
        try
        {
            foreach (var line in File.ReadAllLines(preferencesPath))
            {
                var separator = line.IndexOf('=');
                if (separator < 0) continue;
                var value = line.Substring(separator + 1);
                switch (line.Substring(0, separator))
                {
                    case "Darktide": darktideStrategy.Restore(value); break;
                    case "SpaceMarine2": spaceMarineStrategy.Restore(value); break;
                    case "Discord": discordStrategy.Restore(value); break;
                    case "YouTube": youtubeStrategy.Restore(value); break;
                    case "WarpStrategy": warpStrategy.Restore(value); break;
                    case "WARP":
                        bool enabled;
                        if (bool.TryParse(value, out enabled)) useWarp.Checked = enabled;
                        break;
                    case "Tray":
                        bool inTray;
                        if (bool.TryParse(value, out inTray)) trayMode.Checked = inTray;
                        break;
                    case "StartFilter":
                        bool atLogon;
                        if (bool.TryParse(value, out atLogon)) startFilter.Checked = atLogon;
                        break;
                }
            }
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (IOException error) { ShowPreferencesError("Не удалось прочитать сохранённые настройки.", error); }
        catch (UnauthorizedAccessException error) { ShowPreferencesError("Не удалось прочитать сохранённые настройки.", error); }
    }

    private void SavePreferences(object sender, EventArgs args)
    {
        WritePreferences();
    }

    private bool WritePreferences()
    {
        var temporary = preferencesPath + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(preferencesPath));
            File.WriteAllLines(temporary, new[] {
                "Darktide=" + darktideStrategy.Selected,
                "SpaceMarine2=" + spaceMarineStrategy.Selected,
                "Discord=" + discordStrategy.Selected,
                "YouTube=" + youtubeStrategy.Selected,
                "WarpStrategy=" + warpStrategy.Selected,
                "WARP=" + useWarp.Checked,
                "Tray=" + trayMode.Checked,
                "StartFilter=" + startFilter.Checked
            });
            if (File.Exists(preferencesPath)) File.Replace(temporary, preferencesPath, null);
            else File.Move(temporary, preferencesPath);
            return true;
        }
        catch (IOException error) { ShowPreferencesError("Не удалось сохранить настройки.", error); }
        catch (UnauthorizedAccessException error) { ShowPreferencesError("Не удалось сохранить настройки.", error); }
        return false;
    }

    private void ShowPreferencesError(string text, Exception error)
    {
        message.Text = text;
        settingsMessage.Text = text + " " + error.Message;
        diagnostics = error.Message;
        RefreshControls();
    }

    private string IdleMessage
    {
        get { return warp.Installed
            ? "Используйте WARP, только если ни одна стратегия не подошла.\nPing может возрасти."
            : "Установите Cloudflare WARP или восстановите файлы папки warp."; }
    }

    private sealed class StrategyPicker<T> : Button
    {
        private const int MaximumDropDownHeight = 260;
        private readonly ToolStripDropDown menu = new ToolStripDropDown();
        private readonly StrategyListBox options = new StrategyListBox();
        private readonly Panel viewport = new Panel();
        private readonly DarkScrollBar scrollBar;
        private readonly ToolStripControlHost host;
        private readonly Dictionary<string, int> savedChoices = new Dictionary<string, int>(StringComparer.Ordinal);
        private int selectedIndex;

        public StrategySelection<T> Selected { get; private set; }
        public IEnumerable<KeyValuePair<StrategySelection<T>, string>> Choices
        {
            get { return options.Items.Cast<KeyValuePair<StrategySelection<T>, string>>(); }
        }
        public event EventHandler SelectionChanged;

        public StrategyPicker(string service, T disabled)
        {
            AccessibleName = "Стратегия " + service;
            AccessibleDescription = "Открывает список стратегий";
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderColor = Border;
            FlatAppearance.MouseOverBackColor = Border;
            BackColor = Field;
            ForeColor = Primary;
            TextAlign = ContentAlignment.MiddleCenter;
            options.AccessibleName = AccessibleName;
            options.BorderStyle = BorderStyle.None;
            options.BackColor = Field;
            options.ForeColor = Primary;
            options.IntegralHeight = false;
            options.DrawMode = DrawMode.OwnerDrawFixed;
            options.DisplayMember = "Value";
            options.DrawItem += DrawOption;
            scrollBar = new DarkScrollBar(options, viewport, () => options.ItemHeight) {
                AccessibleName = "Прокрутка списка стратегий"
            };
            options.MouseWheel += scrollBar.ScrollWheel;
            options.KeyboardSelectionChanged += (sender, args) => RevealSelectedOption();
            options.MouseClick += (sender, args) =>
            {
                if (args.Button == MouseButtons.Left) Commit(options.IndexFromPoint(args.Location));
            };
            options.KeyDown += (sender, args) =>
            {
                if (args.KeyCode == Keys.Enter) Commit(options.SelectedIndex);
                else if (args.KeyCode == Keys.Escape) menu.Close();
                else if (args.KeyCode == Keys.PageUp || args.KeyCode == Keys.PageDown)
                {
                    var page = Math.Max(1, viewport.Height / options.ItemHeight);
                    var step = args.KeyCode == Keys.PageUp ? -page : page;
                    options.SelectedIndex = Math.Max(0, Math.Min(options.Items.Count - 1, options.SelectedIndex + step));
                }
                else return;
                args.SuppressKeyPress = true;
            };
            viewport.Controls.Add(options);
            var content = new Panel { BackColor = Field };
            content.Controls.Add(viewport);
            content.Controls.Add(scrollBar);
            host = new ToolStripControlHost(content) { AutoSize = false, Margin = Padding.Empty };
            menu.Items.Add(host);
            menu.BackColor = Border;
            menu.Renderer = new ToolStripProfessionalRenderer(new StrategyColors());
            menu.Closed += (sender, args) => scrollBar.StopWheel();
            menu.AutoSize = false;
            menu.Padding = new Padding(1);
            Add(disabled, service + " · Выключено");
        }

        public void Add(T value, string label)
        {
            Add(new StrategySelection<T>(value), label, new string[0]);
        }

        public void AddNumbered(string prefix, string service, T value, string description, params string[] aliases)
        {
            AddNumbered(prefix, service, new StrategySelection<T>(value), description, aliases);
        }

        public void AddImportedStrategy(string prefix, string service, string name, bool youtube = false, params string[] aliases)
        {
            var profile = DsStrategy.Get(name);
            AddNumbered(prefix, service, new StrategySelection<T>(profile),
                youtube ? profile.YouTubeDescription : profile.Description, aliases);
        }

        private void AddNumbered(string prefix, string service, StrategySelection<T> value, string description, string[] aliases)
        {
            var code = prefix + options.Items.Count.ToString("D2", CultureInfo.InvariantCulture);
            Add(value, code + " - " + service + " · " + description, aliases);
        }

        private void Add(StrategySelection<T> value, string label, string[] aliases)
        {
            var index = options.Items.Add(new KeyValuePair<StrategySelection<T>, string>(value, label));
            savedChoices.Add(value.ToString(), index);
            foreach (var alias in aliases) savedChoices.Add(alias, index);
            if (index == 0) Select(index);
        }

        public void Restore(string value)
        {
            int index;
            if (savedChoices.TryGetValue(value, out index)) Select(index);
        }

        private KeyValuePair<StrategySelection<T>, string> Option(int index)
        {
            return (KeyValuePair<StrategySelection<T>, string>)options.Items[index];
        }

        private void Select(int index)
        {
            selectedIndex = index;
            var item = Option(index);
            Selected = item.Key;
            Text = item.Value;
            AccessibleDescription = Text;
        }

        private void Commit(int index)
        {
            if (index < 0) return;
            var changed = index != selectedIndex;
            Select(index);
            menu.Close();
            Focus();
            if (changed && SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
        }

        private void DrawOption(object sender, DrawItemEventArgs args)
        {
            if (args.Index < 0) return;
            var selected = (args.State & DrawItemState.Selected) != 0;
            using (var background = new SolidBrush(selected ? Border : Field))
                args.Graphics.FillRectangle(background, args.Bounds);
            var foreground = args.Index == selectedIndex ? Color.FromArgb(255, 183, 111) : Primary;
            var textBounds = Rectangle.Inflate(args.Bounds, -12, 0);
            TextRenderer.DrawText(args.Graphics, Option(args.Index).Value, options.Font, textBounds, foreground,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                TextFormatFlags.NoPrefix);
            args.DrawFocusRectangle();
        }

        protected override void OnClick(EventArgs args)
        {
            base.OnClick(args);
            options.Font = Font;
            var rowHeight = Math.Max(Height, Font.Height + 8);
            var listHeight = rowHeight * options.Items.Count;
            var height = Math.Min(MaximumDropDownHeight, listHeight + menu.Padding.Vertical);
            var width = Width - menu.Padding.Horizontal;
            var contentHeight = height - menu.Padding.Vertical;
            var scrollWidth = listHeight > contentHeight ? SystemInformation.VerticalScrollBarWidth : 0;
            options.ItemHeight = rowHeight;
            host.Size = new Size(width, contentHeight);
            viewport.SetBounds(0, 0, width - scrollWidth, contentHeight);
            // Give the ListBox room for every row and move it inside the clipped viewport by pixels.
            options.SetBounds(0, 0, viewport.Width, listHeight);
            scrollBar.SetBounds(viewport.Right, 0, scrollWidth, contentHeight);
            scrollBar.Visible = scrollWidth != 0;
            menu.Size = new Size(Width, height);
            var workingArea = Screen.FromControl(this).WorkingArea;
            var below = workingArea.Bottom - PointToScreen(new Point(0, Height)).Y;
            var above = PointToScreen(Point.Empty).Y - workingArea.Top;
            var openAbove = below < height && above > below;
            options.SelectedIndex = selectedIndex;
            RevealSelectedOption();
            menu.Show(this, new Point(0, openAbove ? -height : Height));
            options.Focus();
        }

        private void RevealSelectedOption()
        {
            if (options.SelectedIndex >= 0)
                scrollBar.Reveal(options.SelectedIndex * options.ItemHeight, options.ItemHeight);
        }

        protected override void OnPaint(PaintEventArgs args)
        {
            base.OnPaint(args);
            var x = ClientSize.Width - Height / 2;
            var y = ClientSize.Height / 2;
            args.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(Secondary, 1.5f))
                args.Graphics.DrawLines(pen, new[] { new Point(x - 4, y - 2), new Point(x, y + 2), new Point(x + 4, y - 2) });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) menu.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class StrategyListBox : ListBox
    {
        private const int KeyDownMessage = 0x0100;
        private const int CharacterMessage = 0x0102;

        public event EventHandler KeyboardSelectionChanged;

        protected override void WndProc(ref Message message)
        {
            base.WndProc(ref message);
            if ((message.Msg == KeyDownMessage || message.Msg == CharacterMessage) && KeyboardSelectionChanged != null)
                KeyboardSelectionChanged(this, EventArgs.Empty);
        }
    }

    private sealed class InformationScrollView : Panel
    {
        private readonly Panel viewport = new Panel();
        private readonly FlowLayoutPanel content;
        private readonly DarkScrollBar scrollBar;

        public InformationScrollView(FlowLayoutPanel content)
        {
            this.content = content;
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
            scrollBar = new DarkScrollBar(content, viewport, () => Font.Height + 5) {
                AccessibleName = "Прокрутка страницы"
            };
            Controls.Add(viewport);
            viewport.Controls.Add(content);
            Controls.Add(scrollBar);
            viewport.MouseWheel += scrollBar.ScrollWheel;
            content.MouseWheel += scrollBar.ScrollWheel;
            content.ControlAdded += (sender, args) =>
            {
                var section = args.Control;
                section.MouseWheel += scrollBar.ScrollWheel;
                section.Enter += (source, eventArgs) => scrollBar.Reveal(section.Top, section.Height);
            };
            content.SizeChanged += (sender, args) => LayoutScrollBar();
            SizeChanged += (sender, args) => LayoutScrollBar();
            VisibleChanged += (sender, args) =>
            {
                if (Visible) LayoutScrollBar();
                else scrollBar.StopWheel();
            };
            LayoutScrollBar();
        }

        private void LayoutScrollBar()
        {
            var height = ClientSize.Height;
            var scrollWidth = content.Height > height
                ? Math.Min(ClientSize.Width, SystemInformation.VerticalScrollBarWidth) : 0;
            viewport.SetBounds(0, 0, ClientSize.Width - scrollWidth, height);
            scrollBar.SetBounds(viewport.Right, 0, scrollWidth, height);
            scrollBar.Visible = scrollWidth > 0;
            scrollBar.RefreshRange();
        }

        protected override void OnMouseWheel(MouseEventArgs args)
        {
            base.OnMouseWheel(args);
            scrollBar.ScrollWheel(this, args);
        }

        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            if (!Focused) return base.ProcessCmdKey(ref message, keyData);
            switch (keyData)
            {
                case Keys.Up: scrollBar.ScrollBy(-Font.Height); break;
                case Keys.Down: scrollBar.ScrollBy(Font.Height); break;
                case Keys.PageUp: scrollBar.ScrollBy(-viewport.Height); break;
                case Keys.PageDown: scrollBar.ScrollBy(viewport.Height); break;
                case Keys.Home: scrollBar.ScrollTo(0); break;
                case Keys.End: scrollBar.ScrollTo(int.MaxValue); break;
                default: return base.ProcessCmdKey(ref message, keyData);
            }
            return true;
        }
    }

    private sealed class DarkScrollBar : Control
    {
        private const int TrackInset = 3;
        private const int MinimumThumbHeight = 18;
        private const double WheelTimeConstantMs = 38;
        private readonly Control content;
        private readonly Control viewport;
        private readonly Func<int> wheelLineHeight;
        private readonly System.Windows.Forms.Timer wheelAnimation = new System.Windows.Forms.Timer { Interval = 15 };
        private readonly Stopwatch wheelFrame = new Stopwatch();
        private int dragStartY;
        private int dragStartOffset;
        private bool dragging;
        private bool hovered;
        private int wheelRemainder;
        private int wheelTarget;
        private double wheelPosition;

        public DarkScrollBar(Control content, Control viewport, Func<int> wheelLineHeight)
        {
            this.content = content;
            this.viewport = viewport;
            this.wheelLineHeight = wheelLineHeight;
            BackColor = Surface;
            SetStyle(ControlStyles.Selectable, false);
            SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
            wheelAnimation.Tick += AdvanceWheel;
        }

        private int ScrollOffset { get { return -content.Top; } }
        private int MaximumOffset { get { return Math.Max(0, content.Height - viewport.Height); } }

        private Rectangle Thumb
        {
            get
            {
                var track = Rectangle.Inflate(ClientRectangle, -TrackInset, -TrackInset);
                var height = Math.Min(track.Height, Math.Max(MinimumThumbHeight,
                    track.Height * viewport.Height / Math.Max(1, content.Height)));
                var travel = track.Height - height;
                var top = track.Top + (int)Math.Round(travel * (double)ScrollOffset / Math.Max(1, MaximumOffset));
                return new Rectangle(track.Left, top, track.Width, height);
            }
        }

        protected override void OnPaint(PaintEventArgs args)
        {
            base.OnPaint(args);
            if (MaximumOffset == 0 || ClientSize.Width <= 2 * TrackInset ||
                ClientSize.Height <= 2 * TrackInset) return;
            var level = dragging || hovered ? 125 : 91;
            using (var brush = new SolidBrush(Color.FromArgb(level, level, level)))
                args.Graphics.FillRectangle(brush, Thumb);
        }

        protected override void OnMouseEnter(EventArgs args)
        {
            base.OnMouseEnter(args);
            hovered = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs args)
        {
            base.OnMouseLeave(args);
            hovered = false;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs args)
        {
            base.OnMouseDown(args);
            if (args.Button != MouseButtons.Left || MaximumOffset == 0) return;
            StopWheel();
            var thumb = Thumb;
            if (!thumb.Contains(args.Location))
            {
                SetOffset(ScrollOffset + (args.Y < thumb.Top ? -viewport.Height : viewport.Height));
                return;
            }
            dragStartY = args.Y;
            dragStartOffset = ScrollOffset;
            dragging = true;
            Capture = true;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs args)
        {
            base.OnMouseMove(args);
            if (!dragging) return;
            var travel = Math.Max(1, ClientSize.Height - 2 * TrackInset - Thumb.Height);
            var offset = (int)Math.Round((args.Y - dragStartY) * (double)MaximumOffset / travel);
            SetOffset(dragStartOffset + offset);
        }

        protected override void OnMouseUp(MouseEventArgs args)
        {
            base.OnMouseUp(args);
            if (args.Button == MouseButtons.Left && dragging) Capture = false;
        }

        protected override void OnMouseCaptureChanged(EventArgs args)
        {
            base.OnMouseCaptureChanged(args);
            if (!Capture) dragging = false;
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs args)
        {
            base.OnMouseWheel(args);
            ScrollWheel(this, args);
        }

        public void ScrollWheel(object sender, MouseEventArgs args)
        {
            var handled = args as HandledMouseEventArgs;
            if (handled != null) handled.Handled = true;
            if (MaximumOffset == 0 || dragging) return;
            var lines = SystemInformation.MouseWheelScrollLines;
            var pixelsPerStep = lines < 0 ? viewport.Height : lines * wheelLineHeight();
            var delta = args.Delta * pixelsPerStep + wheelRemainder;
            wheelRemainder = delta % SystemInformation.MouseWheelScrollDelta;
            var origin = wheelAnimation.Enabled ? wheelTarget : ScrollOffset;
            wheelTarget = Math.Max(0, Math.Min(origin - delta / SystemInformation.MouseWheelScrollDelta, MaximumOffset));
            if (wheelAnimation.Enabled || wheelTarget == ScrollOffset) return;
            wheelPosition = ScrollOffset;
            wheelFrame.Restart();
            wheelAnimation.Start();
        }

        private void AdvanceWheel(object sender, EventArgs args)
        {
            // Retarget during fast wheel input without restarting the animation.
            var elapsed = Math.Min(wheelFrame.Elapsed.TotalMilliseconds, 40);
            wheelFrame.Restart();
            wheelPosition += (wheelTarget - wheelPosition) * (1 - Math.Exp(-elapsed / WheelTimeConstantMs));
            if (Math.Abs(wheelTarget - wheelPosition) < 0.75)
            {
                wheelAnimation.Stop();
                wheelFrame.Stop();
                SetOffset(wheelTarget);
                return;
            }
            SetOffset((int)Math.Round(wheelPosition));
        }

        public void StopWheel()
        {
            wheelAnimation.Stop();
            wheelFrame.Reset();
            wheelRemainder = 0;
        }

        public void Reveal(int top, int height)
        {
            var firstOffsetShowingItem = top + height - viewport.Height;
            ScrollTo(Math.Min(top, Math.Max(ScrollOffset, firstOffsetShowingItem)));
        }

        public void ScrollBy(int pixels)
        {
            ScrollTo(ScrollOffset + pixels);
        }

        public void ScrollTo(int offset)
        {
            StopWheel();
            SetOffset(offset);
        }

        public void RefreshRange()
        {
            SetOffset(ScrollOffset);
            Invalidate();
        }

        private void SetOffset(int offset)
        {
            var clamped = Math.Max(0, Math.Min(offset, MaximumOffset));
            if (clamped == ScrollOffset) return;
            content.Top = -clamped;
            content.Update();
            Invalidate();
            Update();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) wheelAnimation.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class StrategyColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Field; } }
        public override Color MenuBorder { get { return Border; } }
        public override Color MenuItemBorder { get { return Border; } }
        public override Color MenuItemSelected { get { return Border; } }
    }

    protected override void OnHandleCreated(EventArgs args)
    {
        base.OnHandleCreated(args);
        int dark = 1;
        if (DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int)) != 0)
            DwmSetWindowAttribute(Handle, 19, ref dark, sizeof(int));
        int caption = ColorTranslator.ToWin32(Surface);
        int text = ColorTranslator.ToWin32(Primary);
        DwmSetWindowAttribute(Handle, 35, ref caption, sizeof(int));
        DwmSetWindowAttribute(Handle, 36, ref text, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    private async Task Switch()
    {
        if (connectionAttempt != null)
        {
            if (connectionAttempt.IsCancellationRequested) return;
            message.Text = "Отмена подключения…";
            connectionAttempt.Cancel();
            RefreshControls();
            return;
        }
        if (busy || installingUpdate) return;
        if (active || warp.OwnsConnection)
        {
            await StopSession();
            return;
        }

        using (var attempt = new CancellationTokenSource())
        {
            connectionAttempt = attempt;
            var cancellation = attempt.Token;
            busy = true;
            diagnostics = null;
            message.Text = "Запуск сетевого фильтра…";
            RefreshControls();
            try
            {
                await StartSession(cancellation);
                if (cancellation.IsCancellationRequested && await ReleaseSession() && diagnostics == null)
                    message.Text = "Подключение отменено.";
            }
            finally
            {
                connectionAttempt = null;
                busy = false;
                RefreshControls();
            }
        }
    }

    private async Task StartSession(CancellationToken cancellation)
    {
        var profile = darktideStrategy.Selected;
        var spaceMarine = spaceMarineStrategy.Selected;
        var discord = discordStrategy.Selected;
        var youtube = youtubeStrategy.Selected;
        var withWarp = useWarp.Checked && warp.Installed;
        var warpProfile = withWarp ? warpStrategy.Selected : new StrategySelection<WarpStrategy>(WarpStrategy.None);
        try
        {
            active = await Task.Run(() => engine.Start(profile, warpProfile, discord, youtube, spaceMarine, cancellation), cancellation);
            cancellation.ThrowIfCancellationRequested();
            message.Text = active ? "Фильтр запущен." : "Выберите хотя бы одну стратегию или подключите WARP.";
            if (withWarp)
            {
                var result = await ConnectWarp(cancellation);
                cancellation.ThrowIfCancellationRequested();
                message.Text = !active && result == Warp.ConnectedMessage ? "WARP подключён." : result;
            }
        }
        catch (Exception error)
        {
            if (cancellation.IsCancellationRequested) return;
            diagnostics = error.ToString();
            message.Text = active ? "Фильтр запущен. Ошибка Cloudflare WARP." : error.Message;
        }
    }

    private Task<string> ConnectWarp(CancellationToken cancellation)
    {
        if (!warp.UsesOfficialClient)
        {
            if (warp.OtherVpnActive()) return Task.FromResult("WARP пропущен: обнаружен другой активный VPN.");
            if (!warp.Registered && !ConfirmWarpRegistration()) return Task.FromResult("Регистрация WARP отменена.");
        }
        message.Text = active ? "Фильтр запущен. Подключение WARP…" : "Подключение WARP…";
        return warp.UsesOfficialClient ? warp.ConnectOfficial(cancellation) : warp.Connect(cancellation);
    }

    private bool ConfirmWarpRegistration()
    {
        if (!Visible || WindowState == FormWindowState.Minimized) ShowWindow();
        return MessageBox.Show(this,
            "Для первого подключения " + Program.Name + " зарегистрирует устройство в Cloudflare WARP.\n\n" +
            "Условия Cloudflare: https://www.cloudflare.com/application/terms/\n\n" +
            "Вы принимаете условия и хотите зарегистрировать устройство?",
            "Регистрация Cloudflare WARP", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }

    private async Task<bool> StopSession()
    {
        busy = true;
        RefreshControls();
        try { return await ReleaseSession(); }
        finally
        {
            busy = false;
            RefreshControls();
        }
    }

    private async Task<bool> ReleaseSession()
    {
        try
        {
            await warp.Disconnect();
            if (connectionAttempt == null) message.Text = IdleMessage;
        }
        catch (Exception error)
        {
            diagnostics = error.ToString();
            message.Text = "Не удалось отключить WARP. Нажмите кнопку ещё раз.";
        }
        try { await Task.Run(() => engine.Dispose()); }
        catch (Exception error)
        {
            diagnostics = error.ToString();
            message.Text = "Ошибка при остановке фильтра. Откройте подробности.";
        }
        finally { active = false; }
        return !warp.OwnsConnection;
    }

    private void RefreshControls()
    {
        var warpActive = warp.OwnsConnection;
        var sessionActive = active || warpActive;
        var cancelling = connectionAttempt != null && connectionAttempt.IsCancellationRequested;
        var canCancel = connectionAttempt != null && !cancelling;
        status.Text = cancelling ? "Отменяем подключение" : busy ? "Пожалуйста, подождите" :
            active ? "Фильтр включён" : warpActive ? "WARP включён" : "Фильтр выключен";
        status.ForeColor = sessionActive ? Primary : Secondary;
        toggle.Active = sessionActive;
        toggle.Busy = busy;
        toggle.Text = cancelling ? "Отмена…" : busy ? "Подождите" :
            active ? "Выключить" : warpActive ? "Отключить WARP" : "Включить";
        toggle.AccessibleDescription = canCancel ? "Повторное нажатие отменяет подключение." : null;
        toggle.Enabled = (!busy || canCancel) && !installingUpdate;
        toggle.Invalidate();
        tray.Text = Program.Name + " — " + status.Text;
        traySwitch.Text = canCancel ? "Отменить подключение" : busy ? toggle.Text :
            active ? "Выключить фильтр" : warpActive ? "Отключить WARP" : "Включить фильтр";
        traySwitch.Enabled = toggle.Enabled;
        trayExit.Enabled = !busy && !installingUpdate;
        updateButton.Enabled = !busy && !checkingUpdate && !installingUpdate;
        updateButton.Text = availableUpdate == null ? "Проверить обновления" :
            "Установить v" + availableUpdate.Version.ToString(3);
        var canConfigure = !busy && !sessionActive;
        openStrategies.Enabled = canConfigure;
        darktideStrategy.Enabled = canConfigure;
        spaceMarineStrategy.Enabled = canConfigure;
        discordStrategy.Enabled = canConfigure;
        youtubeStrategy.Enabled = canConfigure;
        warpStrategy.Enabled = canConfigure;
        useWarp.Enabled = canConfigure && warp.Installed;
        details.Visible = !busy && diagnostics != null;
        RefreshAutoControls();
    }

    protected override async void OnFormClosing(FormClosingEventArgs args)
    {
        base.OnFormClosing(args);
        if (args.Cancel || args.CloseReason == CloseReason.WindowsShutDown) return;
        if (installingUpdate && !allowClose)
        {
            args.Cancel = true;
            return;
        }
        if (trayMode.Checked && !exitRequested && args.CloseReason == CloseReason.UserClosing)
        {
            args.Cancel = true;
            Hide();
            return;
        }
        if (autoSelecting)
        {
            args.Cancel = true; closeAfterAuto = true;
            autoAttempt.Cancel(); RefreshAutoControls();
            return;
        }
        if (allowClose || (!busy && !active && !warp.OwnsConnection)) return;
        args.Cancel = true;
        if (busy)
        {
            exitRequested = false;
            return;
        }
        if (await StopSession())
        {
            allowClose = true;
            BeginInvoke(new Action(Close));
        }
        else
        {
            exitRequested = false;
            ShowWindow();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (autoAttempt != null) autoAttempt.Cancel();
            health.Dispose();
            tray.Visible = false;
            tray.Dispose();
            trayMenu.Dispose();
            help.Dispose();
            warp.Dispose();
            engine.Dispose();
            if (logo.Image != null) logo.Image.Dispose();
            if (Icon != null) Icon.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class AccentCheckBox : CheckBox
{
    public bool DimWhenDisabled { get; set; }

    protected override void OnPaint(PaintEventArgs args)
    {
        var graphics = args.Graphics;
        graphics.Clear(BackColor);
        var scale = graphics.DpiX / 96f;
        var size = Math.Max(12, (int)Math.Round(12 * scale));
        var box = new Rectangle(1, (ClientSize.Height - size) / 2, size, size);
        var orange = Color.FromArgb(255, 155, 74);
        var foreground = Enabled ? ForeColor : Color.FromArgb(170, 170, 170);
        var borderColor = Color.FromArgb(125, 125, 125);
        if (!Enabled && DimWhenDisabled)
        {
            orange = DisabledAppearance.Blend(orange, BackColor);
            foreground = DisabledAppearance.Blend(ForeColor, BackColor);
            borderColor = DisabledAppearance.Blend(borderColor, BackColor);
        }
        using (var border = new Pen(Checked ? orange : borderColor, scale))
            graphics.DrawRectangle(border, box);
        if (Checked)
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var check = new Pen(orange, 1.5f * scale))
                graphics.DrawLines(check, new[] {
                    new PointF(box.Left + 2.5f * scale, box.Top + 6 * scale),
                    new PointF(box.Left + 5 * scale, box.Top + 8.5f * scale),
                    new PointF(box.Left + 9.5f * scale, box.Top + 3.5f * scale)
                });
        }
        var left = box.Right + (int)Math.Round(5 * scale);
        var textBounds = new Rectangle(left, 0, ClientSize.Width - left, ClientSize.Height);
        var wrapping = AutoSize ? TextFormatFlags.SingleLine : TextFormatFlags.WordBreak;
        TextRenderer.DrawText(graphics, Text, Font, textBounds, foreground,
            TextFormatFlags.VerticalCenter | wrapping | TextFormatFlags.NoPadding);
        if (Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(graphics, textBounds, foreground, BackColor);
    }
}

internal sealed class DimmedSettingsButton : Button
{
    protected override void OnPaint(PaintEventArgs args)
    {
        if (Enabled)
        {
            base.OnPaint(args);
            return;
        }

        var background = Parent.BackColor;
        var graphics = args.Graphics;
        graphics.Clear(DisabledAppearance.Blend(BackColor, background));
        var border = DisabledAppearance.Blend(FlatAppearance.BorderColor, background);
        ControlPaint.DrawBorder(graphics, ClientRectangle, border, ButtonBorderStyle.Solid);
        TextRenderer.DrawText(graphics, Text, Font, ClientRectangle, DisabledAppearance.Blend(ForeColor, background),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }
}

internal static class DisabledAppearance
{
    private const double Opacity = 0.4; // 60% transparency over the settings card.

    public static Color Blend(Color color, Color background)
    {
        return Color.FromArgb(
            (int)Math.Round(background.R + (color.R - background.R) * Opacity),
            (int)Math.Round(background.G + (color.G - background.G) * Opacity),
            (int)Math.Round(background.B + (color.B - background.B) * Opacity));
    }
}

internal sealed class PowerButton : Button
{
    public bool Active { get; set; }
    public bool Busy { get; set; }
    private bool hover;

    protected override void OnSizeChanged(EventArgs args)
    {
        base.OnSizeChanged(args);
        using (var path = new GraphicsPath())
        {
            path.AddEllipse(ClientRectangle);
            var previous = Region;
            Region = new Region(path);
            if (previous != null) previous.Dispose();
        }
    }

    protected override void OnMouseEnter(EventArgs args)
    {
        base.OnMouseEnter(args);
        hover = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs args)
    {
        base.OnMouseLeave(args);
        hover = false;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs args)
    {
        var graphics = args.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Parent.BackColor);
        var scale = Width / 126f;
        var circle = new RectangleF(8 * scale, 8 * scale, Width - 16 * scale, Height - 16 * scale);
        var highlighted = hover && Enabled;
        var ring = highlighted ? 134 : 92;
        var lit = Busy || Active;
        var outlineColor = Busy ? Color.FromArgb(211, 211, 211) :
            Active ? Color.FromArgb(255, 183, 111) : Color.FromArgb(ring, ring, ring);
        var glowColor = Active && !Busy ? Color.FromArgb(255, 115, 27) : outlineColor;
        var shade = highlighted ? 39 : 26;
        using (var halo = new Pen(Color.FromArgb(lit ? 37 : 0, glowColor), 13 * scale))
        using (var glow = new Pen(Color.FromArgb(lit ? 115 : 31, glowColor), 6 * scale))
        using (var outline = new Pen(outlineColor, (lit ? 2.1f : 1.6f) * scale))
        using (var fill = new SolidBrush(Color.FromArgb(shade, shade, shade)))
        {
            graphics.FillEllipse(fill, circle);
            if (lit) graphics.DrawEllipse(halo, circle);
            graphics.DrawEllipse(glow, circle);
            graphics.DrawEllipse(outline, circle);
        }

        var foreground = Enabled ? Color.FromArgb(239, 239, 239) : Color.FromArgb(154, 154, 154);
        using (var glyph = new Pen(foreground, 2.1f * scale))
        {
            glyph.StartCap = LineCap.Round;
            glyph.EndCap = LineCap.Round;
            graphics.DrawArc(glyph, 51 * scale, 36 * scale, 24 * scale, 24 * scale, -53, 286);
            graphics.DrawLine(glyph, 63 * scale, 33 * scale, 63 * scale, 45 * scale);
        }
        var textBounds = new Rectangle((int)(12 * scale), (int)(63 * scale),
            (int)(102 * scale), (int)(36 * scale));
        TextRenderer.DrawText(graphics, Text, Font, textBounds, foreground,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        if (Focused && ShowFocusCues)
        {
            circle.Inflate(-5 * scale, -5 * scale);
            using (var focus = new Pen(foreground) { DashStyle = DashStyle.Dot })
                graphics.DrawEllipse(focus, circle);
        }
    }
}
