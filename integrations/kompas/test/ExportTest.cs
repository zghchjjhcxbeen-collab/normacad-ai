// Проверка экспорта без КОМПАС: подставные объекты API с теми же именами методов
// и свойств, что Exporter вызывает поздним связыванием. Запуск — test.cmd,
// код выхода 0 — все проверки прошли.
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;

namespace NormaCad.Kompas.Tests
{
    public class FakeConverter
    {
        public bool Works { get; set; }
        public bool InvalidOutput { get; set; }
        public bool ThrowAfterWrite { get; set; }

        public int Convert(string source, string target, int command, bool showDialog)
        {
            if (!Works) return 0;
            if (ThrowAfterWrite && source != "") return 0;
            File.WriteAllBytes(target, Encoding.ASCII.GetBytes(InvalidOutput ? "error" : "%PDF-1.4"));
            if (ThrowAfterWrite) throw new IOException("запись прервана");
            return 1;
        }
    }

    public class FakeDocument7
    {
        public int DocumentType { get; set; }
        public string Name { get; set; }
        public string PathName { get; set; }
        public bool Changed { get; set; }
        public bool SaveAsWorks { get; set; }

        public void SaveAs(string target)
        {
            if (!SaveAsWorks) throw new InvalidOperationException("формат PDF не поддержан");
            File.WriteAllBytes(target, Encoding.ASCII.GetBytes("%PDF-1.4"));
        }
    }

    public class FakeApplication7
    {
        public object ActiveDocument { get; set; }
        public FakeConverter PdfConverter { get; set; }

        public object Converter(string library)
        {
            return PdfConverter;
        }
    }

    public class FakeRasterParam
    {
        public int format { get; set; }
        public double extScale { get; set; }
        public int extResolution { get; set; }
        public int colorBPP { get; set; }

        public void Init() { format = 0; extScale = 0; extResolution = 0; colorBPP = 0; }
    }

    public class FakeDocument2D
    {
        public object RasterFormatParam()
        {
            return new FakeRasterParam();
        }

        public bool SaveAsToRasterFormat(string target, object param)
        {
            FakeRasterParam raster = (FakeRasterParam)param;
            // Like SDK Init, the fake starts with zero scale. Raster export must
            // provide a usable scale and resolution rather than relying on defaults.
            if (raster.extScale <= 0 || raster.extResolution <= 0 || raster.colorBPP != 24) return false;
            using (Bitmap bmp = new Bitmap(40, 30)) bmp.Save(target, ImageFormat.Bmp);
            return true;
        }
    }

    public class FakeKompas
    {
        public FakeApplication7 Application7 { get; set; }
        public string SystemPath { get; set; }
        public object Document2D { get; set; }

        public object ksGetApplication7() { return Application7; }
        public int SystemPathCalls { get; private set; }
        public string ksSystemPath(int type) { SystemPathCalls++; return SystemPath ?? ""; }
        public object ActiveDocument2D() { return Document2D; }
    }

    internal static class ExportTest
    {
        private static int failed;

        private static int Main()
        {
            Console.OutputEncoding = Encoding.UTF8;
            string root = Path.Combine(Path.GetTempPath(), "NormaCAD-Kompas-test-" + Guid.NewGuid().ToString("N"));
            string libs = Path.Combine(root, "Bin");
            Directory.CreateDirectory(libs);
            // Штатный конвертер ищется как файл Pdf2d.dll в папках КОМПАС
            File.WriteAllBytes(Path.Combine(libs, "Pdf2d.dll"), new byte[] { 0 });

            FakeKompas host = Kompas(libs, true, true, true, 1);
            ExportResult r = Exporter.ExportActive(host);
            Check("после подключения конвертера пути КОМПАС больше не запрашиваются", host.SystemPathCalls <= 1);
            Check("конвертер работает — PDF без предупреждения",
                r.Path.EndsWith(".pdf") && r.Warning == null);

            FakeKompas broken = Kompas(libs, true, true, false, 1);
            broken.Application7.PdfConverter.InvalidOutput = true;
            r = Exporter.ExportActive(broken);
            Check("файл без заголовка PDF — переход к растру", r.Path.EndsWith(".png"));

            FakeDocument7 saved = (FakeDocument7)broken.Application7.ActiveDocument;
            saved.PathName = Path.Combine(root, saved.Name);
            File.WriteAllText(saved.PathName, "");
            saved.Changed = false;
            broken.Application7.PdfConverter.InvalidOutput = false;
            broken.Application7.PdfConverter.ThrowAfterWrite = true;
            r = Exporter.ExportActive(broken);
            Check("повторный экспорт не принимает остаток прерванной записи", r.Path.EndsWith(".png"));

            r = Exporter.ExportActive(Kompas("", false, true, true, 1));
            Check("нет конвертера — PNG первого листа", r.Path.EndsWith(".png"));
            Check("нет конвертера — причина в предупреждении",
                r.Warning != null
                && r.Warning.StartsWith("Экспорт в PDF не удался, отправлено изображение первого листа.")
                && r.Warning.Contains("Pdf2d.dll не найден"));

            r = Exporter.ExportActive(Kompas("", false, false, true, 1));
            Check("нет конвертера и растра — PDF через «Сохранить как»", r.Path.EndsWith(".pdf"));
            Check("нет конвертера и растра — способ и причины в предупреждении",
                r.Warning != null
                && r.Warning.Contains("«Сохранить как»")
                && r.Warning.Contains("Pdf2d.dll не найден")
                && r.Warning.Contains("растр: документ не графический"));

            Refused("ничего не сработало — отказ с причинами", Kompas("", false, false, false, 1),
                "Не удалось экспортировать документ для проверки.", "SaveAs PDF: формат PDF не поддержан");
            Refused("3D-модель не отправляется", Kompas(libs, true, true, true, 4),
                "Активный документ — 3D-модель.", null);
            FakeKompas empty = Kompas(libs, true, true, true, 1);
            empty.Application7.ActiveDocument = null;
            Refused("нет активного документа", empty,
                "Откройте чертёж в КОМПАС-3D и повторите команду.", null);

            CheckSingleSheetNumbering();

            try { Directory.Delete(root, true); } catch (Exception) { }
            Console.WriteLine(failed == 0 ? "ExportTest: OK" : "ExportTest: ошибок " + failed);
            return failed == 0 ? 0 : 1;
        }

        private static FakeKompas Kompas(string libs, bool converter, bool raster, bool saveAs, int type)
        {
            FakeDocument7 doc = new FakeDocument7
            {
                DocumentType = type, Name = "Вал.cdw", PathName = "", Changed = true, SaveAsWorks = saveAs,
            };
            return new FakeKompas
            {
                Application7 = new FakeApplication7
                {
                    ActiveDocument = doc,
                    PdfConverter = new FakeConverter { Works = converter },
                },
                SystemPath = libs,
                Document2D = raster ? new FakeDocument2D() : null,
            };
        }

        private static void CheckSingleSheetNumbering()
        {
            LocalFacts facts = new LocalFacts { DocType = DocumentKind.Drawing, SheetsTotal = 1 };
            LocalSheet sheet = new LocalSheet { Number = 1, Form = StampForm.First };
            sheet.Graphs[StampGraphs.Sheet] = "3";
            sheet.Graphs[StampGraphs.Sheets] = "5";
            facts.Sheets.Add(sheet);
            Check("один лист многолистового документа — номер сохраняется", !HasNumberingRemark(facts));

            sheet.Graphs[StampGraphs.Sheets] = "1";
            Check("однолистовой документ — заполненный номер замечен", HasNumberingRemark(facts));
            sheet.Graphs[StampGraphs.Sheet] = "";
            Check("однолистовой документ — пустой номер допустим", !HasNumberingRemark(facts));

            sheet.Graphs[StampGraphs.Sheets] = "5";
            facts.SheetsTotal = 2;
            LocalSheet next = new LocalSheet { Number = 2, Form = StampForm.Next };
            sheet.Graphs[StampGraphs.Sheet] = "3";
            next.Graphs[StampGraphs.Sheet] = "5";
            facts.Sheets.Add(next);
            Check("часть многолистового документа — пропуск номера замечен", HasNumberingRemark(facts));
        }

        private static bool HasNumberingRemark(LocalFacts facts)
        {
            foreach (LocalRemark remark in StampCheck.Check(facts))
                if (remark.Rule == "stamp.sheet_number") return true;
            return false;
        }

        private static void Refused(string name, FakeKompas kompas, string start, string part)
        {
            try
            {
                Exporter.ExportActive(kompas);
                Check(name, false);
            }
            catch (UserException ex)
            {
                Check(name, ex.Message.StartsWith(start) && (part == null || ex.Message.Contains(part)));
            }
        }

        private static void Check(string name, bool ok)
        {
            Console.WriteLine((ok ? "ok    " : "FAIL  ") + name);
            if (!ok) failed++;
        }
    }
}
