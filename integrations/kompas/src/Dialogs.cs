// Окна входа и настроек прокси — по макету (login(), proxyDlg()): подписи с двоеточием, поля на всю ширину,
// строка сообщения, ссылки слева и справа, кнопки справа внизу. Только TableLayoutPanel и FlowLayoutPanel.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace NormaCad.Kompas
{
    /// <summary>Общая основа диалога: фиксированное окно только с кнопкой закрытия и колонка с полями.</summary>
    internal abstract class DialogBase : Form
    {
        protected readonly Ui ui = new Ui();
        protected TableLayoutPanel column;

        protected void Setup(string title, int clientWidth, int clientHeight)
        {
            Text = title;
            HandleCreated += delegate { Ui.ThemeCaption(this); };
            Font = ui.Font;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowIcon = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = ui.S(clientWidth, clientHeight);

            column = new TableLayoutPanel();
            column.Dock = DockStyle.Fill;
            column.ColumnCount = 1;
            column.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            column.Padding = ui.P(16, 14, 16, 14);
            column.GrowStyle = TableLayoutPanelGrowStyle.AddRows;
            column.BackColor = Ui.Background;
            column.ForeColor = Ui.Text;
            Controls.Add(column);
        }

        /// <summary>Подпись и поле: подпись на 3 px выше поля, перед следующей подписью — 8 px.</summary>
        protected TextBox AddField(string caption, string value, bool first)
        {
            Label l = ui.MakeLabel(caption);
            l.Margin = new Padding(0, first ? 0 : ui.S(8), 0, ui.S(3));
            Ui.AddRow(column, l);
            TextBox box = new TextBox();
            box.BackColor = Ui.Surface;
            box.ForeColor = Ui.Text;
            box.BorderStyle = BorderStyle.FixedSingle;
            box.Text = value ?? "";
            box.Margin = Padding.Empty;
            Ui.AddRow(column, box);
            return box;
        }

        /// <summary>Строка кнопок справа внизу: растяжка забирает остаток высоты, кнопки — в последней строке.</summary>
        protected void AddButtons(Button accept, Button cancel)
        {
            column.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            column.RowCount = column.RowStyles.Count;
            FlowLayoutPanel row = new FlowLayoutPanel();
            row.FlowDirection = FlowDirection.RightToLeft;
            row.AutoSize = true;
            row.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            row.WrapContents = false;
            row.Margin = Padding.Empty;
            row.Padding = Padding.Empty;
            cancel.Margin = Padding.Empty;
            accept.Margin = new Padding(0, 0, ui.S(8), 0);
            row.Controls.Add(cancel);
            ui.MakePrimary(accept);
            row.Controls.Add(accept);
            Ui.AddRow(column, row);
            AcceptButton = accept;
            CancelButton = cancel;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) ui.Dispose();
            base.Dispose(disposing);
        }
    }

    internal class LoginForm : DialogBase
    {
        private readonly Settings cfg;
        private readonly ApiClient api;
        private readonly TextBox tbServer, tbEmail, tbPassword;
        private readonly Label lblMessage;
        private readonly Button btnOk;

        /// <summary>message — серая строка над кнопками («Вход больше не действует. Войдите снова.»); может быть пустой.</summary>
        public LoginForm(Settings cfg, ApiClient api, string message)
        {
            this.cfg = cfg;
            this.api = api;
            Setup("NormaCAD.PRO — вход", 360, 300);

            tbEmail = AddField("Электронная почта:", cfg.Email, true);
            tbPassword = AddField("Пароль:", "", false);
            tbPassword.UseSystemPasswordChar = true;
            tbServer = AddField("Сервер:", cfg.Server, false);

            // Две строки сообщения: Неверный email или пароль… не помещается в одну
            lblMessage = ui.MakeLabel(message ?? "", Ui.Gray, null);
            lblMessage.MinimumSize = new Size(0, ui.S(32));
            lblMessage.Margin = new Padding(0, ui.S(6), 0, 0);
            Ui.AddRow(column, lblMessage);

            // «Забыли пароль?» слева, «Доступ по запросу» справа
            TableLayoutPanel links = new TableLayoutPanel();
            links.ColumnCount = 2;
            links.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            links.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            links.RowCount = 1;
            links.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            links.AutoSize = true;
            links.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            links.Margin = new Padding(0, ui.S(6), 0, 0);
            LinkLabel forgot = ui.MakeLink("Забыли пароль?");
            forgot.Anchor = AnchorStyles.Left;
            forgot.LinkClicked += delegate
            {
                try { Process.Start(ApiClient.ForgotPasswordUrl(tbServer.Text)); }
                catch (UserException ex) { ShowMessage(ex.Message, true); }
                catch (Exception) { }
            };
            // Свободной регистрации нет: страница /register объясняет, как получить доступ
            LinkLabel register = ui.MakeLink("Доступ по запросу");
            register.Anchor = AnchorStyles.Right;
            register.LinkClicked += delegate
            {
                try { Process.Start(ApiClient.NormalizeServer(tbServer.Text) + "/register"); }
                catch (Exception) { }
            };
            links.Controls.Add(forgot, 0, 0);
            links.Controls.Add(register, 1, 0);
            Ui.AddRow(column, links);

            btnOk = ui.MakeButton("Войти");
            btnOk.Click += delegate { SignIn(); };
            Button cancel = ui.MakeButton("Отмена");
            cancel.DialogResult = DialogResult.Cancel;
            AddButtons(btnOk, cancel);

            Shown += delegate { if (tbEmail.Text != "") tbPassword.Focus(); };
        }

        /// <summary>Для снимков окна (test\WindowShots.cs): пароль и красная строка отказа, как в макете.</summary>
        internal void FillForPreview(string password, string error)
        {
            tbPassword.Text = password;
            ShowMessage(error, true);
        }

        private void ShowMessage(string text, bool error)
        {
            lblMessage.ForeColor = error ? Ui.Critical : Ui.Gray;
            lblMessage.Text = text ?? "";
        }

        private void SignIn()
        {
            ShowMessage("", false);
            string email = tbEmail.Text.Trim();
            if (email == "" || tbPassword.Text == "")
            {
                ShowMessage("Укажите email и пароль.", true);
                return;
            }

            btnOk.Enabled = false;
            Cursor = Cursors.WaitCursor;
            try
            {
                api.BaseUrl = ApiClient.NormalizeServer(tbServer.Text);
                api.Login(email, tbPassword.Text);
                cfg.Server = api.BaseUrl;
                cfg.Email = email;
                cfg.Token = api.Token;
                cfg.Save();
                DialogResult = DialogResult.OK;
            }
            catch (Exception ex)
            {
                // Адрес сервера мог смениться неудачно — возвращаем рабочий
                api.BaseUrl = cfg.Server;
                // Сеть, прокси и устаревшая версия — те же понятные тексты, что и в окне проверки
                ApiException apiEx = ex as ApiException;
                if (apiEx != null && (apiEx.Status == 0 || apiEx.Status == 407 || apiEx.Status == 426))
                {
                    Failure f = Failure.Classify(ex, false, api.BaseUrl);
                    f.Log("вход");
                    ShowMessage(f.Text, true);
                }
                else
                    ShowMessage(ApiClient.LoginError(ex), true);
            }
            finally
            {
                Cursor = Cursors.Default;
                btnOk.Enabled = true;
            }
        }
    }

    /// <summary>Прокси для обмена с сайтом: пусто — системный прокси Windows.</summary>
    internal class ProxyForm : DialogBase
    {
        private readonly Settings cfg;
        private readonly TextBox tbAddress, tbUser, tbPassword;

        public ProxyForm(Settings cfg)
        {
            this.cfg = cfg;
            Setup("Настройки прокси", 360, 246);

            tbAddress = AddField("Адрес прокси (пусто — системный):", cfg.ProxyAddress, true);
            tbUser = AddField("Логин:", cfg.ProxyUser, false);
            tbPassword = AddField("Пароль:", cfg.ProxyPassword, false);
            tbPassword.UseSystemPasswordChar = true;

            LinkLabel windows = ui.MakeLink("Настройки прокси Windows");
            windows.Margin = new Padding(0, ui.S(10), 0, 0);
            windows.Anchor = AnchorStyles.Left;
            windows.LinkClicked += delegate
            {
                try { Process.Start("ms-settings:network-proxy"); }
                catch (Exception) { }
            };
            Ui.AddRow(column, windows);
            windows.Dock = DockStyle.None;

            Button ok = ui.MakeButton("OK");
            ok.Click += delegate { Apply(); };
            Button cancel = ui.MakeButton("Отмена");
            cancel.DialogResult = DialogResult.Cancel;
            AddButtons(ok, cancel);
        }

        private void Apply()
        {
            cfg.ProxyAddress = tbAddress.Text.Trim();
            cfg.ProxyUser = tbUser.Text.Trim();
            cfg.ProxyPassword = tbPassword.Text;
            cfg.Save();
            ProxySetup.Apply(cfg);
            DialogResult = DialogResult.OK;
        }
    }
}
