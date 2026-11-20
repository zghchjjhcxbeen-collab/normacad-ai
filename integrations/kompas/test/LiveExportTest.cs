// Проверка в живом КОМПАС-3D: настоящие документы, настоящий штатный конвертер PDF.
// Запуск — live-test.cmd <папка с копиями документов> [--attach].
// Работает только с копиями из этой папки: открывает их и закрывает без сохранения.
// КОМПАС тест запускает сам и сам закрывает. Уже запущенный КОМПАС чужой — в нём может идти работа:
// без ключа --attach тест к нему не подключается.
// Если расширение установлено (NormaCadSetup.exe), проверяется ещё, что КОМПАС видит его в «Приложениях».
// Коды выхода: 0 — всё прошло, 1 — есть ошибки, 2 — КОМПАС не запущен (не удалось или уже запущен чужой).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace NormaCad.Kompas.Tests
{
    internal static class LiveExportTest
    {
        private const string ProgId = "KOMPAS.Application.5";
        private static readonly string[] KompasProcesses = { "kStudy", "KOMPAS", "kHome" };
        // Отрезков в правке UnsavedEdits: 5 крупных и сотня мелких
        private const int Edits = 105;
        private static int failed;
        private static object k5;
        private static object app7;
        private static int baseline;

        [STAThread]
        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            bool attach = Array.IndexOf(args, "--attach") >= 0;
            string dir = null;
            foreach (string a in args) if (a != "--attach") dir = a;
            if (dir == null || !Directory.Exists(dir))
            {
                Console.WriteLine("Использование: LiveExportTest.exe <папка с копиями документов> [--attach]");
                return 1;
            }

            Dictionary<string, string> files = new Dictionary<string, string>();
            List<string> matrix = new List<string>();
            List<string> missing = new List<string>();
            foreach (string mask in new[] { "*.cdw", "*.spw", "*.kdw", "*.frw", "*.m3d", "*.a3d" })
            {
                string[] found = Directory.GetFiles(dir, mask);
                Array.Sort(found, StringComparer.OrdinalIgnoreCase);
                if (found.Length == 0) missing.Add(mask);
                else { files[mask] = found[0]; matrix.AddRange(found); }
            }

            if (missing.Count > 0)
            {
                Console.WriteLine("Матрица неполна: нет копий " + string.Join(", ", missing.ToArray()));
                return 1;
            }

            bool started;
            if (!Connect(attach, out started)) return 2;

            try
            {
                baseline = Count();
                Console.WriteLine("КОМПАС " + KompasVersion() + ": " + (started ? "запущен тестом" : "уже был запущен")
                    + ", открытых документов: " + baseline);

                Console.WriteLine("Матрица документов: " + matrix.Count);
                foreach (string path in matrix) MatrixDocument(path);
                if (files.ContainsKey("*.cdw")) UnsavedEdits(files["*.cdw"]);

                if (baseline > 0)
                    Console.WriteLine("skip  нет открытых документов: в КОМПАС открыты чужие документы (" + baseline + ")");
                else if (Count() > 0 || Com.Call(app7, "ActiveDocument") != null)
                    Fail("нет открытых документов", "тест не закрыл свои документы: " + OpenNames());
                else
                    NoDocuments();

                Library();
            }
            catch (Exception ex)
            {
                Fail("тест прерван", Describe(ex));
            }
            finally
            {
                if (started)
                {
                    try { Com.Call(app7, "Quit"); }
                    catch (Exception) { }
                }
            }

            if (missing.Count > 0)
                Console.WriteLine("Не было файлов типов: " + string.Join(", ", missing.ToArray()));
            Console.WriteLine(failed == 0 ? "LiveExportTest: OK" : "LiveExportTest: ошибок " + failed);
            return failed == 0 ? 0 : 1;
        }

        // ---- подключение к КОМПАС ----

        private static bool Connect(bool attach, out bool started)
        {
            started = false;
            try
            {
                List<string> running = Running();
                if (running.Count > 0 && !attach)
                {
                    Console.WriteLine("КОМПАС-3D уже запущен (" + string.Join(", ", running.ToArray()) + "): в нём может идти работа, "
                        + "тест к нему не подключается. Закройте КОМПАС или запустите тест с ключом --attach.");
                    return false;
                }
                if (running.Count > 0)
                {
                    k5 = Marshal.GetActiveObject(ProgId);
                }
                else
                {
                    Type t = Type.GetTypeFromProgID(ProgId);
                    if (t == null)
                    {
                        Console.WriteLine("КОМПАС-3D не найден: " + ProgId + " не зарегистрирован.");
                        return false;
                    }
                    Console.WriteLine("Запускаю КОМПАС-3D (около 30 с)...");
                    k5 = Activator.CreateInstance(t);
                    started = true;
                }
                app7 = Com.Call(k5, "ksGetApplication7");
                // Без этого модальные окна КОМПАС («Перестроить спецификацию?») вешают тест
                Com.Set(app7, "HideMessage", 1);
                try { Com.Set(k5, "Visible", true); } catch (Exception) { }
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Не удалось запустить или подключить КОМПАС-3D: " + Describe(ex));
                if (started && app7 != null)
                {
                    try { Com.Call(app7, "Quit"); } catch (Exception) { }
                }
                return false;
            }
        }

        private static List<string> Running()
        {
            List<string> found = new List<string>();
            foreach (string n in KompasProcesses)
            {
                Process[] ps = Process.GetProcessesByName(n);
                if (ps.Length > 0) found.Add(n + ".exe");
                foreach (Process p in ps) p.Dispose();
            }
            return found;
        }

        private static string KompasVersion()
        {
            foreach (string n in KompasProcesses)
                foreach (Process p in Process.GetProcessesByName(n))
                {
                    try { return p.MainModule.FileVersionInfo.FileVersion; }
                    catch (Exception) { }
                    finally { p.Dispose(); }
                }
            return "?";
        }

        // ---- проверки ----

        // Every fixture is exercised, including repeated document formats. Disk hashes cover
        // opening, export, fact reading and closing without saving.
        private static void MatrixDocument(string path)
        {
            string name = "матрица " + Path.GetFileName(path);
            string hash = Hash(path);
            DateTime modified = File.GetLastWriteTimeUtc(path);
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".m3d" || ext == ".a3d") Model(path);
            else if (ext == ".frw") Fragment(path);
            else ExportDocument(path);
            if (Hash(path) != hash || File.GetLastWriteTimeUtc(path) != modified)
                Fail(name, "копия исходного файла изменена");
            else Ok(name, "SHA256 и время исходного файла не изменены");
        }

        private static string Hash(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream file = File.OpenRead(path))
                return Convert.ToBase64String(sha.ComputeHash(file));
        }

        private static void ValidateType(object doc, string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            int expected = ext == ".cdw" ? 1 : ext == ".frw" ? 2 : ext == ".spw" ? 3
                : ext == ".m3d" ? 4 : ext == ".a3d" ? 5 : 6;
            int actual = Convert.ToInt32(Com.Call(doc, "DocumentType"));
            if (actual != expected) throw new InvalidOperationException("тип документа " + actual + ", ожидался " + expected);
        }

        private static string FactsProblem(ExportResult r, string path)
        {
            LocalFacts f = r.Facts;
            if (f == null) return "нет реальных client_facts";
            if (f.Error != "") return "ошибка чтения client_facts: " + f.Error;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            string expected = ext == ".cdw" ? DocumentKind.Drawing : ext == ".spw" ? DocumentKind.Specification
                : ext == ".kdw" ? DocumentKind.Text : DocumentKind.Fragment;
            if (f.DocType != expected) return "неверный тип client_facts: " + f.DocType;
            if (ext != ".frw" && (f.SheetsTotal < 1 || f.Sheets.Count < 1)) return "client_facts не содержат листов";
            Console.WriteLine("      факты: " + f.DocType + ", листов " + f.SheetsTotal + ", замечаний " + f.Remarks.Count);
            return null;
        }

        /// <summary>Открыть документ, экспортировать, проверить PDF; документ экспорт не меняет.</summary>
        private static void ExportDocument(string path)
        {
            string name = "экспорт " + Path.GetFileName(path);
            object doc = null;
            try
            {
                doc = Open(path);
                ValidateType(doc, path);
                string pathBefore = Str(doc, "PathName");
                bool changedBefore = Convert.ToBoolean(Com.Call(doc, "Changed"));
                Stopwatch sw = Stopwatch.StartNew();
                ExportResult r = Exporter.ExportActive(k5);
                sw.Stop();
                string problem = PdfProblem(r) ?? FactsProblem(r, path) ?? Untouched(doc, pathBefore, changedBefore);
                if (problem == null)
                    Ok(name, FileSize(r.Path) + " байт, " + sw.ElapsedMilliseconds + " мс");
                else Fail(name, problem);
            }
            catch (Exception ex)
            {
                Fail(name, Describe(ex));
            }
            finally { CloseDoc(doc); }
        }

        /// <summary>
        /// Фрагмент: штатный конвертер PDF его не выводит (Convert отвечает успехом, файла нет),
        /// поэтому уходит изображение — с объяснением в предупреждении.
        /// </summary>
        private static void Fragment(string path)
        {
            string name = "фрагмент " + Path.GetFileName(path);
            object doc = null;
            try
            {
                doc = Open(path);
                ValidateType(doc, path);
                string pathBefore = Str(doc, "PathName");
                bool changedBefore = Convert.ToBoolean(Com.Call(doc, "Changed"));
                ExportResult r = Exporter.ExportActive(k5);
                string commonProblem = FactsProblem(r, path) ?? Untouched(doc, pathBefore, changedBefore);
                if (commonProblem != null) { Fail(name, commonProblem); return; }
                if (r.Path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    string problem = PdfProblem(r);
                    if (problem == null) Ok(name, "PDF, " + FileSize(r.Path) + " байт");
                    else Fail(name, problem);
                }
                else if (r.Path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && FileSize(r.Path) > 1024
                    && PngProblem(r.Path) == null
                    && r.Warning != null && r.Warning.StartsWith("Экспорт в PDF не удался, отправлено изображение первого листа."))
                    Ok(name, "PNG " + FileSize(r.Path) + " байт с предупреждением: " + r.Warning.Replace("\r\n", " "));
                else
                    Fail(name, "файл " + r.Path + ", предупреждение: " + (r.Warning ?? "нет"));
            }
            catch (Exception ex)
            {
                Fail(name, Describe(ex));
            }
            finally { CloseDoc(doc); }
        }

        /// <summary>Несохранённые правки попадают в PDF, файл на диске и сам документ экспорт не меняет.</summary>
        private static void UnsavedEdits(string path)
        {
            string name = "несохранённые правки " + Path.GetFileName(path);
            object doc = null;
            try
            {
                FileInfo fi = new FileInfo(path);
                DateTime time0 = fi.LastWriteTimeUtc;
                long size0 = fi.Length;

                doc = Open(path);
                ValidateType(doc, path);
                ExportResult a = Exporter.ExportActive(k5);
                string problem = PdfProblem(a);
                if (problem != null) { Fail(name, "экспорт до правки: " + problem); return; }

                object doc2d = Com.Call(k5, "ActiveDocument2D");
                if (doc2d == null) { Fail(name, "ActiveDocument2D вернул null"); return; }
                double[][] segs =
                {
                    new[] { 0.0, 0.0, 400.0, 300.0 }, new[] { 0.0, 300.0, 400.0, 0.0 },
                    new[] { 0.0, 150.0, 400.0, 150.0 }, new[] { 200.0, 0.0, 200.0, 300.0 },
                    new[] { 50.0, 50.0, 350.0, 250.0 },
                };
                foreach (double[] s in segs) Com.Call(doc2d, "ksLineSeg", s[0], s[1], s[2], s[3], 1);
                // Ещё сотня разных отрезков: чтобы разница размера была заметнее шума PDF
                for (int i = 1; i <= 100; i++)
                    Com.Call(doc2d, "ksLineSeg", i * 3.7, 10.0 + i * 0.9, i * 3.7 + 12.3, 20.0 + i * 2.1, 1);

                if (!Convert.ToBoolean(Com.Call(doc, "Changed")))
                { Fail(name, "после ksLineSeg документ не помечен изменённым (Changed == false)"); return; }
                string pathBefore = Str(doc, "PathName");

                ExportResult b = Exporter.ExportActive(k5);
                problem = PdfProblem(b) ?? Untouched(doc, pathBefore, true);
                if (problem != null) { Fail(name, "экспорт после правки: " + problem); return; }

                // Штатный конвертер с сохранённого файла: правки там нет. Конвертируется копия: путь открытого
                // документа конвертер берёт из памяти КОМПАС — вместе с несохранённой правкой
                object conv = Com.Call(app7, "Converter", Path.Combine(
                    Convert.ToString(Com.Call(k5, "ksSystemPath", 5)), "Pdf2d.dll"));
                string copy = Path.Combine(Path.GetDirectoryName(b.Path), "disk" + Path.GetExtension(path));
                File.Copy(path, copy);
                string pdfC = Path.Combine(Path.GetDirectoryName(b.Path), "disk.pdf");
                Com.Call(conv, "Convert", copy, pdfC, 0, false);
                if (!File.Exists(pdfC)) { Fail(name, "конвертер не создал PDF с копии файла на диске"); return; }

                // Размер PDF от прогона к прогону гуляет на килобайты, поэтому считаем сами отрезки:
                // правка — 105 отрезков, они должны быть в PDF активного документа и не быть в PDF сохранённого файла
                int sa = Strokes(a.Path), sb = Strokes(b.Path), sc = Strokes(pdfC);
                fi.Refresh();
                bool diskSame = fi.LastWriteTimeUtc == time0 && fi.Length == size0;

                string detail = "контуров в PDF: до правки " + sa + ", после " + sb + ", сохранённый файл " + sc;
                if (!diskSame) Fail(name, "файл на диске изменился. " + detail);
                else if (sb - sa < Edits) Fail(name, "правка не попала в PDF. " + detail);
                else if (sb - sc < Edits) Fail(name, "в PDF сохранённого файла тоже есть правка — проверка ничего не доказывает. " + detail);
                else Ok(name, detail + "; файл на диске не изменён");
            }
            catch (Exception ex)
            {
                Fail(name, Describe(ex));
            }
            finally { CloseDoc(doc); }
        }

        /// <summary>3D-модель или сборка отклоняются понятным сообщением, КОМПАС жив.</summary>
        private static void Model(string path)
        {
            string name = "3D " + Path.GetFileName(path);
            object doc = null;
            try
            {
                doc = Open(path);
                ValidateType(doc, path);
                string pathBefore = Str(doc, "PathName");
                bool changedBefore = Convert.ToBoolean(Com.Call(doc, "Changed"));
                string msg = null;
                try { Exporter.ExportActive(k5); }
                catch (UserException ex) { msg = ex.Message; }
                string untouched = Untouched(doc, pathBefore, changedBefore);
                if (untouched != null) { Fail(name, untouched); return; }
                if (msg == null) { Fail(name, "исключения UserException не было"); return; }
                if (!msg.StartsWith("Активный документ — 3D-модель."))
                { Fail(name, "другой текст: " + msg); return; }
                try { Com.Call(app7, "ActiveDocument"); }
                catch (Exception ex) { Fail(name, "КОМПАС не отвечает после отказа: " + Describe(ex)); return; }
                Ok(name, "отказ: " + msg);
            }
            catch (Exception ex)
            {
                Fail(name, Describe(ex));
            }
            finally { CloseDoc(doc); }
        }

        /// <summary>Ни одного открытого документа.</summary>
        private static void NoDocuments()
        {
            string name = "нет открытых документов";
            try
            {
                string msg = null;
                try { Exporter.ExportActive(k5); }
                catch (UserException ex) { msg = ex.Message; }
                if (msg == null) Fail(name, "исключения UserException не было");
                else if (msg != "Откройте чертёж в КОМПАС-3D и повторите команду.") Fail(name, "другой текст: " + msg);
                else Ok(name, "отказ: " + msg);
            }
            catch (Exception ex)
            {
                Fail(name, Describe(ex));
            }
        }

        /// <summary>Установленное расширение КОМПАС видит в «Приложениях» вместе с его командами.</summary>
        private static void Library()
        {
            string name = "расширение в «Приложениях»";
            string dll = InstalledDll();
            if (dll == null)
            {
                Console.WriteLine("skip  " + name + ": расширение не установлено (NormaCadSetup.exe)");
                return;
            }
            try
            {
                object libs = Com.Call(Com.Call(app7, "LibraryManager"), "ProceduresLibraries");
                int n = Convert.ToInt32(Com.Call(libs, "Count"));
                int matches = 0, attached = 0;
                bool valid = false;
                for (int i = 0; i < n; i++)
                {
                    object lib = Com.Call(libs, "Item", i);
                    if (lib == null || !string.Equals(Str(lib, "PathName"), dll, StringComparison.OrdinalIgnoreCase)) continue;
                    matches++;
                    bool isAttached = Convert.ToBoolean(Com.Call(lib, "Attach"));
                    if (isAttached) attached++;
                    List<string> commands = new List<string>();
                    bool check = false, account = false;
                    object procs = Com.Call(lib, "Procedures");
                    int count = procs == null ? 0 : Convert.ToInt32(Com.Call(procs, "Count"));
                    for (int j = 0; j < count; j++)
                    {
                        object proc = Com.Call(procs, "Item", j);
                        if (proc == null) continue;
                        string command = Str(proc, "Name");
                        int id = Convert.ToInt32(Com.Call(proc, "Id"));
                        commands.Add(command + " (" + id + ")");
                        if (id == 1 && command == "Проверить документ") check = true;
                        if (id == 2 && command == "Аккаунт NormaCAD.PRO…") account = true;
                    }
                    Console.WriteLine("      приложение #" + i + ": Attach=" + isAttached + ", команды: " + string.Join(", ", commands.ToArray()));
                    if (isAttached && check && account) valid = true;
                }
                string detail = "совпадений пути " + matches + ", подключённых " + attached;
                if (valid) Ok(name, detail + "; обе команды с ID 1 и 2 найдены");
                else Fail(name, detail + "; нет подключённого приложения с обеими ожидаемыми командами");
            }
            catch (Exception ex)
            {
                Fail(name, Describe(ex));
            }
        }

        private static string InstalledDll()
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\NormaCAD.Kompas"))
            {
                string dir = k == null ? null : k.GetValue("InstallLocation") as string;
                if (string.IsNullOrEmpty(dir)) return null;
                string dll = Path.Combine(dir, "NormaCadKompas.rtw");
                return File.Exists(dll) ? dll : null;
            }
        }

        // ---- помощники ----

        private static object Open(string path)
        {
            object docs = Com.Call(app7, "Documents");
            object doc = Com.Call(docs, "Open", path, true, false);
            if (doc == null) throw new InvalidOperationException("КОМПАС не открыл " + path);
            return doc;
        }

        private static void CloseDoc(object doc)
        {
            if (doc == null) return;
            // 0 — kdDoNotSaveChanges: копии на диске остаются как были
            try { Com.Call(doc, "Close", 0); }
            catch (Exception ex) { Console.WriteLine("      не удалось закрыть документ: " + Describe(ex)); }
            Stopwatch sw = Stopwatch.StartNew();
            while (Count() > baseline && sw.ElapsedMilliseconds < 15000) System.Threading.Thread.Sleep(250);
        }

        /// <summary>Экспорт не должен переключить документ на другой файл или снять/поставить отметку изменений.</summary>
        private static string Untouched(object doc, string pathBefore, bool changedBefore)
        {
            string pathAfter = Str(doc, "PathName");
            if (!string.Equals(pathAfter, pathBefore, StringComparison.OrdinalIgnoreCase))
                return "документ переключился на другой файл: " + pathAfter;
            if (Convert.ToBoolean(Com.Call(doc, "Changed")) != changedBefore)
                return "экспорт изменил отметку несохранённых правок";
            return null;
        }

        /// <summary>
        /// Открытых видимых документов. После закрытия 3D-модели или сборки КОМПАС оставляет в Documents
        /// скрытые документы: Count их учитывает, а Item по их номерам возвращает null.
        /// </summary>
        private static int Count()
        {
            try
            {
                object docs = Com.Call(app7, "Documents");
                int n = Convert.ToInt32(Com.Call(docs, "Count"));
                int visible = 0;
                for (int i = 0; i < n; i++)
                    if (Com.Call(docs, "Item", i) != null) visible++;
                return visible;
            }
            catch (Exception) { return 0; }
        }

        private static string OpenNames()
        {
            try
            {
                object docs = Com.Call(app7, "Documents");
                List<string> names = new List<string>();
                int n = Convert.ToInt32(Com.Call(docs, "Count"));
                for (int i = 0; i < n; i++)
                {
                    object d = Com.Call(docs, "Item", i);
                    if (d != null) names.Add(Str(d, "Name") + " (тип " + Com.Call(d, "DocumentType") + ")");
                }
                return string.Join("; ", names.ToArray());
            }
            catch (Exception ex) { return Describe(ex); }
        }

        private static string Str(object o, string property)
        {
            try { return Convert.ToString(Com.Call(o, property)) ?? ""; }
            catch (Exception) { return ""; }
        }

        private static string PdfProblem(ExportResult r)
        {
            if (r == null || string.IsNullOrEmpty(r.Path)) return "экспорт ничего не вернул";
            if (!r.Path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return "не PDF: " + r.Path;
            if (!File.Exists(r.Path)) return "файла нет: " + r.Path;
            if (r.Warning != null) return "предупреждение: " + r.Warning;
            byte[] bytes = File.ReadAllBytes(r.Path);
            if (bytes.Length <= 1024) return "размер " + bytes.Length + " байт, меньше 1 КБ";
            if (Encoding.ASCII.GetString(bytes, 0, 4) != "%PDF") return "нет сигнатуры %PDF";
            return null;
        }

        private static string PngProblem(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
            if (bytes.Length < 24) return "короткий PNG";
            for (int i = 0; i < signature.Length; i++)
                if (bytes[i] != signature[i]) return "нет сигнатуры PNG";
            using (System.Drawing.Image image = System.Drawing.Image.FromFile(path))
                if (image.Width < 1 || image.Height < 1) return "пустое изображение";
            return null;
        }

        private static long FileSize(string path)
        {
            return new FileInfo(path).Length;
        }

        /// <summary>Сколько контуров начато (оператор m) во всех сжатых потоках PDF.</summary>
        private static int Strokes(string pdf)
        {
            byte[] b = File.ReadAllBytes(pdf);
            // latin-1: один байт — один символ, смещения в строке совпадают со смещениями в файле
            string s = Encoding.GetEncoding(28591).GetString(b);
            int count = 0, pos = 0;
            while ((pos = s.IndexOf("stream", pos, StringComparison.Ordinal)) >= 0)
            {
                if (pos >= 3 && string.CompareOrdinal(s, pos - 3, "end", 0, 3) == 0) { pos += 6; continue; }
                int start = pos + 6;
                if (start < s.Length && s[start] == '\r') start++;
                if (start < s.Length && s[start] == '\n') start++;
                int end = s.IndexOf("endstream", start, StringComparison.Ordinal);
                if (end < 0) break;
                string text = Inflate(b, start, end - start);
                if (text != null) count += Regex.Matches(text, @"(?<=\s)m(?=\s)").Count;
                pos = end + 9;
            }
            return count;
        }

        private static string Inflate(byte[] b, int start, int length)
        {
            // Поток FlateDecode — это zlib: два байта заголовка (0x78 ..), затем deflate
            if (length < 3 || b[start] != 0x78) return null;
            try
            {
                using (MemoryStream ms = new MemoryStream(b, start + 2, length - 2))
                using (DeflateStream z = new DeflateStream(ms, CompressionMode.Decompress))
                using (StreamReader r = new StreamReader(z, Encoding.GetEncoding(28591)))
                    return r.ReadToEnd();
            }
            catch (Exception) { return null; }
        }

        private static string Describe(Exception ex)
        {
            return ex.GetType().Name + ": " + ex.Message;
        }

        private static void Ok(string name, string detail)
        {
            Console.WriteLine("ok    " + name + (detail == null ? "" : " — " + detail));
        }

        private static void Fail(string name, string detail)
        {
            failed++;
            Console.WriteLine("FAIL  " + name + (detail == null ? "" : " — " + detail));
        }
    }
}
