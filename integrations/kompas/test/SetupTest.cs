// Проверка установщика без КОМПАС и без записи в настоящие HKCU и APPDATA: временные папки (в пути — пробел,
// кириллица и скобки, как у многих учётных записей Windows) и временный ключ HKCU\Software\NormaCAD-SetupTest-<guid>,
// который в конце удаляется. Вшитые файлы берутся из настоящего bin\NormaCadSetup.exe; регистрация COM
// сверяется с тем, что для той же dll пишет RegAsm /codebase (в файл .reg, без прав администратора).
// Запуск — test.cmd, код выхода 0 — все проверки прошли.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using Microsoft.Win32;
using NormaCad.Kompas.Setup;

namespace NormaCad.Kompas.Tests
{
    public static class SetupTest
    {
        private const string OurId = "6B0F2C7E-5D41-4A9B-9E3A-2F8C1D7A4B65";
        private const string ForeignA = "CD7F0826-F4BD-431C-8C51-6C5F096E8593";
        private const string ForeignB = "APP_KomLib";
        private const string ForeignC = "APP_KompasHome";
        private const string UninstallKey = @"Microsoft\Windows\CurrentVersion\Uninstall\NormaCAD.Kompas";
        private const string ClsidKey = @"Classes\CLSID\{" + OurId + "}";

        private static int failed;

        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            if (args.Length < 1 || !File.Exists(args[0]))
            {
                Console.WriteLine("Использование: SetupTest.exe <bin\\NormaCadSetup.exe>");
                return 1;
            }
            string tmp = Path.Combine(Path.GetTempPath(), "NormaCAD-SetupTest-" + Guid.NewGuid().ToString("N"));
            string keyPath = @"Software\NormaCAD-SetupTest-" + Guid.NewGuid().ToString("N");
            RegistryView view = Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default;
            RegistryKey hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
            RegistryKey software = hkcu.CreateSubKey(keyPath);
            try
            {
                Run(Path.GetFullPath(args[0]), Path.Combine(tmp, "Пользователь (тест)"), software);
                TestSignOut(Path.Combine(tmp, "выход"));
            }
            catch (Exception ex)
            {
                Check("тест не упал с исключением: " + ex, false);
            }
            finally
            {
                software.Close();
                try { hkcu.DeleteSubKeyTree(keyPath, false); } catch (Exception) { }
                try { Directory.Delete(tmp, true); } catch (Exception) { }
            }
            Check("временный ключ реестра удалён", hkcu.OpenSubKey(keyPath) == null);
            Check("временные папки удалены", !Directory.Exists(tmp));
            hkcu.Close();
            Console.WriteLine(failed == 0 ? "SetupTest: OK" : "SetupTest: ошибок " + failed);
            return failed == 0 ? 0 : 1;
        }

        private static void Run(string setupExe, string root, RegistryKey software)
        {
            // ---- вшитые файлы настоящего установщика ----
            Assembly setup = Assembly.ReflectionOnlyLoadFrom(setupExe);
            byte[] dllBytes = Resource(setup, Installer.DllName);
            byte[] entryBytes = Resource(setup, Installer.EntryName);
            byte[] checkBytes = Resource(setup, Installer.CheckName);
            Check("в NormaCadSetup.exe вшиты NormaCadKompas.dll и NormaCadCheck.exe", dllBytes != null && checkBytes != null);
            Check("в установщике есть библиотека команд RTW", entryBytes != null);
            if (dllBytes == null || checkBytes == null || entryBytes == null) return;
            Directory.CreateDirectory(root);
            string probe = Path.Combine(root, "probe.dll");
            File.WriteAllBytes(probe, dllBytes);
            AssemblyName real = AssemblyName.GetAssemblyName(probe);
            Check("версия dll совпадает с версией клиента " + ApiClient.ClientVersion,
                real.Version.ToString(3) == ApiClient.ClientVersion);
            File.Delete(probe);

            // «Старая версия» для проверки обновления — любая управляемая сборка: эта, версии 0.0.0.0
            byte[] oldDll = File.ReadAllBytes(Assembly.GetExecutingAssembly().Location);
            byte[] dllPayload = oldDll;
            bool brokenCheckExe = false;

            SetupOptions o = new SetupOptions();
            o.InstallDir = Path.Combine(root, @"Local\NormaCAD\Kompas");
            o.LogDir = Path.Combine(root, @"Local\NormaCAD");
            o.AsconRoot = Path.Combine(root, @"Roaming\ASCON");
            o.UserDataDir = Path.Combine(root, @"Roaming\NormaCAD");
            o.ExportDir = Path.Combine(root, @"Temp\NormaCAD-Kompas");
            o.SoftwareKey = software;
            o.SelfPath = setupExe;
            o.OpenPayload = delegate (string name)
            {
                if (name == Installer.CheckName && brokenCheckExe) throw new IOException("нет места на диске");
                if (name == Installer.EntryName) return new MemoryStream(entryBytes);
                return new MemoryStream(name == Installer.DllName ? dllPayload : checkBytes);
            };
            List<string> running = new List<string>();
            o.RunningProcesses = delegate { return new List<string>(running); };
            int signOuts = 0;
            bool? signOutAnswer = true;
            o.SignOut = delegate (SetupOptions so)
            {
                signOuts++;
                return File.Exists(Path.Combine(so.UserDataDir, "kompas.cfg")) ? signOutAnswer : null;
            };

            // ---- профили КОМПАС ----
            string kompasRoot = Path.Combine(o.AsconRoot, "KOMPAS-3D");
            string v23 = Path.Combine(kompasRoot, "23"), v24 = Path.Combine(kompasRoot, "24");
            string v25 = Path.Combine(kompasRoot, "25"), v26 = Path.Combine(kompasRoot, "26");
            string v27 = Path.Combine(kompasRoot, "27");
            foreach (string d in new[] { v23, v24, v25, v26, v27 }) Directory.CreateDirectory(d);
            string kit23 = Path.Combine(v23, "kStudy.kit.config");
            File.WriteAllText(kit23, "<?xml version=\"1.0\" encoding=\"utf-16\"?><Kit_Config><Groups>"
                + "<Group groupName=\"foreign\"><Application id=\"" + OurId + "\" /></Group>"
                + "<Group><Application id=\"foreign\" path=\"keep\" /><Application id=\"" + OurId.ToLowerInvariant()
                + "\" path=\"old\" /><Application id=\"" + OurId + "\" path=\"old2\" /></Group></Groups></Kit_Config>", Encoding.Unicode);
            string kit25 = Path.Combine(v25, "kHome.kit.config");
            File.WriteAllText(kit25, "<Kit_Config><Groups><Group /></Groups></Kit_Config>", Encoding.Unicode);
            string absentKit = Path.Combine(v24, "KOMPAS.kit.config");
            string cfg23 = Path.Combine(v23, Installer.ConfigName);
            // Как пишет сам КОМПАС: без объявления <?xml?>, без BOM; наша старая запись — в нижнем регистре
            // и со старым путём, в середине списка
            File.WriteAllText(cfg23,
                "<XmlRoot>\r\n  <Product id=\"kStudy\">\r\n"
                + "    <App id=\"" + ForeignA + "\" path=\"C:\\a\\a.dll\" />\r\n"
                + "    <App id=\"" + OurId.ToLowerInvariant() + "\" path=\"C:\\old\\NormaCadKompas.dll\" />\r\n"
                + "    <App id=\"" + ForeignB + "\" path=\"C:\\b\\b.rtw\" />\r\n"
                + "  </Product>\r\n  <Product id=\"KOMPAS\">\r\n"
                + "    <App id=\"" + ForeignC + "\" path=\"C:\\c\\c.rtw\" />\r\n"
                + "  </Product>\r\n</XmlRoot>", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(v23, "kStudy.cfg"), "x");
            // 24: КОМПАС запускался, но списка приложений ещё не записал — файл не создаётся
            File.WriteAllText(Path.Combine(v24, "kStudy.cfg"), "x");
            // 25: с BOM и объявлением, без разделов Product; редакция — из version.xml
            string cfg25 = Path.Combine(v25, Installer.ConfigName);
            File.WriteAllText(cfg25, "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<XmlRoot />", new UTF8Encoding(true));
            File.WriteAllText(Path.Combine(v25, "version.xml"),
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<config>\r\n    <productInfo name=\"kHome\" version=\"25.0.1\" />\r\n</config>",
                new UTF8Encoding(true));
            // 26: испорченный файл — предупреждение, файл не трогается
            string cfg26 = Path.Combine(v26, Installer.ConfigName);
            File.WriteAllText(cfg26, "<XmlRoot><Product id=\"kStudy\">");
            byte[] bytes26 = File.ReadAllBytes(cfg26);
            // 27: только version.xml с новой редакцией — её процесс тоже считается КОМПАС
            File.WriteAllText(Path.Combine(v27, "version.xml"), "<config><productInfo name=\"kTest\" /></config>");
            string cfg24 = Path.Combine(v24, Installer.ConfigName);
            string dll = Path.Combine(o.InstallDir, Installer.DllName);
            string entry = Path.Combine(o.InstallDir, Installer.EntryName);
            string checkExe = Path.Combine(o.InstallDir, Installer.CheckName);
            string selfCopy = Path.Combine(o.InstallDir, Installer.SetupName);

            // ---- первая установка: «старая» версия ----
            Directory.CreateDirectory(o.InstallDir);
            string legacyUi = Path.Combine(o.InstallDir, "NormaCadKompas.xml");
            File.WriteAllText(legacyUi, "<application/>");
            InstallResult r = Installer.Install(o);
            Check("старое описание меню больше не влияет на загрузку RTW", !File.Exists(legacyUi));
            XElement kitGroup = XDocument.Load(kit23).Root.Element("Groups").Elements("Group").Last();
            XElement kitApp = kitGroup.Elements("Application").Single(a => (string)a.Attribute("id") == Installer.EntryName);
            Check("kit: entry and deduplication", kitApp.Attributes().Count() == 6
                && (string)kitApp.Attribute("libType") == "lbt_rtw_ocx"
                && (string)kitApp.Attribute("path") == Path.GetFullPath(entry) && (string)kitApp.Attribute("libName") == "NormaCAD.PRO"
                && (string)kitApp.Attribute("displayName") == "NormaCAD.PRO"
                && (string)kitApp.Attribute("autostart") == "true");
            Check("kit: foreign entries and UTF-16 preserved", Starts(kit23, 0xFF)
                && kitGroup.Elements("Application").Count() == 2 && (string)kitGroup.Elements("Application").First().Attribute("path") == "keep"
                && XDocument.Load(kit23).Descendants("Group").First().Elements("Application").Count() == 1
                && XDocument.Load(kit25).Descendants("Application").Count() == 1 && !File.Exists(absentKit));
            byte[] kitBefore = File.ReadAllBytes(kit23);
            Check("установка: dll, exe проверки и копия установщика на месте",
                File.Exists(dll) && File.Exists(checkExe) && File.Exists(selfCopy)
                && File.ReadAllBytes(entry).SequenceEqual(entryBytes));
            Check("установка: версия из сборки, прежней не было", r.Version == "0.0.0" && r.Previous == "");
            Check("установка: пункт меню в двух профилях из пяти, оба файла переписаны",
                r.ConfigsReady == 2 && r.ConfigsUpdated == 2);
            Check("установка: испорченный файл — предупреждение с его путём",
                r.Warnings.Count == 1 && r.Warnings[0].Contains(cfg26));
            Check("config 23: чужие записи остались и в прежнем порядке",
                string.Join(",", Ids(cfg23, "kStudy").Where(i => i != Installer.EntryName).ToArray()) == ForeignA + "," + ForeignB
                && AppPath(cfg23, "KOMPAS", ForeignC) == "C:\\c\\c.rtw");
            Check("config 23: наша запись в каждом Product с путём RTW и идентификатором по имени файла",
                AppPath(cfg23, "kStudy", Installer.EntryName) == entry && AppPath(cfg23, "KOMPAS", Installer.EntryName) == entry
                && !Ids(cfg23, "kStudy").Any(i => string.Equals(i, OurId, StringComparison.OrdinalIgnoreCase)));
            Check("config 23: старая запись заменена, не продублирована", CountOurs(cfg23) == 2);
            Check("config 23: как у КОМПАС — без объявления, без BOM, отступ два пробела",
                !Starts(cfg23, 0xEF) && File.ReadAllText(cfg23).StartsWith("<XmlRoot>\r\n  <Product id=\"kStudy\">"));
            Check("config 24: файла не было — не создан", !File.Exists(cfg24));
            Check("config 25: раздел kHome из version.xml, наша запись в нём",
                AppPath(cfg25, "kHome", Installer.EntryName) == entry && CountOurs(cfg25) == 1);
            Check("config 25: BOM и объявление сохранены",
                Starts(cfg25, 0xEF) && File.ReadAllText(cfg25).TrimStart('\uFEFF').StartsWith("<?xml"));
            Check("config 26: испорченный файл не тронут", File.ReadAllBytes(cfg26).SequenceEqual(bytes26));
            Check("установка: временных файлов не осталось", NoLeftovers(root));
            Check("сводка: команда в меню «Приложения»", SetupForm.Summary(r).Contains("в меню «Приложения» появится"));

            // ---- обновление до настоящей версии ----
            dllPayload = dllBytes;
            InstallResult up = Installer.Install(o);
            Check("обновление: прежняя версия 0.0.0, новая — " + ApiClient.ClientVersion,
                up.Previous == "0.0.0" && up.Version == ApiClient.ClientVersion);
            Check("обновление: путь не менялся — профили не переписаны", up.ConfigsReady == 2 && up.ConfigsUpdated == 0);
            Check("обновление: файлы — из установщика", File.ReadAllBytes(dll).SequenceEqual(dllBytes)
                && File.ReadAllBytes(checkExe).SequenceEqual(checkBytes)
                && File.ReadAllBytes(selfCopy).SequenceEqual(File.ReadAllBytes(setupExe)));
            Check("обновление: подраздел версии только новый (" + real.Version + ")",
                string.Join(",", SubKeys(software, ClsidKey + @"\InprocServer32")) == real.Version.ToString());
            Check("обновление: Assembly — полное имя новой сборки",
                Value(software, ClsidKey + @"\InprocServer32", "Assembly") == real.FullName);
            Check("обновление: версия в «Приложениях» Windows", Installer.InstalledVersion(o) == ApiClient.ClientVersion);
            Check("сводка обновления", SetupForm.Summary(up).StartsWith("Обновлено: версия 0.0.0 → " + ApiClient.ClientVersion));

            CompareWithRegAsm(software, dll);
            CheckComExtras(software);
            CheckUninstallEntry(software, o, selfCopy);

            // ---- переустановка той же версии ----
            byte[] before23 = File.ReadAllBytes(cfg23), before25 = File.ReadAllBytes(cfg25);

            InstallResult again = Installer.Install(o);
            Check("kit: repeated install is unchanged", File.ReadAllBytes(kit23).SequenceEqual(kitBefore));
            Check("переустановка: профили не переписаны, пункт меню на месте",
                again.ConfigsReady == 2 && again.ConfigsUpdated == 0
                && File.ReadAllBytes(cfg23).SequenceEqual(before23) && File.ReadAllBytes(cfg25).SequenceEqual(before25));
            Check("переустановка: сводка — «переустановлена», про меню, а не «не запускался»",
                SetupForm.Summary(again).StartsWith("Версия " + ApiClient.ClientVersion + " переустановлена.")
                && SetupForm.Summary(again).Contains("появится"));

            // ---- сбой на середине: старые файлы целы ----
            brokenCheckExe = true;
            byte[] keepDll = File.ReadAllBytes(dll);
            bool threw = false;
            try { Installer.Install(o); }
            catch (SetupException ex) { threw = ex.Message.Contains(Installer.CheckName); }
            brokenCheckExe = false;
            Check("сбой записи: понятная ошибка с именем файла", threw);
            Check("сбой записи: установленная dll не тронута, временных файлов нет",
                File.ReadAllBytes(dll).SequenceEqual(keepDll) && NoLeftovers(root));

            // ---- КОМПАС запущен: ничего не меняется ----
            string assemblyBefore = Value(software, ClsidKey + @"\InprocServer32", "Assembly");
            DateTime dllTime = File.GetLastWriteTimeUtc(dll);
            dllPayload = oldDll;
            running.Add("kStudy");
            string msg = Refused(delegate { Installer.Install(o); });
            Check("КОМПАС запущен: установка отказывает с понятным текстом",
                msg != null && msg.StartsWith("Закройте КОМПАС-3D") && msg.Contains("kStudy.exe"));
            Check("КОМПАС запущен: файлы, реестр и профили не тронуты",
                File.GetLastWriteTimeUtc(dll) == dllTime && File.ReadAllBytes(cfg23).SequenceEqual(before23)
                && Value(software, ClsidKey + @"\InprocServer32", "Assembly") == assemblyBefore);
            msg = Refused(delegate { Installer.Uninstall(o); });
            Check("КОМПАС запущен: удаление отказывает и ничего не удаляет",
                msg != null && Installer.IsInstalled(o) && File.Exists(dll) && CountOurs(cfg23) == 2);
            running.Clear();
            running.Add(Installer.CheckProcess);
            msg = Refused(delegate { Installer.Install(o); });
            Check("открыто окно проверки: просьба закрыть именно его",
                msg != null && msg.StartsWith("Закройте окно проверки NormaCAD.PRO"));
            running.Clear();
            dllPayload = dllBytes;

            List<string> names = Installer.ProcessNames(o);
            Check("процессы КОМПАС: известные редакции, редакция из профиля и окно проверки",
                names.Contains("kStudy") && names.Contains("KOMPAS") && names.Contains("kHome")
                && names.Contains("kTest") && names.Contains(Installer.CheckProcess));
            string me = Process.GetCurrentProcess().ProcessName;
            Check("поиск процессов находит запущенный",
                Installer.FindRunning(new[] { "нет-такого-процесса", me }).SequenceEqual(new[] { me }));

            // ---- удаление (установщик запущен не из папки установки) ----
            Directory.CreateDirectory(o.UserDataDir);
            File.WriteAllText(Path.Combine(o.UserDataDir, "kompas.cfg"), "token=x");
            File.WriteAllText(Path.Combine(o.UserDataDir, "чужой.txt"), "x");
            File.WriteAllText(Path.Combine(o.LogDir, "kompas.log"), "x");
            File.WriteAllText(Path.Combine(o.LogDir, "kompas.log.1"), "x");
            Directory.CreateDirectory(Path.Combine(o.ExportDir, "abc"));
            File.WriteAllText(Path.Combine(o.ExportDir, @"abc\Вал.pdf"), "%PDF");
            UninstallResult u = Installer.Uninstall(o);
            Check("kit: only our entry removed", XDocument.Load(kit23).Descendants("Group").Last().Elements("Application").Count() == 1
                && XDocument.Load(kit23).Descendants("Group").First().Elements("Application").Count() == 1
                && !XDocument.Load(kit25).Descendants("Application").Any() && !File.Exists(absentKit));
            Check("удаление: наших App нет ни в одном профиле", CountOurs(cfg23) == 0 && CountOurs(cfg25) == 0);
            Check("удаление: чужие App остались",
                AppPath(cfg23, "kStudy", ForeignA) == "C:\\a\\a.dll" && AppPath(cfg23, "kStudy", ForeignB) == "C:\\b\\b.rtw"
                && AppPath(cfg23, "KOMPAS", ForeignC) == "C:\\c\\c.rtw");
            Check("удаление: ключи COM и строка в «Приложениях» Windows убраны",
                software.OpenSubKey(ClsidKey) == null && software.OpenSubKey(@"Classes\NormaCAD.KompasCheck") == null
                && software.OpenSubKey(UninstallKey) == null && !Installer.IsInstalled(o));
            Check("удаление: вход на сервере закрыт до удаления настроек", signOuts == 1 && u.SignedOut == true
                && SetupForm.Summary(u).Contains("Вход на сервере закрыт"));
            Check("удаление: папка установки и журналы убраны", !Directory.Exists(o.InstallDir) && !Directory.Exists(o.LogDir));
            Check("удаление: настройки убраны, чужой файл рядом с ними остался",
                !File.Exists(Path.Combine(o.UserDataDir, "kompas.cfg")) && File.Exists(Path.Combine(o.UserDataDir, "чужой.txt")));
            Check("удаление: временные экспорты чертежей убраны", !Directory.Exists(o.ExportDir));
            Check("удаление: отложенная команда не нужна, предупреждение — только про испорченный профиль",
                u.DeferredCommand == null && u.Warnings.Count == 1 && u.Warnings[0].Contains(cfg26));
            Check("удаление: остальные файлы КОМПАС не тронуты",
                File.Exists(Path.Combine(v23, "kStudy.cfg")) && File.ReadAllBytes(cfg26).SequenceEqual(bytes26));
            UninstallResult u2 = Installer.Uninstall(o);
            Check("повторное удаление не падает, выходить уже не из чего", u2.SignedOut == null && u2.Warnings.Count == 1);

            // ---- вход закрыть не удалось ----
            Installer.Install(o);
            File.WriteAllText(Path.Combine(o.UserDataDir, "kompas.cfg"), "token=x");
            signOutAnswer = false;
            UninstallResult u3 = Installer.Uninstall(o);
            signOutAnswer = true;
            Check("нет связи с сервером: удаление проходит, предупреждение про вход",
                !Installer.IsInstalled(o) && u3.SignedOut == false
                && u3.Warnings.Any(w => w.StartsWith("Вход на сервере закрыть не удалось")));

            // ---- удаление из самой папки установки ----
            Installer.Install(o);
            string foreign = Path.Combine(o.InstallDir, "чужой.txt");
            File.WriteAllText(foreign, "x");
            o.SelfPath = selfCopy;
            UninstallResult u4 = Installer.Uninstall(o);
            Check("самоудаление: dll и exe проверки убраны, себя установщик не трогает",
                !File.Exists(dll) && !File.Exists(checkExe) && File.Exists(selfCopy));
            Check("самоудаление: реестр и профили всё равно очищены",
                !Installer.IsInstalled(o) && CountOurs(cfg23) == 0 && CountOurs(cfg25) == 0);
            Check("самоудаление: отложенная команда про этот exe и папки",
                u4.DeferredCommand != null && u4.DeferredCommand.Contains("\"" + selfCopy + "\"")
                && u4.DeferredCommand.Contains("rmdir \"" + o.InstallDir + "\""));
            Installer.RunDeferred(u4.DeferredCommand);
            Check("самоудаление: cmd удалил exe после выхода", WaitGone(selfCopy));
            Check("самоудаление: чужой файл и его папка остались", File.Exists(foreign));
            File.Delete(foreign);

            o.SelfPath = setupExe;
            Installer.Install(o);
            o.SelfPath = selfCopy;
            UninstallResult u5 = Installer.Uninstall(o);
            Installer.RunDeferred(u5.DeferredCommand);
            Check("самоудаление: после exe убраны и пустые папки установки и журнала",
                WaitGone(selfCopy) && WaitGone(o.InstallDir) && WaitGone(o.LogDir));

            Check("сводка без профилей КОМПАС: как добавить вручную",
                SetupForm.Summary(new InstallResult { Version = "0.1.0", DllPath = "C:\\x\\NormaCadKompas.dll" })
                    .Contains("ещё не запускался") && SetupForm.Summary(new InstallResult { DllPath = "C:\\x\\NormaCadKompas.dll" })
                    .Contains("«Добавить приложение…» → C:\\x\\NormaCadKompas.dll"));
        }

        /// <summary>Сверка с RegAsm /codebase: те же ключи и значения, кроме двух признаков библиотеки КОМПАС.</summary>
        private static void CompareWithRegAsm(RegistryKey software, string dll)
        {
            string regasm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                @"Microsoft.NET\" + (Environment.Is64BitOperatingSystem ? "Framework64" : "Framework") + @"\v4.0.30319\RegAsm.exe");
            if (!File.Exists(regasm))
            {
                Console.WriteLine("skip  RegAsm.exe не найден: сверка с RegAsm пропущена");
                return;
            }
            string reg = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(dll)), "regasm.reg");
            ProcessStartInfo psi = new ProcessStartInfo(regasm, "\"" + dll + "\" /codebase /nologo /silent \"/regfile:" + reg + "\"");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using (Process p = Process.Start(psi))
            {
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit();
            }
            Check("RegAsm /regfile отработал", File.Exists(reg));
            if (!File.Exists(reg)) return;

            // Заголовок REGEDIT4, но RegAsm пишет файл в UTF-8: кириллица пути читается как есть
            Dictionary<string, Dictionary<string, string>> expected = ParseReg(File.ReadAllLines(reg, Encoding.UTF8));
            File.Delete(reg);
            Check("RegAsm: в .reg есть класс и ProgId", expected.ContainsKey(@"CLSID\{" + OurId + "}")
                && expected.ContainsKey("NormaCAD.KompasCheck"));

            List<string> diff = new List<string>();
            foreach (KeyValuePair<string, Dictionary<string, string>> key in expected)
            {
                using (RegistryKey k = software.OpenSubKey(@"Classes\" + key.Key))
                {
                    if (k == null) { diff.Add("нет ключа " + key.Key); continue; }
                    foreach (KeyValuePair<string, string> v in key.Value)
                    {
                        string ours = k.GetValue(v.Key == "@" ? null : v.Key) as string;
                        bool mscoree = v.Key == "@" && key.Key.EndsWith(@"\InprocServer32");
                        if (mscoree ? !(ours ?? "").EndsWith(@"\" + v.Value, StringComparison.OrdinalIgnoreCase) : ours != v.Value)
                            diff.Add(key.Key + " " + v.Key + ": у нас «" + ours + "», у RegAsm «" + v.Value + "»");
                    }
                    if (k.ValueCount != key.Value.Count) diff.Add(key.Key + ": значений " + k.ValueCount + " вместо " + key.Value.Count);
                }
            }
            // Лишнего, кроме Kompas_Library, тоже быть не должно. Промежуточные ключи (Implemented Categories)
            // в .reg отдельно не перечисляются — они есть в пути вложенных
            foreach (string top in new[] { @"CLSID\{" + OurId + "}", "NormaCAD.KompasCheck" })
                foreach (string sub in AllKeys(software, @"Classes\" + top, top))
                    if (!expected.Keys.Any(e => e == sub || e.StartsWith(sub + "\\"))
                        && sub != @"CLSID\{" + OurId + @"}\Kompas_Library") diff.Add("лишний ключ " + sub);
            Check("COM: ровно то, что пишет RegAsm /codebase" + (diff.Count > 0 ? " — " + string.Join("; ", diff.ToArray()) : ""),
                diff.Count == 0);
        }

        private static void CheckComExtras(RegistryKey software)
        {
            using (RegistryKey k = software.OpenSubKey(ClsidKey + @"\InprocServer32"))
            {
                string srv = k == null ? null : k.GetValue(null) as string;
                Check("COM: InprocServer32 — полный путь к существующему mscoree.dll, тип REG_SZ, как в примерах SDK",
                    srv != null && Path.IsPathRooted(srv) && File.Exists(srv)
                    && k.GetValueKind(null) == RegistryValueKind.String);
            }
            Check("COM: подраздел Kompas_Library — по нему КОМПАС узнаёт библиотеку",
                software.OpenSubKey(ClsidKey + @"\Kompas_Library") != null);
        }

        private static void CheckUninstallEntry(RegistryKey software, SetupOptions o, string selfCopy)
        {
            using (RegistryKey k = software.OpenSubKey(UninstallKey))
            {
                Check("«Приложения» Windows: запись есть", k != null);
                if (k == null) return;
                Check("«Приложения» Windows: удаление и тихое удаление — копией установщика",
                    (k.GetValue("UninstallString") as string) == "\"" + selfCopy + "\" /uninstall"
                    && (k.GetValue("QuietUninstallString") as string) == "\"" + selfCopy + "\" /uninstall /quiet");
                Check("«Приложения» Windows: имя, издатель, версия",
                    (k.GetValue("DisplayName") as string) == "NormaCAD.PRO для КОМПАС-3D"
                    && (k.GetValue("Publisher") as string) == "NormaCAD.PRO"
                    && (k.GetValue("DisplayVersion") as string) == ApiClient.ClientVersion);
                Check("«Приложения» Windows: NoModify, NoRepair, EstimatedSize — DWORD",
                    k.GetValue("NoModify") is int && (int)k.GetValue("NoModify") == 1
                    && k.GetValue("NoRepair") is int && (int)k.GetValue("NoRepair") == 1
                    && k.GetValue("EstimatedSize") is int && (int)k.GetValue("EstimatedSize") >= 1);
                Check("«Приложения» Windows: InstallLocation, DisplayIcon, InstallDate",
                    (k.GetValue("InstallLocation") as string) == o.InstallDir
                    && (k.GetValue("DisplayIcon") as string) == selfCopy
                    && ((k.GetValue("InstallDate") as string) ?? "").Length == 8);
            }
        }

        // Настоящий выход по токену из kompas.cfg на временном локальном сервере
        private static void TestSignOut(string root)
        {
            SetupOptions o = new SetupOptions();
            o.UserDataDir = Path.Combine(root, "Roaming");
            o.LogDir = Path.Combine(root, "Local");
            Directory.CreateDirectory(o.UserDataDir);
            Check("выход: настроек нет — выходить не из чего", Installer.SignOutSaved(o) == null);

            HttpListener listener = new HttpListener();
            int port = 19000 + new Random().Next(1000);
            string server = "http://127.0.0.1:" + port;
            listener.Prefixes.Add(server + "/");
            try { listener.Start(); }
            catch (Exception ex)
            {
                Console.WriteLine("skip  локальный сервер не запустился: " + ex.Message);
                return;
            }
            string auth = null, path = null;
            Thread t = new Thread(delegate ()
            {
                while (listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = listener.GetContext(); }
                    catch (Exception) { return; }
                    path = ctx.Request.HttpMethod + " " + ctx.Request.Url.AbsolutePath;
                    auth = ctx.Request.Headers["Authorization"];
                    byte[] body = Encoding.UTF8.GetBytes("{\"message\":\"ok\"}");
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.OutputStream.Write(body, 0, body.Length);
                    ctx.Response.Close();
                }
            });
            t.IsBackground = true;
            t.Start();

            string cfg = Path.Combine(o.UserDataDir, "kompas.cfg");
            string envBefore = Environment.GetEnvironmentVariable("NORMACAD_KOMPAS_CFG");
            string logBefore = Environment.GetEnvironmentVariable("NORMACAD_KOMPAS_LOG");
            SaveSettings(cfg, server, "");
            Check("выход: в настройках нет токена — выходить не из чего", Installer.SignOutSaved(o) == null && path == null);

            SaveSettings(cfg, server, "tok-setup");
            bool? ok = Installer.SignOutSaved(o);
            Check("выход: POST /api/auth/logout с токеном из настроек",
                ok == true && path == "POST /api/auth/logout" && auth == "Bearer tok-setup");
            Check("выход: переменные окружения вернулись как были",
                Environment.GetEnvironmentVariable("NORMACAD_KOMPAS_CFG") == envBefore
                && Environment.GetEnvironmentVariable("NORMACAD_KOMPAS_LOG") == logBefore);
            string log = Path.Combine(o.LogDir, "kompas.log");
            Check("выход: запись в журнал удаляемой установки",
                File.Exists(log) && File.ReadAllText(log).Contains("выход: токен отозван"));

            listener.Stop();
            listener.Close();
            Check("выход: сервер недоступен — false, без исключения", Installer.SignOutSaved(o) == false);
        }

        private static void SaveSettings(string file, string server, string token)
        {
            string old = Environment.GetEnvironmentVariable("NORMACAD_KOMPAS_CFG");
            Environment.SetEnvironmentVariable("NORMACAD_KOMPAS_CFG", file);
            try
            {
                Settings s = new Settings();
                s.Server = server;
                s.Token = token;
                s.Save();
            }
            finally { Environment.SetEnvironmentVariable("NORMACAD_KOMPAS_CFG", old); }
        }

        // ---- помощники ----

        private static string Refused(Action action)
        {
            try { action(); }
            catch (KompasRunningException ex) { return ex.Message; }
            return null;
        }

        private static byte[] Resource(Assembly a, string name)
        {
            using (Stream s = a.GetManifestResourceStream(name))
            {
                if (s == null) return null;
                MemoryStream ms = new MemoryStream();
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }

        private static Dictionary<string, Dictionary<string, string>> ParseReg(string[] lines)
        {
            Dictionary<string, Dictionary<string, string>> keys = new Dictionary<string, Dictionary<string, string>>();
            Dictionary<string, string> current = null;
            const string Root = "[HKEY_CLASSES_ROOT\\";
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.StartsWith(Root) && line.EndsWith("]"))
                {
                    current = new Dictionary<string, string>();
                    keys[line.Substring(Root.Length, line.Length - Root.Length - 1)] = current;
                }
                else if (current != null && (line.StartsWith("@=") || line.StartsWith("\"")))
                {
                    int eq = line.StartsWith("@=") ? 1 : line.IndexOf("\"=\"", StringComparison.Ordinal) + 1;
                    string name = line.StartsWith("@=") ? "@" : Unescape(line.Substring(1, eq - 2));
                    current[name] = Unescape(line.Substring(eq + 2, line.Length - eq - 3));
                }
            }
            return keys;
        }

        private static string Unescape(string s)
        {
            return s.Replace("\\\"", "\"").Replace("\\\\", "\\");
        }

        private static List<string> AllKeys(RegistryKey software, string path, string name)
        {
            List<string> all = new List<string>();
            using (RegistryKey k = software.OpenSubKey(path))
            {
                if (k == null) return all;
                all.Add(name);
                foreach (string sub in k.GetSubKeyNames())
                    all.AddRange(AllKeys(software, path + "\\" + sub, name + "\\" + sub));
            }
            return all;
        }

        private static string[] SubKeys(RegistryKey software, string path)
        {
            using (RegistryKey k = software.OpenSubKey(path)) return k == null ? new string[0] : k.GetSubKeyNames();
        }

        private static string Value(RegistryKey software, string path, string name)
        {
            using (RegistryKey k = software.OpenSubKey(path)) return k == null ? null : k.GetValue(name) as string;
        }

        private static bool WaitGone(string path)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (File.Exists(path) || Directory.Exists(path))
            {
                if (sw.ElapsedMilliseconds > 15000) return false;
                Thread.Sleep(100);
            }
            return true;
        }

        private static bool NoLeftovers(string root)
        {
            foreach (string f in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                if (f.EndsWith(".new") || f.EndsWith(".normacad-new")) return false;
            return true;
        }

        private static bool Starts(string file, byte b)
        {
            byte[] bytes = File.ReadAllBytes(file);
            return bytes.Length > 0 && bytes[0] == b;
        }

        private static List<string> Ids(string file, string product)
        {
            List<string> ids = new List<string>();
            foreach (XElement p in XDocument.Load(file).Root.Elements("Product"))
                if ((string)p.Attribute("id") == product)
                    foreach (XElement a in p.Elements("App")) ids.Add((string)a.Attribute("id"));
            return ids;
        }

        private static string AppPath(string file, string product, string id)
        {
            foreach (XElement p in XDocument.Load(file).Root.Elements("Product"))
            {
                if ((string)p.Attribute("id") != product) continue;
                foreach (XElement a in p.Elements("App"))
                    if (string.Equals((string)a.Attribute("id"), id, StringComparison.OrdinalIgnoreCase))
                        return (string)a.Attribute("path");
            }
            return null;
        }

        private static int CountOurs(string file)
        {
            return XDocument.Load(file).Descendants("App")
                .Count(a => string.Equals((string)a.Attribute("id"), Installer.EntryName, StringComparison.OrdinalIgnoreCase));
        }

        private static void Check(string name, bool ok)
        {
            Console.WriteLine((ok ? "ok    " : "FAIL  ") + name);
            if (!ok) failed++;
        }
    }
}
