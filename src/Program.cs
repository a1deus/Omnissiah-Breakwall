using System;
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
    private static void Main()
    {
        bool first;
        // Share the mutex with copies released under the former name.
        using (var instance = new Mutex(true, @"Local\DarktideDiscordPortable", out first))
        {
            if (!first)
            {
                MessageBox.Show("Приложение уже открыто.", Name);
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var preferencesPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Name, "strategies.txt");
            using (var window = new MainWindow(preferencesPath)) Application.Run(window);
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
    private readonly CheckBox useWarp = new WarpCheckBox();
    private readonly StrategyPicker<Strategy> darktideStrategy = new StrategyPicker<Strategy>("Darktide");
    private readonly StrategyPicker<ServiceStrategy> discordStrategy = new StrategyPicker<ServiceStrategy>("Discord");
    private readonly StrategyPicker<ServiceStrategy> youtubeStrategy = new StrategyPicker<ServiceStrategy>("YouTube");
    private readonly Label status = new Label();
    private readonly Label message = new Label();
    private readonly LinkLabel details = new LinkLabel();
    private readonly System.Windows.Forms.Timer health = new System.Windows.Forms.Timer();
    private readonly string preferencesPath;
    private bool active;
    private bool busy;
    private bool allowClose;
    private string diagnostics;

    public MainWindow(string preferencesPath)
    {
        this.preferencesPath = preferencesPath;
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

        logo.SetBounds(178, 23, 92, 92);
        logo.SizeMode = PictureBoxSizeMode.Zoom;
        using (var stream = typeof(Program).Assembly.GetManifestResourceStream("omnissiah.png"))
        using (var artwork = Image.FromStream(stream))
            logo.Image = new Bitmap(artwork);
        Controls.Add(logo);

        var title = new Label {
            Text = Program.Name, TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI Semibold", 23)
        };
        title.SetBounds(24, 121, 400, 45);
        Controls.Add(title);

        status.SetBounds(24, 306, 400, 25);
        status.TextAlign = ContentAlignment.MiddleCenter;
        status.Font = new Font("Segoe UI Semibold", 9);
        Controls.Add(status);

        toggle.SetBounds(161, 176, 126, 126);
        toggle.FlatStyle = FlatStyle.Flat;
        toggle.FlatAppearance.BorderSize = 0;
        toggle.Cursor = Cursors.Hand;
        toggle.Font = new Font("Segoe UI Semibold", 10);
        toggle.Click += async (sender, args) => await Switch();
        Controls.Add(toggle);

        var settings = new Panel { BackColor = Surface };
        settings.SetBounds(38, 343, 372, 155);
        Controls.Add(settings);
        darktideStrategy.SetBounds(28, 12, 316, 29);
        darktideStrategy.Add(Strategy.Standard, "Darktide UDP · основной");
        darktideStrategy.Add(Strategy.Alternate, "Darktide UDP · усиленный + TLS");
        settings.Controls.Add(darktideStrategy);

        discordStrategy.SetBounds(28, 48, 316, 29);
        discordStrategy.Add(ServiceStrategy.Standard, "Discord · основной");
        discordStrategy.Add(ServiceStrategy.BadSequence, "Discord · TLS со смещением");
        discordStrategy.Add(ServiceStrategy.TimestampFake, "Discord · TLS с метками времени");
        discordStrategy.Add(ServiceStrategy.HostSplit, "Discord · подмена имени хоста");
        discordStrategy.Add(ServiceStrategy.TimestampOverlap, "Discord · TLS с перекрытием");
        discordStrategy.Add(ServiceStrategy.SimpleFake, "Discord · простой TLS");
        discordStrategy.Add(ServiceStrategy.SniSplit, "Discord · разбиение по SNI");
        settings.Controls.Add(discordStrategy);

        youtubeStrategy.SetBounds(28, 84, 316, 29);
        youtubeStrategy.Add(ServiceStrategy.Standard, "YouTube · основной");
        youtubeStrategy.Add(ServiceStrategy.TimestampFake, "YouTube · TLS с метками времени");
        youtubeStrategy.Add(ServiceStrategy.HostSplit, "YouTube · подмена имени хоста");
        youtubeStrategy.Add(ServiceStrategy.TimestampOverlap, "YouTube · TLS с перекрытием");
        youtubeStrategy.Add(ServiceStrategy.SimpleFake, "YouTube · простой TLS");
        youtubeStrategy.Add(ServiceStrategy.SniSplit, "YouTube · разбиение по SNI");
        settings.Controls.Add(youtubeStrategy);

        useWarp.Text = "Подключить WARP";
        useWarp.AutoSize = true;
        useWarp.Location = new Point(0, 120);
        settings.Controls.Add(useWarp);
        settings.Layout += (sender, args) =>
            useWarp.Left = (settings.ClientSize.Width - useWarp.Width) / 2;
        settings.PerformLayout();

        message.SetBounds(24, 501, 400, 31);
        message.TextAlign = ContentAlignment.MiddleCenter;
        message.ForeColor = Secondary;
        message.Font = new Font("Segoe UI", 9);
        Controls.Add(message);

        details.Text = "Подробности";
        details.SetBounds(24, 536, 400, 18);
        details.TextAlign = ContentAlignment.MiddleCenter;
        details.Font = new Font("Segoe UI", 8);
        details.LinkColor = Secondary;
        details.ActiveLinkColor = Primary;
        details.LinkClicked += (sender, args) => MessageBox.Show(this,
            diagnostics + Environment.NewLine + Environment.NewLine + engine.Details,
            "Подробности", MessageBoxButtons.OK, MessageBoxIcon.Information);
        Controls.Add(details);
        details.BringToFront();

        var motto = new Label {
            Text = "Слава Омниссии!", TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Secondary, Font = new Font("Segoe UI Semibold", 10)
        };
        motto.SetBounds(24, 592, 400, 24);
        Controls.Add(motto);

        health.Interval = 1200;
        health.Tick += async (sender, args) =>
        {
            if (busy || !active) return;
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
                "WARP=" + useWarp.Checked
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
        diagnostics = error.Message;
        RefreshControls();
    }

    private string IdleMessage
    {
        get { return warp.Installed
            ? "Для проверки входа в Darktide оставьте WARP выключенным."
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
        if (busy) return;
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
                else if (!warp.Registered && MessageBox.Show(this,
                    "Для первого подключения " + Program.Name + " зарегистрирует устройство в Cloudflare WARP.\n\n" +
                    "Условия Cloudflare: https://www.cloudflare.com/application/terms/\n\n" +
                    "Вы принимаете условия и хотите зарегистрировать устройство?",
                    "Регистрация Cloudflare WARP", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes)
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
        toggle.Enabled = !busy;
        toggle.Invalidate();
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
        if (allowClose || (!busy && !active && !warp.OwnsConnection)) return;
        if (args.CloseReason == CloseReason.WindowsShutDown) return;
        args.Cancel = true;
        if (busy) return;
        if (await StopSession())
        {
            allowClose = true;
            BeginInvoke(new Action(Close));
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            health.Dispose();
            warp.Dispose();
            engine.Dispose();
            if (logo.Image != null) logo.Image.Dispose();
            if (Icon != null) Icon.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class WarpCheckBox : CheckBox
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
        TextRenderer.DrawText(graphics, Text, Font, textBounds, foreground,
            TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
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
