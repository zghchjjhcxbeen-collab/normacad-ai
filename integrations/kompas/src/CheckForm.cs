// Главное окно проверки: раскладка и ход проверки. Виды области результата — в ProgressView.cs, ReportView.cs,
// FailureView.cs; окна входа и прокси — в Dialogs.cs; запуск окна из КОМПАС — в Launcher.cs.
// Макет — integrations/kompas/mockup/index.html.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace NormaCad.Kompas
{
    internal class CheckForm : Form
    {
        /// <summary>Что сейчас в области результата.</summary>
        internal enum Pane { Hint, Progress, Report, Failure }

        private const string LoginExpired = "Вход больше не действует. Войдите снова.";

        private readonly Settings cfg;
        private readonly ApiClient api = new ApiClient();
        private readonly Ui ui = new Ui();

        // Панель аккаунта
        private Label lblEmail;
        private LinkLabel lnkAccount;
        private WrapRow metaFlow;
        // Основное окно
        private Label lblDoc, lblStatus;
        private ComboBox cbDomain;
        private Button btnCheck;
        private ProgressBar bar;
        private FramePanel result;
        private LinkLabel lnkSite;
        private CheckBox chkTop;
        private Button btnTheme;

        // Область результата
        private Pane pane = Pane.Hint;
        private Control view;
        private ProgressView progressView;
        private ReportView reportView;

        // Документ и то, что о нём известно
        private string docName = "";
        private LocalFacts facts;
        private string exportWarning = "";
        private string filePath, uploadName;
        private int checkId;

        // КОМПАС и ход проверки
        private int cookie;
        private bool standalone;
        private bool began;
        private bool reloginTried;
        private Thread worker;
        private volatile bool lastRunSent;
        private readonly ManualResetEvent stop = new ManualResetEvent(false);
        private readonly AutoResetEvent wake = new AutoResetEvent(false);

        // Последний показанный сбой
        private Failure shownFailure;
        private bool failureFromAccount;
        private bool accountLoaded;

        /// <summary>Выгрузка активного документа КОМПАС; подменяется в проверке окна (test\WindowShots.cs).</summary>
        internal Func<int, ExportResult> ExportHook;

        /// <summary>Проверка закончилась — отчётом или ошибкой.</summary>
        public event EventHandler Finished;

        public CheckForm()
        {
            cfg = Settings.Load();
            Ui.SetDark(cfg.DarkTheme);
            ProxySetup.Apply(cfg);
            api.BaseUrl = cfg.Server;
            api.Token = cfg.Token;
            ExportHook = delegate(int ck)
            {
                // Объект приложения — свой посредник для этого потока (Global Interface Table)
                object app = KompasLink.Get(ck);
                return Exporter.ExportActive(app);
            };
            Build();
        }

        /// <summary>NormaCadCheck.exe: окно без КОМПАС, «Проверить» отправляет выбранный файл.</summary>
        public bool Standalone
        {
            get { return standalone; }
            set { standalone = value; UpdateDoc(); }
        }

        internal Pane CurrentPane { get { return pane; } }

        internal bool Busy { get { return worker != null; } }

        internal bool AccountLoaded { get { return accountLoaded; } }

        internal ReportView CurrentReport { get { return reportView; } }

        internal string StatusLine { get { return lblStatus.Text; } }

        internal void ClickCheck()
        {
            btnCheck.PerformClick();
        }

        // ------------------------------------------------------------------
        // Раскладка
        // ------------------------------------------------------------------

        private void Build()
        {
            Text = "NormaCAD.PRO — проверка перед нормоконтролем";
            Font = ui.Font;
            AutoScaleMode = AutoScaleMode.None;
            ShowIcon = false;
            MaximizeBox = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = cfg.TopMost;
            ClientSize = ui.S(400, 760);
            BackColor = Ui.Background;
            ForeColor = Ui.Text;
            HandleCreated += delegate { Ui.ThemeCaption(this); };

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.GrowStyle = TableLayoutPanelGrowStyle.AddRows;
            root.Padding = ui.P(14);

            FlowLayoutPanel brand = new FlowLayoutPanel();
            brand.AutoSize = true;
            brand.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            brand.WrapContents = false;
            Control mark = ui.MakeBrandMark();
            mark.Margin = ui.P(0, 0, 8, 0);
            brand.Controls.Add(mark);
            Label brandName = ui.MakeLabel("NormaCAD", Ui.Text, ui.Large);
            brandName.Margin = ui.P(0, 5, 0, 0);
            Label brandPro = ui.MakeLabel(".PRO", Ui.Accent, ui.Large);
            brandPro.Margin = ui.P(0, 5, 0, 0);
            brand.Controls.Add(brandName);
            brand.Controls.Add(brandPro);
            TableLayoutPanel brandHeader = new TableLayoutPanel();
            brandHeader.ColumnCount = 2;
            brandHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            brandHeader.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            brandHeader.RowCount = 1;
            brandHeader.AutoSize = true;
            brandHeader.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            brandHeader.Margin = Padding.Empty;
            brand.Margin = Padding.Empty;
            brand.Anchor = AnchorStyles.Left;
            btnTheme = ui.MakeButton("");
            btnTheme.Name = "themeSwitch";
            btnTheme.AutoSize = false;
            btnTheme.Size = ui.S(106, 30);
            btnTheme.MinimumSize = ui.S(106, 30);
            btnTheme.Anchor = AnchorStyles.Right;
            btnTheme.Margin = ui.P(8, 1, 0, 1);
            btnTheme.AccessibleRole = AccessibleRole.PushButton;
            btnTheme.Click += delegate
            {
                cfg.DarkTheme = !cfg.DarkTheme;
                Ui.ApplyTheme(this, cfg.DarkTheme);
                UpdateThemeAction();
                cfg.Save();
            };
            UpdateThemeAction();
            brandHeader.Controls.Add(brand, 0, 0);
            brandHeader.Controls.Add(btnTheme, 1, 0);
            AddRoot(root, brandHeader, false);

            // Компактный аккаунт в шапке: email, вход и сведения о доступе.
            TableLayoutPanel acc = new TableLayoutPanel();
            acc.ColumnCount = 1;
            acc.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            acc.GrowStyle = TableLayoutPanelGrowStyle.AddRows;
            acc.Padding = ui.P(0, 0, 0, 8);
            acc.AutoSize = true;
            acc.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            TableLayoutPanel head = new TableLayoutPanel();
            head.ColumnCount = 2;
            head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            head.RowCount = 1;
            head.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            head.AutoSize = true;
            head.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            head.BackColor = Ui.Background;
            head.Margin = Padding.Empty;
            lblEmail = ui.MakeLabel("");
            lblEmail.Anchor = AnchorStyles.Left;
            lnkAccount = ui.MakeLink("Выйти");
            lnkAccount.Anchor = AnchorStyles.Right;
            lnkAccount.Margin = new Padding(ui.S(8), 0, 0, 0);
            lnkAccount.LinkClicked += delegate { Guard(AccountClicked); };
            head.Controls.Add(lblEmail, 0, 0);
            head.Controls.Add(lnkAccount, 1, 0);
            Ui.AddRow(acc, head);
            metaFlow = new WrapRow();
            metaFlow.Margin = new Padding(0, ui.S(2), 0, 0);
            Ui.AddRow(acc, metaFlow);
            AddRoot(root, acc, false);

            // Документ и параметры проверки объединены в одну карточку.
            FrameTable documentCard = new FrameTable();
            documentCard.ColumnCount = 1;
            documentCard.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            documentCard.AutoSize = true;
            documentCard.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            documentCard.Padding = ui.P(12);
            Label documentTitle = ui.MakeLabel("Документ", Ui.Gray, ui.Small);
            documentTitle.Margin = ui.P(0, 0, 0, 5);
            Ui.AddRow(documentCard, documentTitle);
            lblDoc = ui.MakeLabel("", Ui.Text, ui.Bold);
            lblDoc.Margin = ui.P(0, 0, 0, 12);
            Ui.AddRow(documentCard, lblDoc);

            // Нормативы и основное действие проверки.
            TableLayoutPanel scope = new TableLayoutPanel();
            scope.ColumnCount = 2;
            scope.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            scope.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            scope.RowCount = 1;
            scope.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            scope.AutoSize = true;
            scope.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Label lblScope = ui.MakeLabel("Нормативы", Ui.Gray, ui.Small);
            lblScope.Anchor = AnchorStyles.Left;
            cbDomain = ui.MakeCombo(180);
            cbDomain.Items.Add("ЕСКД — машиностроение");
            cbDomain.Items.Add("СПДС — строительство");
            cbDomain.SelectedIndex = cfg.Domain == "spds" ? 1 : 0;
            cbDomain.Dock = DockStyle.Fill;
            cbDomain.Margin = new Padding(ui.S(8), 0, 0, 0);
            btnCheck = ui.MakeButton("Проверить");
            btnCheck.Dock = DockStyle.Fill;
            btnCheck.MinimumSize = ui.S(88, 38);
            btnCheck.Font = ui.Bold;
            btnCheck.Margin = ui.P(0, 10, 0, 0);
            ui.MakePrimary(btnCheck);
            btnCheck.Click += delegate { Guard(delegate { StartCheck(true); }); };
            scope.Controls.Add(lblScope, 0, 0);
            scope.Controls.Add(cbDomain, 1, 0);
            scope.RowCount = 2;
            scope.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            scope.Controls.Add(btnCheck, 0, 1);
            scope.SetColumnSpan(btnCheck, 2);
            Ui.AddRow(documentCard, scope);
            AddRoot(root, documentCard, false);

            // Статус и полоса хода проверки
            lblStatus = ui.MakeLabel("", Ui.Gray, null);
            lblStatus.MinimumSize = new Size(0, ui.S(16));
            AddRoot(root, lblStatus, false);
            bar = new BrandProgressBar();
            bar.Minimum = 0;
            bar.Maximum = 100;
            bar.Height = ui.S(4);
            bar.MinimumSize = new Size(0, ui.S(4));
            bar.MaximumSize = new Size(0, ui.S(4));
            AddRoot(root, bar, false);

            // Результат занимает всю оставшуюся высоту окна.
            result = new FramePanel();
            result.Padding = new Padding(ui.S(8) + 1);
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            result.Dock = DockStyle.Fill;
            result.Margin = new Padding(0, 0, 0, ui.S(6));
            root.Controls.Add(result, 0, root.RowStyles.Count - 1);
            root.RowCount = root.RowStyles.Count;

            // Низ: «Открыть отчёт на сайте» слева, «Поверх окон» справа, под ними оговорка серым
            TableLayoutPanel foot = new TableLayoutPanel();
            foot.ColumnCount = 2;
            foot.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            foot.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            foot.RowCount = 2;
            foot.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            foot.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            foot.AutoSize = true;
            foot.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            lnkSite = ui.MakeLink("Открыть отчёт на сайте");
            lnkSite.Anchor = AnchorStyles.Left;
            lnkSite.Visible = false;
            lnkSite.LinkClicked += delegate
            {
                Guard(delegate { if (checkId > 0) Process.Start(api.ReportUrl(checkId)); });
            };
            chkTop = new CheckBox();
            chkTop.UseCompatibleTextRendering = false;
            chkTop.UseMnemonic = false;
            chkTop.AutoSize = true;
            chkTop.Text = "Поверх окон";
            chkTop.Checked = cfg.TopMost;
            chkTop.Anchor = AnchorStyles.Right;
            chkTop.Margin = Padding.Empty;
            chkTop.CheckedChanged += delegate
            {
                TopMost = chkTop.Checked;
                cfg.TopMost = chkTop.Checked;
                cfg.Save();
            };
            Label disclaimer = ui.MakeLabel("Предварительная проверка ИИ. Не заменяет нормоконтролёра.", Ui.Gray, null);
            disclaimer.Dock = DockStyle.Fill;
            disclaimer.Margin = new Padding(0, ui.S(4), 0, 0);
            foot.Controls.Add(lnkSite, 0, 0);
            foot.Controls.Add(chkTop, 1, 0);
            foot.Controls.Add(disclaimer, 0, 1);
            foot.SetColumnSpan(disclaimer, 2);
            AddRoot(root, foot, true);

            Controls.Add(root);
            AcceptButton = btnCheck;
            // Initial placement only; reactivation preserves user movement.
            Shown += delegate { FitToScreen(); };

            FormClosing += delegate
            {
                // Идущую проверку останавливаем, но задание в настройках оставляем: окно продолжит его при следующем открытии
                if (worker != null)
                {
                    SupportLog.Write("окно закрыто во время проверки, задание " + cfg.PendingJob + " сохранено");
                    stop.Set();
                    wake.Set();
                }
                KompasLink.Revoke(cookie);
                cookie = 0;
            };
            Shown += delegate { RefreshAccountAsync(); };

            SetAccountState();
            UpdateDoc();
            ShowHint();
        }

        private void UpdateThemeAction()
        {
            btnTheme.Text = cfg.DarkTheme ? "Светлая тема" : "Тёмная тема";
            btnTheme.AccessibleName = cfg.DarkTheme ? "Включить светлую тему" : "Включить тёмную тему";
            btnTheme.AccessibleDescription = "Тема окна сохраняется для следующего запуска.";
        }

        /// <summary>Строка корневой таблицы: по высоте содержимого, снизу 6 px (кроме последней).</summary>
        private void AddRoot(TableLayoutPanel root, Control c, bool last)
        {
            c.Margin = new Padding(c.Margin.Left, c.Margin.Top, c.Margin.Right, last ? 0 : ui.S(6));
            Ui.AddRow(root, c);
        }

        /// <summary>Initial sidebar placement on the host monitor, preserving later user movement.</summary>
        private void FitToScreen()
        {
            IntPtr host = Process.GetCurrentProcess().MainWindowHandle;
            Rectangle area = (host != IntPtr.Zero ? Screen.FromHandle(host) : Screen.FromPoint(Cursor.Position)).WorkingArea;
            Size frame = new Size(Width - ClientSize.Width, Height - ClientSize.Height);
            Size desired = standalone ? ui.S(520, 600) : ui.S(400, 760);
            int gap = ui.S(8);
            int w = Math.Min(desired.Width + frame.Width, area.Width);
            int h = standalone ? Math.Min(desired.Height + frame.Height, area.Height)
                : Math.Max(1, area.Height - Math.Min(gap * 2, area.Height - 1));
            MinimumSize = new Size(Math.Min(ui.S(320) + frame.Width, w),
                Math.Min(ui.S(460) + frame.Height, h));
            Size = new Size(w, h);
            Location = new Point(Math.Max(area.Left, area.Right - w - gap),
                standalone ? area.Top + Math.Max(0, (area.Height - h) / 2) : area.Top + (area.Height - h) / 2);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) ui.Dispose();
            base.Dispose(disposing);
        }

        // ------------------------------------------------------------------
        // Область результата и строка статуса
        // ------------------------------------------------------------------

        private void SetStatus(string text)
        {
            lblStatus.Text = text ?? "";
        }

        private void SetBar(int percent)
        {
            bar.Value = Math.Max(0, Math.Min(100, percent));
            bar.Invalidate();
        }

        private void SetBusy(bool busy)
        {
            btnCheck.Enabled = !busy;
            cbDomain.Enabled = !busy;
        }

        private void SetPane(Pane p, Control v)
        {
            Control old = view;
            result.SuspendLayout();
            v.Dock = DockStyle.Fill;
            result.Controls.Add(v);
            if (old != null)
            {
                result.Controls.Remove(old);
                old.Dispose();
            }
            result.ResumeLayout(true);
            view = v;
            pane = p;
            if (p != Pane.Progress) progressView = null;
            if (p != Pane.Report) reportView = null;
        }

        /// <summary>Состояние 3: документ выгружен, отправки ещё не было.</summary>
        private void ShowHint()
        {
            string hint = "Здесь появятся замечания и ссылки на требования нормативов.";
            SetPane(Pane.Hint, new HintView(ui, hint));
            SetStatus("Готово к проверке.");
            SetBar(0);
            btnCheck.Text = "Проверить";
            lnkSite.Visible = false;
            checkId = 0;
            shownFailure = null;
            SetBusy(Busy);
        }

        private void UpdateDoc()
        {
            if (lblDoc == null) return;
            string line = docName != "" ? DocText.Line(docName, facts)
                : standalone ? "Файл не выбран" : "Активный документ КОМПАС-3D";
            lblDoc.Text = line;
        }

        // ------------------------------------------------------------------
        // Вход в окно
        // ------------------------------------------------------------------

        /// <summary>Вызывать в потоке окна. Объект приложения КОМПАС — через Global Interface Table (см. Launcher).</summary>
        public void AttachKompas(int newCookie)
        {
            if (newCookie == 0 || newCookie == cookie) return;
            if (Busy)
            {
                // Идущая проверка работает со своим объектом: новый не нужен
                KompasLink.Revoke(newCookie);
                return;
            }
            KompasLink.Revoke(cookie);
            cookie = newCookie;
        }

        /// <summary>
        /// Документ от КОМПАС: окно показывает строку документа и ждёт «Проверить». r == null — команда «Аккаунт».
        /// При первом открытии сохранённое задание продолжается: отчёт, которого окно не дождалось, откроется здесь.
        /// </summary>
        public void Begin(ExportResult r)
        {
            Guard(delegate
            {
                bool first = !began;
                began = true;
                if (Busy) return;
                if (r != null) ApplyExport(r);

                if (r == null && string.IsNullOrEmpty(api.Token) && first)
                {
                    ShowLogin(null);
                }
                if (first && !string.IsNullOrEmpty(api.Token) && cfg.PendingJob != "")
                {
                    ResumePending();
                    return;
                }
                // Новый документ — снова состояние 3; окно, открытое командой «Аккаунт», остаётся как есть
                if (r != null) ShowHint();
            });
        }

        /// <summary>NormaCadCheck.exe: файл выбран (null — окно просто продолжит сохранённую проверку).</summary>
        public void BeginFile(string file, string name, string warn)
        {
            Guard(delegate
            {
                standalone = true;
                began = true;
                if (file == null)
                {
                    if (!string.IsNullOrEmpty(api.Token) && cfg.PendingJob != "") ResumePending();
                    else ShowHint();
                    return;
                }
                filePath = file;
                uploadName = name;
                docName = name;
                facts = null;
                exportWarning = warn ?? "";
                UpdateDoc();
                ShowHint();
                StartCheck(true);
            });
        }

        private void ApplyExport(ExportResult r)
        {
            docName = !string.IsNullOrEmpty(r.DocumentName) ? r.DocumentName : (r.UploadName ?? "");
            facts = r.Facts;
            exportWarning = r.Warning ?? "";
            filePath = r.Path;
            uploadName = r.UploadName;
            UpdateDoc();
        }

        // ------------------------------------------------------------------
        // Ход проверки
        // ------------------------------------------------------------------

        private sealed class RunArgs
        {
            public bool Export;
            public int Cookie;
            public string File = "", Name = "", Domain = "eskd", ResumeJob = "";
            public LocalFacts Facts;
        }

        /// <summary>«Проверить» / «Проверить снова»: в КОМПАС заново выгружает активный документ, в NormaCadCheck — файл.</summary>
        private void StartCheck(bool byUser)
        {
            if (Busy) return;
            if (byUser) reloginTried = false;
            if (string.IsNullOrEmpty(api.Token) && !ShowLogin(null))
            {
                SetStatus("Для проверки нужен вход в NormaCAD.PRO.");
                return;
            }
            if (standalone && filePath == null)
            {
                using (OpenFileDialog dlg = new OpenFileDialog())
                {
                    dlg.Title = "Чертёж для проверки";
                    dlg.Filter = "Чертежи (PDF, PNG, JPG, DWG, DXF)|*.pdf;*.png;*.jpg;*.jpeg;*.dwg;*.dxf|Все файлы|*.*";
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    filePath = dlg.FileName;
                    uploadName = Path.GetFileName(filePath);
                    docName = uploadName;
                    UpdateDoc();
                }
            }

            cfg.Domain = cbDomain.SelectedIndex == 1 ? "spds" : "eskd";
            cfg.Save();
            // Новая отправка заменяет незавершённую: старое задание больше не нужно
            if (cfg.PendingJob != "")
            {
                SupportLog.Write("сохранённое задание " + cfg.PendingJob + " сброшено: запущена новая проверка");
                cfg.ClearPending();
            }

            RunArgs a = new RunArgs();
            a.Export = !standalone;
            a.Cookie = cookie;
            a.File = filePath ?? "";
            a.Name = uploadName ?? "";
            a.Domain = cfg.Domain;
            a.Facts = facts;
            SupportLog.Write("старт проверки: " + (docName != "" ? docName : a.Name));
            if (a.Export)
            {
                BeginRun(a, new HintView(ui, ""), Pane.Hint, "Выгружаю документ из КОМПАС-3D…");
            }
            else
            {
                ProgressView pv = new ProgressView(ui, facts);
                BeginRun(a, pv, Pane.Progress, JobView.Sending().StatusText(facts != null && FactsText.Readable(facts)));
                progressView = pv;
            }
        }

        /// <summary>Продолжить сохранённую проверку: файл заново не отправляется, окно снова опрашивает то же задание.</summary>
        private void ResumePending()
        {
            if (cfg.PendingJob == "" || Busy) return;
            if (string.IsNullOrEmpty(api.Token) && !ShowLogin(null))
            {
                SetStatus("Для проверки нужен вход в NormaCAD.PRO.");
                return;
            }
            string name = cfg.PendingFile != "" ? cfg.PendingFile : cfg.PendingJob;
            if (docName == "")
            {
                docName = name;
                UpdateDoc();
            }
            RunArgs a = new RunArgs();
            a.ResumeJob = cfg.PendingJob;
            a.Facts = facts;
            SupportLog.Write("продолжение проверки, задание " + cfg.PendingJob);
            ProgressView pv = new ProgressView(ui, facts);
            pv.Update(JobView.From(null));
            BeginRun(a, pv, Pane.Progress, "Продолжаю проверку «" + name + "»…");
            progressView = pv;
        }

        private void BeginRun(RunArgs a, Control v, Pane p, string status)
        {
            checkId = 0;
            lnkSite.Visible = false;
            shownFailure = null;
            lastRunSent = false;
            SetPane(p, v);
            if (p == Pane.Progress) progressView = (ProgressView)v;
            btnCheck.Text = "Проверить";
            SetStatus(status);
            SetBar(0);
            SetBusy(true);
            stop.Reset();
            wake.Reset();
            Thread t = new Thread(CheckThread);
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Name = "NormaCAD check";
            worker = t;
            t.Start(a);
        }

        /// <summary>Выполнить в потоке окна и дождаться (запись настроек); если окна уже нет — прямо здесь.</summary>
        private void OnUi(MethodInvoker action)
        {
            try
            {
                if (IsHandleCreated && !IsDisposed)
                {
                    Invoke(action);
                    return;
                }
            }
            catch (InvalidOperationException) { } // в том числе ObjectDisposedException
            action();
        }

        /// <summary>Показать в окне, не дожидаясь; окна уже нет — ничего не делать. Порядок вызовов сохраняется.</summary>
        private void ToUi(MethodInvoker action)
        {
            try
            {
                if (IsHandleCreated && !IsDisposed) BeginInvoke(action);
            }
            catch (InvalidOperationException) { }
        }

        private void CheckThread(object arg)
        {
            RunArgs a = (RunArgs)arg;
            Dictionary<string, object> report = null;
            Exception error = null;
            try
            {
                string jobId = a.ResumeJob;
                Dictionary<string, object> job = null;
                if (jobId == "")
                {
                    string file = a.File, name = a.Name;
                    LocalFacts sendFacts = a.Facts;
                    if (a.Export)
                    {
                        // Заново выгружаем активный документ вместе с несохранёнными правками
                        ExportResult r = ExportHook(a.Cookie);
                        file = r.Path;
                        name = r.UploadName;
                        sendFacts = r.Facts;
                        ToUi(delegate
                        {
                            ApplyExport(r);
                            ProgressView pv = new ProgressView(ui, r.Facts);
                            SetPane(Pane.Progress, pv);
                            progressView = pv;
                            SetStatus(JobView.Sending().StatusText(FactsText.Readable(r.Facts)));
                        });
                    }
                    job = api.SubmitJob(file, name, a.Domain, sendFacts);
                    jobId = J.Str(job, "job_id");
                    lastRunSent = true;
                    // Задание сохраняем сразу: если связь оборвётся или окно закроют, проверку можно будет продолжить
                    string pendingName = name;
                    OnUi(delegate
                    {
                        cfg.PendingJob = jobId;
                        cfg.PendingFile = pendingName;
                        cfg.PendingSince = DateTime.UtcNow;
                        cfg.Save();
                    });
                    SupportLog.Write("задание сохранено: " + jobId);
                }
                else
                {
                    lastRunSent = true;
                }
                report = Poll(jobId, job);
            }
            catch (Exception ex)
            {
                error = ex;
            }
            Dictionary<string, object> doneReport = report;
            Exception doneError = error;
            ToUi(delegate { Completed(doneReport, doneError); });
        }

        /// <summary>
        /// Опрос задания до результата. job — уже полученное состояние или null (тогда запрашиваем сразу).
        /// Возвращает отчёт; null — окно закрыто. При обрыве связи опрос идёт раз в 5 секунд до общего срока 30 минут,
        /// «Получить результат» опрашивает сразу; задание остаётся сохранённым. Забытое сервером задание (404) ищем в истории.
        /// </summary>
        private Dictionary<string, object> Poll(string jobId, Dictionary<string, object> job)
        {
            DateTime deadline = DateTime.UtcNow.AddMinutes(30);
            bool offline = false;
            ApiException lastOffline = null;
            WaitHandle[] handles = new WaitHandle[] { stop, wake };

            while (true)
            {
                if (stop.WaitOne(0)) return null;

                if (job == null)
                {
                    try
                    {
                        job = api.GetJob(jobId);
                        if (offline) SupportLog.Write("связь восстановилась");
                        offline = false;
                    }
                    catch (ApiException ex)
                    {
                        if (ex.Status == 404) return LostJob(jobId);
                        if (ex.Status != 0) throw;
                        // Обрыв связи — не повод бросать разбор: он идёт на сервере
                        lastOffline = ex;
                        if (!offline)
                        {
                            offline = true;
                            SupportLog.Write("нет связи при опросе задания " + jobId);
                            // Любой обрыв во время опроса — «Связь прервалась…», а не «Сервер не отвечает»
                            ApiException cut = new ApiException(0, ex.Message, WebExceptionStatusClosed);
                            ToUi(delegate { ShowFailure(cut, true, "опрос задания", false); });
                        }
                        if (DateTime.UtcNow > deadline) throw lastOffline;
                        WaitHandle.WaitAny(handles, 5000);
                        continue;
                    }
                }

                string status = J.Str(job, "status");
                if (status == "done") return J.Obj(job, "report");
                if (status == "error")
                {
                    // Разбор упал на сервере, связь при этом есть: не путать с обрывом сети
                    OnUi(delegate { ClearJob("разбор завершился ошибкой"); });
                    string error = J.Str(job, "error");
                    throw new UserException(error != "" ? error : "Проверка завершилась ошибкой.");
                }

                JobView v = JobView.From(job);
                ToUi(delegate { ShowJob(v); });

                if (DateTime.UtcNow > deadline)
                {
                    OnUi(delegate { ClearJob("сервер не ответил за 30 минут"); });
                    throw new UserException("Сервер не ответил за 30 минут. Отчёт появится в истории на сайте, если разбор завершится.");
                }

                if (stop.WaitOne(2000)) return null;
                job = null;
            }
        }

        private const System.Net.WebExceptionStatus WebExceptionStatusClosed = System.Net.WebExceptionStatus.ConnectionClosed;

        /// <summary>Сервер забыл задание (перезапуск): ищем готовый отчёт в истории по имени файла и времени.</summary>
        private Dictionary<string, object> LostJob(string jobId)
        {
            string name = cfg.PendingFile;
            SupportLog.Write("сервер не знает задание " + jobId + ", ищу в истории: " + name);
            Dictionary<string, object> found = api.FindInHistory(name, cfg.PendingSince);
            if (found != null) return found;
            OnUi(delegate { ClearJob("не нашлось на сервере"); });
            throw new UserException("Проверка «" + name + "» не нашлась на сервере: возможно, он перезапускался. Отправьте чертёж снова.");
        }

        /// <summary>Забыть сохранённое задание (в потоке окна) и записать причину в журнал.</summary>
        private void ClearJob(string why)
        {
            SupportLog.Write("задание " + cfg.PendingJob + " убрано: " + why);
            cfg.ClearPending();
        }

        /// <summary>Ответ о задании: состояние 4 или 5 (или возврат к ним после обрыва связи).</summary>
        private void ShowJob(JobView v)
        {
            if (!Busy) return;
            if (pane != Pane.Progress || progressView == null)
            {
                ProgressView pv = new ProgressView(ui, facts);
                SetPane(Pane.Progress, pv);
                progressView = pv;
                shownFailure = null;
            }
            progressView.Update(v);
            SetBar(v.Percent);
            SetStatus(v.StatusText(facts != null && FactsText.Readable(facts)));
        }

        private void Completed(Dictionary<string, object> report, Exception error)
        {
            Guard(delegate
            {
                worker = null;
                SetBusy(false);
                if (IsDisposed) return;
                if (report == null && error == null) return; // окно закрывали

                if (error != null)
                {
                    ApiException unauthorized = error as ApiException;
                    if (unauthorized != null && unauthorized.Status == 401)
                    {
                        HandleUnauthorized(error);
                        RefreshAccountAsync();
                        return;
                    }
                    ShowFailure(error, cfg.PendingJob != "", "проверка", false);
                }
                else
                {
                    if (cfg.PendingJob != "") ClearJob("отчёт получен");
                    ShowReport(report);
                }

                // Проверка меняет остаток лимита (или вскрывает истёкший срок) — обновляем строку аккаунта
                RefreshAccountAsync();
                EventHandler finished = Finished;
                if (finished != null) finished(this, EventArgs.Empty);
            });
        }

        /// <summary>Сессия истекла или закрыта на сайте: сразу окно входа (состояние 1), один раз; потом — повтор проверки.</summary>
        private void HandleUnauthorized(Exception error)
        {
            api.Token = "";
            cfg.Token = "";
            cfg.Save();
            SetAccountState();
            if (!reloginTried)
            {
                reloginTried = true;
                if (ShowLogin(LoginExpired))
                {
                    // Задание уже на сервере — продолжаем его, а не отправляем файл заново
                    if (cfg.PendingJob != "") ResumePending(); else StartCheck(false);
                    return;
                }
            }
            ShowFailure(error, cfg.PendingJob != "", "проверка", false);
        }

        // ------------------------------------------------------------------
        // Отчёт
        // ------------------------------------------------------------------

        private void ShowReport(Dictionary<string, object> r)
        {
            if (r == null)
            {
                ShowFailure(new UserException("Сервер вернул пустой отчёт."), true, "проверка", false);
                return;
            }
            List<Card> cards = Cards.Build(r, facts);
            string serverVerdict = J.Str(r, "verdict");
            bool refusal = Cards.IsRefusal(serverVerdict, J.Arr(r, "violations").Length);
            string verdict = Cards.Verdict(serverVerdict, cards, refusal);
            List<string> notes = Cards.Notes(r, exportWarning);
            // Отказ — не оценка: пояснение сервера (почему документ не проверен) остаётся серой строкой под итогом
            if (refusal && J.Str(r, "summary").Trim() != "") notes.Add(J.Str(r, "summary").Trim());

            ReportView rv = new ReportView(ui, cards, verdict, notes);
            SetPane(Pane.Report, rv);
            reportView = rv;
            checkId = J.Int(r, "check_id");
            lnkSite.Visible = checkId > 0;
            SetStatus(JobView.Done().StatusText(false));
            SetBar(100);
            btnCheck.Text = "Проверить снова";
        }

        // ------------------------------------------------------------------
        // Сбои и кнопка действия
        // ------------------------------------------------------------------

        /// <summary>
        /// Сбой в области результата: фраза, одна кнопка действия (если есть) и ссылка «Журнал для поддержки».
        /// Обрыв во время опроса — состояние 19: «Нет связи с сервером.», полоса остаётся, «Проверить» недоступна.
        /// </summary>
        private void ShowFailure(Exception ex, bool duringCheck, string where, bool fromAccount)
        {
            Failure f = Failure.Classify(ex, duringCheck, api.BaseUrl);
            f.Log(where);
            bool interrupted = f.Kind == "interrupted";
            EventHandler action = null;
            if (f.Action != FailureAction.None)
                action = delegate { Guard(delegate { ActionClicked(f, fromAccount); }); };
            SetPane(Pane.Failure, new FailureView(ui, f.Text, f.ActionLabel, action, delegate { Guard(OpenLogFolder); }));
            shownFailure = f;
            failureFromAccount = fromAccount;
            checkId = 0;
            lnkSite.Visible = false;
            btnCheck.Text = "Проверить";
            if (interrupted)
            {
                SetStatus("Нет связи с сервером.");
            }
            else
            {
                SetStatus(fromAccount ? "Не удалось проверить вход."
                    : lastRunSent ? "Проверка не завершена." : "Проверка не отправлена.");
                SetBar(0);
            }
            SetBusy(Busy);
        }

        private void ActionClicked(Failure f, bool fromAccount)
        {
            SupportLog.Write("кнопка «" + f.ActionLabel + "» (" + f.Kind + ")");
            switch (f.Action)
            {
                case FailureAction.Retry:
                    Retry(fromAccount);
                    break;
                case FailureAction.ProxySettings:
                    if (ShowProxy()) Retry(fromAccount);
                    break;
                case FailureAction.OpenSite:
                case FailureAction.Download:
                case FailureAction.ContactTeam:
                    Process.Start(f.Url != "" ? f.Url : api.BaseUrl);
                    break;
                case FailureAction.Login:
                    if (ShowLogin(null)) Retry(fromAccount);
                    break;
                case FailureAction.ChangeServer:
                    // Адрес сервера меняется в том же окне входа
                    if (ShowLogin(null)) ShowHint();
                    break;
                case FailureAction.GetResult:
                    // Опрос ещё идёт — проверяем сразу, не дожидаясь очередных 5 секунд; закончился — продолжаем задание
                    if (Busy) wake.Set(); else ResumePending();
                    break;
            }
        }

        /// <summary>Повторить то, что не удалось: проверку (продолжить сохранённую или отправить заново) либо запрос аккаунта.</summary>
        private void Retry(bool fromAccount)
        {
            shownFailure = null;
            if (fromAccount)
            {
                ShowHint();
                RefreshAccountAsync();
                return;
            }
            reloginTried = false;
            if (cfg.PendingJob != "") ResumePending(); else StartCheck(true);
        }

        /// <summary>«Журнал для поддержки»: папка журнала с выделенным файлом.</summary>
        private void OpenLogFolder()
        {
            string path = SupportLog.Path;
            if (File.Exists(path))
            {
                Process.Start("explorer.exe", "/select,\"" + path + "\"");
                return;
            }
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                Process.Start("explorer.exe", "\"" + dir + "\"");
            else
                SetStatus("Журнал пока пуст.");
        }

        private bool ShowProxy()
        {
            using (ProxyForm dlg = new ProxyForm(cfg))
            {
                dlg.TopMost = TopMost;
                bool ok = dlg.ShowDialog(this) == DialogResult.OK;
                if (ok) SupportLog.Write("настройки прокси изменены");
                return ok;
            }
        }

        // ------------------------------------------------------------------
        // Аккаунт
        // ------------------------------------------------------------------

        private void SetAccountText(string head, Color headColor, IList<string> meta, Color metaColor, string link)
        {
            lblEmail.Text = head ?? "";
            lblEmail.ForeColor = headColor;
            lnkAccount.Text = link;
            metaFlow.SuspendLayout();
            foreach (Control c in metaFlow.Controls) c.Dispose();
            metaFlow.Controls.Clear();
            if (meta != null)
                foreach (string part in meta)
                {
                    Label l = ui.MakeLabel(part, metaColor, null);
                    l.Margin = new Padding(0, 0, ui.S(16), 0);
                    metaFlow.Controls.Add(l);
                }
            metaFlow.ResumeLayout(true);
        }

        /// <summary>Вход есть — пока грузится «Проверяю вход…»; входа нет — «Войти».</summary>
        private void SetAccountState()
        {
            if (string.IsNullOrEmpty(api.Token))
                SetAccountText("Вход не выполнен", Ui.Gray, null, Ui.Gray, "Войти");
            else
                SetAccountText("Проверяю вход…", Ui.Gray, null, Ui.Gray, "Выйти");
        }

        /// <summary>
        /// Строка аккаунта: кто вошёл и что ему доступно. Запрос к серверу идёт в фоне,
        /// окно не замирает; ответ от устаревшего входа (успели выйти или войти заново) отбрасывается.
        /// </summary>
        private void RefreshAccountAsync()
        {
            string token = api.Token;
            accountLoaded = false;
            SetAccountState();
            if (string.IsNullOrEmpty(token))
            {
                accountLoaded = true;
                return;
            }

            ThreadPool.QueueUserWorkItem(delegate
            {
                AccountInfo info = null;
                Exception error = null;
                try { info = api.GetAccount(); }
                catch (Exception ex) { error = ex; }

                ToUi(delegate
                {
                    if (IsDisposed || api.Token != token) return;
                    Guard(delegate { ApplyAccount(info, error); });
                });
            });
        }

        private void ApplyAccount(AccountInfo info, Exception error)
        {
            accountLoaded = true;
            if (error == null)
            {
                string head = info.Email != "" ? info.Email : info.FullName != "" ? info.FullName : cfg.Email;
                SetAccountText(head, Ui.Text, AccountText.Meta(info), Ui.Gray, "Выйти");
                // Связь с аккаунтом вернулась — сбой, показанный из-за неё, больше не актуален
                if (shownFailure != null && failureFromAccount && !Busy) ShowHint();
                return;
            }

            ApiException apiError = error as ApiException;
            if (apiError != null && apiError.Status == 401)
            {
                api.Token = "";
                cfg.Token = "";
                cfg.Save();
                SetAccountState();
                // Вход, проверенный при запуске, больше не действует: сразу окно входа (состояние 1)
                if (!Busy && !loginOpen)
                    BeginInvoke(new MethodInvoker(delegate { Guard(delegate { ShowLogin(LoginExpired); }); }));
                return;
            }

            Failure f = Failure.Classify(error, false, api.BaseUrl);
            string why = apiError != null && apiError.Status == 0
                ? "Не удалось проверить вход: нет связи с сервером" : f.Text;
            SetAccountText(cfg.Email != "" ? cfg.Email : "Вход не проверен", Ui.Text,
                new string[] { why }, Ui.Warning, "Выйти");

            // Область результата занимаем только когда она свободна: готовый отчёт и идущую проверку не затираем
            if (!Busy && pane == Pane.Hint && shownFailure == null)
                ShowFailure(error, false, "строка аккаунта", true);
        }

        private void AccountClicked()
        {
            if (string.IsNullOrEmpty(api.Token))
            {
                ShowLogin(null);
                return;
            }
            if (Busy) return;
            bool closed = api.Logout();
            cfg.Token = "";
            cfg.Save();
            SetAccountState();
            SupportLog.Write("выход из аккаунта из окна: " + (closed ? "вход на сервере закрыт" : "сервер недоступен"));
            if (closed)
                SetStatus("Вы вышли из аккаунта. Вход на сервере закрыт.");
            else
                SetStatus("Вы вышли на этом компьютере, но сервер недоступен: вход закроется сам через 30 дней, "
                    + "его можно закрыть на сайте в списке открытых сессий.");
        }

        private bool loginOpen;

        /// <summary>Окно входа. message — серая строка («Вход больше не действует. Войдите снова.») или null.</summary>
        private bool ShowLogin(string message)
        {
            if (loginOpen) return false;
            loginOpen = true;
            try
            {
                using (LoginForm dlg = new LoginForm(cfg, api, message))
                {
                    dlg.TopMost = TopMost;
                    bool ok = dlg.ShowDialog(this) == DialogResult.OK;
                    RefreshAccountAsync();
                    return ok;
                }
            }
            finally
            {
                loginOpen = false;
            }
        }

        /// <summary>Обработчики окна живут в процессе КОМПАС: исключение наружу не выпускаем.</summary>
        private void Guard(MethodInvoker action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                try
                {
                    SupportLog.Write("ошибка окна: " + ex.GetType().Name + ": " + ex.Message);
                    worker = null;
                    SetBusy(false);
                    ShowFailure(ex, false, "окно", false);
                }
                catch (Exception) { }
            }
        }
    }
}
