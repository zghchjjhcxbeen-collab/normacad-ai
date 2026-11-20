// Снимки окна для сверки с макетом (mockup\*.png): настоящее окно CheckForm на подставном сервере и подставном
// экспорте КОМПАС. Запуск — shots.cmd. Живой КОМПАС и настоящий сервер не нужны, настройки и журнал временные.
//   WindowShots.exe <папка> <метка> [состояния]
//   метка — подпись набора (100 или 150), состояния: account local progress report no-internet limit dialogs
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace NormaCad.Kompas.Tests
{
    public static class WindowShots
    {
        private const int Port = 18047;
        private static readonly string Base = "http://127.0.0.1:" + Port;

        // Сценарий подставного сервера: что отвечать на отправку и опрос
        private static volatile string scenario = "queued";
        private static int remaining = 7;

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Rect rect, int size);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left, Top, Right, Bottom;
        }

        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("WindowShots.exe <папка> <метка> [account local progress report no-internet limit dialogs]");
                return 2;
            }
            string outDir = Path.Combine(args[0], args[1]);
            Directory.CreateDirectory(outDir);
            List<string> wanted = new List<string>();
            for (int i = 2; i < args.Length; i++) wanted.Add(args[i]);
            if (wanted.Count == 0)
                wanted.AddRange(new string[] { "account", "local", "progress", "report", "no-internet", "limit", "dialogs" });

            string work = Path.Combine(Path.GetTempPath(), "normacad-kompas-shots-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            Environment.SetEnvironmentVariable("NORMACAD_KOMPAS_CFG", Path.Combine(work, "kompas.cfg"));
            Environment.SetEnvironmentVariable("NORMACAD_KOMPAS_LOG", Path.Combine(work, "kompas.log"));

            Dpi.EnterThread();
            Application.EnableVisualStyles();

            HttpListener listener = StartServer();
            if (listener == null) return 1;
            int failed = 0;
            try
            {
                foreach (string name in wanted)
                {
                    try
                    {
                        if (name == "dialogs") Dialogs(outDir);
                        else if (!Scenario(name, outDir, work)) failed++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        Console.WriteLine("FAIL " + name + ": " + ex);
                    }
                }
            }
            finally
            {
                listener.Stop();
            }
            return failed == 0 ? 0 : 1;
        }

        // ------------------------------------------------------------------
        // Данные макета
        // ------------------------------------------------------------------

        private static LocalFacts MockupFacts()
        {
            LocalFacts f = new LocalFacts();
            f.DocType = "drawing";
            f.DocTypeLabel = "Чертёж детали";
            f.Designation = "АБВГ.301122.005";
            f.Name = "Корпус";
            LocalSheet s1 = new LocalSheet(); s1.Number = 1; s1.Format = "А3";
            LocalSheet s2 = new LocalSheet(); s2.Number = 2; s2.Format = "А4";
            f.Sheets.Add(s1);
            f.Sheets.Add(s2);
            f.Remarks.Add(Remark(1, "Не заполнена графа «Масса».", "ГОСТ 2.104-2006, графа 5",
                "Масса нужна для расчётов и снабжения: без неё нельзя заказать материал.",
                "укажите массу детали в килограммах, без единицы измерения."));
            f.Remarks.Add(Remark(1, "В графе «Листов» указано 1, а листов в документе 2.", "ГОСТ 2.104-2006, графа 8",
                "Число листов в основной надписи должно совпадать с числом листов документа.", "укажите 2."));
            f.Remarks.Add(Remark(2, "Обозначение отличается от листа 1.", "ГОСТ 2.104-2006, графа 2", "",
                "укажите АБВГ.301122.005, как на листе 1."));
            return f;
        }

        private static LocalRemark Remark(int page, string title, string standard, string detail, string fix)
        {
            LocalRemark r = new LocalRemark();
            r.Page = page;
            r.Title = title;
            r.Standard = standard;
            r.Detail = detail;
            r.Fixes.Add(fix);
            return r;
        }

        private static ExportResult FakeExport(string work)
        {
            ExportResult r = new ExportResult();
            r.Path = Path.Combine(work, "Корпус.pdf");
            File.WriteAllBytes(r.Path, new byte[] { 0x25, 0x50, 0x44, 0x46 });
            r.UploadName = "Корпус.pdf";
            r.DocumentName = "Корпус.cdw";
            r.Facts = MockupFacts();
            return r;
        }

        private const string StageKeys = "receive,normalize,standards,vision,verify";

        private static string Stages(string receive, string normalize, string standards, string vision, string verify, string visionEta)
        {
            return "[{\"key\":\"receive\",\"label\":\"Приём файла\",\"state\":\"" + receive + "\",\"eta_seconds\":null},"
                + "{\"key\":\"normalize\",\"label\":\"Нормализация формата\",\"state\":\"" + normalize + "\",\"eta_seconds\":null},"
                + "{\"key\":\"standards\",\"label\":\"Поиск по базе ГОСТ\",\"state\":\"" + standards + "\",\"eta_seconds\":null},"
                + "{\"key\":\"vision\",\"label\":\"Vision-анализ\",\"state\":\"" + vision + "\",\"eta_seconds\":" + visionEta + "},"
                + "{\"key\":\"verify\",\"label\":\"Проверка достоверности\",\"state\":\"" + verify + "\",\"eta_seconds\":40}]";
        }

        // Состояние 4 макета: перед вами 1 проверка, серверы нагружены
        private static string QueuedJob()
        {
            return "{\"job_id\":\"j-47\",\"status\":\"queued\",\"stage\":\"normalize\",\"progress\":10,\"queue_ahead\":1,"
                + "\"queue_total\":2,\"eta_seconds\":180,\"heavy_load\":true,\"pages_total\":0,\"pages_done\":0,\"stages\":"
                + Stages("done", "pending", "pending", "pending", "pending", "90") + "}";
        }

        // Состояние 5 макета: анализ страницы 1 из 2, осталось около 2 минут
        private static string RunningJob()
        {
            return "{\"job_id\":\"j-47\",\"status\":\"running\",\"stage\":\"vision\",\"progress\":60,\"queue_ahead\":0,\"eta_seconds\":120,"
                + "\"heavy_load\":false,\"pages_total\":2,\"pages_done\":0,\"stages\":"
                + Stages("done", "done", "done", "running", "pending", "60") + "}";
        }

        // Состояние 6 макета: один критический, один лист-замечание сервера, одно «в норме»; три замечания ПК добавляет окно
        private static string DoneJob()
        {
            return "{\"job_id\":\"j-47\",\"status\":\"done\",\"progress\":100,\"report\":{\"verdict\":\"Требует доработки: критические нарушения\","
                + "\"score\":60,\"source\":\"ai-core\",\"pages_total\":2,\"pages_checked\":2,\"check_id\":42,"
                + "\"stats\":{\"critical\":1,\"warnings\":1,\"ok\":1},\"violations\":["
                + "{\"n\":1,\"severity\":\"ok\",\"title\":\"Формат и рамка листа соответствуют требованиям.\",\"standard\":\"ГОСТ 2.301-68\",\"detail\":\"\",\"fixes\":[],\"page\":1},"
                + "{\"n\":2,\"severity\":\"warning\",\"title\":\"Технические требования размещены не над основной надписью.\",\"standard\":\"ГОСТ 2.316-2008\",\"detail\":\"\","
                + "\"fixes\":[\"перенесите текст над основной надписью;\",\"при нехватке места расположите его колонкой левее основной надписи.\"],\"page\":2},"
                + "{\"n\":3,\"severity\":\"critical\",\"title\":\"Не проставлен размер, определяющий положение отверстия Ø8.\",\"standard\":\"ГОСТ 2.307-2011\","
                + "\"detail\":\"Положение отверстия на главном виде не определено: деталь нельзя изготовить по чертежу.\","
                + "\"fixes\":[\"проставьте размер от базовой поверхности до оси отверстия;\",\"задайте координаты оси отверстия от двух баз.\"],\"page\":1}]}}";
        }

        private const string LimitText = "Вы достигли лимита тестовых проверок: 10 за 5 ч. Следующая проверка будет доступна через 2 ч 15 мин. "
            + "Чтобы продолжить сотрудничество, свяжитесь с командой проекта.";

        // ------------------------------------------------------------------
        // Подставной сервер
        // ------------------------------------------------------------------

        private static HttpListener StartServer()
        {
            HttpListener listener = new HttpListener();
            listener.Prefixes.Add(Base + "/");
            try { listener.Start(); }
            catch (Exception ex)
            {
                Console.WriteLine("FAIL сервер не запустился на порту " + Port + ": " + ex.Message);
                return null;
            }
            Thread t = new Thread(delegate()
            {
                while (listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = listener.GetContext(); }
                    catch (Exception) { return; }
                    try { Answer(ctx); }
                    catch (Exception) { }
                }
            });
            t.IsBackground = true;
            t.Start();
            return listener;
        }

        private static void Answer(HttpListenerContext ctx)
        {
            string path = ctx.Request.Url.AbsolutePath;
            string method = ctx.Request.HttpMethod;
            if (method == "POST") new StreamReader(ctx.Request.InputStream).ReadToEnd();
            int status = 200;
            string body = "{}";
            if (path == "/api/auth/me")
                body = "{\"email\":\"inzhener@example.ru\",\"full_name\":\"Инженер\"}";
            else if (path == "/api/users/me/access")
                body = "{\"plan\":\"trial\",\"plan_label\":\"Тестовый доступ\",\"limited\":true,"
                    + "\"access_until\":\"2026-11-18T12:00:00+00:00\",\"expired\":false,\"checks_limit\":10,"
                    + "\"window_hours\":5,\"used\":" + (10 - remaining) + ",\"remaining\":" + remaining
                    + ",\"can_check\":" + (remaining > 0 ? "true" : "false") + "}";
            else if (path == "/api/drawings/jobs" && method == "POST")
            {
                if (scenario == "abort")
                {
                    ctx.Response.Abort();
                    return;
                }
                if (scenario == "limit")
                {
                    remaining = 0;
                    status = 429;
                    body = "{\"detail\":\"" + LimitText + "\",\"code\":\"checks_limit\"}";
                }
                else body = JobFor(scenario);
            }
            else if (path.StartsWith("/api/drawings/jobs/"))
                body = JobFor(scenario);

            byte[] data = Encoding.UTF8.GetBytes(body);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.OutputStream.Write(data, 0, data.Length);
            ctx.Response.Close();
        }

        private static string JobFor(string name)
        {
            if (name == "running") return RunningJob();
            if (name == "report") return DoneJob();
            return QueuedJob();
        }

        // ------------------------------------------------------------------
        // Сценарии окна
        // ------------------------------------------------------------------

        private static bool Scenario(string name, string outDir, string work)
        {
            string file;
            string wait;      // что должно появиться в окне до снимка
            bool click = true;
            switch (name)
            {
                case "account": file = "03-account"; scenario = "queued"; wait = "hint"; click = false; break;
                case "local": file = "04-local"; scenario = "queued"; wait = "queue"; break;
                case "progress": file = "05-progress"; scenario = "running"; wait = "running"; break;
                case "report": file = "06-report"; scenario = "report"; wait = "report"; break;
                case "report-dark": file = "06-report-dark"; scenario = "report"; wait = "report"; break;
                case "no-internet": file = "07-no-internet"; scenario = "abort"; wait = "failure"; break;
                case "limit": file = "17-limit"; scenario = "limit"; wait = "failure"; break;
                default: throw new ArgumentException("неизвестное состояние: " + name);
            }
            remaining = 7;
            Failure.NetworkAvailable = delegate { return name != "no-internet"; };

            // Каждый снимок — с чистыми настройками: вход есть, окно «Поверх окон» снято, сохранённого задания нет
            Settings cfg = new Settings();
            cfg.Server = Base;
            cfg.Email = "inzhener@example.ru";
            cfg.Token = "shots-token";
            cfg.TopMost = false;
            cfg.Save();

            string target = Path.Combine(outDir, file + ".png");
            bool saved = false;
            CheckForm form = new CheckForm();
            form.ExportHook = delegate(int ck) { return FakeExport(work); };
            form.Shown += delegate { form.Begin(FakeExport(work)); };

            System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
            timer.Interval = 100;
            DateTime start = DateTime.UtcNow;
            DateTime reached = DateTime.MaxValue;
            bool clicked = false;
            timer.Tick += delegate
            {
                if (DateTime.UtcNow - start > TimeSpan.FromSeconds(40))
                {
                    timer.Stop();
                    Console.WriteLine("FAIL " + name + ": не дождались состояния, окно: " + form.CurrentPane + ", статус «" + form.StatusLine + "»");
                    form.Close();
                    return;
                }
                if (!form.AccountLoaded) return;
                if (click && !clicked)
                {
                    clicked = true;
                    form.ClickCheck();
                    return;
                }
                if (reached == DateTime.MaxValue)
                {
                    if (!Reached(form, wait)) return;
                    reached = DateTime.UtcNow;
                    if (wait == "report")
                    {
                        ReportView report = form.CurrentReport;
                        report.ExpandDetail(0);
                        if (name == "report-dark")
                        {
                            Button theme = (Button)form.Controls.Find("themeSwitch", true)[0];
                            theme.PerformClick();
                            if (!Ui.IsDark || !Settings.Load().DarkTheme || form.CurrentReport != report)
                            {
                                timer.Stop();
                                Console.WriteLine("FAIL смена темы открытого отчёта");
                                form.Close();
                            }
                        }
                    }
                    return;
                }
                // Даём окну дорисоваться (полоса, раскладка)
                if (DateTime.UtcNow - reached < TimeSpan.FromMilliseconds(900)) return;
                timer.Stop();
                if (Environment.GetEnvironmentVariable("NORMACAD_SHOTS_DUMP") == "1") Dump(form, 0);
                Capture(form, target);
                saved = true;
                Console.WriteLine("ok   " + target);
                form.Close();
            };
            form.Shown += delegate { timer.Start(); };
            Application.Run(form);
            timer.Dispose();
            return saved;
        }

        // Дерево контролов с размерами: помогает понять раскладку (NORMACAD_SHOTS_DUMP=1)
        private static void Dump(Control c, int depth)
        {
            string text = c is Label || c is Button || c is LinkLabel ? " <" + c.Text.Replace("\n", " ") + ">" : "";
            if (text.Length > 50) text = text.Substring(0, 50) + "…";
            Console.WriteLine(new string(' ', depth * 2) + c.GetType().Name + " " + c.Bounds + (c.Visible ? "" : " hidden") + text);
            foreach (Control child in c.Controls) Dump(child, depth + 1);
        }

        private static bool Reached(CheckForm form, string wait)
        {
            switch (wait)
            {
                case "hint":
                    return form.CurrentPane == CheckForm.Pane.Hint && !form.Busy;
                case "queue":
                    return form.CurrentPane == CheckForm.Pane.Progress
                        && form.StatusLine == "Основная надпись проверена на ПК. Чертёж отправлен на сервер.";
                case "running":
                    return form.CurrentPane == CheckForm.Pane.Progress && form.StatusLine == "Идёт проверка на сервере.";
                case "report":
                    return form.CurrentPane == CheckForm.Pane.Report && !form.Busy && form.AccountLoaded;
                default:
                    return form.CurrentPane == CheckForm.Pane.Failure && !form.Busy && form.AccountLoaded;
            }
        }

        // Окна входа и настроек прокси — как login(), login-error и proxyDlg() в макете
        private static void Dialogs(string outDir)
        {
            Settings cfg = new Settings();
            cfg.Email = "inzhener@example.ru";
            cfg.Server = "https://normacad.pro";
            ApiClient api = new ApiClient();
            ShootDialog(new LoginForm(cfg, api, "Вход больше не действует. Войдите снова."), Path.Combine(outDir, "01-login.png"));
            LoginForm err = new LoginForm(cfg, api, "");
            err.FillForPreview("12345678", ApiClient.LoginError(new ApiException(401, "x")));
            ShootDialog(err, Path.Combine(outDir, "02-login-error.png"));
            ShootDialog(new ProxyForm(new Settings()), Path.Combine(outDir, "15-proxy-settings.png"));
        }

        private static void ShootDialog(Form dlg, string target)
        {
            dlg.StartPosition = FormStartPosition.CenterScreen;
            System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
            timer.Interval = 800;
            timer.Tick += delegate
            {
                timer.Stop();
                Capture(dlg, target);
                Console.WriteLine("ok   " + target);
                dlg.Close();
            };
            dlg.Shown += delegate { timer.Start(); };
            Application.Run(dlg);
            timer.Dispose();
            dlg.Dispose();
        }

        // ------------------------------------------------------------------
        // Снимок окна: PrintWindow с PW_RENDERFULLCONTENT, обрезка по видимой рамке окна
        // ------------------------------------------------------------------

        // PrintWindow оставляет невидимые поля изменения размера чёрными: обрезаем их по средней строке и столбцу
        private static Rectangle VisibleBounds(Bitmap bmp)
        {
            int left = 0, right = bmp.Width - 1, bottom = bmp.Height - 1;
            int midY = bmp.Height / 2, midX = bmp.Width / 2;
            while (left < midX && IsBlack(bmp.GetPixel(left, midY))) left++;
            while (right > midX && IsBlack(bmp.GetPixel(right, midY))) right--;
            while (bottom > midY && IsBlack(bmp.GetPixel(midX, bottom))) bottom--;
            return Rectangle.FromLTRB(left, 0, right + 1, bottom + 1);
        }

        private static bool IsBlack(Color c)
        {
            return c.R == 0 && c.G == 0 && c.B == 0;
        }

        private static void Capture(Form form, string target)
        {
            IntPtr h = form.Handle;
            Rect outer;
            GetWindowRect(h, out outer);
            int w = outer.Right - outer.Left, ht = outer.Bottom - outer.Top;
            using (Bitmap full = new Bitmap(w, ht))
            {
                using (Graphics g = Graphics.FromImage(full))
                {
                    g.Clear(Color.White);
                    IntPtr hdc = g.GetHdc();
                    try { PrintWindow(h, hdc, 2); }
                    finally { g.ReleaseHdc(hdc); }
                }
                Rectangle crop = VisibleBounds(full);
                using (Bitmap cut = full.Clone(crop, full.PixelFormat))
                    cut.Save(target, ImageFormat.Png);
            }
        }
    }
}
