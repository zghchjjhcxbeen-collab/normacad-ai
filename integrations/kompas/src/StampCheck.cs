// Быстрые проверки основной надписи на ПК, без ИИ: только то, что StampReader прочитал
// из КОМПАС. Правила — надёжные: пустые обязательные графы, «Листов» против числа
// листов, обозначение и наименование по листам, нумерация листов. Ложное замечание
// хуже пропущенного: сомнительный случай пропускаем. Код без COM — проверяется без КОМПАС.
// Данные — классы LocalFacts, LocalSheet, LocalRemark (ApiClient.cs), на сервер они уходят
// полем client_facts (ClientFacts, описание — CLIENT_FACTS.md).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NormaCad.Kompas
{
    /// <summary>Вид основной надписи листа — по имени стиля оформления КОМПАС.</summary>
    internal static class StampForm
    {
        public const string First = "first";  // форма 1 или 2 ГОСТ 2.104 — первый лист
        public const string Next = "next";    // форма 2а — последующие листы
        public const string Title = "title";  // титульный лист
        public const string None = "none";    // без основной надписи
        public const string Other = "other";  // СПДС, извещение, свой стиль — не проверяем

        /// <summary>
        /// Только стили ЕСКД с формами ГОСТ 2.104 (чертёж, текстовый документ, спецификация
        /// и ведомости по ГОСТ 2.106, групповые по ГОСТ 2.113). У СПДС (ГОСТ Р 21.101)
        /// те же номера ячеек значат другие графы — такие листы не проверяем.
        /// </summary>
        public static string FromStyleName(string name)
        {
            string s = (name ?? "").ToLowerInvariant().Replace('ё', 'е');
            if (s.Contains("титульн")) return Title;
            if (s.Contains("без основной надписи") || s.Contains("без оформления")) return None;
            if (!(s.Contains("2.104") || s.Contains("2.106") || s.Contains("2.113"))) return Other;
            if (s.Contains("посл")) return Next;
            if (s.Contains("первый лист")) return First;
            return Other;
        }
    }

    internal static class DocumentKind
    {
        public const string Drawing = "drawing";
        public const string Fragment = "fragment";
        public const string Specification = "specification";
        public const string Text = "text";
    }

    /// <summary>Графы основной надписи по ГОСТ 2.104.</summary>
    internal static class StampGraphs
    {
        public const int Name = 1, Designation = 2, Material = 3, Litera = 4, Mass = 5,
            Scale = 6, Sheet = 7, Sheets = 8, Organization = 9, FirstUse = 25;

        /// <summary>
        /// Какие графы есть у формы листа (и только они читаются). Номер листа спецификации и
        /// текстового документа КОМПАС ставит сам при выводе — через API его не прочитать.
        /// </summary>
        public static int[] ForForm(string form, string kind)
        {
            bool autoNumber = kind == DocumentKind.Specification || kind == DocumentKind.Text;
            if (form == StampForm.First)
            {
                if (!autoNumber) return ClientFacts.Graphs;
                List<int> list = new List<int>(ClientFacts.Graphs);
                list.Remove(Sheet);
                return list.ToArray();
            }
            if (form == StampForm.Next) return autoNumber ? new[] { Designation } : new[] { Designation, Sheet };
            return new int[0];
        }

        public static string Label(int graph)
        {
            switch (graph)
            {
                case Name: return "Наименование";
                case Designation: return "Обозначение";
                case Material: return "Материал";
                case Litera: return "Литера";
                case Mass: return "Масса";
                case Scale: return "Масштаб";
                case Sheet: return "Лист";
                case Sheets: return "Листов";
                case Organization: return "Организация";
                default: return "Графа " + graph.ToString(CultureInfo.InvariantCulture);
            }
        }
    }

    /// <summary>Видимый текст графы из того, что хранит КОМПАС, и ключи для сравнения.</summary>
    internal static class StampText
    {
        public const int MaxLength = 300;

        // Ссылка на масштаб вида: ^(#3~1:2)1;323011413231.000000;1~ — на листе видно «1:2»
        private static readonly Regex Reference = new Regex(@"\^\(#\d+~([^)]*)\)[^~]*~");
        // Дробь в материале: Лист$dБ-ПН-О-4 ГОСТ 19903-74;Ст3пс-4 ГОСТ 14637-89$
        private static readonly Regex Fraction = new Regex(@"\$[A-Za-z]?([^$;]*);([^$]*)\$");
        // Спецсимвол КОМПАС: @4~
        private static readonly Regex Special = new Regex(@"@\d+~");
        private static readonly Regex Spaces = new Regex(@"\s+");
        private static readonly Regex Code = new Regex(@"(?:\s|\d)([А-Я]{1,3}\d{0,2})$");
        private const string LatinLook = "ABCEHKMOPTXY";
        private const string CyrillicLook = "АВСЕНКМОРТХУ";

        public static string Clean(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string s = Reference.Replace(raw, "$1");
            s = Fraction.Replace(s, delegate(Match m)
            {
                return " " + m.Groups[1].Value.Trim() + " / " + m.Groups[2].Value.Trim() + " ";
            });
            s = Special.Replace(s, "");
            s = s.Replace("$", "");
            s = Spaces.Replace(s, " ").Trim();
            return s.Length > MaxLength ? s.Substring(0, MaxLength) : s;
        }

        /// <summary>Заглавные, латиница-двойник — в кириллицу: «A» и «А» на листе не различить.</summary>
        public static string Unify(string s)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in (s ?? "").ToUpperInvariant().Replace('Ё', 'Е'))
            {
                int i = LatinLook.IndexOf(c);
                sb.Append(i >= 0 ? CyrillicLook[i] : c);
            }
            return sb.ToString();
        }

        /// <summary>Ключ сравнения обозначений и наименований: без пробелов, регистра и двойников.</summary>
        public static string Key(string s)
        {
            return Spaces.Replace(Unify(s), "");
        }

        /// <summary>Код документа в конце обозначения (ГОСТ 2.102): «АБВГ.301122.005 СБ» → «СБ».</summary>
        public static string DocumentCode(string designation)
        {
            Match m = Code.Match(Spaces.Replace(Unify(designation), " ").Trim());
            return m.Success ? m.Groups[1].Value : "";
        }

        /// <summary>Номер листа или число листов: целое больше нуля, иначе null.</summary>
        public static int? Number(string s)
        {
            int n;
            if (int.TryParse((s ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out n) && n > 0)
                return n;
            return null;
        }
    }

    internal static class StampCheck
    {
        public const string Gost = "ГОСТ 2.104-2006";
        public const string GostSpecification = "ГОСТ Р 2.106-2019";

        // Коды по ГОСТ 2.102: без кода — чертёж детали, СБ — сборочный чертёж, ВО, ГЧ, МЧ —
        // общего вида, габаритный, монтажный. У схем и прочего масштаб и массу не требуем.
        private static readonly string[] MassCodes = { "", "СБ" };
        private static readonly string[] ScaleCodes = { "", "СБ", "ВО", "ГЧ", "МЧ" };

        /// <summary>Дописать к прочитанному подписи для окна и замечания ПК.</summary>
        public static void Complete(LocalFacts f)
        {
            foreach (LocalSheet s in f.Sheets) s.Format = FormatName(s);
            LocalSheet first = FirstForm(f);
            f.Designation = first != null ? Graph(first, StampGraphs.Designation) : "";
            f.Name = first != null ? Graph(first, StampGraphs.Name) : "";
            f.DocTypeLabel = KindLabel(f);
            f.Note = Note(f);
            f.Remarks = Check(f);
        }

        public static List<LocalRemark> Check(LocalFacts f)
        {
            List<LocalRemark> found = new List<LocalRemark>();
            if (f == null || f.Error != "" || f.Sheets.Count == 0) return found;
            if (f.DocType == DocumentKind.Drawing)
            {
                RequiredDrawing(f, found);
                SheetsCount(f, found);
                SameAsFirst(f, found);
                Numbering(f, found);
            }
            else if (f.DocType == DocumentKind.Specification || f.DocType == DocumentKind.Text)
            {
                // Основная надпись у спецификации и текстового документа общая на все листы:
                // каждое замечание — один раз, на первом листе с полной формой
                RequiredShared(f, found);
                SheetsCount(f, found);
            }
            // По листам; внутри листа — в порядке правил
            List<LocalRemark> sorted = new List<LocalRemark>();
            int maxPage = 0;
            foreach (LocalRemark r in found) maxPage = Math.Max(maxPage, r.Page);
            for (int p = 0; p <= maxPage; p++)
                foreach (LocalRemark r in found) if (r.Page == p) sorted.Add(r);
            if (sorted.Count > ClientFacts.MaxRemarks)
                sorted.RemoveRange(ClientFacts.MaxRemarks, sorted.Count - ClientFacts.MaxRemarks);
            return sorted;
        }

        public static string Graph(LocalSheet s, int graph)
        {
            string text;
            return s.Graphs.TryGetValue(graph, out text) && text != null ? text.Trim() : "";
        }

        /// <summary>Лист проверяется: форма ЕСКД, полная или для последующих листов.</summary>
        public static bool Checked(LocalSheet s)
        {
            return s.Form == StampForm.First || s.Form == StampForm.Next;
        }

        public static LocalSheet FirstForm(LocalFacts f)
        {
            foreach (LocalSheet s in f.Sheets)
                if (s.Form == StampForm.First) return s;
            return null;
        }

        private static bool Blank(LocalSheet s, int graph)
        {
            return s.Graphs.ContainsKey(graph) && Graph(s, graph) == "";
        }

        private static string Standard(LocalFacts f, int graph)
        {
            string s = Gost + ", графа " + graph.ToString(CultureInfo.InvariantCulture);
            return f.DocType == DocumentKind.Specification ? s + "; " + GostSpecification : s;
        }

        private static LocalRemark Remark(LocalFacts f, string rule, int page, int graph,
            string title, string detail, params string[] fixes)
        {
            LocalRemark r = new LocalRemark();
            r.Rule = rule;
            r.Page = page;
            r.Title = title;
            r.Standard = Standard(f, graph);
            r.Detail = detail ?? "";
            r.Fixes.AddRange(fixes);
            return r;
        }

        private static LocalRemark Empty(LocalFacts f, LocalSheet s, int graph, string detail, params string[] fixes)
        {
            return Remark(f, "stamp.required", s.Number, graph,
                "Не заполнена графа «" + StampGraphs.Label(graph) + "».", detail, fixes);
        }

        private static void RequiredDrawing(LocalFacts f, List<LocalRemark> found)
        {
            foreach (LocalSheet s in f.Sheets)
            {
                if (!Checked(s)) continue;
                if (s.Form == StampForm.First && Blank(s, StampGraphs.Name))
                    found.Add(Empty(f, s, StampGraphs.Name, "В графе 1 указывают наименование изделия.",
                        "Укажите наименование изделия, как в спецификации."));
                if (Blank(s, StampGraphs.Designation))
                    found.Add(Empty(f, s, StampGraphs.Designation, "Обозначение документа указывают на каждом листе.",
                        "Укажите обозначение документа."));
                if (s.Form != StampForm.First) continue;

                string designation = Graph(s, StampGraphs.Designation);
                // Без обозначения не понять, деталь это или сборка: масса, масштаб и материал — только при нём
                if (designation == "") continue;
                string code = StampText.DocumentCode(designation);
                // Материал (графа 3) не требуем: сборку без кода СБ («Каркас АБВГ.01.000» в образцах
                // КОМПАС) от детали по обозначению не отличить — замер дал только ложные замечания
                if (Array.IndexOf(MassCodes, code) >= 0 && Blank(s, StampGraphs.Mass))
                    found.Add(Empty(f, s, StampGraphs.Mass,
                        "На чертеже детали и на сборочном чертеже указывают массу изделия.",
                        "Укажите массу в килограммах, без единицы измерения."));
                if (Array.IndexOf(ScaleCodes, code) >= 0 && Blank(s, StampGraphs.Scale))
                    found.Add(Empty(f, s, StampGraphs.Scale,
                        "Масштаб изображения указывают по ГОСТ 2.302.",
                        "Укажите масштаб, например 1:1."));
            }
        }

        private static void RequiredShared(LocalFacts f, List<LocalRemark> found)
        {
            LocalSheet s = FirstForm(f);
            if (s == null) return;
            if (Blank(s, StampGraphs.Name))
                found.Add(Empty(f, s, StampGraphs.Name, "В графе 1 указывают наименование изделия или документа.",
                    "Укажите наименование."));
            if (Blank(s, StampGraphs.Designation))
                found.Add(Empty(f, s, StampGraphs.Designation, "Обозначение документа указывают на каждом листе.",
                    "Укажите обозначение документа."));
        }

        /// <summary>«Листов» заполняют на первом листе; меньше, чем листов в документе, — ошибка.
        /// Больше — не судим: остальные листы могут быть в другом файле.</summary>
        private static void SheetsCount(LocalFacts f, List<LocalRemark> found)
        {
            LocalSheet s = f.DocType == DocumentKind.Drawing ? f.Sheets[0] : FirstForm(f);
            if (s == null || s.Form != StampForm.First || !s.Graphs.ContainsKey(StampGraphs.Sheets)) return;
            int total = Math.Max(f.SheetsTotal, f.Sheets.Count);
            string totalText = total.ToString(CultureInfo.InvariantCulture);
            string text = Graph(s, StampGraphs.Sheets);
            if (text == "")
            {
                // Номер первого листа больше 1 — документ продолжается из другого файла, «Листов» не здесь
                int? first = StampText.Number(Graph(s, StampGraphs.Sheet));
                if (first.HasValue && first.Value > 1) return;
                found.Add(Remark(f, "stamp.required", s.Number, StampGraphs.Sheets, "Не заполнена графа «Листов».",
                    "Общее количество листов документа указывают на первом листе.", "Укажите " + totalText + "."));
                return;
            }
            int? stated = StampText.Number(text);
            if (stated.HasValue && stated.Value < total)
                found.Add(Remark(f, "stamp.sheets_count", s.Number, StampGraphs.Sheets,
                    "В графе «Листов» указано " + stated.Value.ToString(CultureInfo.InvariantCulture)
                    + ", а листов в документе " + totalText + ".",
                    "", "Укажите " + totalText + "."));
        }

        /// <summary>Обозначение (и наименование там, где оно есть на листе) — как на листе 1.</summary>
        private static void SameAsFirst(LocalFacts f, List<LocalRemark> found)
        {
            LocalSheet first = f.Sheets[0];
            if (!Checked(first)) return;
            string designation = Graph(first, StampGraphs.Designation);
            string name = first.Form == StampForm.First ? Graph(first, StampGraphs.Name) : "";
            for (int i = 1; i < f.Sheets.Count; i++)
            {
                LocalSheet s = f.Sheets[i];
                if (!Checked(s)) continue;
                string number = s.Number.ToString(CultureInfo.InvariantCulture);
                string other = Graph(s, StampGraphs.Designation);
                if (designation != "" && other != "" && StampText.Key(other) != StampText.Key(designation))
                    found.Add(Remark(f, "stamp.designation_mismatch", s.Number, StampGraphs.Designation,
                        "Обозначение отличается от листа 1.",
                        "На листе " + number + " — «" + other + "», на листе 1 — «" + designation + "».",
                        "Укажите " + designation + ", как на листе 1."));
                string otherName = s.Form == StampForm.First ? Graph(s, StampGraphs.Name) : "";
                if (name != "" && otherName != "" && StampText.Key(otherName) != StampText.Key(name))
                    found.Add(Remark(f, "stamp.name_mismatch", s.Number, StampGraphs.Name,
                        "Наименование отличается от листа 1.",
                        "На листе " + number + " — «" + otherName + "», на листе 1 — «" + name + "».",
                        "Укажите «" + name + "», как на листе 1."));
            }
        }

        /// <summary>
        /// Нумерация листов чертежа (графа 7). Один лист — графу не заполняют. Несколько —
        /// номер на каждом листе, по порядку. Номер первого листа задаёт начало счёта:
        /// документ может продолжаться из другого файла. После сбоя счёт идёт от указанного
        /// номера: один пропуск — одно замечание, а не по замечанию на каждый следующий лист.
        /// </summary>
        private static void Numbering(LocalFacts f, List<LocalRemark> found)
        {
            if (Math.Max(f.SheetsTotal, f.Sheets.Count) == 1)
            {
                LocalSheet only = f.Sheets[0];
                // Один лист в файле может быть частью многолистового документа.
                // Явно указанное «Листов» > 1 не позволяет требовать очистки номера.
                int? documentSheets = StampText.Number(Graph(only, StampGraphs.Sheets));
                if (documentSheets.HasValue && documentSheets.Value > 1) return;
                if (Checked(only) && Graph(only, StampGraphs.Sheet) != "")
                    found.Add(Remark(f, "stamp.sheet_number", only.Number, StampGraphs.Sheet,
                        "На документе из одного листа графу «Лист» не заполняют.", "", "Очистите графу «Лист»."));
                return;
            }
            int? expected = null;
            foreach (LocalSheet s in f.Sheets)
            {
                if (!Checked(s) || !s.Graphs.ContainsKey(StampGraphs.Sheet))
                {
                    if (expected.HasValue) expected = expected.Value + 1;
                    continue;
                }
                string text = Graph(s, StampGraphs.Sheet);
                int? n = StampText.Number(text);
                int want = expected.HasValue ? expected.Value : s.Number;
                string wantText = want.ToString(CultureInfo.InvariantCulture);
                if (text == "")
                {
                    found.Add(Remark(f, "stamp.sheet_number", s.Number, StampGraphs.Sheet, "Не указан номер листа.",
                        "В документе из нескольких листов номер указывают на каждом листе.", "Укажите " + wantText + "."));
                    expected = want + 1;
                    continue;
                }
                if (!n.HasValue)
                {
                    // «2а», «2-1» — не судим и считаем дальше по порядку
                    expected = want + 1;
                    continue;
                }
                if (expected.HasValue && n.Value != want)
                    found.Add(Remark(f, "stamp.sheet_number", s.Number, StampGraphs.Sheet,
                        "Номер листа " + n.Value.ToString(CultureInfo.InvariantCulture) + ", а по порядку это лист " + wantText + ".",
                        "Листы нумеруют по порядку, без пропусков и повторов.", "Укажите " + wantText + "."));
                expected = n.Value + 1;
            }
        }

        /// <summary>Тип документа для окна: «Чертёж детали», «Сборочный чертёж», «Спецификация»…</summary>
        public static string KindLabel(LocalFacts f)
        {
            switch (f.DocType)
            {
                case DocumentKind.Drawing:
                    LocalSheet first = FirstForm(f);
                    if (first == null) return "Чертёж";
                    string designation = Graph(first, StampGraphs.Designation);
                    string code = StampText.DocumentCode(designation);
                    if (designation != "" && code == "") return "Чертёж детали";
                    if (code == "СБ") return "Сборочный чертёж";
                    return "Чертёж";
                case DocumentKind.Specification: return "Спецификация";
                case DocumentKind.Text: return "Текстовый документ";
                case DocumentKind.Fragment: return "Фрагмент";
                default: return "Документ";
            }
        }

        /// <summary>Формат листа для окна, кириллицей, как в ГОСТ 2.301: «А3», «А4×3».</summary>
        public static string FormatName(LocalSheet s)
        {
            if (s.FormatCode == "") return "";
            if (s.FormatCode == "user")
                return s.WidthMm > 0 && s.HeightMm > 0
                    ? Math.Round(s.WidthMm).ToString(CultureInfo.InvariantCulture) + "×"
                        + Math.Round(s.HeightMm).ToString(CultureInfo.InvariantCulture) + " мм"
                    : "свой формат";
            string name = "А" + s.FormatCode.Substring(1);
            return s.Multiplicity > 1 ? name + "×" + s.Multiplicity.ToString(CultureInfo.InvariantCulture) : name;
        }

        /// <summary>Чего проверка на ПК не охватила; пусто — проверено всё.</summary>
        private static string Note(LocalFacts f)
        {
            if (f.Error != "") return "";
            if (f.DocType == DocumentKind.Fragment) return "У фрагмента нет основной надписи — проверять на ПК нечего.";
            List<string> other = new List<string>();
            int checkedCount = 0;
            foreach (LocalSheet s in f.Sheets)
            {
                if (Checked(s)) checkedCount++;
                else if (s.Form == StampForm.Other) other.Add(s.Number.ToString(CultureInfo.InvariantCulture));
            }
            List<string> parts = new List<string>();
            if (checkedCount == 0)
                parts.Add("Основная надпись не по формам ГОСТ 2.104 (СПДС или свой стиль) — на ПК не проверялась.");
            else if (other.Count > 0)
                parts.Add("Не проверялись листы с другой формой основной надписи: " + string.Join(", ", other.ToArray()) + ".");
            if (f.SheetsTotal > f.Sheets.Count)
                parts.Add("Проверены первые " + f.Sheets.Count.ToString(CultureInfo.InvariantCulture) + " листов из "
                    + f.SheetsTotal.ToString(CultureInfo.InvariantCulture) + ".");
            return string.Join(" ", parts.ToArray());
        }
    }
}
