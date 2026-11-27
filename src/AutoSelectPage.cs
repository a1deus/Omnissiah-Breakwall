using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed partial class MainWindow
{
    private readonly Panel autoPage = new Panel { Dock = DockStyle.Fill, Visible = false };
    private readonly Button openAuto = new DimmedSettingsButton();
    private readonly Button runAuto = new Button();
    private readonly AutoProgressBar autoProgress = new AutoProgressBar();
    private readonly Label autoPercent = new Label();
    private readonly Label autoStatus = new Label();
    private readonly Label autoResultsTitle = new Label();
    private readonly FlowLayoutPanel autoResults = new FlowLayoutPanel();
    private InformationScrollView autoResultsView;
    private readonly Dictionary<AutoGroup, CheckBox> autoGroups = new Dictionary<AutoGroup, CheckBox>();
    private readonly List<Button> autoApplyButtons = new List<Button>();
    private CancellationTokenSource autoAttempt;
    private bool autoSelecting, closeAfterAuto;
    private int autoGeneration;
    private Func<AutoSelector> autoSelectorFactory;

    private void BuildAutoPage()
    {
        Controls.Add(autoPage);
        AddInformationHeading(autoPage, "Автоподбор");
        var hint = new Label {
            Text = "Darktide: TLS и UDP-ответ серверов. Остальные игры: TLS. Для UDP нужны недавние адреса из локальных логов Darktide. Матч не проверяется.",
            ForeColor = Secondary, Font = new Font("Segoe UI", 9)
        };
        hint.SetBounds(34, 96, 380, 48); autoPage.Controls.Add(hint);
        help.SetToolTip(hint, "Darktide: адреса берутся только из строк подключения в недавних локальных логах. Логи, адреса и результаты не сохраняются и не отправляются разработчику. Если адресов нет, сначала запустите игру до попытки входа в хаб. Space Marine 2: игровой UDP не проверяется.");
        var groupCard = new Panel { BackColor = Surface, AccessibleName = "Группы для автоподбора" };
        groupCard.SetBounds(34, 154, 380, 112); autoPage.Controls.Add(groupCard);
        var groups = new[] { AutoGroup.Darktide, AutoGroup.SpaceMarine2, AutoGroup.Youtube, AutoGroup.Discord, AutoGroup.Warp };
        for (var index = 0; index < groups.Length; index++)
        {
            var group = groups[index];
            var option = new AccentCheckBox { Text = AutoSelector.GroupName(group), ForeColor = Primary, BackColor = Surface };
            option.SetBounds(12 + index % 2 * 190, 8 + index / 2 * 34, 168, 28);
            option.CheckedChanged += (sender, args) => RefreshAutoControls();
            autoGroups.Add(group, option); groupCard.Controls.Add(option);
        }
        StyleAutoButton(runAuto); runAuto.Text = "Запустить подбор";
        runAuto.SetBounds(34, 278, 380, 34);
        runAuto.Click += async (sender, args) => await RunAutoSelection();
        autoPage.Controls.Add(runAuto);
        autoProgress.SetBounds(34, 329, 324, 10); autoProgress.Visible = false;
        autoPage.Controls.Add(autoProgress);
        autoPercent.SetBounds(363, 320, 51, 25); autoPercent.TextAlign = ContentAlignment.MiddleRight;
        autoPercent.ForeColor = Secondary; autoPercent.Visible = false; autoPage.Controls.Add(autoPercent);
        autoStatus.SetBounds(34, 352, 380, 45); autoStatus.Font = new Font("Segoe UI", 9);
        autoStatus.ForeColor = Secondary; autoStatus.Text = "Отметьте хотя бы одну группу. Подбор может занять несколько минут.";
        autoPage.Controls.Add(autoStatus);
        autoResultsTitle.Text = "Прошли проверку"; autoResultsTitle.Font = new Font("Segoe UI Semibold", 11);
        autoResultsTitle.SetBounds(34, 406, 380, 26); autoResultsTitle.Visible = false; autoPage.Controls.Add(autoResultsTitle);
        autoResults.FlowDirection = FlowDirection.TopDown; autoResults.WrapContents = false;
        autoResults.AutoSize = true; autoResults.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        autoResultsView = new InformationScrollView(autoResults) { AccessibleName = "Результаты автоподбора", Visible = false };
        autoResultsView.SetBounds(34, 440, 380 + SystemInformation.VerticalScrollBarWidth, 211);
        autoPage.Controls.Add(autoResultsView);
        autoSelectorFactory = () => new AutoSelector(new AutoSession(engine, warp),
            new AutoProbe(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "profiles"), new CurlAutoHttp(CurlAutoHttp.SystemCurl)));
        RefreshAutoControls();
    }

    private static void StyleAutoButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat; button.Cursor = Cursors.Hand;
        button.BackColor = Field; button.ForeColor = Primary;
        button.FlatAppearance.BorderColor = Border;
        button.FlatAppearance.MouseOverBackColor = Border;
    }

    private void RefreshAutoControls()
    {
        var idle = !busy && !active && !warp.OwnsConnection && !installingUpdate;
        // The page must remain reachable when a user navigates away mid-run.
        openAuto.Enabled = autoSelecting || idle;
        foreach (var option in autoGroups.Values) option.Enabled = idle;
        runAuto.Enabled = autoSelecting ? autoAttempt != null && !autoAttempt.IsCancellationRequested :
            idle && autoGroups.Values.Any(option => option.Checked);
        runAuto.Text = autoSelecting ? autoAttempt != null && autoAttempt.IsCancellationRequested ? "Останавливаем…" : "Остановить подбор" : "Запустить подбор";
        if (!autoSelecting && autoGeneration == 0)
            autoStatus.Text = autoGroups.Values.Any(option => option.Checked) ?
                "Нажмите «Запустить подбор». Проверка может занять несколько минут." :
                "Отметьте хотя бы одну группу. Подбор может занять несколько минут.";
        foreach (var button in autoApplyButtons) button.Enabled = idle;
    }

    private List<AutoCandidate> AutoCandidates()
    {
        var candidates = new List<AutoCandidate>();
        if (autoGroups[AutoGroup.Darktide].Checked)
            foreach (var option in darktideStrategy.Choices.Skip(1)) {
                var candidate = Candidate(AutoGroup.Darktide, option.Key.ToString(), option.Value); candidate.Darktide = option.Key; candidates.Add(candidate);
            }
        if (autoGroups[AutoGroup.SpaceMarine2].Checked)
            foreach (var option in spaceMarineStrategy.Choices.Skip(1)) {
                var candidate = Candidate(AutoGroup.SpaceMarine2, option.Key.ToString(), option.Value); candidate.SpaceMarine = option.Key; candidates.Add(candidate);
            }
        if (autoGroups[AutoGroup.Youtube].Checked)
            foreach (var option in youtubeStrategy.Choices.Skip(1)) {
                var candidate = Candidate(AutoGroup.Youtube, option.Key.ToString(), option.Value); candidate.Youtube = option.Key; candidates.Add(candidate);
            }
        if (autoGroups[AutoGroup.Discord].Checked)
            foreach (var option in discordStrategy.Choices.Skip(1)) {
                var candidate = Candidate(AutoGroup.Discord, option.Key.ToString(), option.Value); candidate.Discord = option.Key; candidates.Add(candidate);
            }
        if (autoGroups[AutoGroup.Warp].Checked)
            foreach (var option in warpStrategy.Choices.Skip(1)) {
                var candidate = Candidate(AutoGroup.Warp, option.Key.ToString(), option.Value); candidate.Warp = option.Key; candidates.Add(candidate);
            }
        return candidates;
    }

    private static AutoCandidate Candidate(AutoGroup group, string id, string label)
    {
        return new AutoCandidate { Group = group, Id = id, Label = label, Code = label.Split(' ')[0] };
    }

    private async Task RunAutoSelection()
    {
        if (autoSelecting)
        {
            autoStatus.Text = "Останавливаем проверку и отключаем проверочное подключение…";
            autoAttempt.Cancel();
            RefreshAutoControls(); return;
        }
        if (busy || active || warp.OwnsConnection || installingUpdate) return;
        var candidates = AutoCandidates(); if (candidates.Count == 0) return;
        if (!File.Exists(CurlAutoHttp.SystemCurl)) {
            autoStatus.Text = "Не найден системный curl.exe для проверки HTTPS. Обновите компоненты Windows."; return;
        }
        if (autoGroups[AutoGroup.Warp].Checked && !warp.Installed) {
            autoStatus.Text = "Не найдены файлы клиента WARP. Восстановите папку warp или установите официальный клиент."; return;
        }
        // This is the same registration consent used by the ordinary WARP path.
        if (autoGroups[AutoGroup.Warp].Checked && !warp.UsesOfficialClient && !warp.Registered && !ConfirmWarpRegistration()) {
            autoStatus.Text = "Подбор не запущен: регистрация WARP отменена."; return;
        }
        ClearAutoResults();
        var selector = autoSelectorFactory();
        var generation = ++autoGeneration;
        using (var attempt = new CancellationTokenSource())
        {
            autoAttempt = attempt; autoSelecting = true; busy = true;
            autoProgress.Value = 0; autoPercent.Text = "0%";
            autoProgress.Visible = autoPercent.Visible = true;
            autoStatus.Text = "Подготовка проверки…";
            message.Text = "Идёт автоподбор. Его можно остановить на странице «Автоподбор»."; RefreshControls();
            var completed = false;
            string failure = null;
            var progress = new Progress<AutoProgress>(update => {
                if (IsDisposed || generation != autoGeneration || !autoSelecting) return;
                autoProgress.Value = Math.Max(0, Math.Min(100, update.Percent)); autoPercent.Text = update.Percent + "%";
                autoStatus.Text = update.Message + "\n" + update.Completed + " из " + update.Total;
            });
            try { await selector.Run(candidates, progress, attempt.Token); completed = true; }
            catch (OperationCanceledException) { }
            catch (AutoEnvironmentException error) { selector.Results.Clear(); failure = error.Message; }
            catch (Exception error) { failure = error.Message; }
            finally
            {
                autoSelecting = false; autoAttempt = null; busy = false;
                active = engine.Running;
                if (completed) { autoProgress.Value = 100; autoPercent.Text = "100%"; }
                else { var percent = selector.Results.Count * 100 / candidates.Count; autoProgress.Value = percent; autoPercent.Text = percent + "%"; }
                ShowAutoResults(selector.Results.Where(result => result.Passed));
                var passed = selector.Results.Count(result => result.Passed);
                var baseline = selector.Baselines.Where(pair => pair.Value.Passed).Select(pair => AutoSelector.GroupName(pair.Key)).ToArray();
                autoStatus.Text = failure ?? (completed ? "Подбор завершён. Прошли проверку: " + passed + "." :
                    "Подбор остановлен. Проверено " + selector.Results.Count + " из " + candidates.Count + ", прошли: " + passed + ".");
                if (baseline.Length != 0 && failure == null) autoStatus.Text += "\nБез стратегии тоже доступно: " + string.Join(", ", baseline) + ".";
                if (selector.SkippedGroups.Count != 0 && failure == null)
                    autoStatus.Text = string.Join("\n", selector.SkippedGroups.Values) + "\nПрошли проверку: " + passed + ".";
                message.Text = failure == null ? IdleMessage : "Автоподбор остановлен. Подробности на его странице.";
                help.SetToolTip(autoStatus, autoStatus.Text);
                RefreshControls();
                if (closeAfterAuto) {
                    closeAfterAuto = false;
                    if (!active && !warp.OwnsConnection) { allowClose = true; BeginInvoke(new Action(Close)); }
                    else { exitRequested = false; ShowWindow(); }
                }
            }
        }
    }

    private void ClearAutoResults()
    {
        autoApplyButtons.Clear();
        foreach (Control control in autoResults.Controls.Cast<Control>().ToArray()) control.Dispose();
        autoResults.Controls.Clear(); autoResults.Top = 0;
        autoResultsTitle.Visible = autoResultsView.Visible = false;
    }

    private void ShowAutoResults(IEnumerable<AutoResult> results)
    {
        ClearAutoResults();
        var count = 0;
        foreach (var result in results)
        {
            var candidate = result.Candidate;
            var row = new Panel { BackColor = Surface, Size = new Size(380, 70), Margin = new Padding(0, 0, 0, 6), AccessibleName = candidate.Code };
            var code = new Label { Text = candidate.Code + " · " + AutoSelector.GroupName(candidate.Group),
                Font = new Font("Segoe UI Semibold", 10), AutoEllipsis = true };
            code.SetBounds(12, 8, 236, 25); row.Controls.Add(code);
            var apply = new Button { Text = "Применить", AccessibleName = "Применить " + candidate.Code };
            StyleAutoButton(apply); apply.SetBounds(252, 8, 116, 29);
            help.SetToolTip(apply, candidate.Label + "\n" + result.Detail);
            apply.Click += (sender, args) => ApplyAutoCandidate(candidate);
            row.Controls.Add(apply); autoApplyButtons.Add(apply);
            var scope = new Label { Text = AutoSelector.Scope(candidate.Group), ForeColor = Secondary,
                Font = new Font("Segoe UI", 8), AutoEllipsis = true };
            scope.SetBounds(12, 43, 356, 20); row.Controls.Add(scope);
            autoResults.Controls.Add(row); count++;
        }
        autoResultsTitle.Text = "Прошли проверку: " + count;
        autoResultsTitle.Visible = autoResultsView.Visible = true;
    }

    private void ApplyAutoCandidate(AutoCandidate candidate)
    {
        if (busy || active || warp.OwnsConnection || installingUpdate) return;
        switch (candidate.Group) {
            case AutoGroup.Darktide: darktideStrategy.Restore(candidate.Id); break;
            case AutoGroup.SpaceMarine2: spaceMarineStrategy.Restore(candidate.Id); break;
            case AutoGroup.Discord: discordStrategy.Restore(candidate.Id); break;
            case AutoGroup.Youtube: youtubeStrategy.Restore(candidate.Id); break;
            case AutoGroup.Warp: warpStrategy.Restore(candidate.Id); break;
        }
        if (!WritePreferences()) { autoStatus.Text = "Вариант выбран, но сохранить настройки не удалось. Откройте «Подробности» на главном экране."; return; }
        autoStatus.Text = "Выбран " + candidate.Code + ". Настройка сохранена." +
            (candidate.Group == AutoGroup.Warp ? "\nДля подключения отметьте «Подключить WARP» на главном экране." : "\nВернитесь на главный экран и нажмите «Включить».");
        help.SetToolTip(autoStatus, autoStatus.Text);
    }
}

internal sealed class AutoProgressBar : Control
{
    private int value;
    public AutoProgressBar()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
        AccessibleRole = AccessibleRole.ProgressBar; AccessibleName = "Ход автоподбора";
    }
    public int Value
    {
        get { return value; }
        set { this.value = Math.Max(0, Math.Min(100, value)); AccessibleDescription = this.value + "%"; Invalidate(); }
    }
    protected override void OnPaint(PaintEventArgs args)
    {
        args.Graphics.Clear(Color.FromArgb(36, 36, 36));
        using (var brush = new SolidBrush(Color.FromArgb(228, 143, 50)))
            args.Graphics.FillRectangle(brush, 0, 0, ClientSize.Width * value / 100, ClientSize.Height);
    }
}
