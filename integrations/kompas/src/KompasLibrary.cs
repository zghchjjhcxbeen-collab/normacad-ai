// Прикладная библиотека КОМПАС-3D: команда «Проверить документ» в меню приложений.
// Берёт активный документ, экспортирует его в PDF и отдаёт окну проверки.
//
// Интерфейсы КОМПАС вызываются поздним связыванием (IDispatch): сборке не нужны
// Kompas6API5.dll и KompasAPI7.dll из SDK, и она не привязана к версии КОМПАС.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("NormaCAD.PRO для КОМПАС-3D")]
[assembly: AssemblyProduct("NormaCAD.PRO")]
[assembly: AssemblyVersion("0.1.1.0")]
[assembly: ComVisible(false)]

namespace NormaCad.Kompas
{
    // Contract transcribed from SDK Common/KompasLibrary.dll. Keep declaration
    // order: KOMPAS can query the dual interface and call its native vtable.
    [ComVisible(true), Guid("025A21B0-0192-4A7C-A3F0-CA54AAA4FADB")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    public interface IKompasLibrary
    {
        [DispId(17001)] int Version { get; }
        [DispId(17002), System.Runtime.CompilerServices.IndexerName("IsFunctionEnable")]
        bool this[int functionId] { get; }
        [DispId(17003)] string LibraryName { get; }
        [DispId(17004)] string DisplayLibraryName { get; }
        [DispId(17005)] string LibraryHelpFile { get; }
        [DispId(17006)] int ProtectNumber { get; }
        [DispId(17007)] int RunLibraryCommand(int command, int demoMode);
        [DispId(17008)] bool IsOnApplication7 { get; }
        [DispId(17009)] bool InitLibrary([MarshalAs(UnmanagedType.IDispatch)] object application);
        [DispId(17010)] bool BeginUnloadLibrary();
        [DispId(17011)] bool FillLibraryMenu(IKompasLibraryMenu menu);
        [DispId(17012)] bool GetLibraryCommandState(int command, out bool enable, out int check);
        [DispId(17013)] string GetDisableReason(int command);
        [DispId(17014)] bool FillContextPanel([MarshalAs(UnmanagedType.IDispatch)] object panel);
        [DispId(17015)] bool ContextPanelStyleComboChanged(string id, int type, int value);
        [DispId(17016)] [return: MarshalAs(UnmanagedType.IDispatch)] object GetKompasConverter();
        [DispId(17017)] bool CreateMacroFromSample(int reference);
    }

    [ComVisible(true), Guid("C35D07D7-0248-40C5-A335-1D4FF1610F0A")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    public interface IKompasLibraryMenu
    {
        [DispId(18001)] bool AddMenuCommand(int id, string title);
        [DispId(18002)] bool AddSeparator();
        [DispId(18003)] bool AddSubMenu(string title);
        [DispId(18004)] bool EndSubMenu();
    }
    /// <summary>Вызовы COM-объектов КОМПАС по имени.</summary>
    internal static class Com
    {
        private const BindingFlags Flags = BindingFlags.InvokeMethod | BindingFlags.GetProperty;

        public static object Call(object target, string name, params object[] args)
        {
            try
            {
                return target.GetType().InvokeMember(name, Flags, null, target, args);
            }
            catch (TargetInvocationException ex)
            {
                throw ex.InnerException ?? ex;
            }
        }

        public static void Set(object target, string name, object value)
        {
            try
            {
                target.GetType().InvokeMember(name, BindingFlags.SetProperty, null, target, new object[] { value });
            }
            catch (TargetInvocationException ex)
            {
                throw ex.InnerException ?? ex;
            }
        }
    }

    internal class ExportResult
    {
        public string Path;
        public string UploadName;
        public string Warning;
        /// <summary>Имя документа КОМПАС с расширением («Корпус.cdw»): окно показывает его, а не имя выгруженного PDF.</summary>
        public string DocumentName = "";
        /// <summary>Что прочитано на ПК без ИИ (#45); null — не читалось.</summary>
        public LocalFacts Facts = null;
    }

    internal static class Exporter
    {
        // ksDocumentTypeEnum (API7)
        private const int DocPart = 4;
        private const int DocAssembly = 5;
        private const int DocTechAssembly = 7;
        // ksRasterFormatParam.format (API5)
        private const int RasterPng = 3;

        /// <summary>
        /// Экспорт активного документа и чтение его основной надписи без ИИ (#45).
        /// Вызывать только из потока КОМПАС. Чтение — после экспорта, и его сбой
        /// экспорту не мешает: причина — в Facts.Error.
        /// </summary>
        public static ExportResult ExportActive(object kompas5)
        {
            ExportResult result = Export(kompas5);
            result.Facts = StampReader.ReadActive(kompas5);
            return result;
        }

        /// <summary>
        /// Экспорт активного документа. Вызывать только из потока КОМПАС.
        /// Способы пробуются от самого безопасного: штатный конвертер PDF ничего
        /// не меняет в документе, SaveAs — последним, с проверкой, что документ
        /// остался привязан к своему файлу.
        /// </summary>
        private static ExportResult Export(object kompas5)
        {
            object app7 = Com.Call(kompas5, "ksGetApplication7");
            object doc = app7 == null ? null : Com.Call(app7, "ActiveDocument");
            if (doc == null)
                throw new UserException("Откройте чертёж в КОМПАС-3D и повторите команду.");

            int type = SafeInt(doc, "DocumentType");
            if (type == DocPart || type == DocAssembly || type == DocTechAssembly)
                throw new UserException(
                    "Активный документ — 3D-модель. Нормоконтроль проверяет чертежи, "
                    + "спецификации и текстовые документы: откройте нужный документ и повторите.");

            string name = SafeStr(doc, "Name");
            string pathName = SafeStr(doc, "PathName");
            bool changed = SafeBool(doc, "Changed");

            string stem = CleanName(System.IO.Path.GetFileNameWithoutExtension(name));
            string dir = NewTempDir();
            string pdf = System.IO.Path.Combine(dir, stem + ".pdf");
            List<string> errors = new List<string>();
            ExportResult result = new ExportResult();
            result.DocumentName = name;

            object converter = PdfConverter(kompas5, app7, errors);
            if (converter != null)
            {
                // Пустое имя источника — активный документ вместе с несохранёнными правками
                if (Convert(converter, "", pdf, errors, "конвертер PDF, активный документ"))
                    return Done(result, pdf);
                if (pathName != "" && !changed && File.Exists(pathName)
                    && Convert(converter, pathName, pdf, errors, "конвертер PDF, файл с диска"))
                    return Done(result, pdf);
            }

            string png = System.IO.Path.Combine(dir, stem + ".png");
            if (Raster(kompas5, png, errors))
            {
                result.Warning = "Экспорт в PDF не удался, отправлено изображение первого листа."
                    + Reasons(errors);
                return Done(result, png);
            }

            if (SaveAsPdf(doc, pdf, errors))
            {
                result.Warning = "Штатный конвертер PDF не сработал, PDF сохранён командой «Сохранить как»."
                    + Reasons(errors);
                string now = SafeStr(doc, "PathName");
                if (!string.Equals(now, pathName, StringComparison.OrdinalIgnoreCase))
                    result.Warning = "ВНИМАНИЕ: КОМПАС переключил документ на файл PDF. "
                        + "Сохраните чертёж командой «Сохранить как…» в исходный файл .cdw.\r\n"
                        + result.Warning;
                return Done(result, pdf);
            }

            throw new UserException(
                "Не удалось экспортировать документ для проверки.\n\n"
                + string.Join("\n", errors.ToArray())
                + "\n\nСохраните чертёж в PDF вручную и проверьте его на сайте normacad.pro.");
        }

        /// <summary>Почему не сработали способы до удачного: видно в окне проверки, а не теряется.</summary>
        private static string Reasons(List<string> errors)
        {
            return errors.Count == 0 ? "" : "\r\nНе сработало: " + string.Join("; ", errors.ToArray()) + ".";
        }

        private static ExportResult Done(ExportResult result, string path)
        {
            result.Path = path;
            result.UploadName = System.IO.Path.GetFileName(path);
            return result;
        }

        private static IEnumerable<string> ConverterDirectories(object kompas5)
        {
            string bin = null;
            try
            {
                using (Process current = Process.GetCurrentProcess())
                    bin = Path.GetDirectoryName(current.MainModule.FileName);
            }
            catch (Exception) { }
            if (!string.IsNullOrEmpty(bin)) yield return bin;
            for (int i = 0; i <= 9; i++)
            {
                string path = null;
                try
                {
                    path = System.Convert.ToString(Com.Call(kompas5, "ksSystemPath", i));
                }
                catch (Exception) { }
                if (!string.IsNullOrEmpty(path)) yield return path;
            }
        }

        private static object PdfConverter(object kompas5, object app7, List<string> errors)
        {
            HashSet<string> tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool found = false;
            foreach (string dir in ConverterDirectories(kompas5))
            {
                string lib = System.IO.Path.Combine(dir, "Pdf2d.dll");
                if (!tried.Add(lib) || !File.Exists(lib)) continue;
                found = true;
                try
                {
                    object conv = Com.Call(app7, "Converter", lib);
                    if (conv != null) return conv;
                    errors.Add("конвертер PDF: КОМПАС не подключил " + lib);
                }
                catch (Exception ex)
                {
                    errors.Add("конвертер PDF: " + ex.Message);
                }
            }
            if (!found) errors.Add("конвертер PDF: Pdf2d.dll не найден");
            return null;
        }

        private static bool Convert(object converter, string source, string target, List<string> errors, string label)
        {
            try
            {
                File.Delete(target); // Предыдущая попытка могла оставить частичный файл.
                Com.Call(converter, "Convert", source, target, 0, false);
                if (HasPdfHeader(target)) return true;
                errors.Add(label + ": файл PDF не создан");
            }
            catch (Exception ex)
            {
                errors.Add(label + ": " + ex.Message);
            }
            return false;
        }

        // Фрагменты задаются в миллиметрах без границы листа: при 200 dpi
        // фрагмент шириной 8 м потребовал бы растр шириной более 60 000 px.
        private static double RasterScale(object kompas5, object doc2d, int dpi)
        {
            try
            {
                object bounds = Com.Call(kompas5, "GetParamStruct", 15); // koRectParam
                object reference = Com.Call(doc2d, "Reference");
                if (!System.Convert.ToBoolean(Com.Call(doc2d, "ksGetObjGabaritRect", reference, bounds))) return 1;
                object low = Com.Call(bounds, "GetpBot");
                object high = Com.Call(bounds, "GetpTop");
                double w = Math.Abs(System.Convert.ToDouble(Com.Call(high, "x")) - System.Convert.ToDouble(Com.Call(low, "x")));
                double h = Math.Abs(System.Convert.ToDouble(Com.Call(high, "y")) - System.Convert.ToDouble(Com.Call(low, "y")));
                double longest = Math.Max(w, h);
                if (longest > 0 && !double.IsInfinity(longest))
                    return Math.Min(1, 4094 * 25.4 / dpi / longest);
            }
            catch (Exception) { } // Старые версии API допускают экспорт без геометрических границ.
            return 1;
        }

        private static bool Raster(object kompas5, string target, List<string> errors)
        {
            try
            {
                object doc2d = Com.Call(kompas5, "ActiveDocument2D");
                if (doc2d == null)
                {
                    errors.Add("растр: документ не графический");
                    return false;
                }
                object par = Com.Call(doc2d, "RasterFormatParam");
                Com.Call(par, "Init");
                // SDK: Init zeros every field, including extScale. A zero scale can
                // make SaveAsToRasterFormat fail on fragments with real geometry.
                Com.Set(par, "format", (short)RasterPng);
                const int dpi = 200;
                Com.Set(par, "extScale", RasterScale(kompas5, doc2d, dpi));
                Com.Set(par, "extResolution", dpi);
                Com.Set(par, "colorBPP", (short)24);
                if (!System.Convert.ToBoolean(Com.Call(doc2d, "SaveAsToRasterFormat", target, par)))
                {
                    errors.Add("растр: SaveAsToRasterFormat вернул false");
                    return false;
                }
                if (!Exists(target))
                {
                    errors.Add("растр: файл не создан");
                    return false;
                }
                // Номер формата в разных версиях API мог значить не PNG: пересохраняем наверняка
                string tmp = target + ".tmp";
                using (Image img = Image.FromFile(target)) img.Save(tmp, ImageFormat.Png);
                File.Delete(target);
                File.Move(tmp, target);
                return true;
            }
            catch (Exception ex)
            {
                errors.Add("растр: " + ex.Message);
                return false;
            }
        }

        private static bool SaveAsPdf(object doc, string target, List<string> errors)
        {
            try
            {
                File.Delete(target);
                Com.Call(doc, "SaveAs", target);
                if (HasPdfHeader(target)) return true;
                errors.Add("SaveAs PDF: файл PDF не создан");
            }
            catch (Exception ex)
            {
                errors.Add("SaveAs PDF: " + ex.Message);
            }
            return false;
        }

        private static bool HasPdfHeader(string path)
        {
            if (!File.Exists(path)) return false;
            using (FileStream stream = File.OpenRead(path))
            {
                foreach (char expected in "%PDF-")
                    if (stream.ReadByte() != expected) return false;
            }
            return true;
        }

        private static bool Exists(string path)
        {
            return File.Exists(path) && new FileInfo(path).Length > 0;
        }

        private static string CleanName(string stem)
        {
            if (string.IsNullOrEmpty(stem)) return "Чертёж";
            foreach (char c in System.IO.Path.GetInvalidFileNameChars()) stem = stem.Replace(c, '_');
            return stem.Trim() == "" ? "Чертёж" : stem.Trim();
        }

        /// <summary>Своя папка на каждый экспорт; вчерашние экспорты убираем.</summary>
        private static string NewTempDir()
        {
            string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NormaCAD-Kompas");
            Directory.CreateDirectory(root);
            try
            {
                foreach (string old in Directory.GetDirectories(root))
                    if (Directory.GetCreationTimeUtc(old) < DateTime.UtcNow.AddDays(-1))
                        Directory.Delete(old, true);
            }
            catch (Exception) { }
            string dir = System.IO.Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static string SafeStr(object doc, string name)
        {
            try { return System.Convert.ToString(Com.Call(doc, name)) ?? ""; }
            catch (Exception) { return ""; }
        }

        private static int SafeInt(object doc, string name)
        {
            try { return System.Convert.ToInt32(Com.Call(doc, name)); }
            catch (Exception) { return 0; }
        }

        private static bool SafeBool(object doc, string name)
        {
            // Не смогли узнать — считаем изменённым: файл с диска тогда не отправим
            try { return System.Convert.ToBoolean(Com.Call(doc, name)); }
            catch (Exception) { return true; }
        }
    }

    /// <summary>Точка входа, которую вызывает КОМПАС.</summary>
    [ComVisible(true)]
    [Guid("6B0F2C7E-5D41-4A9B-9E3A-2F8C1D7A4B65")]
    [ProgId("NormaCAD.KompasCheck")]
    [ClassInterface(ClassInterfaceType.AutoDual)]
    [ComDefaultInterface(typeof(IKompasLibrary))]
    public class KompasCheck : IKompasLibrary
    {
        [ComVisible(false)]
        public static void RunNative(int command)
        {
            if (command != 1 && command != 2) return;
            object kompas = Marshal.GetActiveObject(KompasLink.ProgId);
            new KompasCheck().ExternalRunCommand((short)command, 0, kompas);
        }

        private object application;
        public int Version { get { return 1; } }
        [System.Runtime.CompilerServices.IndexerName("IsFunctionEnable")]
        public bool this[int functionId]
        {
            get { return functionId == 17003 || functionId == 17007 ||
                functionId == 17009 || functionId == 17010 || functionId == 17011 ||
                functionId == 17012 || functionId == 17013; }
        }
        public string LibraryName { get { return GetLibraryName(); } }
        public string DisplayLibraryName { get { return Title; } }
        public string LibraryHelpFile { get { return ""; } }
        public int ProtectNumber { get { return 0; } }
        public bool IsOnApplication7 { get { return false; } }
        public bool InitLibrary(object value) { application = value; return value != null; }
        public bool BeginUnloadLibrary() { application = null; return true; }
        public bool FillLibraryMenu(IKompasLibraryMenu menu)
        {
            if (menu == null) return false;
            return menu.AddMenuCommand(1, "Проверить документ") &&
                menu.AddMenuCommand(2, "Аккаунт NormaCAD.PRO…");
        }
        public int RunLibraryCommand(int command, int demoMode)
        {
            if (application == null || command < 1 || command > 2) return 0;
            ExternalRunCommand((short)command, (short)demoMode, application);
            return 1;
        }
        public bool GetLibraryCommandState(int command, out bool enable, out int check)
        { enable = command == 1 || command == 2; check = 0; return true; }
        public string GetDisableReason(int command) { return ""; }
        public bool FillContextPanel(object panel) { return false; }
        public bool ContextPanelStyleComboChanged(string id, int type, int value) { return false; }
        public object GetKompasConverter() { return null; }
        public bool CreateMacroFromSample(int reference) { return false; }
        private const string Title = "NormaCAD.PRO";
        private const short MenuItem = 1;
        private const short EndMenu = 3;

        [return: MarshalAs(UnmanagedType.BStr)]
        public string GetLibraryName()
        {
            return "NormaCAD.PRO — проверка перед нормоконтролем";
        }

        // KOMPAS v23 reads the native MENU (2000) from this module. A managed
        // embedded resource is insufficient. This matches SDK CSharp Step2.
        public object ExternalGetResourceModule()
        {
            return Assembly.GetExecutingAssembly().Location;
        }

        [return: MarshalAs(UnmanagedType.BStr)]
        public string ExternalMenuItem(short number, ref short itemType, ref short command)
        {
            itemType = MenuItem;
            switch (number)
            {
                case 1:
                    command = 1;
                    return "Проверить документ";
                case 2:
                    command = 2;
                    return "Аккаунт NormaCAD.PRO…";
                default:
                    command = -1;
                    itemType = EndMenu;
                    return string.Empty;
            }
        }

        public void ExternalRunCommand(
            [In] short command, [In] short mode, [In, MarshalAs(UnmanagedType.IDispatch)] object kompas_)
        {
            // Необработанное исключение уронило бы КОМПАС вместе с несохранённой работой
            try
            {
                if (command == 1)
                {
                    ExportResult r;
                    Cursor.Current = Cursors.WaitCursor;
                    try { r = Exporter.ExportActive(kompas_); }
                    finally { Cursor.Current = Cursors.Default; }
                    Launcher.Open(r, kompas_);
                }
                else if (command == 2)
                {
                    Launcher.Open(null, kompas_);
                }
            }
            catch (UserException ex)
            {
                MessageBox.Show(ex.Message, Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка расширения: " + ex.Message, Title,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // КОМПАС узнаёт .NET-библиотеку по ключу Kompas_Library у её CLSID
        [ComRegisterFunction]
        public static void RegisterKompasLib(Type t)
        {
            string keyName = @"SOFTWARE\Classes\CLSID\{" + t.GUID.ToString().ToUpperInvariant() + "}";
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(keyName, true))
            {
                if (key == null) return;
                key.CreateSubKey("Kompas_Library").Close();
                using (RegistryKey inproc = key.OpenSubKey("InprocServer32", true))
                {
                    if (inproc != null)
                        inproc.SetValue(null, System.IO.Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.System), "mscoree.dll"));
                }
            }
        }

        [ComUnregisterFunction]
        public static void UnregisterKompasLib(Type t)
        {
            string keyName = @"SOFTWARE\Classes\CLSID\{" + t.GUID.ToString().ToUpperInvariant() + "}";
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(keyName, true))
            {
                if (key != null) key.DeleteSubKey("Kompas_Library", false);
            }
        }
    }
}
