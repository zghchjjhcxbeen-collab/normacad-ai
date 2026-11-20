// Проверка того, что показывает окно, без WinForms и без сети: этапы и очередь из ответа сервера,
// карточки отчёта, фильтры, итог, строки документа, аккаунта и данных с ПК (src\CheckView.cs).
// Запуск — test-client.cmd, код выхода 0 — все проверки прошли.
using System;
using System.Collections.Generic;

namespace NormaCad.Kompas.Tests
{
    public static class ViewTest
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

        private static void Eq(string name, int expected, int actual)
        {
            Eq(name, expected.ToString(), actual.ToString());
        }

        public static int Main()
        {
            try
            {
                TestRu();
                TestEta();
                TestQueue();
                TestRunning();
                TestStagesEdge();
                TestCards();
                TestVerdict();
                TestFilter();
                TestFacts();
                TestDocLine();
                TestAccount();
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine("FAIL неожиданное исключение: " + ex);
            }
            Console.WriteLine();
            Console.WriteLine("Проверок прошло: " + passed + ", не прошло: " + failed);
            return failed == 0 ? 0 : 1;
        }

        private static void TestRu()
        {
            string[] expect = { "проверок", "проверка", "проверки", "проверок", "проверок", "проверок", "проверка", "проверки", "проверок", "проверок" };
            int[] n = { 0, 1, 2, 5, 11, 12, 21, 22, 25, 111 };
            for (int i = 0; i < n.Length; i++)
                Eq("ru: " + n[i] + " " + expect[i], expect[i], Ru.Plural(n[i], "проверка", "проверки", "проверок"));
            Eq("ru: строчная первая буква", "не заполнена графа", Ru.LowerFirst("Не заполнена графа"));
            Eq("ru: аббревиатура не трогается", "ГОСТ 2.104 не соблюдён", Ru.LowerFirst("ГОСТ 2.104 не соблюдён"));
            Eq("ru: одна буква", "в", Ru.LowerFirst("В"));
            Eq("ru: пусто", "", Ru.LowerFirst(null));
        }

        private static void TestEta()
        {
            Eq("eta: меньше минуты — очередь", "Меньше минуты", Eta.Wait(30));
            Eq("eta: меньше минуты — осталось", "Осталось меньше минуты", Eta.Left(59));
            Eq("eta: меньше минуты — этап", "меньше минуты", Eta.Stage(0));
            Eq("eta: 90 с — 2 мин (округление вверх, как Math.round)", "Примерно через 2 мин", Eta.Wait(90));
            Eq("eta: 150 с — 3 мин", "Осталось примерно 3 мин", Eta.Left(150));
            Eq("eta: этап 60 с", "около 1 мин", Eta.Stage(60));
            Eq("eta: час ровно", "Примерно через 1 ч 0 мин", Eta.Wait(3600));
            Eq("eta: 1 ч 5 мин", "Осталось примерно 1 ч 5 мин", Eta.Left(3900));
            Eq("eta: отрицательное — меньше минуты", "Меньше минуты", Eta.Wait(-5));
        }

        private static string Stages(string receive, string normalize, string standards, string vision, string verify,
            string visionEta)
        {
            return "[{\"key\":\"receive\",\"label\":\"Приём файла\",\"state\":\"" + receive + "\",\"eta_seconds\":null},"
                + "{\"key\":\"normalize\",\"label\":\"Нормализация формата\",\"state\":\"" + normalize + "\",\"eta_seconds\":null},"
                + "{\"key\":\"standards\",\"label\":\"Поиск по базе ГОСТ\",\"state\":\"" + standards + "\",\"eta_seconds\":null},"
                + "{\"key\":\"vision\",\"label\":\"Vision-анализ\",\"state\":\"" + vision + "\",\"eta_seconds\":" + visionEta + "},"
                + "{\"key\":\"verify\",\"label\":\"Проверка достоверности\",\"state\":\"" + verify + "\",\"eta_seconds\":40}]";
        }

        // Состояние 4 макета: файл принят, перед ним одна проверка, серверы нагружены
        private static void TestQueue()
        {
            JobView v = JobView.From(J.Parse(
                "{\"job_id\":\"j1\",\"status\":\"queued\",\"stage\":\"normalize\",\"progress\":20,\"queue_ahead\":1,"
                + "\"queue_total\":2,\"eta_seconds\":180,\"eta_wait_seconds\":120,\"heavy_load\":true,"
                + "\"pages_total\":0,\"pages_done\":0,\"stages\":" + Stages("done", "pending", "pending", "pending", "pending", "90") + "}"));
            Eq("queue: этапов пять", 5, v.Stages.Count);
            Eq("queue: приём — готово", "Приём файла|готово", v.Stages[0].Name + "|" + v.Stages[0].State);
            Check("queue: приём — вид done", v.Stages[0].Kind == StageKind.Done);
            Eq("queue: анализ — имя как на сайте, ждёт", "Анализ чертежа моделью|ждёт", v.Stages[3].Name + "|" + v.Stages[3].State);
            Check("queue: ждущий этап — вид Waiting", v.Stages[3].Kind == StageKind.Waiting);
            Eq("queue: строки места и времени", "Вы в очереди: перед вами 1 проверка|Примерно через 3 мин", string.Join("|", v.Lines.ToArray()));
            Eq("queue: нагрузка серым", "Сейчас серверы нагружены, проверка займёт больше обычного", v.Hint);
            Eq("queue: статус с данными ПК", "Основная надпись проверена на ПК. Чертёж отправлен на сервер.", v.StatusText(true));
            Eq("queue: статус без данных ПК", "Чертёж отправлен на сервер.", v.StatusText(false));
            Eq("queue: процент", 20, v.Percent);
            Check("queue: не Running", !v.Running);

            JobView three = JobView.From(J.Parse("{\"status\":\"queued\",\"queue_ahead\":3,\"eta_seconds\":600,\"stages\":[]}"));
            Eq("queue: 3 проверки", "Вы в очереди: перед вами 3 проверки", three.Lines[0]);
            Eq("queue: без нагрузки — подсказка о примерном времени", "Время примерное: оно зависит от нагрузки и от самих чертежей.", three.Hint);
            JobView five = JobView.From(J.Parse("{\"status\":\"queued\",\"queue_ahead\":5,\"eta_seconds\":600,\"stages\":[]}"));
            Eq("queue: 5 проверок", "Вы в очереди: перед вами 5 проверок", five.Lines[0]);
            JobView many = JobView.From(J.Parse("{\"status\":\"queued\",\"queue_ahead\":21,\"eta_seconds\":600,\"stages\":[]}"));
            Eq("queue: 21 проверка", "Вы в очереди: перед вами 21 проверка", many.Lines[0]);
        }

        // Состояние 5 макета: идёт анализ первой страницы из двух
        private static void TestRunning()
        {
            JobView v = JobView.From(J.Parse(
                "{\"status\":\"running\",\"stage\":\"vision\",\"progress\":30,\"queue_ahead\":0,\"eta_seconds\":120,"
                + "\"heavy_load\":false,\"pages_total\":2,\"pages_done\":0,\"stages\":" + Stages("done", "done", "done", "running", "pending", "60") + "}"));
            Eq("running: идущий этап со страницей", "Анализ чертежа моделью: страница 1 из 2", v.Stages[3].Name);
            Eq("running: время у идущего этапа", "идёт, около 1 мин", v.Stages[3].State);
            Check("running: вид Running", v.Stages[3].Kind == StageKind.Running);
            Eq("running: у ждущего этапа времени нет", "ждёт", v.Stages[4].State);
            Eq("running: одна строка «Проверка идёт…»", "Проверка идёт. Осталось примерно 2 мин.", string.Join("|", v.Lines.ToArray()));
            Eq("running: подсказка о примерном времени", "Время примерное: оно зависит от нагрузки и от самих чертежей.", v.Hint);
            Eq("running: статус", "Идёт проверка на сервере.", v.StatusText(true));
            Check("running: Running", v.Running);

            JobView last = JobView.From(J.Parse(
                "{\"status\":\"running\",\"queue_ahead\":0,\"eta_seconds\":30,\"pages_total\":3,\"pages_done\":3,\"stages\":"
                + Stages("done", "done", "done", "running", "pending", "0") + "}"));
            Eq("running: страница не больше числа страниц", "Анализ чертежа моделью: страница 3 из 3", last.Stages[3].Name);
            Eq("running: нулевое время этапа не пишется", "идёт", last.Stages[3].State);
            Eq("running: меньше минуты", "Проверка идёт. Осталось меньше минуты.", last.Lines[0]);

            JobView single = JobView.From(J.Parse(
                "{\"status\":\"running\",\"queue_ahead\":0,\"eta_seconds\":60,\"pages_total\":1,\"pages_done\":0,\"stages\":"
                + Stages("done", "done", "done", "running", "pending", "50") + "}"));
            Eq("running: одна страница — без «страница 1 из 1»", "Анализ чертежа моделью", single.Stages[3].Name);
        }

        private static void TestStagesEdge()
        {
            JobView old = JobView.From(J.Parse("{\"status\":\"running\",\"stages\":[{\"key\":\"x-new\",\"label\":\"Новый этап\",\"state\":\"running\"}]}"));
            Check("edge: старый сервер без eta — строк нет", old.Lines.Count == 0 && old.Hint == "");
            Eq("edge: неизвестный этап — подпись сервера", "Новый этап", old.Stages[0].Name);

            JobView queuedRunning = JobView.From(J.Parse(
                "{\"status\":\"queued\",\"queue_ahead\":2,\"eta_seconds\":300,\"stages\":"
                + Stages("running", "pending", "pending", "pending", "pending", "60") + "}"));
            Eq("edge: в очереди у этапа времени нет", "идёт", queuedRunning.Stages[0].State);

            JobView zeroAhead = JobView.From(J.Parse("{\"status\":\"queued\",\"queue_ahead\":0,\"eta_seconds\":100,\"stages\":[]}"));
            Eq("edge: очередь пуста — «Проверка идёт», как на сайте", "Проверка идёт. Осталось примерно 2 мин.", zeroAhead.Lines[0]);

            JobView done = JobView.From(J.Parse("{\"status\":\"done\",\"progress\":100,\"eta_seconds\":null,\"stages\":"
                + Stages("done", "done", "done", "done", "done", "null") + "}"));
            Check("edge: готово — строк очереди нет", done.Lines.Count == 0 && done.Hint == "");
            Eq("edge: готово — статус", "Проверка завершена.", done.StatusText(false));
            Eq("edge: готово — процент", 100, done.Percent);

            JobView sending = JobView.Sending();
            Eq("edge: отправка — приём идёт, остальное ждёт", "идёт|ждёт|ждёт|ждёт|ждёт",
                sending.Stages[0].State + "|" + sending.Stages[1].State + "|" + sending.Stages[2].State + "|"
                + sending.Stages[3].State + "|" + sending.Stages[4].State);
            Eq("edge: отправка — статус", "Отправка на сервер…", sending.StatusText(false));
            Eq("edge: отправка с ПК — статус", "Основная надпись проверена на ПК. Отправка на сервер…", sending.StatusText(true));

            JobView empty = JobView.From(null);
            Check("edge: null — пустой ход без исключений", empty.Stages.Count == 0 && empty.Lines.Count == 0);
            Eq("edge: процент не больше 100", 100, JobView.From(J.Parse("{\"progress\":140}")).Percent);
        }

        // Данные ПК как в макете: чертёж детали, два листа, три замечания по основной надписи
        private static LocalFacts MockupFacts()
        {
            LocalFacts f = new LocalFacts();
            f.DocType = "drawing";
            f.DocTypeLabel = "Чертёж детали";
            f.Designation = "АБВГ.301122.005";
            f.Name = "Корпус";
            LocalSheet s1 = new LocalSheet(); s1.Number = 1; s1.Format = "А3";
            LocalSheet s2 = new LocalSheet(); s2.Number = 2; s2.Format = "А4"; s2.Vertical = true;
            f.Sheets.Add(s1);
            f.Sheets.Add(s2);
            f.Remarks.Add(Remark(1, "Не заполнена графа «Масса».", "ГОСТ 2.104-2006, графа 5", "Масса нужна для расчётов.", "укажите массу детали в килограммах, без единицы измерения."));
            f.Remarks.Add(Remark(1, "В графе «Листов» указано 1, а листов в документе 2.", "ГОСТ 2.104-2006, графа 8", "Число листов не совпадает.", "укажите 2."));
            f.Remarks.Add(Remark(2, "Обозначение отличается от листа 1.", "ГОСТ 2.104-2006, графа 2", "", "укажите АБВГ.301122.005, как на листе 1."));
            return f;
        }

        private static LocalRemark Remark(int page, string title, string standard, string detail, string fix)
        {
            LocalRemark r = new LocalRemark();
            r.Page = page;
            r.Title = title;
            r.Standard = standard;
            r.Detail = detail;
            if (fix != null) r.Fixes.Add(fix);
            return r;
        }

        private const string MockupReport =
            "{\"verdict\":\"Требует доработки: критические нарушения\",\"score\":60,\"source\":\"ai-core\",\"pages_total\":2,\"pages_checked\":2,"
            + "\"check_id\":42,\"stats\":{\"critical\":1,\"warnings\":1,\"ok\":1},\"violations\":["
            + "{\"n\":1,\"severity\":\"ok\",\"title\":\"Формат и рамка листа соответствуют требованиям.\",\"standard\":\"ГОСТ 2.301-68\",\"detail\":\"\",\"fixes\":[],\"page\":1},"
            + "{\"n\":2,\"severity\":\"warning\",\"title\":\"Технические требования размещены не над основной надписью.\",\"standard\":\"ГОСТ 2.316-2008\",\"detail\":\"\","
            + "\"fixes\":[\"перенесите текст над основной надписью;\",\" при нехватке места расположите его колонкой левее основной надписи. \",\"  \"],\"page\":2},"
            + "{\"n\":3,\"severity\":\"critical\",\"title\":\"Не проставлен размер, определяющий положение отверстия Ø8.\",\"standard\":\"ГОСТ 2.307-2011\","
            + "\"detail\":\"Положение отверстия на главном виде не определено: деталь нельзя изготовить по чертежу.\","
            + "\"fixes\":[\"проставьте размер от базовой поверхности до оси отверстия;\",\"задайте координаты оси отверстия от двух баз.\"],\"page\":1}]}";

        private static void TestCards()
        {
            List<Card> cards = Cards.Build(J.Parse(MockupReport), MockupFacts());
            Eq("cards: шесть карточек — три сервера и три ПК", 6, cards.Count);
            string order = "";
            foreach (Card c in cards) order += c.SeverityLabel + "/" + c.PageLabel + "/" + c.SourceLabel + "; ";
            Eq("cards: порядок как в макете",
                "Критическое/Лист 1/сервер; Замечание/Лист 1/ПК; Замечание/Лист 1/ПК; Замечание/Лист 2/ПК; Замечание/Лист 2/сервер; В норме/Лист 1/сервер; ",
                order);
            Card crit = cards[0];
            Eq("cards: поля замечания сервера", "Не проставлен размер, определяющий положение отверстия Ø8.|ГОСТ 2.307-2011",
                crit.Title + "|" + crit.Standard);
            Eq("cards: несколько способов — «Способы исправить:»", "Способы исправить:", crit.FixesCaption);
            Eq("cards: один способ — «Как исправить:»", "Как исправить:", cards[1].FixesCaption);
            Card serverWarn = cards[4];
            Eq("cards: способы обрезаны, пустые выброшены", "перенесите текст над основной надписью;|при нехватке места расположите его колонкой левее основной надписи.",
                string.Join("|", serverWarn.Fixes.ToArray()));
            Check("cards: пояснение ПК сохранено", cards[1].Detail == "Масса нужна для расчётов." && cards[1].FromPc);
            Check("cards: замечание ПК без пояснения", cards[3].Detail == "");

            List<Card> single = Cards.Build(J.Parse(
                "{\"pages_total\":1,\"violations\":[{\"severity\":\"info\",\"title\":\"x\"},{\"severity\":\"critical\",\"title\":\"y\",\"page\":null}]}"), null);
            Check("cards: одиночный лист — лист 1", single[0].Page == 1 && single[1].Page == 1);
            Eq("cards: неизвестная важность — замечание", "Замечание", single[1].SeverityLabel);
            Eq("cards: критическое — первым", "y", single[0].Title);

            List<Card> multi = Cards.Build(J.Parse(
                "{\"pages_total\":3,\"violations\":[{\"severity\":\"warning\",\"title\":\"весь документ\"},{\"severity\":\"warning\",\"title\":\"лист 3\",\"page\":3}]}"), null);
            Eq("cards: без листа в многостраничном — в конце, без подписи листа", "лист 3|весь документ|",
                multi[0].Title + "|" + multi[1].Title + "|" + multi[1].PageLabel);

            LocalFacts broken = MockupFacts();
            broken.Error = "КОМПАС не отдал основную надпись";
            Eq("cards: данные ПК с ошибкой — без карточек ПК", 3, Cards.Build(J.Parse(MockupReport), broken).Count);
            Eq("cards: данных ПК нет", 3, Cards.Build(J.Parse(MockupReport), null).Count);
            Eq("cards: отчёта нет, только ПК", 3, Cards.Build(null, MockupFacts()).Count);
        }

        private static void TestVerdict()
        {
            List<Card> cards = Cards.Build(J.Parse(MockupReport), MockupFacts());
            Eq("verdict: как в макете", "Требует доработки. Критических: 1, замечаний: 4, в норме: 1.",
                Cards.Verdict("Требует доработки: критические нарушения", cards, false));
            List<Card> clean = Cards.Build(J.Parse("{\"violations\":[{\"severity\":\"ok\",\"title\":\"a\"},{\"severity\":\"ok\",\"title\":\"b\"}]}"), null);
            Eq("verdict: только «в норме»", "Соответствует требованиям. Критических: 0, замечаний: 0, в норме: 2.",
                Cards.Verdict("Соответствует требованиям", clean, false));
            Eq("verdict: замечаний нет", "Соответствует требованиям. Замечаний нет.", Cards.Verdict("Соответствует требованиям", new List<Card>(), false));
            Eq("verdict: отказ — текст сервера как есть", "Документ не является чертежом",
                Cards.Verdict(" Документ не является чертежом ", new List<Card>(), true));
            List<Card> pcOnly = Cards.Build(J.Parse("{\"violations\":[]}"), MockupFacts());
            Eq("verdict: сервер без замечаний, ПК нашёл — требует доработки", "Требует доработки. Критических: 0, замечаний: 3, в норме: 0.",
                Cards.Verdict("Соответствует требованиям", pcOnly, false));

            List<string> notes = Cards.Notes(J.Parse("{\"source\":\"stub\",\"pages_total\":12,\"pages_checked\":10}"), "Отправлено изображение первого листа.");
            Eq("notes: предупреждение, заглушка, страницы",
                "Отправлено изображение первого листа.|ИИ на сервере не подключён: это ответ-заглушка, а не разбор чертежа.|Проверено страниц: 10 из 12.",
                string.Join("|", notes.ToArray()));
            Check("refusal: не является чертежом", Cards.IsRefusal("Документ не является чертежом", 0));
            Check("refusal: формат не поддерживается", Cards.IsRefusal("Формат не поддерживается", 0));
            Check("refusal: есть замечания — не отказ", !Cards.IsRefusal("Документ не является чертежом", 2));
            Check("refusal: обычный итог — не отказ", !Cards.IsRefusal("Соответствует требованиям", 0));
            Eq("done: статус", "Проверка завершена.", JobView.Done().StatusText(true));
            Eq("done: процент", 100, JobView.Done().Percent);
            Eq("notes: сказать нечего", 0, Cards.Notes(J.Parse("{\"source\":\"ai-core\",\"pages_total\":2,\"pages_checked\":2}"), null).Count);
        }

        private static void TestFilter()
        {
            List<Card> cards = Cards.Build(J.Parse(MockupReport), MockupFacts());
            CardFilter f = new CardFilter();
            Eq("filter: все", 6, f.Apply(cards).Count);
            Eq("filter: счётчик", "Показано: 6 из 6", CardFilter.Counter(f.Apply(cards).Count, cards.Count));
            f.Severity = "warning";
            Eq("filter: замечания", 4, f.Apply(cards).Count);
            f.Page = "2";
            Eq("filter: замечания листа 2", 2, f.Apply(cards).Count);
            f.Source = "pc";
            Eq("filter: замечания листа 2 от ПК", 1, f.Apply(cards).Count);
            f.Source = "server";
            Eq("filter: замечания листа 2 от сервера", 1, f.Apply(cards).Count);
            f = new CardFilter();
            f.Source = "pc";
            Eq("filter: только ПК", 3, f.Apply(cards).Count);
            f = new CardFilter();
            f.Severity = "critical";
            f.Page = "2";
            Eq("filter: ничего не подошло", 0, f.Apply(cards).Count);
            Eq("filter: счётчик при нуле", "Показано: 0 из 6", CardFilter.Counter(0, 6));

            Eq("filter: пункты важности", "все,критические,замечания,в норме", Labels(CardFilter.SeverityChoices(cards)));
            Eq("filter: пункты листа", "все,1,2", Labels(CardFilter.PageChoices(cards)));
            Eq("filter: пункты источника", "все,сервер,ПК", Labels(CardFilter.SourceChoices(cards)));
            List<Card> serverOnly = Cards.Build(J.Parse("{\"pages_total\":1,\"violations\":[{\"severity\":\"warning\",\"title\":\"x\"}]}"), null);
            Eq("filter: только то, что есть — важность", "все,замечания", Labels(CardFilter.SeverityChoices(serverOnly)));
            Eq("filter: только то, что есть — источник", "все,сервер", Labels(CardFilter.SourceChoices(serverOnly)));
            Eq("filter: значение пункта", "critical", CardFilter.SeverityChoices(cards)[1].Value);
        }

        private static string Labels(List<Choice> list)
        {
            List<string> s = new List<string>();
            foreach (Choice c in list) s.Add(c.ToString());
            return string.Join(",", s.ToArray());
        }

        private static void TestFacts()
        {
            LocalFacts f = MockupFacts();
            List<KeyValuePair<string, string>> rows = FactsText.Rows(f);
            string kv = "";
            foreach (KeyValuePair<string, string> r in rows) kv += r.Key + " " + r.Value + "|";
            Eq("facts: строки как в макете", "Тип документа: Чертёж детали|Обозначение: АБВГ.301122.005|Форматы: лист 1 — А3, лист 2 — А4|", kv);
            Eq("facts: заголовок замечаний", "Замечания по основной надписи: 3", FactsText.RemarksHeader(f));
            Eq("facts: строка замечания", "Лист 1: не заполнена графа «Масса».", FactsText.RemarkLine(f.Remarks[0]));
            Eq("facts: строка замечания 2", "Лист 1: в графе «Листов» указано 1, а листов в документе 2.", FactsText.RemarkLine(f.Remarks[1]));
            Eq("facts: замечание без листа", "Нет подписи «Разраб.».", FactsText.RemarkLine(Remark(0, "Нет подписи «Разраб.».", "", "", null)));
            Eq("facts: свёрнуто — 3 замечания", "На ПК: 3 замечания по основной надписи.", FactsText.Collapsed(f));
            f.Remarks.RemoveAt(2);
            f.Remarks.RemoveAt(1);
            Eq("facts: свёрнуто — 1 замечание", "На ПК: 1 замечание по основной надписи.", FactsText.Collapsed(f));
            for (int i = 0; i < 4; i++) f.Remarks.Add(Remark(1, "x", "", "", null));
            Eq("facts: свёрнуто — 5 замечаний", "На ПК: 5 замечаний по основной надписи.", FactsText.Collapsed(f));
            f.Remarks.Clear();
            Eq("facts: свёрнуто — нет замечаний", "На ПК: замечаний по основной надписи нет.", FactsText.Collapsed(f));
            Eq("facts: заголовок — нет замечаний", "Замечаний по основной надписи нет.", FactsText.RemarksHeader(f));

            LocalFacts one = new LocalFacts();
            LocalSheet s = new LocalSheet(); s.Number = 1; s.Format = "А4";
            one.Sheets.Add(s);
            rows = FactsText.Rows(one);
            Check("facts: один лист — «Формат:», пустые строки пропущены", rows.Count == 1 && rows[0].Key == "Формат:" && rows[0].Value == "А4");

            LocalFacts broken = new LocalFacts();
            broken.Error = "КОМПАС не отдал основную надпись.";
            Check("facts: с ошибкой — не читается", !FactsText.Readable(broken) && FactsText.Rows(broken).Count == 0);
            Eq("facts: строка ошибки", "На ПК проверить не удалось: КОМПАС не отдал основную надпись.", FactsText.Failed(broken));
            Check("facts: null — не читается", !FactsText.Readable(null) && FactsText.RemarkCount(null) == 0);
        }

        private static void TestDocLine()
        {
            Eq("doc: как в макете", "Корпус.cdw — чертёж, 2 листа (А3, А4)", DocText.Line("Корпус.cdw", MockupFacts()));
            Eq("doc: без данных ПК — вид по расширению", "Корпус.cdw — чертёж", DocText.Line("Корпус.cdw", null));
            Eq("doc: спецификация", "Сборка.spw — спецификация", DocText.Line("Сборка.spw", null));
            Eq("doc: фрагмент", "Узел.frw — фрагмент", DocText.Line("Узел.frw", null));
            Eq("doc: текстовый документ", "ПЗ.kdw — текстовый документ", DocText.Line("ПЗ.kdw", null));
            Eq("doc: имя PDF и тип из данных ПК — подпись ПК строчными", "Корпус.pdf — чертёж детали, 2 листа (А3, А4)", DocText.Line("Корпус.pdf", MockupFacts()));
            LocalFacts noLabel = MockupFacts();
            noLabel.DocTypeLabel = "";
            Eq("doc: у PDF нет подписи типа — без вида", "Корпус.pdf — 2 листа (А3, А4)", DocText.Line("Корпус.pdf", noLabel));
            LocalFacts total = MockupFacts();
            total.SheetsTotal = 120;
            Eq("doc: листов больше, чем прочитано — число из SheetsTotal", "Корпус.cdw — чертёж, 120 листов (А3, А4)", DocText.Line("Корпус.cdw", total));
            total.SheetsTotal = 1;
            Eq("doc: SheetsTotal меньше прочитанного — по прочитанным", "Корпус.cdw — чертёж, 2 листа (А3, А4)", DocText.Line("Корпус.cdw", total));
            Eq("doc: неизвестно что — только имя", "План.pdf", DocText.Line("План.pdf", null));
            LocalFacts broken = MockupFacts();
            broken.Error = "сбой";
            Eq("doc: данные ПК с ошибкой — без листов", "Корпус.cdw — чертёж", DocText.Line("Корпус.cdw", broken));
            Eq("doc: данные ПК с ошибкой, неизвестное расширение — только имя", "Корпус.pdf", DocText.Line("Корпус.pdf", broken));
            LocalFacts many = new LocalFacts();
            foreach (string fmt in new string[] { "А1", "А3", "А3", "А3", "А3" })
            {
                LocalSheet s = new LocalSheet();
                s.Format = fmt;
                many.Sheets.Add(s);
            }
            Eq("doc: 5 листов, форматы без повторов", "Схема.cdw — чертёж, 5 листов (А1, А3)", DocText.Line("Схема.cdw", many));
            LocalFacts one = new LocalFacts();
            LocalSheet a4 = new LocalSheet(); a4.Format = "А4";
            one.Sheets.Add(a4);
            Eq("doc: 1 лист", "Деталь.cdw — чертёж, 1 лист (А4)", DocText.Line("Деталь.cdw", one));
            Eq("doc: регистр расширения не важен", "ДЕТАЛЬ.CDW — чертёж", DocText.Line("ДЕТАЛЬ.CDW", null));
        }

        private static AccountInfo Account(string access)
        {
            return AccountInfo.FromJson(J.Parse("{\"email\":\"inzhener@example.ru\"}"), J.Parse(access));
        }

        private static void TestAccount()
        {
            AccountInfo trial = Account("{\"plan\":\"trial\",\"plan_label\":\"Тестовый доступ\",\"limited\":true,"
                + "\"access_until\":\"2026-11-18T12:00:00+00:00\",\"expired\":false,\"checks_limit\":10,\"window_hours\":5,"
                + "\"used\":3,\"remaining\":7,\"can_check\":true}");
            Eq("account: как в макете", "Тестовый доступ до 18.11.2026|Осталось 7 из 10 проверок за 5 ч",
                string.Join("|", AccountText.Meta(trial).ToArray()));

            AccountInfo limit = Account("{\"plan\":\"trial\",\"plan_label\":\"Тестовый доступ\",\"limited\":true,"
                + "\"access_until\":\"2026-11-18T12:00:00+00:00\",\"expired\":false,\"checks_limit\":10,\"window_hours\":5,"
                + "\"used\":10,\"remaining\":0,\"can_check\":false}");
            Eq("account: лимит исчерпан", "Тестовый доступ до 18.11.2026|Осталось 0 из 10 проверок за 5 ч",
                string.Join("|", AccountText.Meta(limit).ToArray()));

            AccountInfo expired = Account("{\"plan\":\"trial\",\"plan_label\":\"Тестовый доступ\",\"limited\":true,"
                + "\"access_until\":\"2026-10-18T12:00:00+00:00\",\"expired\":true,\"checks_limit\":10,\"window_hours\":5,"
                + "\"used\":0,\"remaining\":10,\"can_check\":false}");
            Eq("account: срок истёк", "Тестовый доступ истёк 18.10.2026", string.Join("|", AccountText.Meta(expired).ToArray()));

            Eq("account: без ограничений", "Без ограничений",
                string.Join("|", AccountText.Meta(Account("{\"plan\":\"\",\"limited\":false,\"checks_limit\":null}")).ToArray()));
            Eq("account: план без слова «доступ»", "Доступ ПРО до 18.11.2026",
                string.Join("|", AccountText.Meta(Account("{\"plan\":\"pro\",\"plan_label\":\"ПРО\",\"limited\":true,"
                    + "\"access_until\":\"2026-11-18T12:00:00+00:00\",\"checks_limit\":null}")).ToArray()));
            Eq("account: из 1 проверки", "Осталось 1 из 1 проверки за 24 ч",
                AccountText.Meta(Account("{\"checks_limit\":1,\"remaining\":1,\"window_hours\":24}"))[0]);
            Eq("account: из 21 проверки, остаток по used", "Осталось 20 из 21 проверки",
                AccountText.Meta(Account("{\"checks_limit\":21,\"used\":1}"))[0]);
            Eq("account: остаток не меньше нуля", "Осталось 0 из 5 проверок",
                AccountText.Meta(Account("{\"checks_limit\":5,\"used\":9}"))[0]);
        }
    }
}
