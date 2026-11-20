// Чтение основной надписи активного документа КОМПАС: тип документа, листы, форматы,
// тексты граф. Позднее связывание, как в KompasLibrary.cs. Имена методов и констант
// сверены с SDK КОМПАС-3D v23 (ksAPI7.h, ksConstants.h, ldefin2d.h) и живым КОМПАС.
//
// Номера ячеек штампа КОМПАС совпадают с номерами граф ГОСТ 2.104: 1–9, 25; литера
// (графа 4) — ячейки 40–42. IStamp.GetNextColumnId у чертежей сразу отдаёт −1, поэтому
// ячейки читаются по номерам. В ячейках 43–45 КОМПАС держит путь и имя файла — их не
// читаем: имя файла на сервер не уходит. Фамилии (110–115) и даты тоже не читаем.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace NormaCad.Kompas
{
    internal static class StampReader
    {
        // DocumentTypeEnum (ksConstants.h)
        private const int DocDrawing = 1;
        private const int DocFragment = 2;
        private const int DocSpecification = 3;
        private const int DocTextual = 6;
        // ksDocumentFormatEnum: 0…5 — А0…А5, 6 — пользовательский формат
        private const int FormatUser = 6;
        // Типы библиотек стилей для KompasObject.ksGetLibraryStylesArray (ldefin2d.h)
        private const short GraphicLayouts = 5;
        private const short TextLayouts = 6;
        private const short SpcLayouts = 7;
        // ko_LibraryStyleParam (ksConstants.h) — параметры стиля: styleId, styleName
        private const short LibraryStyleParam = 82;

        /// <summary>Ячейки штампа КОМПАС, из которых складывается графа ГОСТ 2.104.</summary>
        internal static int[] Cells(int graph)
        {
            return graph == StampGraphs.Litera ? new[] { 40, 41, 42 } : new[] { graph };
        }

        /// <summary>
        /// Прочитать активный документ и проверить основную надпись. Вызывать из потока КОМПАС.
        /// Исключений не бросает: сбой чтения не мешает экспорту, причина — в Error.
        /// </summary>
        public static LocalFacts ReadActive(object kompas5)
        {
            LocalFacts f;
            try
            {
                object app7 = Com.Call(kompas5, "ksGetApplication7");
                object doc = app7 == null ? null : Com.Call(app7, "ActiveDocument");
                if (doc == null)
                {
                    f = new LocalFacts();
                    f.Error = "нет активного документа";
                }
                else
                {
                    f = Read(kompas5, doc);
                    if (f.Error == "") StampCheck.Complete(f);
                }
            }
            catch (Exception ex)
            {
                f = new LocalFacts();
                f.Error = ex.Message;
            }
            Log(f);
            return f;
        }

        /// <summary>Что прочитано, без проверки. Тип документа не из проверяемых — Error.</summary>
        public static LocalFacts Read(object kompas5, object doc)
        {
            LocalFacts f = new LocalFacts();
            f.DocType = Kind(Convert.ToInt32(Com.Call(doc, "DocumentType")));
            if (f.DocType == "")
            {
                f.Error = "документ этого типа на ПК не проверяется";
                return f;
            }
            // У фрагмента нет листов оформления и основной надписи
            if (f.DocType == DocumentKind.Fragment) return f;

            object sheets = Com.Call(doc, "LayoutSheets");
            if (sheets == null)
            {
                f.Error = "КОМПАС не отдал листы документа";
                return f;
            }
            int count = Convert.ToInt32(Com.Call(sheets, "Count"));
            f.SheetsTotal = Math.Max(0, count);
            StyleNames names = new StyleNames(kompas5, f.DocType);
            for (int i = 0; i < count && i < ClientFacts.MaxSheets; i++)
            {
                LocalSheet s = new LocalSheet();
                s.Number = i + 1;
                try
                {
                    ReadSheet(Com.Call(sheets, "Item", i), s, names, f.DocType);
                }
                catch (Exception ex)
                {
                    // Лист, прочитанный не до конца, не проверяем: ложное замечание хуже пропущенного
                    SupportLog.Write("лист " + s.Number.ToString(CultureInfo.InvariantCulture) + " не прочитан: " + ex.Message);
                    s.Form = StampForm.Other;
                    s.Graphs.Clear();
                }
                f.Sheets.Add(s);
            }
            return f;
        }

        private static string Kind(int type)
        {
            switch (type)
            {
                case DocDrawing: return DocumentKind.Drawing;
                case DocFragment: return DocumentKind.Fragment;
                case DocSpecification: return DocumentKind.Specification;
                case DocTextual: return DocumentKind.Text;
                default: return "";
            }
        }

        private static void ReadSheet(object sheet, LocalSheet s, StyleNames names, string kind)
        {
            if (sheet == null) throw new InvalidOperationException("КОМПАС не отдал лист");
            ReadFormat(sheet, s);

            string library = Convert.ToString(Com.Call(sheet, "LayoutLibraryFileName")) ?? "";
            int style = (int)Math.Round(Convert.ToDouble(Com.Call(sheet, "LayoutStyleNumber"), CultureInfo.InvariantCulture));
            s.StyleName = names.Get(library, style);
            s.Form = StampForm.FromStyleName(s.StyleName);

            int[] graphs = StampGraphs.ForForm(s.Form, kind);
            if (graphs.Length == 0) return;
            object stamp = Com.Call(sheet, "Stamp");
            if (stamp == null) throw new InvalidOperationException("нет основной надписи");
            foreach (int g in graphs)
            {
                List<string> parts = new List<string>();
                foreach (int cell in Cells(g))
                {
                    object text = Com.Call(stamp, "Text", cell);
                    string str = text == null ? "" : StampText.Clean(Convert.ToString(Com.Call(text, "Str")));
                    if (str != "") parts.Add(str);
                }
                s.Graphs[g] = string.Join(" ", parts.ToArray());
            }
        }

        private static void ReadFormat(object sheet, LocalSheet s)
        {
            try
            {
                object format = Com.Call(sheet, "Format");
                if (format == null) return;
                int f = Convert.ToInt32(Com.Call(format, "Format"));
                int multiplicity = Convert.ToInt32(Com.Call(format, "FormatMultiplicity"));
                bool vertical = Convert.ToBoolean(Com.Call(format, "VerticalOrientation"));
                double width = Convert.ToDouble(Com.Call(format, "FormatWidth"), CultureInfo.InvariantCulture);
                double height = Convert.ToDouble(Com.Call(format, "FormatHeight"), CultureInfo.InvariantCulture);
                s.FormatCode = f >= 0 && f <= 5 ? "A" + f.ToString(CultureInfo.InvariantCulture) : (f == FormatUser ? "user" : "");
                s.Multiplicity = multiplicity;
                s.Vertical = vertical;
                s.WidthMm = width;
                s.HeightMm = height;
            }
            catch (Exception ex)
            {
                // Формат — для сведения: без него основная надпись проверяется так же
                s.FormatCode = "";
                SupportLog.Write("лист " + s.Number.ToString(CultureInfo.InvariantCulture) + ", формат не прочитан: " + ex.Message);
            }
        }

        /// <summary>Журнал поддержки: числа и имена стилей, без текстов граф.</summary>
        private static void Log(LocalFacts f)
        {
            if (f.Error != "")
            {
                SupportLog.Write("основная надпись не прочитана: " + f.Error);
                return;
            }
            List<string> forms = new List<string>();
            int checkedCount = 0;
            foreach (LocalSheet s in f.Sheets)
            {
                if (StampCheck.Checked(s)) checkedCount++;
                string item = s.Form + (s.StyleName != "" ? " «" + s.StyleName + "»" : "");
                if (!forms.Contains(item)) forms.Add(item);
            }
            SupportLog.Write("основная надпись: " + f.DocType + ", листов " + f.SheetsTotal.ToString(CultureInfo.InvariantCulture)
                + ", проверено " + checkedCount.ToString(CultureInfo.InvariantCulture)
                + ", замечаний " + f.Remarks.Count.ToString(CultureInfo.InvariantCulture)
                + (forms.Count > 0 ? "; формы: " + string.Join(", ", forms.ToArray()) : ""));
        }

        /// <summary>
        /// Имена стилей оформления по номеру: KompasObject.ksGetLibraryStylesArray, один раз на
        /// библиотеку. По имени понятно, какая это форма основной надписи (StampForm.FromStyleName).
        /// </summary>
        private class StyleNames
        {
            private readonly object kompas5;
            private readonly short[] types;
            private readonly Dictionary<string, Dictionary<int, string>> cache = new Dictionary<string, Dictionary<int, string>>();

            public StyleNames(object kompas5, string kind)
            {
                this.kompas5 = kompas5;
                // Сначала библиотека стилей своего типа документа, потом остальные
                if (kind == DocumentKind.Specification) types = new[] { SpcLayouts, GraphicLayouts, TextLayouts };
                else if (kind == DocumentKind.Text) types = new[] { TextLayouts, GraphicLayouts, SpcLayouts };
                else types = new[] { GraphicLayouts, TextLayouts, SpcLayouts };
            }

            public string Get(string library, int style)
            {
                if (string.IsNullOrEmpty(library)) return "";
                foreach (short type in types)
                {
                    string name;
                    if (Load(library, type).TryGetValue(style, out name)) return name ?? "";
                }
                return "";
            }

            private Dictionary<int, string> Load(string library, short type)
            {
                string key = library.ToLowerInvariant() + "|" + type.ToString(CultureInfo.InvariantCulture);
                Dictionary<int, string> map;
                if (cache.TryGetValue(key, out map)) return map;
                map = new Dictionary<int, string>();
                cache[key] = map;
                object arr = null;
                try
                {
                    arr = Com.Call(kompas5, "ksGetLibraryStylesArray", library, type);
                    if (arr == null) return map;
                    int n = Convert.ToInt32(Com.Call(arr, "ksGetArrayCount"));
                    object par = Com.Call(kompas5, "GetParamStruct", LibraryStyleParam);
                    for (int i = 0; i < n && par != null; i++)
                    {
                        if (Convert.ToInt32(Com.Call(arr, "ksGetArrayItem", i, par)) == 0) continue;
                        map[Convert.ToInt32(Com.Call(par, "styleId"))] = Convert.ToString(Com.Call(par, "styleName")) ?? "";
                    }
                }
                catch (Exception)
                {
                    // Имён нет — формы не узнать, листы останутся без проверки
                }
                finally
                {
                    if (arr != null)
                    {
                        try { Com.Call(arr, "ksDeleteArray"); }
                        catch (Exception) { }
                    }
                }
                return map;
            }
        }
    }
}
