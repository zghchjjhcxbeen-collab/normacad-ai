// Обмен с сайтом NormaCAD.PRO и настройки расширения.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace NormaCad.Kompas
{
    /// <summary>Ошибка, текст которой можно показать инженеру как есть.</summary>
    internal class UserException : Exception
    {
        public UserException(string message) : base(message) { }
    }

    /// <summary>
    /// Что прочитано на ПК без ИИ (#45): тип документа, листы, основная надпись и замечания по ней.
    /// Заполняет чтение из КОМПАС, показывает окно, на сервер уходит в поле client_facts.
    /// </summary>
    internal class LocalFacts
    {
        /// <summary>Машинный тип документа — как в описании client_facts.</summary>
        public string DocType = "";
        /// <summary>Тип для показа: «Чертёж детали», «Спецификация»…</summary>
        public string DocTypeLabel = "";
        /// <summary>Обозначение и наименование с первого листа (графы 2 и 1).</summary>
        public string Designation = "";
        public string Name = "";
        public List<LocalSheet> Sheets = new List<LocalSheet>();
        public List<LocalRemark> Remarks = new List<LocalRemark>();
        /// <summary>Почему прочитать не удалось; пусто — прочитано.</summary>
        public string Error = "";
        /// <summary>Листов в документе; в Sheets — не больше ClientFacts.MaxSheets.</summary>
        public int SheetsTotal = 0;
        /// <summary>Чего проверка на ПК не охватила: «у фрагмента нет основной надписи»…; пусто — всё проверено.</summary>
        public string Note = "";
    }

    internal class LocalSheet
    {
        public int Number = 0;
        /// <summary>«А3», «А4×3».</summary>
        public string Format = "";
        public bool Vertical = false;
        /// <summary>Формат для client_facts: A0…A5, user; пусто — формат не прочитан.</summary>
        public string FormatCode = "";
        public int Multiplicity = 0;
        public double WidthMm = 0;
        public double HeightMm = 0;
        /// <summary>Вид основной надписи (StampForm): first, next, title, none, other.</summary>
        public string Form = "other";
        /// <summary>Имя стиля оформления КОМПАС — для журнала; на сервер не уходит.</summary>
        public string StyleName = "";
        /// <summary>Графа ГОСТ 2.104 → видимый текст; только графы формы листа из белого списка.</summary>
        public Dictionary<int, string> Graphs = new Dictionary<int, string>();
    }

    /// <summary>Замечание ПК; поля — как у замечания в отчёте сервера.</summary>
    internal class LocalRemark
    {
        public string Severity = "warning";
        public int Page = 0;
        public string Title = "";
        public string Standard = "";
        public string Detail = "";
        public List<string> Fixes = new List<string>();
        /// <summary>Правило ПК, которое дало замечание: stamp.required, stamp.sheets_count…</summary>
        public string Rule = "";
    }

    /// <summary>
    /// Поле client_facts (CLIENT_FACTS.md): что прочитано на ПК, JSON до 64 КБ. Уходят только
    /// графы из белого списка — фамилии, подписи и даты остаются на ПК; имени файла нет вовсе.
    /// </summary>
    internal static class ClientFacts
    {
        public const int MaxBytes = 64 * 1024;
        public const int MaxSheets = 100;
        public const int MaxRemarks = 50;
        /// <summary>Графы ГОСТ 2.104, которые уходят на сервер.</summary>
        public static readonly int[] Graphs = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 25 };

        /// <summary>JSON для поля; пусто — отправлять нечего. Не влезло — меньше листов, затем без пояснений.</summary>
        public static string Json(LocalFacts f)
        {
            if (f == null || f.Error != "" || f.DocType == "") return "";
            foreach (int limit in new[] { MaxSheets, 20, 1 })
            {
                string json = Serialize(f, limit, true);
                if (Encoding.UTF8.GetByteCount(json) <= MaxBytes) return json;
            }
            string brief = Serialize(f, 1, false);
            return Encoding.UTF8.GetByteCount(brief) <= MaxBytes ? brief : "";
        }

        private static string Serialize(LocalFacts f, int sheetLimit, bool details)
        {
            Dictionary<string, object> doc = new Dictionary<string, object>();
            doc["kind"] = f.DocType;
            doc["sheets_total"] = Math.Max(f.SheetsTotal, f.Sheets.Count);

            List<object> sheets = new List<object>();
            foreach (LocalSheet s in f.Sheets)
            {
                if (sheets.Count >= Math.Min(sheetLimit, MaxSheets)) break;
                Dictionary<string, object> sheet = new Dictionary<string, object>();
                sheet["n"] = s.Number;
                if (s.FormatCode != "")
                {
                    sheet["format"] = s.FormatCode;
                    if (s.Multiplicity > 0) sheet["multiplicity"] = s.Multiplicity;
                    sheet["orientation"] = s.Vertical ? "portrait" : "landscape";
                    if (s.WidthMm > 0 && s.HeightMm > 0)
                    {
                        sheet["width_mm"] = Math.Round(s.WidthMm, 1);
                        sheet["height_mm"] = Math.Round(s.HeightMm, 1);
                    }
                }
                sheet["form"] = s.Form;
                Dictionary<string, object> graphs = new Dictionary<string, object>();
                foreach (int g in Graphs)
                {
                    string text;
                    if (s.Graphs.TryGetValue(g, out text))
                        graphs[g.ToString(CultureInfo.InvariantCulture)] = Cut(text, 300);
                }
                sheet["graphs"] = graphs;
                sheets.Add(sheet);
            }

            List<object> remarks = new List<object>();
            foreach (LocalRemark r in f.Remarks)
            {
                if (remarks.Count >= MaxRemarks) break;
                Dictionary<string, object> item = new Dictionary<string, object>();
                item["rule"] = r.Rule;
                item["severity"] = "warning";
                if (r.Page > 0) item["page"] = r.Page;
                item["title"] = Cut(r.Title, 255);
                item["standard"] = Cut(r.Standard, 255);
                item["detail"] = details ? Cut(r.Detail, 1000) : "";
                List<string> fixes = new List<string>();
                foreach (string fix in r.Fixes) if (fixes.Count < 3) fixes.Add(Cut(fix, 300));
                item["fixes"] = fixes.ToArray();
                remarks.Add(item);
            }

            Dictionary<string, object> root = new Dictionary<string, object>();
            root["v"] = 1;
            root["source"] = "kompas";
            root["doc"] = doc;
            root["sheets"] = sheets;
            root["remarks"] = remarks;
            return new JavaScriptSerializer().Serialize(root);
        }

        private static string Cut(string s, int max)
        {
            s = s ?? "";
            return s.Length > max ? s.Substring(0, max) : s;
        }
    }

    /// <summary>
    /// Отказ сервера. Status == 0 — до сервера не достучались (причина — в NetStatus).
    /// Code — машинный код отказа ("trial_expired", "checks_limit", "client_outdated"), если сервер его передал.
    /// MinVersion и DownloadUrl приходят в теле ответа 426 (устаревший клиент).
    /// </summary>
    internal class ApiException : Exception
    {
        public readonly int Status;
        public readonly string Code;
        public readonly WebExceptionStatus NetStatus;
        public readonly string MinVersion;
        public readonly string DownloadUrl;
        public ApiException(int status, string message) : this(status, message, "", WebExceptionStatus.Success, "", "") { }
        public ApiException(int status, string message, string code) : this(status, message, code, WebExceptionStatus.Success, "", "") { }
        public ApiException(int status, string message, WebExceptionStatus netStatus) : this(status, message, "", netStatus, "", "") { }
        public ApiException(int status, string message, string code, string minVersion, string downloadUrl)
            : this(status, message, code, WebExceptionStatus.Success, minVersion, downloadUrl) { }

        private ApiException(int status, string message, string code, WebExceptionStatus netStatus, string minVersion, string downloadUrl)
            : base(message)
        {
            Status = status;
            Code = code ?? "";
            NetStatus = netStatus;
            MinVersion = minVersion ?? "";
            DownloadUrl = downloadUrl ?? "";
        }
    }

    /// <summary>Чтение JSON без сторонних библиотек: словари и массивы JavaScriptSerializer.</summary>
    internal static class J
    {
        public static Dictionary<string, object> Parse(string text)
        {
            JavaScriptSerializer ser = new JavaScriptSerializer();
            ser.MaxJsonLength = int.MaxValue;
            return ser.DeserializeObject(text) as Dictionary<string, object>;
        }

        public static string Quote(string value)
        {
            return new JavaScriptSerializer().Serialize(value ?? "");
        }

        public static object Raw(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v)) return null;
            return v;
        }

        public static string Str(Dictionary<string, object> d, string key)
        {
            object v = Raw(d, key);
            return v == null ? "" : Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static int Int(Dictionary<string, object> d, string key)
        {
            object v = Raw(d, key);
            if (v == null) return 0;
            try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); }
            catch (Exception) { return 0; }
        }

        public static Dictionary<string, object> Obj(Dictionary<string, object> d, string key)
        {
            return Raw(d, key) as Dictionary<string, object>;
        }

        public static object[] Arr(Dictionary<string, object> d, string key)
        {
            return Raw(d, key) as object[] ?? new object[0];
        }

        public static bool Bool(Dictionary<string, object> d, string key)
        {
            object v = Raw(d, key);
            if (v is bool) return (bool)v;
            return false;
        }

        /// <summary>Целое или null, если поля нет, оно null или не число.</summary>
        public static int? IntOrNull(Dictionary<string, object> d, string key)
        {
            object v = Raw(d, key);
            if (v == null || v is string || v is bool) return null;
            try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); }
            catch (Exception) { return null; }
        }

        /// <summary>Дата ISO в местном времени или null.</summary>
        public static DateTime? Date(Dictionary<string, object> d, string key)
        {
            string s = Str(d, key);
            if (s == "") return null;
            try
            {
                return DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToLocalTime();
            }
            catch (Exception) { return null; }
        }
    }

    /// <summary>
    /// Настройки в %APPDATA%\NormaCAD\kompas.cfg. Пароль не хранится вовсе:
    /// на диске лежит только токен входа, зашифрованный DPAPI под учётную запись Windows.
    /// Старый вход (sid=) не переносится — расширение попросит войти заново: сервер больше
    /// не принимает номер сессии как токен.
    /// </summary>
    internal class Settings
    {
        public const string DefaultServer = "https://normacad.pro";

        public string Server = DefaultServer;
        public string Email = "";
        public string Domain = "eskd";
        public string Token = "";
        public bool TopMost = true;
        public bool DarkTheme = false;

        // Прокси: пусто — системный (с учётными данными Windows); пароль на диске под DPAPI, как токен
        public string ProxyAddress = "";
        public string ProxyUser = "";
        public string ProxyPassword = "";

        // Незавершённая проверка: задание отправлено, а ответа окно не дождалось (обрыв связи, закрытие)
        public string PendingJob = "";
        public string PendingFile = "";
        public DateTime PendingSince = DateTime.MinValue;

        private static string FilePath
        {
            get
            {
                // Переопределение пути — для проверки на локальном стенде, чтобы не трогать рабочие настройки
                string custom = Environment.GetEnvironmentVariable("NORMACAD_KOMPAS_CFG");
                if (!string.IsNullOrEmpty(custom)) return custom;
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "NormaCAD", "kompas.cfg");
            }
        }

        public static Settings Load()
        {
            Settings s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                foreach (string line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string value = line.Substring(eq + 1).Trim();
                    if (key == "server" && value != "") s.Server = value;
                    else if (key == "email") s.Email = value;
                    else if (key == "domain" && (value == "eskd" || value == "spds")) s.Domain = value;
                    else if (key == "topmost") s.TopMost = value != "0";
                    else if (key == "theme") s.DarkTheme = value == "dark";
                    else if (key == "token") s.Token = Unprotect(value);
                    else if (key == "proxy") s.ProxyAddress = value;
                    else if (key == "proxy_user") s.ProxyUser = value;
                    else if (key == "proxy_pass") s.ProxyPassword = Unprotect(value);
                    else if (key == "job") s.PendingJob = value;
                    else if (key == "job_file") s.PendingFile = value;
                    else if (key == "job_since") s.PendingSince = ParseSince(value);
                    // Ключ sid= (вход прежних версий) пропускаем: сервер его уже не примет
                }
            }
            catch (Exception)
            {
                // Битый файл настроек — не повод не запускаться: работаем с умолчаниями
            }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllLines(FilePath, new string[]
                {
                    "server=" + Server,
                    "email=" + Email,
                    "domain=" + Domain,
                    "topmost=" + (TopMost ? "1" : "0"),
                    "theme=" + (DarkTheme ? "dark" : "light"),
                    "token=" + Protect(Token),
                    "proxy=" + ProxyAddress,
                    "proxy_user=" + ProxyUser,
                    "proxy_pass=" + Protect(ProxyPassword),
                    "job=" + PendingJob,
                    "job_file=" + PendingFile,
                    "job_since=" + (PendingSince == DateTime.MinValue ? "" : PendingSince.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)),
                }, Encoding.UTF8);
            }
            catch (Exception)
            {
                // Не сохранились — в следующий раз спросим вход заново
            }
        }

        /// <summary>Забыть незавершённую проверку и сразу сохранить настройки.</summary>
        public void ClearPending()
        {
            PendingJob = "";
            PendingFile = "";
            PendingSince = DateTime.MinValue;
            Save();
        }

        private static DateTime ParseSince(string value)
        {
            if (value == "") return DateTime.MinValue;
            try
            {
                return DateTime.Parse(value, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
            }
            catch (Exception) { return DateTime.MinValue; }
        }

        private static string Protect(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            byte[] data = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(data);
        }

        private static string Unprotect(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            try
            {
                byte[] data = ProtectedData.Unprotect(
                    Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(data);
            }
            catch (Exception)
            {
                return "";
            }
        }
    }

    /// <summary>Сведения об учётной записи и доступе — одна строка для окна расширения.</summary>
    internal class AccountInfo
    {
        public string Email = "";
        public string FullName = "";
        public string Plan = "";
        public string PlanLabel = "";
        public string Organization = "";
        public bool Limited;
        public bool Expired;
        public bool CanCheck;
        public DateTime? AccessUntil;
        public int? ChecksLimit;
        public int? Remaining;
        public int WindowHours;
        public int Used;
        public string Message = "";

        /// <summary>me — ответ /api/auth/me, access — ответ /api/users/me/access; любой может быть null.</summary>
        public static AccountInfo FromJson(Dictionary<string, object> me, Dictionary<string, object> access)
        {
            AccountInfo a = new AccountInfo();
            a.Email = J.Str(me, "email");
            a.FullName = J.Str(me, "full_name");
            a.Plan = J.Str(access, "plan");
            a.PlanLabel = J.Str(access, "plan_label");
            a.Organization = J.Str(access, "organization");
            a.Limited = J.Bool(access, "limited");
            a.Expired = J.Bool(access, "expired");
            // Нет сведений о доступе — не запрещаем проверку заранее, окончательно решает сервер
            a.CanCheck = J.Raw(access, "can_check") == null ? true : J.Bool(access, "can_check");
            a.AccessUntil = J.Date(access, "access_until");
            a.ChecksLimit = J.IntOrNull(access, "checks_limit");
            a.Remaining = J.IntOrNull(access, "remaining");
            a.WindowHours = J.Int(access, "window_hours");
            a.Used = J.Int(access, "used");
            a.Message = J.Str(access, "message");
            return a;
        }

        public string Summary()
        {
            List<string> parts = new List<string>();
            if (Email != "") parts.Add(Email);
            else if (FullName != "") parts.Add(FullName);

            string until = AccessUntil.HasValue
                ? AccessUntil.Value.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) : "";

            if (Expired)
            {
                parts.Add("срок доступа истёк" + (until != "" ? " " + until : "")
                    + " — новые проверки недоступны");
                return string.Join(" · ", parts.ToArray());
            }

            bool any = false;
            if (Limited || until != "")
            {
                string label = PlanLabel != "" ? PlanLabel : "Доступ";
                parts.Add(label + (until != "" ? " до " + until : ""));
                any = true;
            }
            if (ChecksLimit.HasValue)
            {
                int left = Remaining.HasValue ? Remaining.Value : Math.Max(0, ChecksLimit.Value - Used);
                if (left <= 0)
                    parts.Add("лимит проверок исчерпан");
                else
                    parts.Add("осталось " + left.ToString(CultureInfo.InvariantCulture)
                        + " из " + ChecksLimit.Value.ToString(CultureInfo.InvariantCulture) + " проверок"
                        + (WindowHours > 0 ? " за " + WindowHours.ToString(CultureInfo.InvariantCulture) + " ч" : ""));
                any = true;
            }
            if (!any) parts.Add("без ограничений");
            return string.Join(" · ", parts.ToArray());
        }
    }

    /// <summary>
    /// Что окно предлагает сделать при сбое: подпись и действие кнопки. ContactTeam — страница «Контакты»
    /// на сайте (лимит, истёкший доступ), GetResult — повторный опрос задания после обрыва связи.
    /// </summary>
    internal enum FailureAction { None, Retry, ProxySettings, OpenSite, Download, Login, ChangeServer, ContactTeam, GetResult }

    /// <summary>
    /// Сбой, объяснённый человеческим языком: ключ вида, одна понятная фраза, кнопка действия.
    /// Тексты простые, без кодов ошибок и английских сообщений .NET.
    /// </summary>
    internal class Failure
    {
        public string Kind = "";
        public string Text = "";
        public FailureAction Action = FailureAction.None;
        public string ActionLabel = "";
        public string Url = "";

        // Есть ли сеть вообще; подменяется в проверках
        internal static Func<bool> NetworkAvailable = delegate ()
        {
            try { return NetworkInterface.GetIsNetworkAvailable(); }
            catch (Exception) { return true; }
        };

        private static Failure Make(string kind, string text, FailureAction action, string url)
        {
            Failure f = new Failure();
            f.Kind = kind;
            f.Text = text;
            f.Action = action;
            f.Url = url ?? "";
            switch (action)
            {
                case FailureAction.Retry: f.ActionLabel = "Повторить"; break;
                case FailureAction.ProxySettings: f.ActionLabel = "Настройки прокси"; break;
                case FailureAction.OpenSite: f.ActionLabel = "Открыть сайт"; break;
                case FailureAction.Download: f.ActionLabel = "Скачать новую версию"; break;
                case FailureAction.Login: f.ActionLabel = "Войти"; break;
                case FailureAction.ChangeServer: f.ActionLabel = "Изменить адрес сервера"; break;
                case FailureAction.ContactTeam: f.ActionLabel = "Связаться с командой"; break;
                case FailureAction.GetResult: f.ActionLabel = "Получить результат"; break;
            }
            return f;
        }

        private static Failure NoInternet()
        {
            return Make("no_internet", "Нет подключения к интернету. Проверьте сеть и повторите.", FailureAction.Retry, "");
        }

        /// <summary>Разобрать исключение обмена. duringCheck — сбой случился, пока шла проверка чертежа.</summary>
        public static Failure Classify(Exception ex, bool duringCheck, string baseUrl)
        {
            string site = (baseUrl ?? "").TrimEnd('/');
            WebException web = ex as WebException;
            if (web != null && web.Response == null)
                ex = new ApiException(0, web.Message, web.Status);

            ApiException api = ex as ApiException;
            if (api == null)
            {
                string msg = ex == null ? "" : ex.Message;
                return Make("refused", msg == "" ? "Не удалось выполнить действие." : msg, FailureAction.None, "");
            }

            if (api.Status == 0)
            {
                switch (api.NetStatus)
                {
                    case WebExceptionStatus.NameResolutionFailure:
                        if (!NetworkAvailable()) return NoInternet();
                        return Make("bad_address", "Сервер с таким адресом не найден. Проверьте адрес сервера.",
                            FailureAction.ChangeServer, "");
                    case WebExceptionStatus.ProxyNameResolutionFailure:
                        return Make("proxy", "Не удаётся найти прокси-сервер. Проверьте настройки прокси.",
                            FailureAction.ProxySettings, "");
                    case WebExceptionStatus.SecureChannelFailure:
                    case WebExceptionStatus.TrustFailure:
                        return Make("tls", "Не удалось установить защищённое соединение: возможно, сертификат сайта подменяет антивирус или прокси. Проверьте настройки прокси или отключите проверку HTTPS в антивирусе.",
                            FailureAction.ProxySettings, "");
                    case WebExceptionStatus.Timeout:
                        return Make("timeout", "Сервер долго не отвечает. Повторите попытку.", FailureAction.Retry, "");
                    case WebExceptionStatus.ConnectFailure:
                        if (!NetworkAvailable()) return NoInternet();
                        return Make("server_down", "Сервер не отвечает. Повторите позже.", FailureAction.Retry, "");
                    case WebExceptionStatus.Success:
                        // Сервер ответил, но не то, что нужно (например, без токена): у него свой текст
                        return Make("refused", api.Message, FailureAction.None, "");
                }
                if (duringCheck)
                    return Make("interrupted", "Связь прервалась во время проверки. Разбор продолжается на сервере — окно продолжит, когда связь вернётся.",
                        FailureAction.GetResult, "");
                if (!NetworkAvailable()) return NoInternet();
                return Make("server_down", "Сервер не отвечает. Повторите позже.", FailureAction.Retry, "");
            }

            if (api.Status == 407)
                return Make("proxy_auth", "Прокси требует вход. Укажите имя и пароль прокси в настройках.",
                    FailureAction.ProxySettings, "");
            if (api.Status == 426)
                return Make("outdated", "Эта версия расширения устарела. Скачайте новую версию.",
                    FailureAction.Download, api.DownloadUrl != "" ? api.DownloadUrl : site);
            if (api.Status == 429)
            {
                // Текст лимита — дословно от сервера: в нём и лимит, и когда освободится следующая проверка
                if (api.Code == "checks_limit")
                    return Make("limit", api.Message, FailureAction.ContactTeam, site + "/contacts");
                return Make("rate", "Слишком много запросов. Подождите минуту и повторите.", FailureAction.Retry, "");
            }
            if (api.Status == 403 && api.Code == "trial_expired")
                return Make("expired", "Срок доступа истёк: новые проверки недоступны, история и отчёты на сайте открываются.",
                    FailureAction.ContactTeam, site + "/contacts");
            if (api.Status == 401)
                return Make("unauthorized", "Вход истёк или закрыт. Войдите снова.", FailureAction.Login, "");
            if (api.Status == 502 || api.Status == 503 || api.Status == 504)
                return Make("server_down", "Сервер временно недоступен. Повторите позже.", FailureAction.Retry, "");
            if (api.Status >= 500)
                return Make("server_error", "На сервере произошёл сбой. Повторите позже.", FailureAction.Retry, "");
            return Make("refused", api.Message, FailureAction.None, "");
        }

        /// <summary>Записать сбой в журнал поддержки (вид и текст, без секретов).</summary>
        public void Log(string where)
        {
            SupportLog.Write("сбой [" + Kind + "] " + (where ?? "") + ": " + Text);
        }
    }

    /// <summary>
    /// Прокси для всех запросов. Пусто в настройках — системный прокси Windows; учётные данные —
    /// указанные имя и пароль, иначе текущие данные входа в Windows (NTLM/Kerberos корпоративного прокси).
    /// </summary>
    internal static class ProxySetup
    {
        private static IWebProxy current;
        private static readonly object sync = new object();

        public static IWebProxy Current
        {
            get
            {
                lock (sync)
                {
                    if (current == null) current = Build("", "", "");
                    return current;
                }
            }
        }

        public static void Apply(Settings cfg)
        {
            IWebProxy built = cfg == null ? Build("", "", "") : Build(cfg.ProxyAddress, cfg.ProxyUser, cfg.ProxyPassword);
            lock (sync) current = built;
            bool manual = cfg != null && !string.IsNullOrEmpty(cfg.ProxyAddress);
            SupportLog.Write("прокси: " + (manual ? "задан вручную" : "системный")
                + (cfg != null && !string.IsNullOrEmpty(cfg.ProxyUser) ? ", с именем и паролем" : ", учётные данные Windows"));
        }

        private static IWebProxy Build(string address, string user, string password)
        {
            IWebProxy proxy = null;
            if (!string.IsNullOrEmpty(address))
            {
                try
                {
                    string a = address.Trim();
                    if (a.IndexOf("://", StringComparison.Ordinal) < 0) a = "http://" + a;
                    WebProxy w = new WebProxy(a);
                    w.BypassProxyOnLocal = true;
                    proxy = w;
                }
                catch (Exception)
                {
                    proxy = null;
                }
            }
            if (proxy == null)
            {
                try { proxy = WebRequest.GetSystemWebProxy(); }
                catch (Exception) { proxy = null; }
            }
            if (proxy == null) return null;
            if (!string.IsNullOrEmpty(user))
                proxy.Credentials = new NetworkCredential(user, password ?? "");
            else
                proxy.Credentials = CredentialCache.DefaultCredentials;
            return proxy;
        }
    }

    /// <summary>
    /// Журнал для поддержки: %LOCALAPPDATA%\NormaCAD\kompas.log (путь можно переопределить
    /// переменной NORMACAD_KOMPAS_LOG). Секреты вырезаются при записи; запись никогда не бросает исключений.
    /// </summary>
    internal static class SupportLog
    {
        // Порог ротации; в проверках уменьшается
        internal static long MaxBytes = 512 * 1024;
        private const int Generations = 3;
        private static readonly object sync = new object();
        internal static bool SessionStarted;

        public static string Path
        {
            get
            {
                string custom = Environment.GetEnvironmentVariable("NORMACAD_KOMPAS_LOG");
                if (!string.IsNullOrEmpty(custom)) return custom;
                return System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "NormaCAD", "kompas.log");
            }
        }

        public static void Write(string text)
        {
            try
            {
                lock (sync)
                {
                    string path = Path;
                    string dir = System.IO.Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    Rotate(path);
                    string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " ";
                    string lines = "";
                    if (!SessionStarted)
                    {
                        SessionStarted = true;
                        lines = stamp + "=== сеанс: NormaCAD-Kompas/" + ApiClient.ClientVersion
                            + ", " + Environment.OSVersion.VersionString + ", .NET " + Environment.Version + " ===\r\n";
                    }
                    lines += stamp + Mask(text) + "\r\n";
                    File.AppendAllText(path, lines, Encoding.UTF8);
                }
            }
            catch (Exception)
            {
                // Журнал — помощь поддержке, а не повод сломать работу
            }
        }

        private static void Rotate(string path)
        {
            FileInfo info = new FileInfo(path);
            if (!info.Exists || info.Length <= MaxBytes) return;
            string oldest = path + "." + Generations;
            if (File.Exists(oldest)) File.Delete(oldest);
            for (int i = Generations - 1; i >= 1; i--)
            {
                string from = path + "." + i;
                if (File.Exists(from)) File.Move(from, path + "." + (i + 1));
            }
            File.Move(path, path + ".1");
        }

        private static readonly Regex[] Secrets = new Regex[]
        {
            // Authorization: Bearer <токен>
            new Regex(@"(Bearer\s+)[^\s"",;']+", RegexOptions.IgnoreCase),
            // "password":"…", "token":"…", "access_token":"…"
            new Regex(@"(""[A-Za-z_]*(?:password|token)""\s*:\s*"")(?:[^""\\]|\\.)*("")", RegexOptions.IgnoreCase),
            // token=…, sid=…, ncad_sid=…, proxy_pass=…, password=…
            new Regex(@"((?:ncad_)?sid|token|proxy_pass|password)=[^\s&;"",']+", RegexOptions.IgnoreCase),
            // пароль в адресе прокси: http://user:пароль@host
            new Regex(@"(://[^/\s:@]+:)[^/\s@]+(@)"),
        };

        /// <summary>Вырезать из текста токены, пароли и номера сессий.</summary>
        public static string Mask(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string r = text;
            r = Secrets[0].Replace(r, "$1***");
            r = Secrets[1].Replace(r, "$1***$2");
            r = Secrets[2].Replace(r, "$1=***");
            r = Secrets[3].Replace(r, "$1***$2");
            return r;
        }
    }

    /// <summary>Клиент /api/auth и /api/drawings. Авторизация — токен в заголовке Authorization: Bearer.</summary>
    internal class ApiClient
    {
        public const string ClientVersion = "0.1.1";
        public const long MaxFileSize = 50L * 1024 * 1024;
        private const string UserAgent = "NormaCAD-Kompas/" + ClientVersion + " (Windows; KOMPAS-3D)";

        public string BaseUrl = Settings.DefaultServer;
        public string Token = "";

        static ApiClient()
        {
            // Старым версиям .NET Framework TLS 1.2 нужно включать явно. Значение 0
            // (SystemDefault) не трогаем: там протокол выбирает сама Windows.
            try
            {
                if (ServicePointManager.SecurityProtocol != 0)
                    ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            }
            catch (Exception) { }
        }

        /// <summary>Пароль по открытому каналу не отправляем: http допустим только для своего компьютера.</summary>
        public static string NormalizeServer(string server)
        {
            string s = (server ?? "").Trim().TrimEnd('/');
            if (s == "") s = Settings.DefaultServer;
            if (!s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                s = "https://" + s;
            Uri uri;
            if (!Uri.TryCreate(s, UriKind.Absolute, out uri))
                throw new UserException("Адрес сервера указан неверно.");
            if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
                throw new UserException("Адрес сервера должен начинаться с https://");
            return s;
        }

        /// <summary>Ссылка «Забыли пароль?» на сайте выбранного сервера.</summary>
        public static string ForgotPasswordUrl(string server)
        {
            return NormalizeServer(server) + "/forgot-password";
        }

        /// <summary>Вход по email и паролю. Возвращает сведения о пользователе, токен остаётся в Token.</summary>
        public Dictionary<string, object> Login(string email, string password)
        {
            HttpWebRequest req = NewRequest("POST", "/api/auth/token", false);
            req.Timeout = 30000;
            string body = "{\"email\":" + J.Quote(email) + ",\"password\":" + J.Quote(password) + "}";
            Dictionary<string, object> json;
            try
            {
                json = J.Parse(Execute(req, Encoding.UTF8.GetBytes(body), "application/json"));
            }
            catch (ApiException)
            {
                SupportLog.Write("вход не удался: " + email);
                throw;
            }
            string token = J.Str(json, "token");
            if (token == "")
            {
                SupportLog.Write("вход не удался (сервер не выдал токен): " + email);
                throw new ApiException(0, "Сервер не выдал токен входа. Проверьте адрес сервера.");
            }
            Token = token;
            SupportLog.Write("вход: " + email);
            return J.Obj(json, "user");
        }

        /// <summary>
        /// Выход: токен очищается всегда. true — сервер подтвердил отзыв токена или сообщил,
        /// что тот уже недействителен (401); false — до сервера не достучались или он отказал.
        /// </summary>
        public bool Logout()
        {
            if (string.IsNullOrEmpty(Token)) return true;
            bool revoked;
            try
            {
                HttpWebRequest req = NewRequest("POST", "/api/auth/logout", true);
                // Выход вызывается из окна: при недоступном сервере не ждём 100 с по умолчанию
                req.Timeout = 10000;
                Execute(req, new byte[0], "application/json");
                revoked = true;
            }
            catch (ApiException ex)
            {
                revoked = ex.Status == 401;
            }
            Token = "";
            SupportLog.Write("выход: токен " + (revoked ? "отозван" : "не удалось отозвать на сервере"));
            return revoked;
        }

        /// <summary>Кто вошёл и что ему доступно: /api/auth/me и /api/users/me/access.</summary>
        public AccountInfo GetAccount()
        {
            HttpWebRequest meReq = NewRequest("GET", "/api/auth/me", true);
            meReq.Timeout = 30000;
            Dictionary<string, object> me = J.Parse(Execute(meReq, null, null));
            HttpWebRequest accessReq = NewRequest("GET", "/api/users/me/access", true);
            accessReq.Timeout = 30000;
            Dictionary<string, object> access = J.Parse(Execute(accessReq, null, null));
            return AccountInfo.FromJson(me, access);
        }

        /// <summary>Понятный текст для окна входа; коды и служебные подробности не показываем.</summary>
        public static string LoginError(Exception ex)
        {
            ApiException api = ex as ApiException;
            if (api != null)
            {
                if (api.Status == 401)
                    return "Неверный email или пароль. Если доступа ещё нет — запросите приглашение (ссылка «Доступ по запросу»).";
                if (api.Status == 403)
                    return string.IsNullOrEmpty(api.Message) ? "Вход запрещён. Обратитесь к администратору." : api.Message;
                if (api.Status == 429)
                    return "Слишком много попыток входа. Подождите минуту и повторите.";
                if (api.Status == 0)
                    // Единственный «нулевой» отказ не из-за сети — сервер ответил, но без токена: у него свой текст
                    return api.Message.StartsWith("Сервер не выдал", StringComparison.Ordinal)
                        ? api.Message
                        : "Нет связи с сервером. Проверьте интернет и адрес сервера.";
            }
            return ex.Message;
        }

        /// <summary>Понятный текст отказа в проверке (после входа).</summary>
        public static string RefusalText(Exception ex)
        {
            ApiException api = ex as ApiException;
            if (api != null)
            {
                if (api.Code == "trial_expired")
                    return "Срок доступа истёк: новые проверки недоступны, история и отчёты на сайте открываются. Чтобы продлить доступ, свяжитесь с командой проекта.";
                if (api.Code == "checks_limit")
                    return api.Message;
                if (api.Status == 401)
                    return "Вход истёк или закрыт. Войдите снова.";
            }
            return ex.Message;
        }

        /// <summary>facts — что прочитано на ПК (#45), уйдёт в поле client_facts; null — данных с ПК нет.</summary>
        public Dictionary<string, object> SubmitJob(string filePath, string uploadName, string domain, LocalFacts facts)
        {
            string clientFacts = ClientFacts.Json(facts);
            try
            {
                return Submit(filePath, uploadName, domain, clientFacts);
            }
            catch (ApiException ex)
            {
                // Факты КОМПАС сервер не принял (422 про client_facts) — чертёж всё равно проверяем, без них
                if (clientFacts == "" || ex.Status != 422
                    || ex.Message.IndexOf("client_facts", StringComparison.Ordinal) < 0) throw;
                SupportLog.Write("сервер не принял client_facts (" + ex.Message + "), отправляю без них");
                return Submit(filePath, uploadName, domain, "");
            }
        }

        private Dictionary<string, object> Submit(string filePath, string uploadName, string domain, string clientFacts)
        {
            FileInfo info = new FileInfo(filePath);
            if (!info.Exists) throw new UserException("Файл экспорта не найден: " + filePath);
            if (info.Length > MaxFileSize)
                throw new UserException("Файл больше 50 МБ — сайт такой не принимает.");

            string boundary = "----NormaCadKompas" + Guid.NewGuid().ToString("N");
            byte[] body;
            using (MemoryStream ms = new MemoryStream())
            {
                WriteText(ms, "--" + boundary + "\r\n"
                    + "Content-Disposition: form-data; name=\"domain\"\r\n\r\n" + domain + "\r\n");
                // Тип документа и основную надпись сервер берёт из client_facts: их прочитал КОМПАС
                if (clientFacts != "")
                {
                    WriteText(ms, "--" + boundary + "\r\n"
                        + "Content-Disposition: form-data; name=\"client_facts\"\r\n\r\n" + clientFacts + "\r\n");
                    SupportLog.Write("client_facts: " + Encoding.UTF8.GetByteCount(clientFacts).ToString(CultureInfo.InvariantCulture) + " байт");
                }
                // model и doc_type не передаём: сервер берёт модель по умолчанию и сам определяет тип документа
                WriteText(ms, "--" + boundary + "\r\n"
                    + "Content-Disposition: form-data; name=\"file\"; filename=\""
                    + uploadName.Replace("\"", "'") + "\"\r\n"
                    + "Content-Type: application/octet-stream\r\n\r\n");
                byte[] file = File.ReadAllBytes(filePath);
                ms.Write(file, 0, file.Length);
                WriteText(ms, "\r\n--" + boundary + "--\r\n");
                body = ms.ToArray();
            }

            HttpWebRequest req = NewRequest("POST", "/api/drawings/jobs", true);
            req.Timeout = 300000;
            return J.Parse(Execute(req, body, "multipart/form-data; boundary=" + boundary));
        }

        public Dictionary<string, object> GetJob(string jobId)
        {
            HttpWebRequest req = NewRequest("GET", "/api/drawings/jobs/" + Uri.EscapeDataString(jobId), true);
            req.Timeout = 30000;
            return J.Parse(Execute(req, null, null));
        }

        /// <summary>
        /// Найти в истории проверку, отправленную с этого окна, если ответ на задание потерян
        /// (сервер перезапущен, задание забыто через 30 минут). Возвращает отчёт с полем "check_id" или null.
        /// </summary>
        public Dictionary<string, object> FindInHistory(string fileName, DateTime sinceUtc)
        {
            if (sinceUtc.Kind == DateTimeKind.Local) sinceUtc = sinceUtc.ToUniversalTime();
            DateTime threshold = sinceUtc.AddMinutes(-2);
            HttpWebRequest req = NewRequest("GET", "/api/drawings/history?q=" + Uri.EscapeDataString(fileName ?? "") + "&limit=10", true);
            req.Timeout = 30000;
            Dictionary<string, object> list = J.Parse(Execute(req, null, null));
            foreach (object item in J.Arr(list, "items"))
            {
                Dictionary<string, object> d = item as Dictionary<string, object>;
                if (d == null) continue;
                if (!string.Equals(J.Str(d, "filename"), fileName, StringComparison.OrdinalIgnoreCase)) continue;
                DateTime created;
                if (!DateTime.TryParse(J.Str(d, "created_at"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out created)) continue;
                if (created < threshold) continue;
                int? id = J.IntOrNull(d, "id");
                if (id == null) continue;
                HttpWebRequest detail = NewRequest("GET", "/api/drawings/history/" + id.Value.ToString(CultureInfo.InvariantCulture), true);
                detail.Timeout = 30000;
                Dictionary<string, object> report;
                try { report = J.Parse(Execute(detail, null, null)); }
                catch (ApiException e)
                {
                    if (e.Status == 404) return null;
                    throw;
                }
                if (report == null) return null;
                report["check_id"] = id.Value;
                SupportLog.Write("проверка найдена в истории: номер " + id.Value.ToString(CultureInfo.InvariantCulture));
                return report;
            }
            return null;
        }

        public string ReportUrl(int checkId)
        {
            return BaseUrl.TrimEnd('/') + "/history?open=" + checkId.ToString(CultureInfo.InvariantCulture);
        }

        internal HttpWebRequest NewRequest(string method, string path, bool withSession)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(BaseUrl.TrimEnd('/') + path);
            req.Method = method;
            req.UserAgent = UserAgent;
            req.Accept = "application/json";
            req.ReadWriteTimeout = 300000;
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            req.ServicePoint.Expect100Continue = false;
            req.Headers["X-NormaCAD-Client"] = "kompas/" + ClientVersion;
            req.Proxy = ProxySetup.Current;
            if (withSession && !string.IsNullOrEmpty(Token))
                req.Headers["Authorization"] = "Bearer " + Token;
            return req;
        }

        private string Execute(HttpWebRequest req, byte[] body, string contentType)
        {
            string what = req.Method + " " + req.RequestUri.AbsolutePath;
            Stopwatch clock = Stopwatch.StartNew();
            try
            {
                if (body != null)
                {
                    req.ContentType = contentType;
                    req.ContentLength = body.Length;
                    using (Stream s = req.GetRequestStream()) s.Write(body, 0, body.Length);
                }
                string answer;
                int ok;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    ok = (int)resp.StatusCode;
                    answer = ReadAll(resp);
                }
                SupportLog.Write(what + " -> " + ok.ToString(CultureInfo.InvariantCulture) + ", " + clock.ElapsedMilliseconds + " мс");
                return answer;
            }
            catch (WebException ex)
            {
                HttpWebResponse r = ex.Response as HttpWebResponse;
                ApiException result;
                if (r == null)
                {
                    result = new ApiException(0, "Нет связи с сервером " + BaseUrl + ": " + ex.Message, ex.Status);
                }
                else
                {
                    int code = (int)r.StatusCode;
                    string text = "";
                    try { text = ReadAll(r); } catch (Exception) { }
                    r.Close();
                    result = new ApiException(code, Detail(text, code), ErrorCode(text),
                        code == 426 ? BodyField(text, "min_version") : "", code == 426 ? BodyField(text, "download_url") : "");
                }
                LogFailed(what, clock, result);
                throw result;
            }
            catch (IOException ex)
            {
                // Обрыв при чтении ответа или записи тела
                SocketException sock = ex.InnerException as SocketException;
                WebExceptionStatus net = sock != null && sock.SocketErrorCode == SocketError.TimedOut
                    ? WebExceptionStatus.Timeout : WebExceptionStatus.ConnectionClosed;
                ApiException result = new ApiException(0, "Нет связи с сервером " + BaseUrl + ": " + ex.Message, net);
                LogFailed(what, clock, result);
                throw result;
            }
        }

        private void LogFailed(string what, Stopwatch clock, ApiException e)
        {
            string head = what + " -> " + (e.Status == 0 ? "нет ответа (" + e.NetStatus + ")" : e.Status.ToString(CultureInfo.InvariantCulture))
                + ", " + clock.ElapsedMilliseconds + " мс";
            if (e.Code != "") head += ", " + e.Code;
            SupportLog.Write(head);
            Failure.Classify(e, false, BaseUrl).Log(what);
        }

        private static string BodyField(string text, string key)
        {
            try { return J.Str(J.Parse(text), key); }
            catch (Exception) { return ""; }
        }

        /// <summary>Машинный код отказа ("code" в теле ответа) или пустая строка.</summary>
        internal static string ErrorCode(string text)
        {
            try { return J.Str(J.Parse(text), "code"); }
            catch (Exception) { return ""; }
        }

        private static string ReadAll(HttpWebResponse resp)
        {
            using (Stream s = resp.GetResponseStream())
            using (StreamReader reader = new StreamReader(s, Encoding.UTF8))
                return reader.ReadToEnd();
        }

        private static void WriteText(Stream s, string text)
        {
            byte[] b = Encoding.UTF8.GetBytes(text);
            s.Write(b, 0, b.Length);
        }

        /// <summary>Текст отказа: detail FastAPI (строка или список ошибок 422), иначе — по коду.</summary>
        private static string Detail(string text, int code)
        {
            try
            {
                Dictionary<string, object> d = J.Parse(text);
                object detail = J.Raw(d, "detail");
                if (detail is string && (string)detail != "") return (string)detail;
                object[] list = detail as object[];
                if (list != null && list.Length > 0)
                {
                    List<string> parts = new List<string>();
                    foreach (object item in list)
                    {
                        Dictionary<string, object> error = item as Dictionary<string, object>;
                        string msg = J.Str(error, "msg");
                        // FastAPI хранит имя поля в loc, а не в msg. Сохраняем путь,
                        // чтобы SubmitJob распознал отказ client_facts и повторил без них.
                        List<string> location = new List<string>();
                        foreach (object segment in J.Arr(error, "loc"))
                            location.Add(Convert.ToString(segment, CultureInfo.InvariantCulture));
                        if (msg != "") parts.Add(location.Count == 0 ? msg
                            : string.Join(".", location.ToArray()) + ": " + msg);
                    }
                    if (parts.Count > 0) return string.Join("; ", parts.ToArray());
                }
            }
            catch (Exception) { }

            if (code == 401) return "Требуется вход в NormaCAD.PRO.";
            if (code == 403) return "Вход запрещён. Обратитесь к администратору.";
            if (code == 413) return "Файл слишком большой.";
            if (code == 429) return "Слишком много запросов. Подождите минуту и повторите.";
            if (code == 502 || code == 503 || code == 504) return "Сервер проверки временно недоступен. Повторите позже.";
            return "Ошибка сервера (код " + code.ToString(CultureInfo.InvariantCulture) + ").";
        }
    }
}
