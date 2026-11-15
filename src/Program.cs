using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
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

internal sealed class MainWindow : Form
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
    private readonly CheckBox useWarp = new AccentCheckBox();
    private readonly StrategyPicker<Strategy> darktideStrategy = new StrategyPicker<Strategy>("Darktide");
    private readonly StrategyPicker<ServiceStrategy> discordStrategy = new StrategyPicker<ServiceStrategy>("Discord");
    private readonly StrategyPicker<ServiceStrategy> youtubeStrategy = new StrategyPicker<ServiceStrategy>("YouTube");
    private readonly Label status = new Label();
    private readonly Label message = new Label();
    private readonly LinkLabel details = new LinkLabel();
    private readonly System.Windows.Forms.Timer health = new System.Windows.Forms.Timer();
    private readonly Panel mainPage = new Panel { Dock = DockStyle.Fill };
    private readonly Panel settingsPage = new Panel { Dock = DockStyle.Fill, Visible = false };
    private readonly Panel privacyPage = new Panel { Dock = DockStyle.Fill, Visible = false };
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
        ClientSize = new Size(448, 635);
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Background;
        ForeColor = Primary;
        Font = new Font("Segoe UI", 10);
        Controls.Add(mainPage);
        Controls.Add(settingsPage);
        Controls.Add(privacyPage);

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
        settings.SetBounds(38, 343, 372, 179);
        mainPage.Controls.Add(settings);

        var strategiesHeading = new Label {
            Text = "Стратегии", TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI Semibold", 10)
        };
        strategiesHeading.SetBounds(28, 7, 316, 21);
        settings.Controls.Add(strategiesHeading);

        darktideStrategy.SetBounds(28, 36, 316, 29);
        darktideStrategy.Add(Strategy.Standard, "Darktide UDP · основной");
        darktideStrategy.Add(Strategy.Alternate, "Darktide UDP · усиленный + TLS");
        settings.Controls.Add(darktideStrategy);

        discordStrategy.SetBounds(28, 72, 316, 29);
        discordStrategy.Add(ServiceStrategy.Standard, "Discord · основной");
        discordStrategy.Add(ServiceStrategy.BadSequence, "Discord · TLS со смещением");
        discordStrategy.Add(ServiceStrategy.TimestampFake, "Discord · TLS с метками времени");
        discordStrategy.Add(ServiceStrategy.HostSplit, "Discord · подмена имени хоста");
        discordStrategy.Add(ServiceStrategy.TimestampOverlap, "Discord · TLS с перекрытием");
        discordStrategy.Add(ServiceStrategy.SimpleFake, "Discord · простой TLS");
        discordStrategy.Add(ServiceStrategy.SniSplit, "Discord · разбиение по SNI");
        settings.Controls.Add(discordStrategy);

        youtubeStrategy.SetBounds(28, 108, 316, 29);
        youtubeStrategy.Add(ServiceStrategy.Standard, "YouTube · основной");
        youtubeStrategy.Add(ServiceStrategy.TimestampFake, "YouTube · TLS с метками времени");
        youtubeStrategy.Add(ServiceStrategy.HostSplit, "YouTube · подмена имени хоста");
        youtubeStrategy.Add(ServiceStrategy.TimestampOverlap, "YouTube · TLS с перекрытием");
        youtubeStrategy.Add(ServiceStrategy.SimpleFake, "YouTube · простой TLS");
        youtubeStrategy.Add(ServiceStrategy.SniSplit, "YouTube · разбиение по SNI");
        settings.Controls.Add(youtubeStrategy);

        useWarp.Text = "Подключить WARP";
        useWarp.AutoSize = true;
        useWarp.Location = new Point(0, 144);
        settings.Controls.Add(useWarp);
        settings.Layout += (sender, args) =>
            useWarp.Left = (settings.ClientSize.Width - useWarp.Width) / 2;
        settings.PerformLayout();

        message.SetBounds(24, 525, 400, 31);
        message.TextAlign = ContentAlignment.MiddleCenter;
        message.ForeColor = Secondary;
        message.Font = new Font("Segoe UI", 9);
        mainPage.Controls.Add(message);

        details.Text = "Подробности";
        details.SetBounds(24, 560, 400, 18);
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
        motto.SetBounds(24, 592, 400, 24);
        mainPage.Controls.Add(motto);

        ConfigureNavigation();
        BuildSettingsPage();
        BuildPrivacyPage();
        ConfigureTray();

        health.Interval = 1200;
        health.Tick += async (sender, args) =>
        {
            if (busy || installingUpdate || !active) return;
            if (engine.Running)
            {
                if (warp.BuiltInNeedsStop)
                {
                    busy = true;
                    RefreshControls();
                    try
                    {
                        await warp.Disconnect();
                        message.Text = "Туннель WARP остановлен. Фильтр работает.";
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
            diagnostics = "Процесс winws завершился.";
            await StopSession();
            if (!warp.OwnsConnection)
                message.Text = "Фильтр остановился. Откройте подробности.";
        };
        health.Start();
        message.Text = IdleMessage;
        RefreshControls();
        RestorePreferences();
        darktideStrategy.SelectionChanged += SavePreferences;
        discordStrategy.SelectionChanged += SavePreferences;
        youtubeStrategy.SelectionChanged += SavePreferences;
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
            ShowPage(settingsPage.Visible ? mainPage : settingsPage);
        };
        Controls.Add(navigation);
        navigation.BringToFront();
        ShowPage(mainPage);
    }

    private void ShowPage(Panel page)
    {
        mainPage.Visible = page == mainPage;
        settingsPage.Visible = page == settingsPage;
        privacyPage.Visible = page == privacyPage;
        navigation.Text = page == mainPage ? "\uE713" : "\uE72B";
        navigation.AccessibleName = page == mainPage ? "Настройки" :
            page == privacyPage ? "Назад в настройки" : "Назад";
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

        var footer = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 102, ColumnCount = 1, RowCount = 3 };
        footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
        footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
        footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 23));
        var privacy = PageLink("Политика конфиденциальности");
        privacy.Dock = DockStyle.Fill;
        privacy.LinkClicked += (sender, args) =>
        {
            ShowPage(privacyPage);
            navigation.Focus();
        };
        footer.Controls.Add(privacy, 0, 0);
        var source = PageLink("Исходный код");
        source.Dock = DockStyle.Fill;
        source.LinkClicked += (sender, args) => OpenLink("https://github.com/a1deus/Omnissiah-Breakwall");
        footer.Controls.Add(source, 0, 1);
        footer.Controls.Add(new Label {
            Text = "v" + typeof(Program).Assembly.GetName().Version.ToString(3),
            TextAlign = ContentAlignment.MiddleCenter, ForeColor = Secondary, Dock = DockStyle.Fill
        }, 0, 2);
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

    private void BuildPrivacyPage()
    {
        var heading = new Label {
            Text = "Политика\nконфиденциальности", Font = new Font("Segoe UI Semibold", 20),
            TextAlign = ContentAlignment.MiddleLeft, AccessibleName = "Политика конфиденциальности"
        };
        heading.SetBounds(34, 20, 342, 72);
        privacyPage.Controls.Add(heading);

        var policy = new RichTextBox {
            BackColor = Background, ForeColor = Primary, BorderStyle = BorderStyle.None,
            ReadOnly = true, WordWrap = true, DetectUrls = false, ScrollBars = RichTextBoxScrollBars.Vertical,
            AccessibleName = heading.AccessibleName,
            Text = "Лаунчер хранит выбранные стратегии и состояние галочки WARP локально в профиле Windows. " +
                "Лаунчер не отправляет эти настройки или диагностические отчёты разработчику.\n\n" +
                "При нажатии «Проверить обновления» лаунчер запрашивает сведения о последнем релизе у GitHub. " +
                "Если пользователь подтверждает установку, он скачивает архив из GitHub Releases. " +
                "GitHub получает IP соединения и обычные данные HTTP-запроса.\n\n" +
                "Если пользователь включает WARP, клиент связывается с Cloudflare для подключения, " +
                "а трафик, направленный в туннель, проходит через сеть Cloudflare. При первой регистрации " +
                "встроенного клиента Cloudflare получает IP соединения и данные для регистрации устройства. " +
                "Регистрационные данные встроенного WARP хранятся в профиле Windows в зашифрованном виде. " +
                "При использовании официального клиента данные хранятся у него."
        };
        policy.SetBounds(34, 102, 380, 340);
        privacyPage.Controls.Add(policy);

        var cloudflare = PageLink("Политика конфиденциальности Cloudflare WARP");
        cloudflare.TextAlign = ContentAlignment.MiddleLeft;
        cloudflare.SetBounds(34, 454, 380, 30);
        cloudflare.LinkClicked += (sender, args) => OpenLink("https://developers.cloudflare.com/warp-client/privacy/");
        privacyPage.Controls.Add(cloudflare);

        var github = PageLink("Политика конфиденциальности GitHub");
        github.TextAlign = ContentAlignment.MiddleLeft;
        github.SetBounds(34, 489, 380, 30);
        github.LinkClicked += (sender, args) => OpenLink("https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement");
        privacyPage.Controls.Add(github);
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
                    case "Discord": discordStrategy.Restore(value); break;
                    case "YouTube": youtubeStrategy.Restore(value); break;
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
        var temporary = preferencesPath + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(preferencesPath));
            File.WriteAllLines(temporary, new[] {
                "Darktide=" + darktideStrategy.Selected,
                "Discord=" + discordStrategy.Selected,
                "YouTube=" + youtubeStrategy.Selected,
                "WARP=" + useWarp.Checked,
                "Tray=" + trayMode.Checked,
                "StartFilter=" + startFilter.Checked
            });
            if (File.Exists(preferencesPath)) File.Replace(temporary, preferencesPath, null);
            else File.Move(temporary, preferencesPath);
        }
        catch (IOException error) { ShowPreferencesError("Не удалось сохранить настройки.", error); }
        catch (UnauthorizedAccessException error) { ShowPreferencesError("Не удалось сохранить настройки.", error); }
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
            ? "Используйте WARP, если ни одна стратегия не подошла для Darktide."
            : "Установите Cloudflare WARP или восстановите файлы папки warp."; }
    }

    private sealed class StrategyPicker<T> : Button
    {
        private readonly ContextMenuStrip menu = new ContextMenuStrip();

        public T Selected { get; private set; }
        public event EventHandler SelectionChanged;

        public StrategyPicker(string service)
        {
            AccessibleName = "Стратегия " + service;
            AccessibleDescription = "Открывает список стратегий";
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderColor = Border;
            FlatAppearance.MouseOverBackColor = Border;
            BackColor = Field;
            ForeColor = Primary;
            menu.BackColor = Field;
            menu.ForeColor = Primary;
            menu.ShowImageMargin = false;
            menu.Renderer = new ToolStripProfessionalRenderer(new StrategyColors());
            menu.AutoSize = false;
        }

        public void Add(T value, string label)
        {
            var item = new ToolStripMenuItem(label) { Tag = value, AutoSize = false, TextAlign = ContentAlignment.MiddleCenter };
            item.Click += (sender, args) =>
            {
                Select(item);
                if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
            };
            menu.Items.Add(item);
            if (menu.Items.Count == 1) Select(item);
        }

        public void Restore(string value)
        {
            foreach (ToolStripMenuItem item in menu.Items)
            {
                if (item.Tag.ToString() != value) continue;
                Select(item);
                return;
            }
        }

        private void Select(ToolStripMenuItem item)
        {
            Selected = (T)item.Tag;
            Text = item.Text;
            AccessibleDescription = "Выбрано: " + item.Text;
            foreach (ToolStripMenuItem option in menu.Items)
                option.ForeColor = option == item ? Color.FromArgb(255, 183, 111) : Primary;
        }

        protected override void OnClick(EventArgs args)
        {
            base.OnClick(args);
            var rowHeight = Math.Max(Height, Font.Height + 8);
            menu.Size = new Size(Width, rowHeight * menu.Items.Count + 4);
            foreach (ToolStripItem item in menu.Items)
                item.Size = new Size(Width - 2, rowHeight);
            menu.Show(this, new Point(0, Height));
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
        if (busy || installingUpdate) return;
        if (active || warp.OwnsConnection)
        {
            await StopSession();
            return;
        }

        busy = true;
        diagnostics = null;
        message.Text = "Запуск сетевого фильтра…";
        RefreshControls();
        var profile = darktideStrategy.Selected;
        var discord = discordStrategy.Selected;
        var youtube = youtubeStrategy.Selected;
        var withWarp = useWarp.Checked && warp.Installed;
        try
        {
            await Task.Run(() => engine.Start(profile, withWarp, discord, youtube));
            active = true;
            message.Text = "Фильтр запущен для Darktide, Discord и YouTube.";
            if (withWarp)
            {
                if (warp.UsesOfficialClient)
                {
                    message.Text = "Фильтр запущен. Подключение WARP…";
                    message.Text = await warp.ConnectOfficial();
                }
                else if (warp.OtherVpnActive())
                    message.Text = "WARP пропущен: обнаружен другой активный VPN.";
                else if (!warp.Registered && !ConfirmWarpRegistration())
                    message.Text = "Фильтр работает. Регистрация WARP отменена.";
                else
                {
                    message.Text = "Фильтр запущен. Подключение WARP…";
                    message.Text = await warp.Connect();
                }
            }
        }
        catch (Exception error)
        {
            diagnostics = error.ToString();
            message.Text = active ? "Фильтр запущен. Ошибка Cloudflare WARP." : error.Message;
        }
        finally
        {
            busy = false;
            RefreshControls();
        }
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
        try
        {
            await warp.Disconnect();
            message.Text = IdleMessage;
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
        finally
        {
            active = false;
            busy = false;
            RefreshControls();
        }
        return !warp.OwnsConnection;
    }

    private void RefreshControls()
    {
        status.Text = busy ? "Пожалуйста, подождите" :
            active ? "Фильтр включён" : "Фильтр выключен";
        status.ForeColor = active ? Primary : Secondary;
        toggle.Active = active;
        toggle.Text = busy ? "Подождите" :
            active ? "Выключить" : warp.OwnsConnection ? "Отключить WARP" : "Включить";
        toggle.Enabled = !busy && !installingUpdate;
        toggle.Invalidate();
        tray.Text = Program.Name + " — " + status.Text;
        traySwitch.Text = busy ? "Подождите" :
            active ? "Выключить фильтр" : warp.OwnsConnection ? "Отключить WARP" : "Включить фильтр";
        traySwitch.Enabled = trayExit.Enabled = !busy && !installingUpdate;
        updateButton.Enabled = !busy && !checkingUpdate && !installingUpdate;
        updateButton.Text = availableUpdate == null ? "Проверить обновления" :
            "Установить v" + availableUpdate.Version.ToString(3);
        var canConfigure = !busy && !active && !warp.OwnsConnection;
        darktideStrategy.Enabled = canConfigure;
        discordStrategy.Enabled = canConfigure;
        youtubeStrategy.Enabled = canConfigure;
        useWarp.Enabled = canConfigure && warp.Installed;
        details.Visible = !busy && diagnostics != null;
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
    protected override void OnPaint(PaintEventArgs args)
    {
        var graphics = args.Graphics;
        graphics.Clear(BackColor);
        var scale = graphics.DpiX / 96f;
        var size = Math.Max(12, (int)Math.Round(12 * scale));
        var box = new Rectangle(1, (ClientSize.Height - size) / 2, size, size);
        var orange = Color.FromArgb(255, 155, 74);
        var foreground = Enabled ? ForeColor : Color.FromArgb(170, 170, 170);
        using (var border = new Pen(Checked ? orange : Color.FromArgb(125, 125, 125), scale))
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

internal sealed class PowerButton : Button
{
    public bool Active { get; set; }
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
        var outlineColor = Active ? Color.FromArgb(255, 183, 111) : Color.FromArgb(ring, ring, ring);
        var glowColor = Active ? Color.FromArgb(255, 115, 27) : outlineColor;
        var shade = highlighted ? 39 : 26;
        using (var halo = new Pen(Color.FromArgb(Active ? 37 : 0, glowColor), 13 * scale))
        using (var glow = new Pen(Color.FromArgb(Active ? 115 : 31, glowColor), 6 * scale))
        using (var outline = new Pen(outlineColor, (Active ? 2.1f : 1.6f) * scale))
        using (var fill = new SolidBrush(Color.FromArgb(shade, shade, shade)))
        {
            graphics.FillEllipse(fill, circle);
            if (Active) graphics.DrawEllipse(halo, circle);
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
