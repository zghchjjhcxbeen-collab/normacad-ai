// NormaCadCheck.exe — то же окно проверки без КОМПАС: для готового PDF и для отладки.
//   NormaCadCheck.exe [файл]
//     без файла: если есть незавершённая проверка, окно откроется сразу и продолжит её;
//     иначе спросит файл.
//   NormaCadCheck.exe --selftest <сервер> <email> <файл-с-паролем> <чертёж> [eskd|spds] [logout]
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace NormaCad.Kompas
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--selftest") return SelfTest(args);

            Dpi.EnterThread();
            Application.EnableVisualStyles();
            string file = args.Length > 0 ? args[0] : null;
            // Сохранённое задание ждёт продолжения: файл спрашивать незачем, окно само вернётся к проверке
            bool resume = file == null && Settings.Load().PendingJob != "";
            if (file == null && !resume)
            {
                using (OpenFileDialog dlg = new OpenFileDialog())
                {
                    dlg.Title = "Чертёж для проверки";
                    dlg.Filter = "Чертежи (PDF, PNG, JPG, DWG, DXF)|*.pdf;*.png;*.jpg;*.jpeg;*.dwg;*.dxf|Все файлы|*.*";
                    if (dlg.ShowDialog() != DialogResult.OK) return 0;
                    file = dlg.FileName;
                }
            }

            CheckForm form = new CheckForm();
            form.Standalone = true;
            form.Shown += delegate
            {
                form.BeginFile(file, file == null ? null : Path.GetFileName(file), null);
            };

            // Снимок окна после проверки — чтобы посмотреть вёрстку без ручного запуска
            string shot = Environment.GetEnvironmentVariable("NORMACAD_KOMPAS_SHOT");
            if (!string.IsNullOrEmpty(shot))
            {
                form.Finished += delegate
                {
                    System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
                    timer.Interval = 700;
                    timer.Tick += delegate
                    {
                        timer.Stop();
                        using (Bitmap bmp = new Bitmap(form.Width, form.Height))
                        {
                            form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                            bmp.Save(shot, ImageFormat.Png);
                        }
                        form.Close();
                    };
                    timer.Start();
                };
            }

            Application.Run(form);
            return 0;
        }

        /// <summary>Вход, отправка и опрос без окна; сессию кладёт в настройки. Итог — в файл рядом с чертежом.</summary>
        private static int SelfTest(string[] args)
        {
            string log = args.Length > 4 ? args[4] + ".selftest.txt" : "selftest.txt";
            List<string> lines = new List<string>();
            int code = 1;
            try
            {
                if (args.Length < 5) throw new UserException("Мало аргументов.");
                ApiClient api = new ApiClient();
                api.BaseUrl = ApiClient.NormalizeServer(args[1]);
                Dictionary<string, object> user = api.Login(args[2], File.ReadAllText(args[3]).Trim());
                lines.Add("login ok: " + J.Str(user, "email"));
                lines.Add("account: " + api.GetAccount().Summary());

                Settings cfg = Settings.Load();
                cfg.Server = api.BaseUrl;
                cfg.Email = args[2];
                cfg.Token = api.Token;
                cfg.Save();

                string domain = args.Length > 5 ? args[5] : "eskd";
                Dictionary<string, object> job = api.SubmitJob(args[4], Path.GetFileName(args[4]), domain, null);
                lines.Add("job: " + J.Str(job, "status"));
                DateTime deadline = DateTime.UtcNow.AddMinutes(10);
                while (J.Str(job, "status") != "done" && J.Str(job, "status") != "error")
                {
                    if (DateTime.UtcNow > deadline) throw new UserException("Таймаут опроса.");
                    Thread.Sleep(1000);
                    job = api.GetJob(J.Str(job, "job_id"));
                }
                lines.Add("final: " + J.Str(job, "status") + " " + J.Str(job, "error"));
                Dictionary<string, object> r = J.Obj(job, "report");
                if (r != null)
                {
                    lines.Add("source=" + J.Str(r, "source") + " model=" + J.Str(r, "model")
                        + " score=" + J.Int(r, "score") + " verdict=" + J.Str(r, "verdict"));
                    lines.Add("violations=" + J.Arr(r, "violations").Length + " check_id=" + J.Int(r, "check_id"));
                    code = 0;
                }

                if (args.Length > 6 && args[6] == "logout")
                {
                    // Токен должен быть отозван на сервере: следующий запрос с ним обязан дать 401
                    string oldToken = api.Token;
                    lines.Add("logout: " + api.Logout());
                    cfg.Token = "";
                    cfg.Save();
                    api.Token = oldToken;
                    try
                    {
                        api.GetAccount();
                        lines.Add("after logout: still valid (ожидался 401)");
                        code = 1;
                    }
                    catch (ApiException ex)
                    {
                        lines.Add("after logout: " + ex.Status);
                        if (ex.Status != 401) code = 1;
                    }
                }
            }
            catch (Exception ex)
            {
                lines.Add("FAIL: " + ex.GetType().Name + ": " + ex.Message);
            }
            File.WriteAllLines(log, lines.ToArray());
            return code;
        }
    }
}
