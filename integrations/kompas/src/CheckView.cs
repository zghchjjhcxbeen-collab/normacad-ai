// Что показывает окно проверки, без WinForms: этапы и очередь из ответа сервера, карточки
// отчёта и фильтры, итог, строки документа и аккаунта, данные с ПК. Проверяется в test\ViewTest.cs.
// Слова этапов и очереди — как на странице проверки сайта (public/assets/js/i18n.js, ключи queue.*,
// и queue-eta.js); тексты окна — как в макете integrations/kompas/mockup.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace NormaCad.Kompas
{
    /// <summary>Русские числительные, числа и регистр первой буквы.</summary>
    internal static class Ru
    {
        private static readonly CultureInfo Culture = new CultureInfo("ru-RU");

        /// <summary>Форма слова при числе: 1 проверка, 2 проверки, 5 проверок — как I18N.plural на сайте.</summary>
        public static string Plural(int n, string one, string few, string many)
        {
            int a = Math.Abs(n) % 100;
            int b = a % 10;
            if (b == 1 && a != 11) return one;
            if (b >= 2 && b <= 4 && (a < 12 || a > 14)) return few;
            return many;
        }

        public static string Num(int n)
        {
            return n.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Строчная первая буква: «Лист 1: не заполнена графа…». Аббревиатуру в начале («ГОСТ…», «ЕСКД…»)
        /// не трогаем — у неё и вторая буква заглавная.
        /// </summary>
        public static string LowerFirst(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            if (s.Length > 1 && char.IsUpper(s[1])) return s;
            return char.ToLower(s[0], Culture) + s.Substring(1);
        }
    }

    /// <summary>Время словами — как duration() в queue-eta.js. Всегда «примерно»: точных обещаний окно не даёт.</summary>
    internal static class Eta
    {
        /// <summary>«Примерно через 3 мин», «Примерно через 1 ч 5 мин», «Меньше минуты» — ожидание в очереди.</summary>
        public static string Wait(int seconds)
        {
            return Text(seconds, "Примерно через {0} мин", "Примерно через {0} ч {1} мин", "Меньше минуты");
        }

        /// <summary>«Осталось примерно 2 мин» — идёт проверка.</summary>
        public static string Left(int seconds)
        {
            return Text(seconds, "Осталось примерно {0} мин", "Осталось примерно {0} ч {1} мин", "Осталось меньше минуты");
        }

        /// <summary>«около 1 мин» — короткая метка у идущего этапа.</summary>
        public static string Stage(int seconds)
        {
            return Text(seconds, "около {0} мин", "около {0} ч {1} мин", "меньше минуты");
        }

        private static string Text(int seconds, string minutes, string hours, string less)
        {
            int s = Math.Max(0, seconds);
            if (s < 60) return less;
            int total = Math.Max(1, (int)Math.Round(s / 60.0, MidpointRounding.AwayFromZero));
            if (total < 60) return string.Format(CultureInfo.InvariantCulture, minutes, total);
            return string.Format(CultureInfo.InvariantCulture, hours, total / 60, total % 60);
        }
    }

    internal enum StageKind { Done, Running, Waiting }

    /// <summary>Строка списка этапов: название слева, состояние справа («готово», «идёт, около 1 мин», «ждёт»).</summary>
    internal class StageRow
    {
        public string Key = "";
        public string Name = "";
        public string State = "";
        public StageKind Kind = StageKind.Waiting;
    }

    /// <summary>
    /// Ход проверки из ответа GET /api/drawings/jobs/{id}: этапы, место в очереди и время.
    /// Числа считает сервер (stages, queue_ahead, eta_seconds, heavy_load, pages_done, pages_total), здесь — только слова.
    /// </summary>
    internal class JobView
    {
        private static readonly string[] Order = { "receive", "normalize", "standards", "vision", "verify" };

        /// <summary>Названия этапов по ключу — как на сайте; подпись сервера — запасная (у него «Vision-анализ»).</summary>
        private static readonly Dictionary<string, string> Names = new Dictionary<string, string>
        {
            { "receive", "Приём файла" },
            { "normalize", "Нормализация формата" },
            { "standards", "Поиск по базе ГОСТ" },
            { "vision", "Анализ чертежа моделью" },
            { "verify", "Проверка достоверности" },
        };

        public string Status = "";
        public int Percent;
        public int Ahead;
        public bool Heavy;
        public readonly List<StageRow> Stages = new List<StageRow>();

        /// <summary>Строки об очереди и времени: «Вы в очереди…» и «Примерно через…» или «Проверка идёт. Осталось…».</summary>
        public readonly List<string> Lines = new List<string>();

        /// <summary>Серая строка под ними: нагрузка серверов или «Время примерное…»; пусто — сказать нечего.</summary>
        public string Hint = "";

        public bool Running { get { return Status == "running"; } }

        public static string StageName(string key, string label)
        {
            string name;
            if (key != null && Names.TryGetValue(key, out name)) return name;
            return string.IsNullOrEmpty(label) ? (key ?? "") : label;
        }

        /// <summary>Пока сервер не ответил на отправку: файл передаётся, остальные этапы ждут.</summary>
        public static JobView Sending()
        {
            JobView v = new JobView();
            v.Status = "sending";
            foreach (string key in Order)
            {
                StageRow row = new StageRow();
                row.Key = key;
                row.Name = Names[key];
                row.Kind = key == "receive" ? StageKind.Running : StageKind.Waiting;
                row.State = key == "receive" ? "идёт" : "ждёт";
                v.Stages.Add(row);
            }
            return v;
        }

        public static JobView From(Dictionary<string, object> job)
        {
            JobView v = new JobView();
            v.Status = J.Str(job, "status");
            v.Percent = Math.Max(0, Math.Min(100, J.Int(job, "progress")));
            v.Ahead = Math.Max(0, J.Int(job, "queue_ahead"));
            v.Heavy = J.Bool(job, "heavy_load");
            int pagesTotal = J.Int(job, "pages_total");
            int pagesDone = J.Int(job, "pages_done");

            foreach (object raw in J.Arr(job, "stages"))
            {
                Dictionary<string, object> s = raw as Dictionary<string, object>;
                if (s == null) continue;
                StageRow row = new StageRow();
                row.Key = J.Str(s, "key");
                row.Name = StageName(row.Key, J.Str(s, "label"));
                string state = J.Str(s, "state");
                if (state == "done")
                {
                    row.Kind = StageKind.Done;
                    row.State = "готово";
                }
                else if (state == "running")
                {
                    row.Kind = StageKind.Running;
                    // Многостраничный документ: страница, которую сейчас смотрит модель (queue.vision_page)
                    if (row.Key == "vision" && pagesTotal > 1)
                        row.Name += ": страница " + Ru.Num(Math.Min(pagesDone + 1, pagesTotal)) + " из " + Ru.Num(pagesTotal);
                    row.State = "идёт";
                    // Время у этапа — только у идущей проверки без очереди, как stageTime() на сайте
                    int? eta = J.IntOrNull(s, "eta_seconds");
                    if (v.Status == "running" && v.Ahead == 0 && eta.HasValue && eta.Value > 0)
                        row.State += ", " + Eta.Stage(eta.Value);
                }
                else
                {
                    row.Kind = StageKind.Waiting;
                    row.State = "ждёт";
                }
                v.Stages.Add(row);
            }

            // Место и время — только у задания в работе и только если сервер их посчитал (describe() на сайте)
            int? total = J.IntOrNull(job, "eta_seconds");
            if ((v.Status == "queued" || v.Status == "running") && total.HasValue)
            {
                if (v.Ahead > 0)
                {
                    v.Lines.Add(Ru.Plural(v.Ahead, "Вы в очереди: перед вами {0} проверка",
                        "Вы в очереди: перед вами {0} проверки", "Вы в очереди: перед вами {0} проверок")
                        .Replace("{0}", Ru.Num(v.Ahead)));
                    v.Lines.Add(Eta.Wait(total.Value));
                }
                else
                {
                    v.Lines.Add("Проверка идёт. " + Eta.Left(total.Value) + ".");
                }
                v.Hint = v.Heavy
                    ? "Сейчас серверы нагружены, проверка займёт больше обычного"
                    : "Время примерное: оно зависит от нагрузки и от самих чертежей.";
            }
            return v;
        }

        /// <summary>Задание закончено: отчёт получен.</summary>
        public static JobView Done()
        {
            JobView v = new JobView();
            v.Status = "done";
            v.Percent = 100;
            return v;
        }

        /// <summary>Строка статуса окна. withFacts — на ПК основная надпись уже проверена.</summary>
        public string StatusText(bool withFacts)
        {
            if (Status == "running") return "Идёт проверка на сервере.";
            if (Status == "done") return "Проверка завершена.";
            string pc = withFacts ? "Основная надпись проверена на ПК. " : "";
            if (Status == "sending") return pc + "Отправка на сервер…";
            return pc + "Чертёж отправлен на сервер.";
        }
    }

    /// <summary>Карточка отчёта: замечание сервера или ПК. Поля — как у замечания в отчёте сервера.</summary>
    internal class Card
    {
        /// <summary>critical | warning | ok.</summary>
        public string Severity = "warning";
        /// <summary>Лист документа; 0 — не указан (замечание ко всему многостраничному документу).</summary>
        public int Page;
        public bool FromPc;
        public string Title = "";
        public string Standard = "";
        public string Detail = "";
        public readonly List<string> Fixes = new List<string>();
        internal int Order;

        public string SeverityLabel
        {
            get { return Severity == "critical" ? "Критическое" : Severity == "ok" ? "В норме" : "Замечание"; }
        }

        public string PageLabel { get { return Page > 0 ? "Лист " + Ru.Num(Page) : ""; } }

        public string SourceLabel { get { return FromPc ? "ПК" : "сервер"; } }

        /// <summary>Подпись перед способами: один — «Как исправить:», несколько — «Способы исправить:».</summary>
        public string FixesCaption { get { return Fixes.Count > 1 ? "Способы исправить:" : "Как исправить:"; } }
    }

    /// <summary>Сборка карточек: замечания сервера и замечания ПК (#45) в одном списке.</summary>
    internal static class Cards
    {
        internal static string Severity(string s)
        {
            return s == "critical" || s == "ok" ? s : "warning";
        }

        private static int Rank(string severity)
        {
            return severity == "critical" ? 0 : severity == "warning" ? 1 : 2;
        }

        /// <summary>
        /// report — отчёт сервера (может быть null), facts — данные с ПК (null или с ошибкой — без замечаний ПК).
        /// Порядок: по важности, затем по листу (без листа — в конце), при равных — сначала ПК; дальше — как пришло.
        /// </summary>
        public static List<Card> Build(Dictionary<string, object> report, LocalFacts facts)
        {
            List<Card> cards = new List<Card>();
            // У одиночного листа сервер страницу не пишет: это лист 1
            bool single = J.Int(report, "pages_total") <= 1;
            foreach (object raw in J.Arr(report, "violations"))
            {
                Dictionary<string, object> v = raw as Dictionary<string, object>;
                if (v == null) continue;
                Card c = new Card();
                c.Severity = Severity(J.Str(v, "severity"));
                int? page = J.IntOrNull(v, "page");
                c.Page = page.HasValue && page.Value > 0 ? page.Value : (single ? 1 : 0);
                c.Title = J.Str(v, "title").Trim();
                c.Standard = J.Str(v, "standard").Trim();
                c.Detail = J.Str(v, "detail").Trim();
                foreach (object f in J.Arr(v, "fixes"))
                {
                    string fix = Convert.ToString(f, CultureInfo.InvariantCulture);
                    if (fix != null && fix.Trim() != "") c.Fixes.Add(fix.Trim());
                }
                cards.Add(c);
            }

            if (facts != null && string.IsNullOrEmpty(facts.Error) && facts.Remarks != null)
            {
                foreach (LocalRemark r in facts.Remarks)
                {
                    if (r == null) continue;
                    Card c = new Card();
                    c.FromPc = true;
                    c.Severity = Severity(r.Severity);
                    c.Page = Math.Max(0, r.Page);
                    c.Title = (r.Title ?? "").Trim();
                    c.Standard = (r.Standard ?? "").Trim();
                    c.Detail = (r.Detail ?? "").Trim();
                    if (r.Fixes != null)
                        foreach (string fix in r.Fixes)
                            if (fix != null && fix.Trim() != "") c.Fixes.Add(fix.Trim());
                    cards.Add(c);
                }
            }

            for (int i = 0; i < cards.Count; i++) cards[i].Order = i;
            cards.Sort(Compare);
            return cards;
        }

        private static int Compare(Card a, Card b)
        {
            int d = Rank(a.Severity).CompareTo(Rank(b.Severity));
            if (d != 0) return d;
            d = PageKey(a.Page).CompareTo(PageKey(b.Page));
            if (d != 0) return d;
            d = (a.FromPc ? 0 : 1).CompareTo(b.FromPc ? 0 : 1);
            if (d != 0) return d;
            return a.Order.CompareTo(b.Order);
        }

        private static int PageKey(int page)
        {
            return page > 0 ? page : int.MaxValue;
        }

        /// <summary>Сколько карточек каждой важности: [критических, замечаний, в норме].</summary>
        public static int[] Count(List<Card> cards)
        {
            int[] n = new int[3];
            if (cards == null) return n;
            foreach (Card c in cards) n[Rank(c.Severity)]++;
            return n;
        }

        /// <summary>
        /// Итог над карточками: «Требует доработки. Критических: 1, замечаний: 4, в норме: 1.» Считает и замечания ПК.
        /// Отказ (лист не чертёж, формат не поддерживается) — вердикт сервера как есть: это не оценка.
        /// </summary>
        public static string Verdict(string serverVerdict, List<Card> cards, bool refusal)
        {
            if (refusal) return (serverVerdict ?? "").Trim();
            if (cards == null || cards.Count == 0) return "Соответствует требованиям. Замечаний нет.";
            int[] n = Count(cards);
            string head = n[0] + n[1] > 0 ? "Требует доработки." : "Соответствует требованиям.";
            return head + " Критических: " + Ru.Num(n[0]) + ", замечаний: " + Ru.Num(n[1]) + ", в норме: " + Ru.Num(n[2]) + ".";
        }

        /// <summary>
        /// Отказ без проверки: модель не признала документ чертежом («не является чертежом») или формат не
        /// поддерживается. Нарушений нет, балл 0 — это не оценка. То же правило, что is_refusal на сервере
        /// и checkOutcome на сайте.
        /// </summary>
        public static bool IsRefusal(string verdict, int violations)
        {
            string low = (verdict ?? "").ToLowerInvariant();
            return violations == 0 && (low.Contains("не являет") || low.Contains("не поддерживается"));
        }

        /// <summary>
        /// Строки под итогом — только когда есть что сказать: предупреждение экспорта, заглушка вместо ИИ,
        /// проверены не все страницы.
        /// </summary>
        public static List<string> Notes(Dictionary<string, object> report, string exportWarning)
        {
            List<string> notes = new List<string>();
            if (!string.IsNullOrEmpty(exportWarning)) notes.Add(exportWarning.Trim());
            if (J.Str(report, "source") == "stub")
                notes.Add("ИИ на сервере не подключён: это ответ-заглушка, а не разбор чертежа.");
            int total = J.Int(report, "pages_total");
            int done = J.Int(report, "pages_checked");
            if (total > 1 && done < total)
                notes.Add("Проверено страниц: " + Ru.Num(done) + " из " + Ru.Num(total) + ".");
            return notes;
        }
    }

    /// <summary>Пункт выпадающего списка фильтра: подпись и значение.</summary>
    internal class Choice
    {
        public readonly string Label;
        public readonly string Value;

        public Choice(string label, string value)
        {
            Label = label;
            Value = value;
        }

        public override string ToString()
        {
            return Label;
        }
    }

    /// <summary>Фильтры отчёта «Важность», «Лист», «Источник». Пустое значение — «все».</summary>
    internal class CardFilter
    {
        public string Severity = "";
        public string Page = "";
        public string Source = "";

        public const string All = "все";

        public bool Match(Card c)
        {
            if (Severity != "" && c.Severity != Severity) return false;
            if (Page != "" && Ru.Num(c.Page) != Page) return false;
            if (Source == "pc" && !c.FromPc) return false;
            if (Source == "server" && c.FromPc) return false;
            return true;
        }

        public List<Card> Apply(List<Card> cards)
        {
            List<Card> shown = new List<Card>();
            if (cards == null) return shown;
            foreach (Card c in cards) if (Match(c)) shown.Add(c);
            return shown;
        }

        public static string Counter(int shown, int total)
        {
            return "Показано: " + Ru.Num(shown) + " из " + Ru.Num(total);
        }

        /// <summary>Пункты «Важность»: «все» и только те, что есть в отчёте.</summary>
        public static List<Choice> SeverityChoices(List<Card> cards)
        {
            List<Choice> list = new List<Choice>();
            list.Add(new Choice(All, ""));
            int[] n = Cards.Count(cards);
            if (n[0] > 0) list.Add(new Choice("критические", "critical"));
            if (n[1] > 0) list.Add(new Choice("замечания", "warning"));
            if (n[2] > 0) list.Add(new Choice("в норме", "ok"));
            return list;
        }

        /// <summary>Пункты «Лист»: «все» и номера листов из отчёта по порядку.</summary>
        public static List<Choice> PageChoices(List<Card> cards)
        {
            List<Choice> list = new List<Choice>();
            list.Add(new Choice(All, ""));
            SortedDictionary<int, bool> pages = new SortedDictionary<int, bool>();
            if (cards != null)
                foreach (Card c in cards) if (c.Page > 0) pages[c.Page] = true;
            foreach (int p in pages.Keys) list.Add(new Choice(Ru.Num(p), Ru.Num(p)));
            return list;
        }

        /// <summary>Пункты «Источник»: «все», «сервер», «ПК» — те, что есть.</summary>
        public static List<Choice> SourceChoices(List<Card> cards)
        {
            List<Choice> list = new List<Choice>();
            list.Add(new Choice(All, ""));
            bool server = false, pc = false;
            if (cards != null)
                foreach (Card c in cards)
                {
                    if (c.FromPc) pc = true; else server = true;
                }
            if (server) list.Add(new Choice("сервер", "server"));
            if (pc) list.Add(new Choice("ПК", "pc"));
            return list;
        }
    }

    /// <summary>Что прочитано на ПК (#45) — словами окна: группа «Проверено на ПК, без ИИ» и строка над ходом проверки.</summary>
    internal static class FactsText
    {
        /// <summary>Есть что показать: данные прочитаны без ошибки.</summary>
        public static bool Readable(LocalFacts f)
        {
            return f != null && string.IsNullOrEmpty(f.Error);
        }

        public static int RemarkCount(LocalFacts f)
        {
            if (!Readable(f) || f.Remarks == null) return 0;
            int n = 0;
            foreach (LocalRemark r in f.Remarks) if (r != null) n++;
            return n;
        }

        /// <summary>Строки «подпись — значение»: тип документа, обозначение, форматы листов. Пустые пропускаются.</summary>
        public static List<KeyValuePair<string, string>> Rows(LocalFacts f)
        {
            List<KeyValuePair<string, string>> rows = new List<KeyValuePair<string, string>>();
            if (!Readable(f)) return rows;
            if (!string.IsNullOrEmpty(f.DocTypeLabel)) rows.Add(new KeyValuePair<string, string>("Тип документа:", f.DocTypeLabel));
            if (!string.IsNullOrEmpty(f.Designation)) rows.Add(new KeyValuePair<string, string>("Обозначение:", f.Designation));
            List<LocalSheet> sheets = Sheets(f);
            if (sheets.Count == 1)
                rows.Add(new KeyValuePair<string, string>("Формат:", sheets[0].Format));
            else if (sheets.Count > 1)
            {
                List<string> parts = new List<string>();
                for (int i = 0; i < sheets.Count; i++)
                {
                    int n = sheets[i].Number > 0 ? sheets[i].Number : i + 1;
                    parts.Add("лист " + Ru.Num(n) + " — " + sheets[i].Format);
                }
                rows.Add(new KeyValuePair<string, string>("Форматы:", string.Join(", ", parts.ToArray())));
            }
            return rows;
        }

        private static List<LocalSheet> Sheets(LocalFacts f)
        {
            List<LocalSheet> list = new List<LocalSheet>();
            if (f == null || f.Sheets == null) return list;
            foreach (LocalSheet s in f.Sheets)
                if (s != null && !string.IsNullOrEmpty(s.Format)) list.Add(s);
            return list;
        }

        /// <summary>«Замечания по основной надписи: 3» или «Замечаний по основной надписи нет.»</summary>
        public static string RemarksHeader(LocalFacts f)
        {
            int n = RemarkCount(f);
            return n > 0 ? "Замечания по основной надписи: " + Ru.Num(n) : "Замечаний по основной надписи нет.";
        }

        /// <summary>«Лист 1: не заполнена графа «Масса».»</summary>
        public static string RemarkLine(LocalRemark r)
        {
            string title = (r.Title ?? "").Trim();
            return r.Page > 0 ? "Лист " + Ru.Num(r.Page) + ": " + Ru.LowerFirst(title) : title;
        }

        /// <summary>Свёрнутая группа над ходом проверки: «На ПК: 3 замечания по основной надписи.»</summary>
        public static string Collapsed(LocalFacts f)
        {
            int n = RemarkCount(f);
            if (n == 0) return "На ПК: замечаний по основной надписи нет.";
            return "На ПК: " + Ru.Num(n) + " " + Ru.Plural(n, "замечание", "замечания", "замечаний") + " по основной надписи.";
        }

        /// <summary>Чтение на ПК не удалось: «На ПК проверить не удалось: …».</summary>
        public static string Failed(LocalFacts f)
        {
            return "На ПК проверить не удалось: " + (f == null ? "" : (f.Error ?? "").Trim());
        }
    }

    /// <summary>Строка документа над областью проверки: «Корпус.cdw — чертёж, 2 листа (А3, А4)».</summary>
    internal static class DocText
    {
        public static string Line(string name, LocalFacts facts)
        {
            List<string> about = new List<string>();
            string kind = Kind(name, facts);
            if (kind != "") about.Add(kind);
            if (FactsText.Readable(facts) && facts.Sheets != null)
            {
                List<string> formats = new List<string>();
                int n = 0;
                foreach (LocalSheet s in facts.Sheets)
                {
                    if (s == null) continue;
                    n++;
                    if (!string.IsNullOrEmpty(s.Format) && !formats.Contains(s.Format)) formats.Add(s.Format);
                }
                // Листов в документе больше, чем прочитано (ограничение client_facts), — считаем по полному числу
                if (facts.SheetsTotal > n) n = facts.SheetsTotal;
                if (n > 0)
                    about.Add(Ru.Num(n) + " " + Ru.Plural(n, "лист", "листа", "листов")
                        + (formats.Count > 0 ? " (" + string.Join(", ", formats.ToArray()) + ")" : ""));
            }
            string head = (name ?? "").Trim();
            if (about.Count == 0) return head;
            return head + " — " + string.Join(", ", about.ToArray());
        }

        /// <summary>
        /// Вид документа по расширению файла КОМПАС; у неизвестного расширения (PDF после экспорта) — тип из данных ПК
        /// строчными буквами («чертёж детали»); пусто — неизвестно.
        /// </summary>
        public static string Kind(string name, LocalFacts facts)
        {
            string n = name ?? "";
            int dot = n.LastIndexOf('.');
            string ext = dot >= 0 ? n.Substring(dot).ToLowerInvariant() : "";
            switch (ext)
            {
                case ".cdw": return "чертёж";
                case ".frw": return "фрагмент";
                case ".spw": return "спецификация";
                case ".kdw": return "текстовый документ";
            }
            if (!FactsText.Readable(facts)) return "";
            return (facts.DocTypeLabel ?? "").Trim().ToLowerInvariant();
        }
    }

    /// <summary>Серая строка панели аккаунта: срок доступа и остаток проверок — отдельными частями.</summary>
    internal static class AccountText
    {
        public static List<string> Meta(AccountInfo a)
        {
            List<string> parts = new List<string>();
            string until = a.AccessUntil.HasValue
                ? a.AccessUntil.Value.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) : "";
            // «Тестовый доступ до 18.11.2026», у плана без слова «доступ» — «Доступ ПРО до …»
            string label = a.PlanLabel == "" ? "Доступ"
                : a.PlanLabel.IndexOf("доступ", StringComparison.OrdinalIgnoreCase) >= 0 ? a.PlanLabel
                : "Доступ " + a.PlanLabel;

            if (a.Expired)
            {
                parts.Add(label + " истёк" + (until != "" ? " " + until : ""));
                return parts;
            }
            if (a.Limited || until != "") parts.Add(label + (until != "" ? " до " + until : ""));
            if (a.ChecksLimit.HasValue)
            {
                int limit = a.ChecksLimit.Value;
                int left = Math.Max(0, a.Remaining.HasValue ? a.Remaining.Value : limit - a.Used);
                parts.Add("Осталось " + Ru.Num(left) + " из " + Ru.Num(limit) + " "
                    + Ru.Plural(limit, "проверки", "проверок", "проверок")
                    + (a.WindowHours > 0 ? " за " + Ru.Num(a.WindowHours) + " ч" : ""));
            }
            if (parts.Count == 0) parts.Add("Без ограничений");
            return parts;
        }
    }
}
