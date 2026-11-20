// Живой прогон чтения основной надписи и правил ПК (#45) на КОПИЯХ документов.
// Запуск — live-stamp.cmd <папка с копиями>. Только свой КОМПАС: если КОМПАС уже
// запущен, это КОМПАС пользователя — прогон отказывается (код 2). Перед запуском —
// замок .claude\kompas.lock (README.txt). Код выхода 0 — все документы прочитаны.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace NormaCad.Kompas.Tests
{
    internal static class LiveStampTest
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            if (args.Length < 1 || !Directory.Exists(args[0]))
            {
                Console.WriteLine("Использование: live-stamp.cmd <папка с копиями .cdw .spw .kdw .frw>");
                return 1;
            }
            try
            {
                Marshal.GetActiveObject("KOMPAS.Application.5");
                Console.WriteLine("КОМПАС уже запущен — это КОМПАС пользователя, прогон его не трогает.");
                return 2;
            }
            catch (Exception) { }

            object k5 = Activator.CreateInstance(Type.GetTypeFromProgID("KOMPAS.Application.5"));
            object app7 = Com.Call(k5, "ksGetApplication7");
            int failed = 0, docs = 0, remarks = 0;
            try
            {
                // Без этого модальные окна КОМПАС («Перестроить спецификацию?») вешают прогон
                Com.Set(app7, "HideMessage", 1);
                List<string> files = new List<string>();
                foreach (string mask in new[] { "*.cdw", "*.spw", "*.kdw", "*.frw" })
                    files.AddRange(Directory.GetFiles(args[0], mask, SearchOption.AllDirectories));
                files.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (string path in files)
                {
                    object doc = null;
                    try
                    {
                        doc = Com.Call(Com.Call(app7, "Documents"), "Open", path, true, true);
                        LocalFacts f = StampReader.ReadActive(k5);
                        string json = ClientFacts.Json(f);
                        docs++;
                        Console.WriteLine("=== " + Path.GetFileName(path) + " | " + f.DocType + " " + f.DocTypeLabel
                            + " | листов " + f.SheetsTotal + (f.Error != "" ? " | ОШИБКА " + f.Error : "")
                            + (f.Note != "" ? " | " + f.Note : ""));
                        foreach (LocalSheet s in f.Sheets)
                            Console.WriteLine("  лист " + s.Number + " " + s.Format + " " + s.Form + " «" + s.StyleName + "» "
                                + "2=«" + StampCheck.Graph(s, 2) + "» 7=«" + StampCheck.Graph(s, 7) + "» 8=«" + StampCheck.Graph(s, 8) + "»");
                        foreach (LocalRemark r in f.Remarks)
                            Console.WriteLine("  ! лист " + r.Page + ": " + r.Title + " [" + r.Rule + "] " + r.Detail);
                        remarks += f.Remarks.Count;
                        // Имя и путь файла на сервер не уходят
                        if (f.Error != "" || json.Contains(Path.GetDirectoryName(path).Replace("\\", "\\\\")))
                        {
                            failed++;
                            Console.WriteLine("  FAIL " + (f.Error != "" ? "не прочитан" : "в client_facts путь файла"));
                        }
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        Console.WriteLine("=== " + Path.GetFileName(path) + " | FAIL " + ex.Message);
                    }
                    finally
                    {
                        if (doc != null) { try { Com.Call(doc, "Close", 0); } catch (Exception) { } }
                    }
                }
            }
            finally
            {
                try { Com.Call(app7, "Quit"); } catch (Exception) { }
            }
            Console.WriteLine("Документов: " + docs + ", замечаний ПК: " + remarks + ", ошибок: " + failed);
            return failed == 0 ? 0 : 1;
        }
    }
}
