// Проверка клиента сайта без сети и без КОМПАС: настройки с токеном, заголовок Authorization,
// сводка по учётной записи, тексты отказов. Запуск — test-client.cmd,
// код выхода 0 — все проверки прошли.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;

namespace NormaCad.Kompas.Tests
{
    public static class ClientTest
    {
        private static int failed;
        private static int passed;

        private static void Check(string name, bool ok)
        {
            if (ok) passed++; else failed++;
            Console.WriteLine((ok ? "ok   " : "FAIL ") + name);
        }

        private static void Eq(string name, string expected, string actual)
        {
            bool ok = expected == actual;
            Check(name, ok);
            if (!ok)
            {
                Console.WriteLine("       ожидалось: " + expected);
                Console.WriteLine("       получено:  " + actual);
            }
        }

        private static Dictionary<string, object> Json(string text)
        {
            return J.Parse(text);
        }

        public static int Main()
        {
            string oldCfg = Environment.GetEnvironmentVariable("NORMACAD_KOMPAS_CFG");
            string cfg = Path.Combine(Path.GetTempPath(), "normacad-kompas-test-" + Guid.NewGuid().ToString("N") + ".cfg");
            Environment.SetEnvironmentVariable("NORMACAD_KOMPAS_CFG", cfg);
            string oldLog = Environment.GetEnvironmentVariable("NORMACAD_KOMPAS_LOG");
            string log = Path.Combine(Path.GetTempPath(), "normacad-kompas-test-" + Guid.NewGuid().ToString("N") + ".log");
            Environment.SetEnvironmentVariable("NORMACAD_KOMPAS_LOG", log);
            try
            {
                TestSettings(cfg);
                TestProxy(cfg);
                TestPending(cfg);
                TestRequest();
                TestAccount();
                TestTexts();
                TestFailures();
                TestMask();
                TestLog(log);
                TestServer();
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine("FAIL неожиданное исключение: " + ex);
            }
            finally
            {
                Environment.SetEnvironmentVariable("NORMACAD_KOMPAS_CFG", oldCfg);
                Environment.SetEnvironmentVariable("NORMACAD_KOMPAS_LOG", oldLog);
                try { File.Delete(cfg); } catch (Exception) { }
                foreach (string suffix in new string[] { "", ".1", ".2", ".3", ".4" })
                    try { File.Delete(log + suffix); } catch (Exception) { }
            }
            Console.WriteLine();
            Console.WriteLine("Проверок прошло: " + passed + ", не прошло: " + failed);
            return failed == 0 ? 0 : 1;
        }

        private static void TestSettings(string cfg)
        {
            Settings s = new Settings();
            Check("settings: по умолчанию светлая тема", !s.DarkTheme);
            s.DarkTheme = true;
            s.Email = "user@x.ru";
            s.Token = "tok-SECRET123";
            s.Save();
            string raw = File.ReadAllText(cfg);
            Check("settings: токен не лежит открытым текстом", raw.IndexOf("SECRET123") < 0);
            Check("settings: ключ token= записан", raw.Contains("token="));
            Check("settings: ключ sid= больше не пишется", !raw.Contains("sid="));
            Settings back = Settings.Load();
            Check("settings: тёмная тема сохраняется", back.DarkTheme);
            Eq("settings: токен читается обратно", "tok-SECRET123", back.Token);
            Eq("settings: email читается обратно", "user@x.ru", back.Email);

            // Файл старой версии: то же значение (рабочее, зашифрованное), но под ключом sid= и без token=.
            // Сервер номер сессии как токен больше не принимает, поэтому он не переносится.
            File.WriteAllText(cfg, raw.Replace("token=", "sid="));
            Eq("settings: старый sid= не переносится в токен", "", Settings.Load().Token);

            File.WriteAllText(cfg, "email=legacy@x.ru\r\n");
            Check("settings: старый файл без темы — светлая", !Settings.Load().DarkTheme);
            File.WriteAllText(cfg, "theme=unknown\r\n");
            Check("settings: неизвестная тема — светлая", !Settings.Load().DarkTheme);

            // Пустой токен после выхода
            Settings empty = new Settings();
            empty.Save();
            Check("settings: светлая тема сохраняется", !Settings.Load().DarkTheme);
            Eq("settings: пустой токен остаётся пустым", "", Settings.Load().Token);

            File.WriteAllText(cfg, "token=не-base64-вообще\r\n");
            Eq("settings: битый токен даёт пустую строку", "", Settings.Load().Token);
        }

        private static Failure Classify(ApiException e, bool duringCheck)
        {
            return Failure.Classify(e, duringCheck, "https://normacad.pro");
        }

        private static ApiException Net(WebExceptionStatus st)
        {
            return new ApiException(0, "Нет связи: " + st, st);
        }

        private static void CheckFailure(string name, Failure f, string kind, FailureAction action, string label, string url)
        {
            Check("failure: " + name + " — вид " + kind, f.Kind == kind);
            Check("failure: " + name + " — действие и подпись кнопки", f.Action == action && f.ActionLabel == label);
            Check("failure: " + name + " — есть понятный текст", f.Text != "" && f.Text.IndexOf("Exception") < 0);
            if (url != null) Eq("failure: " + name + " — адрес", url, f.Url);
        }

        private static void TestFailures()
        {
            Func<bool> oldNet = Failure.NetworkAvailable;
            try
            {
                Failure.NetworkAvailable = delegate () { return true; };
                CheckFailure("DNS при сети", Classify(Net(WebExceptionStatus.NameResolutionFailure), false),
                    "bad_address", FailureAction.ChangeServer, "Изменить адрес сервера", null);
                CheckFailure("отказ соединения", Classify(Net(WebExceptionStatus.ConnectFailure), false),
                    "server_down", FailureAction.Retry, "Повторить", null);
                CheckFailure("прокси не найден", Classify(Net(WebExceptionStatus.ProxyNameResolutionFailure), false),
                    "proxy", FailureAction.ProxySettings, "Настройки прокси", null);
                CheckFailure("TLS", Classify(Net(WebExceptionStatus.SecureChannelFailure), false),
                    "tls", FailureAction.ProxySettings, "Настройки прокси", null);
                CheckFailure("недоверенный сертификат", Classify(Net(WebExceptionStatus.TrustFailure), false),
                    "tls", FailureAction.ProxySettings, "Настройки прокси", null);
                CheckFailure("таймаут", Classify(Net(WebExceptionStatus.Timeout), false),
                    "timeout", FailureAction.Retry, "Повторить", null);
                CheckFailure("таймаут в проверке", Classify(Net(WebExceptionStatus.Timeout), true),
                    "timeout", FailureAction.Retry, "Повторить", null);
                CheckFailure("обрыв в проверке", Classify(Net(WebExceptionStatus.ConnectionClosed), true),
                    "interrupted", FailureAction.GetResult, "Получить результат", null);
                CheckFailure("ReceiveFailure в проверке", Classify(Net(WebExceptionStatus.ReceiveFailure), true),
                    "interrupted", FailureAction.GetResult, "Получить результат", null);
                CheckFailure("KeepAliveFailure в проверке", Classify(Net(WebExceptionStatus.KeepAliveFailure), true),
                    "interrupted", FailureAction.GetResult, "Получить результат", null);
                Eq("failure: текст обрыва в проверке",
                    "Связь прервалась во время проверки. Разбор продолжается на сервере — окно продолжит, когда связь вернётся.",
                    Classify(Net(WebExceptionStatus.ConnectionClosed), true).Text);
                CheckFailure("обрыв вне проверки", Classify(Net(WebExceptionStatus.ConnectionClosed), false),
                    "server_down", FailureAction.Retry, "Повторить", null);

                Failure.NetworkAvailable = delegate () { return false; };
                CheckFailure("DNS без сети", Classify(Net(WebExceptionStatus.NameResolutionFailure), false),
                    "no_internet", FailureAction.Retry, "Повторить", null);
                CheckFailure("отказ соединения без сети", Classify(Net(WebExceptionStatus.ConnectFailure), false),
                    "no_internet", FailureAction.Retry, "Повторить", null);
                Failure.NetworkAvailable = delegate () { return true; };

                CheckFailure("407", Classify(new ApiException(407, "Proxy Authentication Required"), false),
                    "proxy_auth", FailureAction.ProxySettings, "Настройки прокси", null);
                CheckFailure("426", Classify(new ApiException(426, "Обновите", "client_outdated", "0.2.0", "https://normacad.pro/kompas.zip"), false),
                    "outdated", FailureAction.Download, "Скачать новую версию", "https://normacad.pro/kompas.zip");
                Eq("failure: 426 без адреса — сайт", "https://normacad.pro",
                    Classify(new ApiException(426, "Обновите", "client_outdated"), false).Url);
                CheckFailure("429 лимит", Classify(new ApiException(429, "Лимит исчерпан. Новые проверки с 14:30.", "checks_limit"), false),
                    "limit", FailureAction.ContactTeam, "Связаться с командой", "https://normacad.pro/contacts");
                Eq("failure: 429 лимит — текст сервера", "Лимит исчерпан. Новые проверки с 14:30.",
                    Classify(new ApiException(429, "Лимит исчерпан. Новые проверки с 14:30.", "checks_limit"), false).Text);
                CheckFailure("429 без кода", Classify(new ApiException(429, "Слишком много запросов."), false),
                    "rate", FailureAction.Retry, "Повторить", null);
                CheckFailure("403 срок", Classify(new ApiException(403, "Срок", "trial_expired"), false),
                    "expired", FailureAction.ContactTeam, "Связаться с командой", "https://normacad.pro/contacts");
                CheckFailure("401", Classify(new ApiException(401, "Требуется вход"), false),
                    "unauthorized", FailureAction.Login, "Войти", null);
                CheckFailure("502", Classify(new ApiException(502, "x"), false), "server_down", FailureAction.Retry, "Повторить", null);
                CheckFailure("503", Classify(new ApiException(503, "x"), false), "server_down", FailureAction.Retry, "Повторить", null);
                CheckFailure("504", Classify(new ApiException(504, "x"), false), "server_down", FailureAction.Retry, "Повторить", null);
                CheckFailure("500", Classify(new ApiException(500, "x"), false), "server_error", FailureAction.Retry, "Повторить", null);
                CheckFailure("413", Classify(new ApiException(413, "Файл слишком большой."), false),
                    "refused", FailureAction.None, "", null);
                Eq("failure: 4xx — текст сервера", "Файл слишком большой.",
                    Classify(new ApiException(413, "Файл слишком большой."), false).Text);
                CheckFailure("UserException", Failure.Classify(new UserException("Файл больше 50 МБ."), false, "https://normacad.pro"),
                    "refused", FailureAction.None, "", null);
                Eq("failure: UserException — его текст", "Файл больше 50 МБ.",
                    Failure.Classify(new UserException("Файл больше 50 МБ."), false, "https://normacad.pro").Text);
                CheckFailure("ответ без токена", Classify(new ApiException(0, "Сервер не выдал токен входа. Проверьте адрес сервера."), false),
                    "refused", FailureAction.None, "", null);
                Eq("failure: адрес сайта без косой черты в конце", "https://normacad.pro/contacts",
                    Failure.Classify(new ApiException(429, "x", "checks_limit"), false, "https://normacad.pro/").Url);
            }
            finally
            {
                Failure.NetworkAvailable = oldNet;
            }
        }

        private static void TestMask()
        {
            string m = SupportLog.Mask("Authorization: Bearer abc.DEF-123_xyz ok");
            Check("mask: Bearer вырезан, остальное цело",
                m.IndexOf("abc.DEF") < 0 && m.StartsWith("Authorization: Bearer ") && m.EndsWith(" ok"));
            m = SupportLog.Mask("{\"email\":\"u@x.ru\",\"password\":\"p@ss\\\"tail\"}");
            Check("mask: пароль в JSON вырезан, email цел", m.IndexOf("p@ss") < 0 && m.IndexOf("tail") < 0 && m.Contains("u@x.ru"));
            m = SupportLog.Mask("{\"token\":\"tok-SECRET\",\"token_type\":\"bearer\",\"user\":{}}");
            Check("mask: токен в JSON вырезан, token_type цел", m.IndexOf("SECRET") < 0 && m.Contains("\"token_type\":\"bearer\""));
            m = SupportLog.Mask("GET /x?token=ZZZ&a=1 sid=QQQ proxy_pass=PPP Cookie: ncad_sid=RRR; k=v");
            Check("mask: token=/sid=/proxy_pass=/ncad_sid= вырезаны",
                m.IndexOf("ZZZ") < 0 && m.IndexOf("QQQ") < 0 && m.IndexOf("PPP") < 0 && m.IndexOf("RRR") < 0);
            Check("mask: прочие параметры целы", m.Contains("a=1") && m.Contains("k=v"));
            m = SupportLog.Mask("прокси http://user:hunter2@proxy.local:3128");
            Check("mask: пароль в адресе прокси вырезан",
                m.IndexOf("hunter2") < 0 && m.Contains("user") && m.Contains("proxy.local:3128"));
            Eq("mask: обычный текст без изменений", "вход: user@x.ru, GET /api/auth/me -> 200, 15 мс",
                SupportLog.Mask("вход: user@x.ru, GET /api/auth/me -> 200, 15 мс"));
            Eq("mask: null — пустая строка", "", SupportLog.Mask(null));
        }

        private static void TestLog(string log)
        {
            long oldMax = SupportLog.MaxBytes;
            try
            {
                Eq("log: путь из NORMACAD_KOMPAS_LOG", log, SupportLog.Path);
                SupportLog.SessionStarted = false;
                foreach (string suffix in new string[] { "", ".1", ".2", ".3" }) File.Delete(log + suffix);
                SupportLog.Write("первая запись");
                SupportLog.Write("Authorization: Bearer SECRETTOKEN, \"password\":\"SECRETPASS\"");
                string[] lines = File.ReadAllLines(log, Encoding.UTF8);
                Check("log: первая строка сеанса несёт версию клиента",
                    lines.Length > 0 && lines[0].Contains("NormaCAD-Kompas/" + ApiClient.ClientVersion));
                string all = string.Join("\n", lines);
                Check("log: запись с меткой времени", lines.Length == 3 && System.Text.RegularExpressions.Regex.IsMatch(
                    lines[1], @"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3} первая запись$"));
                Check("log: секреты в файл не попали", all.IndexOf("SECRETTOKEN") < 0 && all.IndexOf("SECRETPASS") < 0);

                // Ротация: log -> .1 -> .2 -> .3, старше удаляется
                SupportLog.MaxBytes = 300;
                for (int i = 0; i < 60; i++) SupportLog.Write("строка для ротации номер " + i);
                Check("log: после ротации есть .1, .2 и .3", File.Exists(log + ".1") && File.Exists(log + ".2") && File.Exists(log + ".3"));
                Check("log: .4 не создаётся", !File.Exists(log + ".4"));
                Check("log: текущий файл не раздувается", new FileInfo(log).Length < 1000);
                Check("log: самая свежая запись в текущем файле", File.ReadAllText(log, Encoding.UTF8).Contains("номер 59"));

                // Недоступный путь журнал не роняет
                Environment.SetEnvironmentVariable("NORMACAD_KOMPAS_LOG", Path.Combine(log, "no.log"));
                bool threw = false;
                try { SupportLog.Write("в никуда"); } catch (Exception) { threw = true; }
                Check("log: недоступный путь не бросает исключений", !threw);
            }
            finally
            {
                SupportLog.MaxBytes = oldMax;
                Environment.SetEnvironmentVariable("NORMACAD_KOMPAS_LOG", log);
            }
        }

        private static void TestProxy(string cfg)
        {
            Settings s = new Settings();
            s.ProxyAddress = "proxy.corp.local:3128";
            s.ProxyUser = "ivanov";
            s.ProxyPassword = "PROXY-SECRET-777";
            s.Save();
            string raw = File.ReadAllText(cfg);
            Check("proxy: пароль прокси не лежит открытым текстом", raw.IndexOf("PROXY-SECRET-777") < 0);
            Check("proxy: ключи proxy=, proxy_user=, proxy_pass= записаны",
                raw.Contains("proxy=proxy.corp.local:3128") && raw.Contains("proxy_user=ivanov") && raw.Contains("proxy_pass="));
            Settings back = Settings.Load();
            Check("proxy: поля читаются обратно",
                back.ProxyAddress == "proxy.corp.local:3128" && back.ProxyUser == "ivanov" && back.ProxyPassword == "PROXY-SECRET-777");

            ProxySetup.Apply(back);
            WebProxy w = ProxySetup.Current as WebProxy;
            Check("proxy: ручной адрес даёт WebProxy", w != null && w.Address.Host == "proxy.corp.local" && w.Address.Port == 3128);
            Check("proxy: локальные адреса идут мимо прокси", w != null && w.BypassProxyOnLocal);
            NetworkCredential cred = ProxySetup.Current.Credentials as NetworkCredential;
            Check("proxy: имя и пароль переданы", cred != null && cred.UserName == "ivanov" && cred.Password == "PROXY-SECRET-777");

            back.ProxyUser = "";
            back.ProxyPassword = "";
            ProxySetup.Apply(back);
            Check("proxy: без имени — учётные данные Windows",
                object.ReferenceEquals(ProxySetup.Current.Credentials, CredentialCache.DefaultCredentials));

            ProxySetup.Apply(new Settings());
            WebProxy sys = ProxySetup.Current as WebProxy;
            Check("proxy: пустой адрес — системный прокси, не наш",
                ProxySetup.Current != null && (sys == null || sys.Address == null || sys.Address.Host != "proxy.corp.local"));
            Check("proxy: системный — учётные данные Windows",
                object.ReferenceEquals(ProxySetup.Current.Credentials, CredentialCache.DefaultCredentials));
            Settings fresh = new Settings();
            Eq("proxy: по умолчанию в настройках пусто", "", fresh.ProxyAddress + fresh.ProxyUser + fresh.ProxyPassword);
        }

        private static void TestPending(string cfg)
        {
            DateTime since = new DateTime(2026, 10, 20, 9, 30, 15, DateTimeKind.Utc);
            Settings s = new Settings();
            s.PendingJob = "job-42";
            s.PendingFile = "План этажа.cdw";
            s.PendingSince = since;
            s.Save();
            string raw = File.ReadAllText(cfg);
            Check("pending: ключи job=, job_file=, job_since= записаны, дата ISO",
                raw.Contains("job=job-42") && raw.Contains("job_file=План этажа.cdw") && raw.Contains("job_since=2026-10-20T09:30:15"));
            Settings back = Settings.Load();
            Check("pending: читается обратно",
                back.PendingJob == "job-42" && back.PendingFile == "План этажа.cdw"
                && back.PendingSince == since && back.PendingSince.Kind == DateTimeKind.Utc);
            back.ClearPending();
            Settings cleared = Settings.Load();
            Check("pending: ClearPending очищает и сохраняет",
                cleared.PendingJob == "" && cleared.PendingFile == "" && cleared.PendingSince == DateTime.MinValue);
            Check("pending: в файле значений не осталось", !File.ReadAllText(cfg).Contains("job=job-42"));
            File.WriteAllText(cfg, "job_since=не дата\r\n");
            Check("pending: битая дата — как будто нет", Settings.Load().PendingSince == DateTime.MinValue);
        }

        private static void TestRequest()
        {
            ApiClient c = new ApiClient();
            c.BaseUrl = "http://127.0.0.1:8011";
            c.Token = "abc123";
            HttpWebRequest r = c.NewRequest("GET", "/api/auth/me", true);
            Eq("request: Authorization Bearer", "Bearer abc123", r.Headers["Authorization"]);
            Check("request: Cookie не ставится", r.Headers["Cookie"] == null);
            Eq("request: адрес", "http://127.0.0.1:8011/api/auth/me", r.RequestUri.ToString());
            Eq("request: заголовок версии клиента", "kompas/" + ApiClient.ClientVersion, r.Headers["X-NormaCAD-Client"]);
            Check("request: прокси из ProxySetup", object.ReferenceEquals(r.Proxy, ProxySetup.Current));
            Check("request: User-Agent с версией клиента",
                r.UserAgent == "NormaCAD-Kompas/" + ApiClient.ClientVersion + " (Windows; KOMPAS-3D)");

            HttpWebRequest anon = c.NewRequest("POST", "/api/auth/token", false);
            Check("request: без сессии нет Authorization", anon.Headers["Authorization"] == null);

            c.Token = "";
            HttpWebRequest noToken = c.NewRequest("GET", "/api/auth/me", true);
            Check("request: пустой токен — нет Authorization", noToken.Headers["Authorization"] == null);

            Check("logout: без токена — true без обращения к серверу", c.Logout());
        }

        private static void TestAccount()
        {
            Dictionary<string, object> me = Json("{\"email\":\"user@x.ru\",\"full_name\":\"Иван Петров\"}");

            Dictionary<string, object> free = Json(
                "{\"plan\":\"full\",\"plan_label\":\"Полный доступ\",\"organization\":\"\",\"limited\":false,"
                + "\"access_until\":null,\"expired\":false,\"checks_limit\":null,\"window_hours\":24,\"used\":3,"
                + "\"remaining\":null,\"reset_at\":null,\"can_check\":true,\"message\":\"\"}");
            AccountInfo a = AccountInfo.FromJson(me, free);
            Eq("account: без ограничений", "user@x.ru · без ограничений", a.Summary());
            Check("account: поля разобраны", a.FullName == "Иван Петров" && a.CanCheck && !a.Limited
                && a.ChecksLimit == null && a.AccessUntil == null && a.Used == 3);

            Dictionary<string, object> trial = Json(
                "{\"plan\":\"trial\",\"plan_label\":\"Тестовый доступ\",\"organization\":\"ООО Ромашка\",\"limited\":true,"
                + "\"access_until\":\"2026-10-31T12:00:00+00:00\",\"expired\":false,\"checks_limit\":10,\"window_hours\":24,"
                + "\"used\":3,\"remaining\":7,\"reset_at\":\"2026-10-20T10:00:00+00:00\",\"can_check\":true,\"message\":\"ok\"}");
            a = AccountInfo.FromJson(me, trial);
            Eq("account: лимит 7 из 10",
                "user@x.ru · Тестовый доступ до 31.10.2026 · осталось 7 из 10 проверок за 24 ч", a.Summary());
            Check("account: поля пробного доступа", a.Limited && a.ChecksLimit == 10 && a.Remaining == 7
                && a.WindowHours == 24 && a.Organization == "ООО Ромашка" && a.AccessUntil.HasValue
                && a.Message == "ok");

            Dictionary<string, object> expired = Json(
                "{\"plan\":\"trial\",\"plan_label\":\"Тестовый доступ\",\"limited\":true,"
                + "\"access_until\":\"2026-10-31T12:00:00+00:00\",\"expired\":true,\"checks_limit\":10,\"window_hours\":24,"
                + "\"used\":0,\"remaining\":10,\"can_check\":false}");
            a = AccountInfo.FromJson(me, expired);
            Eq("account: срок истёк",
                "user@x.ru · срок доступа истёк 31.10.2026 — новые проверки недоступны", a.Summary());
            Check("account: истёк — проверка недоступна", a.Expired && !a.CanCheck);

            Dictionary<string, object> none = Json(
                "{\"plan\":\"trial\",\"plan_label\":\"Тестовый доступ\",\"limited\":true,"
                + "\"access_until\":\"2026-10-31T12:00:00+00:00\",\"expired\":false,\"checks_limit\":10,\"window_hours\":24,"
                + "\"used\":10,\"remaining\":0,\"can_check\":false}");
            a = AccountInfo.FromJson(me, none);
            Eq("account: остаток 0",
                "user@x.ru · Тестовый доступ до 31.10.2026 · лимит проверок исчерпан", a.Summary());

            a = AccountInfo.FromJson(null, null);
            Eq("account: оба словаря null", "без ограничений", a.Summary());
            Check("account: null — проверка не запрещена заранее", a.CanCheck && !a.Expired && !a.Limited);

            a = AccountInfo.FromJson(me, null);
            Eq("account: нет сведений о доступе", "user@x.ru · без ограничений", a.Summary());

            a = AccountInfo.FromJson(null, trial);
            Eq("account: нет сведений о пользователе",
                "Тестовый доступ до 31.10.2026 · осталось 7 из 10 проверок за 24 ч", a.Summary());

            Dictionary<string, object> bad = Json("{\"access_until\":\"не дата\",\"checks_limit\":\"много\",\"used\":null}");
            a = AccountInfo.FromJson(me, bad);
            Check("account: мусор в полях не роняет разбор", a.AccessUntil == null && a.ChecksLimit == null && a.Used == 0);
        }

        private static void TestTexts()
        {
            Eq("login: 401", "Неверный email или пароль. Если доступа ещё нет — запросите приглашение (ссылка «Доступ по запросу»).",
                ApiClient.LoginError(new ApiException(401, "Неверный email или пароль")));
            Eq("login: 403 текст сервера", "Учётная запись отключена",
                ApiClient.LoginError(new ApiException(403, "Учётная запись отключена")));
            Eq("login: 403 без текста", "Вход запрещён. Обратитесь к администратору.",
                ApiClient.LoginError(new ApiException(403, "")));
            Eq("login: 429", "Слишком много попыток входа. Подождите минуту и повторите.",
                ApiClient.LoginError(new ApiException(429, "Слишком много запросов.")));
            Eq("login: нет связи", "Нет связи с сервером. Проверьте интернет и адрес сервера.",
                ApiClient.LoginError(new ApiException(0, "Нет связи с сервером https://x: The remote name could not be resolved")));
            Eq("login: сервер не выдал токен", "Сервер не выдал токен входа. Проверьте адрес сервера.",
                ApiClient.LoginError(new ApiException(0, "Сервер не выдал токен входа. Проверьте адрес сервера.")));
            Eq("login: UserException", "Адрес сервера указан неверно.",
                ApiClient.LoginError(new UserException("Адрес сервера указан неверно.")));
            Eq("login: прочее", "что-то", ApiClient.LoginError(new InvalidOperationException("что-то")));

            Eq("refusal: trial_expired",
                "Срок доступа истёк: новые проверки недоступны, история и отчёты на сайте открываются. Чтобы продлить доступ, свяжитесь с командой проекта.",
                ApiClient.RefusalText(new ApiException(403, "Тестовый срок закончился", "trial_expired")));
            Eq("refusal: checks_limit текст сервера", "Лимит исчерпан. Новые проверки с 14:30.",
                ApiClient.RefusalText(new ApiException(429, "Лимит исчерпан. Новые проверки с 14:30.", "checks_limit")));
            Eq("refusal: 401", "Вход истёк или закрыт. Войдите снова.",
                ApiClient.RefusalText(new ApiException(401, "Требуется вход в NormaCAD.PRO.")));
            Eq("refusal: прочее", "Файл слишком большой.",
                ApiClient.RefusalText(new ApiException(413, "Файл слишком большой.")));
            Eq("refusal: старый конструктор — пустой Code", "", new ApiException(500, "x").Code);

            Eq("errorcode: code из тела", "checks_limit",
                ApiClient.ErrorCode("{\"detail\":\"x\",\"code\":\"checks_limit\",\"reset_at\":\"2026-10-20T10:00:00Z\"}"));
            Eq("errorcode: нет code", "", ApiClient.ErrorCode("{\"detail\":\"x\"}"));
            Eq("errorcode: не JSON", "", ApiClient.ErrorCode("<html>502</html>"));

            Eq("forgot: loopback по http", "http://127.0.0.1:8011/forgot-password",
                ApiClient.ForgotPasswordUrl("http://127.0.0.1:8011"));
            Eq("forgot: без схемы — https", "https://127.0.0.1:8011/forgot-password",
                ApiClient.ForgotPasswordUrl("127.0.0.1:8011"));
            Eq("forgot: пустой адрес — сайт по умолчанию", Settings.DefaultServer + "/forgot-password",
                ApiClient.ForgotPasswordUrl(" "));
            bool threw = false;
            try { ApiClient.ForgotPasswordUrl("http://example.com"); }
            catch (UserException) { threw = true; }
            Check("forgot: http не для своего компьютера — UserException", threw);
        }

        // Вход и выход против временного локального сервера, который отвечает как настоящий
        private static void TestServer()
        {
            HttpListener listener = new HttpListener();
            int port = 18000 + new Random().Next(1000);
            string prefix = "http://127.0.0.1:" + port + "/";
            listener.Prefixes.Add(prefix);
            try { listener.Start(); }
            catch (Exception ex)
            {
                Console.WriteLine("skip  локальный сервер не запустился: " + ex.Message);
                return;
            }

            string lastAuth = "";
            string lastClient = "";
            string lastQuery = "";
            int factsRequests = 0;
            string recent = DateTime.UtcNow.AddSeconds(-30).ToString("yyyy-MM-ddTHH:mm:ss+00:00");
            string stale = DateTime.UtcNow.AddHours(-3).ToString("yyyy-MM-ddTHH:mm:ss+00:00");
            System.Threading.Thread t = new System.Threading.Thread(delegate ()
            {
                while (listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = listener.GetContext(); }
                    catch (Exception) { return; }
                    string path = ctx.Request.Url.AbsolutePath;
                    string auth = ctx.Request.Headers["Authorization"] ?? "";
                    lastAuth = auth;
                    lastClient = ctx.Request.Headers["X-NormaCAD-Client"] ?? "";
                    string query = ctx.Request.Url.Query;
                    int status = 200;
                    string body = "{}";
                    if (path == "/api/auth/token")
                    {
                        string input = new StreamReader(ctx.Request.InputStream).ReadToEnd();
                        if (input.Contains("\"password\":\"good\""))
                            body = "{\"token\":\"tok-1\",\"token_type\":\"bearer\",\"user\":{\"email\":\"user@x.ru\",\"full_name\":\"Иван\"}}";
                        else if (input.Contains("\"password\":\"notoken\"")) body = "{\"user\":{}}";
                        else if (input.Contains("\"password\":\"off\"")) { status = 403; body = "{\"detail\":\"Учётная запись отключена\"}"; }
                        else { status = 401; body = "{\"detail\":\"Неверный email или пароль\"}"; }
                    }
                    else if (path == "/api/auth/logout")
                    {
                        if (auth == "Bearer tok-1") body = "{\"ok\":true}";
                        else if (auth == "Bearer dead") { status = 401; body = "{\"detail\":\"Требуется вход\"}"; }
                        else { status = 500; body = "{\"detail\":\"сбой\"}"; }
                    }
                    else if (path == "/api/auth/me" && auth == "Bearer old")
                    {
                        status = 426;
                        body = "{\"detail\":\"Версия устарела\",\"code\":\"client_outdated\",\"min_version\":\"0.2.0\",\"download_url\":\"https://normacad.pro/kompas\"}";
                    }
                    else if (path == "/api/drawings/history")
                    {
                        lastQuery = query;
                        if (query.Contains("q=a.cdw"))
                            body = "{\"items\":[{\"id\":7,\"filename\":\"a.cdw\",\"created_at\":\"" + recent + "\"}]}";
                        else if (query.Contains("q=old.cdw"))
                            body = "{\"items\":[{\"id\":5,\"filename\":\"old.cdw\",\"created_at\":\"" + stale + "\"}]}";
                        else if (query.Contains("q=gone.cdw"))
                            body = "{\"items\":[{\"id\":9,\"filename\":\"gone.cdw\",\"created_at\":\"" + recent + "\"}]}";
                        else body = "{\"items\":[]}";
                    }
                    else if (path == "/api/drawings/history/7")
                        body = "{\"id\":7,\"filename\":\"a.cdw\",\"issues\":[]}";
                    else if (path.StartsWith("/api/drawings/history/"))
                    {
                        status = 404;
                        body = "{\"detail\":\"Не найдено\"}";
                    }
                    else if (path == "/api/auth/me")
                        body = "{\"email\":\"user@x.ru\",\"full_name\":\"Иван\"}";
                    else if (path == "/api/users/me/access")
                        body = "{\"plan\":\"trial\",\"plan_label\":\"Тестовый доступ\",\"limited\":true,"
                            + "\"access_until\":\"2026-10-31T12:00:00+00:00\",\"expired\":false,\"checks_limit\":10,"
                            + "\"window_hours\":24,\"used\":3,\"remaining\":7,\"can_check\":true}";
                    else if (path == "/api/drawings/jobs" && auth == "Bearer facts")
                    {
                        factsRequests++;
                        string input = new StreamReader(ctx.Request.InputStream).ReadToEnd();
                        if (input.Contains("name=\"client_facts\""))
                        {
                            status = 422;
                            body = "{\"detail\":[{\"loc\":[\"body\",\"client_facts\"],\"msg\":\"Value error\",\"type\":\"value_error\"}]}";
                        }
                        else body = "{\"job_id\":\"accepted-without-facts\"}";
                    }
                    else if (path == "/api/drawings/jobs")
                    {
                        status = 403;
                        body = "{\"detail\":\"Срок закончился\",\"code\":\"trial_expired\"}";
                    }
                    byte[] data = System.Text.Encoding.UTF8.GetBytes(body);
                    ctx.Response.StatusCode = status;
                    ctx.Response.ContentType = "application/json; charset=utf-8";
                    ctx.Response.OutputStream.Write(data, 0, data.Length);
                    ctx.Response.Close();
                }
            });
            t.IsBackground = true;
            t.Start();

            try
            {
                ApiClient c = new ApiClient();
                c.BaseUrl = prefix.TrimEnd('/');

                Dictionary<string, object> user = c.Login("user@x.ru", "good");
                Eq("server: Login запоминает токен", "tok-1", c.Token);
                Eq("server: Login возвращает user", "user@x.ru", J.Str(user, "email"));

                AccountInfo a = c.GetAccount();
                Eq("server: GetAccount шлёт Bearer", "Bearer tok-1", lastAuth);
                Eq("server: GetAccount сводка",
                    "user@x.ru · Тестовый доступ до 31.10.2026 · осталось 7 из 10 проверок за 24 ч", a.Summary());

                ApiClient bad = new ApiClient();
                bad.BaseUrl = c.BaseUrl;
                ApiException ex401 = null;
                try { bad.Login("user@x.ru", "wrong"); } catch (ApiException e) { ex401 = e; }
                Check("server: Login 401 → ApiException 401", ex401 != null && ex401.Status == 401);
                Check("server: после отказа токен пуст", bad.Token == "");

                ApiException exOff = null;
                try { bad.Login("user@x.ru", "off"); } catch (ApiException e) { exOff = e; }
                Check("server: Login 403 → текст сервера",
                    exOff != null && exOff.Status == 403 && ApiClient.LoginError(exOff) == "Учётная запись отключена");

                ApiException exNo = null;
                try { bad.Login("user@x.ru", "notoken"); } catch (ApiException e) { exNo = e; }
                Check("server: ответ без токена → ApiException(0)",
                    exNo != null && exNo.Status == 0 && exNo.Message == "Сервер не выдал токен входа. Проверьте адрес сервера.");

                ApiException exJob = null;
                try { c.SubmitJob(MakeUploadFile(), "x.pdf", "eskd", null); } catch (ApiException e) { exJob = e; }
                Check("server: отказ в проверке несёт Code",
                    exJob != null && exJob.Status == 403 && exJob.Code == "trial_expired");

                Eq("server: заголовок версии клиента дошёл", "kompas/" + ApiClient.ClientVersion, lastClient);

                ApiClient factsClient = new ApiClient();
                factsClient.BaseUrl = c.BaseUrl;
                factsClient.Token = "facts";
                Dictionary<string, object> accepted = null;
                try
                {
                    accepted = factsClient.SubmitJob(MakeUploadFile(), "x.pdf", "eskd",
                        new LocalFacts { DocType = "drawing" });
                }
                catch (ApiException) { }
                Check("server: 422 loc client_facts — повтор без фактов принят",
                    accepted != null && J.Str(accepted, "job_id") == "accepted-without-facts");
                Check("server: 422 loc client_facts — ровно две отправки", factsRequests == 2);

                // История: потерянное задание ищем по имени файла и времени отправки
                DateTime sent = DateTime.UtcNow.AddMinutes(-1);
                Dictionary<string, object> found = c.FindInHistory("a.cdw", sent);
                Check("history: найдена свежая проверка", found != null && J.Int(found, "check_id") == 7 && J.Str(found, "filename") == "a.cdw");
                Check("history: имя файла ушло в запрос", lastQuery.Contains("q=a.cdw") && lastQuery.Contains("limit=10"));
                Check("history: нет такой записи — null", c.FindInHistory("none.cdw", sent) == null);
                Check("history: старая запись не берётся", c.FindInHistory("old.cdw", sent) == null);
                Check("history: запись есть, а отчёт 404 — null", c.FindInHistory("gone.cdw", sent) == null);
                Check("history: запись на 1,5 минуты раньше отправки (запас 2 мин) берётся",
                    c.FindInHistory("a.cdw", DateTime.UtcNow.AddSeconds(-30).AddSeconds(60)) != null);
                Check("history: запись раньше запаса не берётся",
                    c.FindInHistory("a.cdw", DateTime.UtcNow.AddMinutes(5)) == null);

                // 426: устаревший клиент
                ApiClient old = new ApiClient();
                old.BaseUrl = c.BaseUrl;
                old.Token = "old";
                ApiException ex426 = null;
                try { old.GetAccount(); } catch (ApiException e) { ex426 = e; }
                Check("server: 426 → ApiException с версией и адресом",
                    ex426 != null && ex426.Status == 426 && ex426.Code == "client_outdated"
                    && ex426.MinVersion == "0.2.0" && ex426.DownloadUrl == "https://normacad.pro/kompas");
                Failure f426 = Failure.Classify(ex426, false, old.BaseUrl);
                Check("server: 426 → заглушка outdated со ссылкой", f426.Kind == "outdated"
                    && f426.Action == FailureAction.Download && f426.Url == "https://normacad.pro/kompas");

                Check("server: Logout подтверждён", c.Logout());
                Eq("server: Logout чистит токен", "", c.Token);

                c.Token = "dead";
                Check("server: Logout при 401 — true", c.Logout());
                Eq("server: токен чист после 401", "", c.Token);

                c.Token = "other";
                Check("server: Logout при сбое сервера — false", !c.Logout());
                Eq("server: токен чист после сбоя", "", c.Token);
            }
            finally
            {
                listener.Stop();
            }

            ApiClient offline = new ApiClient();
            offline.BaseUrl = prefix.TrimEnd('/');
            offline.Token = "tok-1";
            ApiException exDown = null;
            try { offline.GetAccount(); } catch (ApiException e) { exDown = e; }
            Check("server: сервер выключен → Status 0 с NetStatus",
                exDown != null && exDown.Status == 0
                && (exDown.NetStatus == WebExceptionStatus.ConnectFailure || exDown.NetStatus == WebExceptionStatus.NameResolutionFailure));
            Check("server: сервер выключен → в тексте нет токена",
                exDown != null && Failure.Classify(exDown, false, offline.BaseUrl).Text.IndexOf("tok-1") < 0
                && exDown.Message.IndexOf("tok-1") < 0);
            Check("server: Logout без связи — false", !offline.Logout());
            Eq("server: токен чист без связи", "", offline.Token);
        }

        // Любой существующий небольшой файл: SubmitJob читает его до обращения к серверу
        private static string MakeUploadFile()
        {
            string p = Path.Combine(Path.GetTempPath(), "normacad-kompas-test-upload.pdf");
            File.WriteAllBytes(p, new byte[] { 0x25, 0x50, 0x44, 0x46 });
            return p;
        }
    }
}
