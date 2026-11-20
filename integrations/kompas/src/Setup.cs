// Установщик расширения NormaCAD.PRO для КОМПАС-3D: один файл NormaCadSetup.exe, права администратора не нужны.
// Всё ставится текущему пользователю Windows:
//   файлы — в %LOCALAPPDATA%\NormaCAD\Kompas;
//   COM-класс — в HKCU\Software\Classes: те же ключи, что RegAsm /codebase пишет в HKEY_CLASSES_ROOT, и ещё два
//     признака из примеров SDK КОМПАС — подраздел Kompas_Library и полный путь к mscoree.dll;
//   пункт меню «Приложения» — запись App в UI_AppPaths.config каждой версии КОМПАС, которая уже запускалась;
//   строка для удаления через «Приложения» Windows — в HKCU\...\Uninstall.
// КОМПАС при выходе перезаписывает UI_AppPaths.config и держит загруженную библиотеку открытой, поэтому
// установка и удаление ничего не меняют, пока он запущен.
// Всё внешнее (папки, ключ реестра, запущенные процессы, выход на сервере) задаётся в SetupOptions:
// тест подставляет временные папки, временный ключ реестра и свои ответы.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;

[assembly: AssemblyTitle("Установка NormaCAD.PRO для КОМПАС-3D")]
[assembly: AssemblyProduct("NormaCAD.PRO")]

namespace NormaCad.Kompas.Setup
{
    public class SetupException : Exception
    {
        public SetupException(string message) : base(message) { }
    }

    /// <summary>Запущен КОМПАС или окно проверки: установка и удаление в это время ничего не трогают.</summary>
    public class KompasRunningException : SetupException
    {
        public readonly List<string> Processes;

        public KompasRunningException(List<string> processes) : base(Describe(processes))
        {
            Processes = processes;
        }

        private static string Describe(List<string> processes)
        {
            bool window = false, kompas = false;
            List<string> exe = new List<string>();
            foreach (string p in processes)
            {
                if (string.Equals(p, Installer.CheckProcess, StringComparison.OrdinalIgnoreCase)) window = true;
                else kompas = true;
                exe.Add(p + ".exe");
            }
            string what = kompas && window ? "КОМПАС-3D и окно проверки NormaCAD.PRO"
                : kompas ? "КОМПАС-3D" : "окно проверки NormaCAD.PRO";
            return "Закройте " + what + " и повторите (запущено: " + string.Join(", ", exe.ToArray()) + ").";
        }
    }

    public class SetupOptions
    {
        /// <summary>Папка установки: %LOCALAPPDATA%\NormaCAD\Kompas.</summary>
        public string InstallDir;
        /// <summary>Папка ASCON в APPDATA; профили версий — в KOMPAS-3D\&lt;версия&gt;.</summary>
        public string AsconRoot;
        /// <summary>Настройки расширения со входом: %APPDATA%\NormaCAD (файл kompas.cfg).</summary>
        public string UserDataDir;
        /// <summary>Журнал расширения: %LOCALAPPDATA%\NormaCAD (kompas.log и три старых).</summary>
        public string LogDir;
        /// <summary>Временные экспорты чертежей: %TEMP%\NormaCAD-Kompas.</summary>
        public string ExportDir;
        /// <summary>Ключ «Software» в HKCU (в тесте — временный ключ вместо него).</summary>
        public RegistryKey SoftwareKey;
        /// <summary>Путь к запущенному NormaCadSetup.exe: его копия кладётся в папку установки.</summary>
        public string SelfPath;
        /// <summary>Источник вшитых файлов по имени (по умолчанию — ресурсы этой сборки).</summary>
        public Func<string, Stream> OpenPayload;
        /// <summary>Запущенные процессы, при которых ставить нельзя (по умолчанию — поиск КОМПАС и окна проверки).</summary>
        public Func<List<string>> RunningProcesses;
        /// <summary>Закрыть вход на сервере: true — закрыт, false — не удалось, null — входа не было.</summary>
        public Func<SetupOptions, bool?> SignOut;
    }

    public class InstallResult
    {
        public string Version = "";
        /// <summary>Версия, что стояла до этой установки; пусто — не стояло.</summary>
        public string Previous = "";
        public string DllPath = "";
        /// <summary>Профилей КОМПАС, где после установки есть пункт в меню «Приложения».</summary>
        public int ConfigsReady;
        /// <summary>Из них файлов, которые пришлось переписать (в остальных запись уже стояла).</summary>
        public int ConfigsUpdated;
        public List<string> Warnings = new List<string>();
    }

    public class UninstallResult
    {
        public int ConfigsCleaned;
        /// <summary>Вход на сервере: true — закрыт, false — закрыть не удалось, null — входа не было.</summary>
        public bool? SignedOut;
        public List<string> Warnings = new List<string>();
        /// <summary>Команда cmd для самоудаления, если установщик запущен из папки установки; иначе null.</summary>
        public string DeferredCommand;
    }

    public class Installer
    {
        public const string DllName = "NormaCadKompas.dll";
        public const string EntryName = "NormaCadKompas.rtw";
        private const string LegacyUiName = "NormaCadKompas.xml";
        public const string CheckName = "NormaCadCheck.exe";
        public const string SetupName = "NormaCadSetup.exe";
        public const string CheckProcess = "NormaCadCheck";
        public const string Clsid = "{6B0F2C7E-5D41-4A9B-9E3A-2F8C1D7A4B65}";
        public const string ProgId = "NormaCAD.KompasCheck";
        public const string ClassName = "NormaCad.Kompas.KompasCheck";
        public const string UninstallName = "NormaCAD.Kompas";
        public const string DisplayName = "NormaCAD.PRO для КОМПАС-3D";
        public const string ConfigName = "UI_AppPaths.config";
        public const string RuntimeVersion = "v4.0.30319";
        // Категория «.NET Category»: RegAsm отмечает ею каждый класс, который регистрирует
        public const string NetCategory = "{62C8FE65-4EBB-45E7-B440-6E39B2CDBF29}";

        /// <summary>Редакции КОМПАС: имя процесса совпадает с id продукта в профиле.</summary>
        public static readonly string[] KnownProducts = { "kStudy", "KOMPAS", "kHome" };
        private static readonly string[] LogNames = { "kompas.log", "kompas.log.1", "kompas.log.2", "kompas.log.3" };
        private const string UninstallPath = @"Microsoft\Windows\CurrentVersion\Uninstall\" + UninstallName;

        // КОМПАС идентифицирует RTW без XML по имени файла.
        private static bool IsOurApp(string id)
        {
            return string.Equals(id, EntryName, StringComparison.OrdinalIgnoreCase)
                || string.Equals((id ?? "").Trim('{', '}'), Clsid.Trim('{', '}'), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Установка по умолчанию: реальные HKCU, APPDATA и TEMP текущего пользователя.</summary>
        public static SetupOptions DefaultOptions(string selfPath)
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            SetupOptions o = new SetupOptions();
            o.InstallDir = Path.Combine(Path.Combine(local, "NormaCAD"), "Kompas");
            o.LogDir = Path.Combine(local, "NormaCAD");
            o.AsconRoot = Path.Combine(roaming, "ASCON");
            o.UserDataDir = Path.Combine(roaming, "NormaCAD");
            o.ExportDir = Path.Combine(Path.GetTempPath(), "NormaCAD-Kompas");
            // КОМПАС v18 и новее — 64-битный и ищет COM-классы в 64-битном представлении реестра:
            // пишем туда, даже если установщик почему-то запущен 32-битным процессом
            using (RegistryKey hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, NativeView))
                o.SoftwareKey = hkcu.OpenSubKey("Software", true);
            o.SelfPath = selfPath;
            return o;
        }

        private static RegistryView NativeView
        {
            get { return Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default; }
        }

        // ---------- установка ----------

        public static InstallResult Install(SetupOptions o)
        {
            EnsureClosed(o);
            InstallResult result = new InstallResult();
            result.Previous = InstalledVersion(o);

            try { Directory.CreateDirectory(o.InstallDir); }
            catch (Exception ex) { throw new SetupException("Не удалось создать папку " + o.InstallDir + ": " + ex.Message); }

            string dll = Path.Combine(o.InstallDir, DllName);
            string entry = Path.Combine(o.InstallDir, EntryName);
            string selfCopy = Path.Combine(o.InstallDir, SetupName);
            WriteFiles(o, dll, selfCopy);
            try { File.Delete(Path.Combine(o.InstallDir, LegacyUiName)); }
            catch (Exception ex) { throw new SetupException("Не удалось убрать устаревшее описание меню: " + ex.Message); }
            result.DllPath = entry;

            AssemblyName an;
            try { an = AssemblyName.GetAssemblyName(dll); }
            catch (Exception ex) { throw new SetupException("Файл " + DllName + " повреждён: " + ex.Message); }
            result.Version = an.Version.ToString(3);

            try
            {
                RegisterCom(o.SoftwareKey, dll, an);
                WriteUninstallEntry(o, selfCopy, result.Version);
            }
            catch (Exception ex)
            {
                throw new SetupException("Не удалось записать реестр: " + ex.Message);
            }

            foreach (string file in ConfigFiles(o.AsconRoot))
            {
                try
                {
                    if (SetEntry(file, entry)) result.ConfigsUpdated++;
                    result.ConfigsReady++;
                }
                catch (Exception ex)
                {
                    result.Warnings.Add("Не обновлён " + file + ": " + ex.Message);
                }
            }
            foreach (string file in KitFiles(o.AsconRoot))
            {
                try { SetKitEntry(file, entry); }
                catch (Exception ex) { result.Warnings.Add("Could not update " + file + ": " + ex.Message); }
            }
            return result;
        }

        private static Stream OpenResource(string name)
        {
            Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
            if (s == null) throw new SetupException("В установщике нет вшитого файла " + name + ".");
            return s;
        }

        // Всё пишется сначала рядом, во временные файлы, и только потом подменяет старые:
        // сбой на середине (нет места, файл занят) не оставляет половину новой версии и половину старой
        private static void WriteFiles(SetupOptions o, string dll, string selfCopy)
        {
            Func<string, Stream> open = o.OpenPayload ?? OpenResource;
            List<string[]> staged = new List<string[]>();
            try
            {
                staged.Add(Stage(delegate { return open(DllName); }, DllName, dll));
                staged.Add(Stage(delegate { return open(EntryName); }, EntryName, Path.Combine(o.InstallDir, EntryName)));
                staged.Add(Stage(delegate { return open(CheckName); }, CheckName, Path.Combine(o.InstallDir, CheckName)));
                // Копия установщика — для удаления через «Приложения» Windows. Байтами, а не File.Copy:
                // так на копию не переходит отметка «файл скачан из интернета» (поток Zone.Identifier)
                if (!string.IsNullOrEmpty(o.SelfPath) && File.Exists(o.SelfPath) && !SamePath(o.SelfPath, selfCopy))
                    staged.Add(Stage(delegate { return File.OpenRead(o.SelfPath); }, SetupName, selfCopy));
                foreach (string[] s in staged) Swap(s[0], s[1]);
            }
            finally
            {
                foreach (string[] s in staged) TryDelete(s[0]);
            }
        }

        private static string[] Stage(Func<Stream> open, string name, string target)
        {
            string tmp = target + ".new";
            try
            {
                using (Stream src = open())
                using (FileStream dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                    src.CopyTo(dst);
            }
            catch (SetupException) { TryDelete(tmp); throw; }
            catch (Exception ex)
            {
                TryDelete(tmp);
                throw new SetupException("Не удалось записать " + name + ": " + ex.Message);
            }
            return new string[] { tmp, target };
        }

        private static void Swap(string tmp, string target)
        {
            try
            {
                if (File.Exists(target)) File.Delete(target);
                File.Move(tmp, target);
            }
            catch (Exception ex)
            {
                throw new SetupException("Файл " + target + " занят: " + ex.Message
                    + " Закройте КОМПАС-3D и окно проверки NormaCAD.PRO и повторите.");
            }
        }

        /// <summary>
        /// Ключи RegAsm /codebase, только в HKCU\Software\Classes, и два признака библиотеки КОМПАС из примеров SDK:
        /// подраздел Kompas_Library и полный путь к mscoree.dll в InprocServer32. Прежняя регистрация сначала
        /// удаляется целиком: после обновления не остаётся подраздела InprocServer32\&lt;старая версия&gt;.
        /// </summary>
        private static void RegisterCom(RegistryKey software, string dll, AssemblyName an)
        {
            UnregisterCom(software);
            string codeBase = "file:///" + dll.Replace('\\', '/');
            string mscoree = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "mscoree.dll");
            using (RegistryKey prog = software.CreateSubKey(@"Classes\" + ProgId))
            {
                prog.SetValue(null, ClassName);
                using (RegistryKey c = prog.CreateSubKey("CLSID")) c.SetValue(null, Clsid);
            }
            using (RegistryKey key = software.CreateSubKey(@"Classes\CLSID\" + Clsid))
            {
                key.SetValue(null, ClassName);
                using (RegistryKey inproc = key.CreateSubKey("InprocServer32"))
                {
                    inproc.SetValue(null, mscoree);
                    inproc.SetValue("ThreadingModel", "Both");
                    WriteClassValues(inproc, an.FullName, codeBase);
                    using (RegistryKey version = inproc.CreateSubKey(an.Version.ToString()))
                        WriteClassValues(version, an.FullName, codeBase);
                }
                using (RegistryKey p = key.CreateSubKey("ProgId")) p.SetValue(null, ProgId);
                key.CreateSubKey(@"Implemented Categories\" + NetCategory).Close();
                key.CreateSubKey("Kompas_Library").Close();
            }
        }

        private static void WriteClassValues(RegistryKey key, string assembly, string codeBase)
        {
            key.SetValue("Class", ClassName);
            key.SetValue("Assembly", assembly);
            key.SetValue("RuntimeVersion", RuntimeVersion);
            key.SetValue("CodeBase", codeBase);
        }

        private static void UnregisterCom(RegistryKey software)
        {
            software.DeleteSubKeyTree(@"Classes\" + ProgId, false);
            software.DeleteSubKeyTree(@"Classes\CLSID\" + Clsid, false);
        }

        private static void WriteUninstallEntry(SetupOptions o, string selfCopy, string version)
        {
            long bytes = 0;
            foreach (string n in new[] { DllName, EntryName, CheckName, LegacyUiName, SetupName })
            {
                string f = Path.Combine(o.InstallDir, n);
                if (File.Exists(f)) bytes += new FileInfo(f).Length;
            }
            using (RegistryKey key = o.SoftwareKey.CreateSubKey(UninstallPath))
            {
                string cmd = "\"" + selfCopy + "\" /uninstall";
                key.SetValue("DisplayName", DisplayName);
                key.SetValue("DisplayVersion", version);
                key.SetValue("Publisher", "NormaCAD.PRO");
                key.SetValue("InstallLocation", o.InstallDir);
                key.SetValue("DisplayIcon", selfCopy);
                key.SetValue("UninstallString", cmd);
                key.SetValue("QuietUninstallString", cmd + " /quiet");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                key.SetValue("EstimatedSize", (int)Math.Max(1, (bytes + 1023) / 1024), RegistryValueKind.DWord);
                key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            }
        }

        // ---------- удаление ----------

        public static UninstallResult Uninstall(SetupOptions o)
        {
            EnsureClosed(o);
            UninstallResult result = new UninstallResult();

            foreach (string file in KitFiles(o.AsconRoot))
            {
                try { SetKitEntry(file, null); }
                catch (Exception ex) { result.Warnings.Add("Could not update " + file + ": " + ex.Message); }
            }
            foreach (string file in ConfigFiles(o.AsconRoot))
            {
                try { if (SetEntry(file, null)) result.ConfigsCleaned++; }
                catch (Exception ex) { result.Warnings.Add("Не обновлён " + file + ": " + ex.Message); }
            }

            try
            {
                UnregisterCom(o.SoftwareKey);
                o.SoftwareKey.DeleteSubKeyTree(UninstallPath, false);
            }
            catch (Exception ex)
            {
                throw new SetupException("Не удалось удалить записи реестра: " + ex.Message);
            }

            // Вход закрывается тем же запросом, что кнопка «Выйти»: иначе токен жил бы на сервере ещё 30 дней
            try { result.SignedOut = (o.SignOut ?? SignOutSaved)(o); }
            catch (Exception) { result.SignedOut = false; }
            if (result.SignedOut == false)
                result.Warnings.Add("Вход на сервере закрыть не удалось: он закроется сам через 30 дней, "
                    + "закрыть сразу можно на сайте в списке открытых сессий.");

            // Только свои файлы: в папках NormaCAD могут лежать файлы других программ
            if (!string.IsNullOrEmpty(o.UserDataDir)) DeleteFile(Path.Combine(o.UserDataDir, "kompas.cfg"), result);
            if (!string.IsNullOrEmpty(o.LogDir))
                foreach (string n in LogNames) DeleteFile(Path.Combine(o.LogDir, n), result);
            if (!string.IsNullOrEmpty(o.ExportDir) && Directory.Exists(o.ExportDir))
            {
                try { Directory.Delete(o.ExportDir, true); }
                catch (Exception ex) { result.Warnings.Add("Не удалена папка " + o.ExportDir + ": " + ex.Message); }
            }

            string selfToDelete = null;
            foreach (string n in new[] { DllName, EntryName, CheckName, LegacyUiName, SetupName })
            {
                string f = Path.Combine(o.InstallDir, n);
                if (!File.Exists(f)) continue;
                if (!string.IsNullOrEmpty(o.SelfPath) && SamePath(f, o.SelfPath))
                {
                    selfToDelete = f;
                    continue;
                }
                DeleteFile(f, result);
            }

            // Папки — только пустые; папка установки лежит внутри папки журнала
            string[] dirs = { o.InstallDir, o.LogDir, o.UserDataDir };
            if (selfToDelete != null)
            {
                // Запущенный exe не может удалить себя сам: это сделает cmd после выхода установщика
                result.DeferredCommand = DeferredDelete(selfToDelete, dirs);
            }
            foreach (string dir in dirs) TryRemoveEmptyDir(dir);
            return result;
        }

        /// <summary>
        /// Закрыть вход расширения на сервере: прочитать токен из kompas.cfg удаляемой установки и отправить
        /// POST /api/auth/logout тем же клиентом, что окно проверки (адрес сервера и прокси — из тех же настроек).
        /// </summary>
        public static bool? SignOutSaved(SetupOptions o)
        {
            if (string.IsNullOrEmpty(o.UserDataDir)) return null;
            string cfgPath = Path.Combine(o.UserDataDir, "kompas.cfg");
            if (!File.Exists(cfgPath)) return null;
            string oldCfg = Environment.GetEnvironmentVariable("NORMACAD_KOMPAS_CFG");
            string oldLog = Environment.GetEnvironmentVariable("NORMACAD_KOMPAS_LOG");
            try
            {
                // Settings и журнал берут пути из этих переменных: так читается именно удаляемая установка
                Environment.SetEnvironmentVariable("NORMACAD_KOMPAS_CFG", cfgPath);
                if (!string.IsNullOrEmpty(o.LogDir))
                    Environment.SetEnvironmentVariable("NORMACAD_KOMPAS_LOG", Path.Combine(o.LogDir, "kompas.log"));
                Settings cfg = Settings.Load();
                if (string.IsNullOrEmpty(cfg.Token)) return null;
                ProxySetup.Apply(cfg);
                ApiClient api = new ApiClient();
                api.BaseUrl = cfg.Server;
                api.Token = cfg.Token;
                return api.Logout();
            }
            finally
            {
                Environment.SetEnvironmentVariable("NORMACAD_KOMPAS_CFG", oldCfg);
                Environment.SetEnvironmentVariable("NORMACAD_KOMPAS_LOG", oldLog);
            }
        }

        /// <summary>
        /// Команда cmd: после выхода установщика удалить его файл и пустые папки. Удаление пробуется
        /// до 30 раз с паузой в секунду — сколько нужно, чтобы процесс установщика успел закрыться.
        /// </summary>
        public static string DeferredDelete(string file, IEnumerable<string> dirs)
        {
            string q = "\"" + file + "\"";
            StringBuilder sb = new StringBuilder();
            sb.Append("(for /l %i in (1,1,30) do @(if exist ").Append(q)
                .Append(" (del /f /q ").Append(q).Append(" >nul 2>&1 & if exist ").Append(q)
                .Append(" ping -n 2 127.0.0.1 >nul)))");
            foreach (string dir in dirs)
                if (!string.IsNullOrEmpty(dir))
                    sb.Append(" & rmdir \"").Append(dir.TrimEnd('\\', '/')).Append("\" 2>nul");
            return sb.ToString();
        }

        /// <summary>Запуск отложенной команды: скрытое окно, без ожидания, текущая папка — не удаляемая.</summary>
        public static void RunDeferred(string command)
        {
            if (string.IsNullOrEmpty(command)) return;
            ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c \"" + command + "\"");
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            // Папку, в которой стоит cmd, удалить нельзя
            psi.WorkingDirectory = Path.GetTempPath();
            Process.Start(psi).Dispose();
        }

        private static void DeleteFile(string path, UninstallResult result)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) { result.Warnings.Add("Не удалён файл " + path + ": " + ex.Message); }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception) { }
        }

        private static void TryRemoveEmptyDir(string dir)
        {
            try
            {
                // Только пустую: чужие файлы не трогаем
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) Directory.Delete(dir, false);
            }
            catch (Exception) { }
        }

        public static bool IsInstalled(SetupOptions o)
        {
            using (RegistryKey k = o.SoftwareKey.OpenSubKey(UninstallPath)) return k != null;
        }

        public static string InstalledVersion(SetupOptions o)
        {
            using (RegistryKey k = o.SoftwareKey.OpenSubKey(UninstallPath))
                return k == null ? "" : Convert.ToString(k.GetValue("DisplayVersion")) ?? "";
        }

        // ---------- запущенный КОМПАС ----------

        private static void EnsureClosed(SetupOptions o)
        {
            List<string> running = o.RunningProcesses != null ? o.RunningProcesses() : FindRunning(ProcessNames(o));
            if (running != null && running.Count > 0) throw new KompasRunningException(running);
        }

        /// <summary>
        /// Имена процессов, при которых ставить и удалять нельзя: известные редакции КОМПАС, исполняемый файл
        /// из регистрации KOMPAS.Application, редакции из профилей пользователя и окно проверки NormaCadCheck.exe.
        /// </summary>
        public static List<string> ProcessNames(SetupOptions o)
        {
            List<string> names = new List<string>();
            foreach (string p in KnownProducts) AddName(names, p);
            foreach (string progId in new[] { "KOMPAS.Application.5", "Kompas.Application.7" })
                AddName(names, ServerExe(progId));
            string root = Path.Combine(o.AsconRoot ?? "", "KOMPAS-3D");
            try
            {
                if (Directory.Exists(root))
                    foreach (string dir in Directory.GetDirectories(root)) AddName(names, ProductOf(dir));
            }
            catch (Exception) { }
            AddName(names, CheckProcess);
            return names;
        }

        public static List<string> FindRunning(IEnumerable<string> names)
        {
            List<string> found = new List<string>();
            foreach (string n in names)
            {
                Process[] ps = Process.GetProcessesByName(n);
                if (ps.Length > 0) found.Add(n);
                foreach (Process p in ps) p.Dispose();
            }
            return found;
        }

        private static void AddName(List<string> names, string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            foreach (string n in names)
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return;
            names.Add(name);
        }

        /// <summary>Имя exe без расширения из LocalServer32 того, кто зарегистрирован под progId.</summary>
        private static string ServerExe(string progId)
        {
            try
            {
                using (RegistryKey hkcr = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, NativeView))
                using (RegistryKey c = hkcr.OpenSubKey(progId + @"\CLSID"))
                {
                    string clsid = c == null ? null : c.GetValue(null) as string;
                    if (string.IsNullOrEmpty(clsid)) return null;
                    using (RegistryKey s = hkcr.OpenSubKey(@"CLSID\" + clsid + @"\LocalServer32"))
                    {
                        string cmd = s == null ? null : (s.GetValue(null) as string ?? "").Trim();
                        if (string.IsNullOrEmpty(cmd)) return null;
                        string exe = cmd.StartsWith("\"")
                            ? cmd.Substring(1, Math.Max(0, cmd.IndexOf('"', 1) - 1))
                            : cmd.Split(' ')[0];
                        return Path.GetFileNameWithoutExtension(exe);
                    }
                }
            }
            catch (Exception) { return null; }
        }

        // ---------- UI_AppPaths.config ----------

        /// <summary>
        /// Списки приложений профилей КОМПАС: %APPDATA%\ASCON\KOMPAS-3D\&lt;версия&gt;\UI_AppPaths.config.
        /// Новый файл не создаётся: его пишет сам КОМПАС при первом выходе вместе со своими приложениями,
        /// и файл с одной нашей записью мог бы их заслонить.
        /// </summary>
        private static List<string> ConfigFiles(string asconRoot)
        {
            List<string> files = new List<string>();
            string root = Path.Combine(asconRoot ?? "", "KOMPAS-3D");
            if (!Directory.Exists(root)) return files;
            foreach (string dir in Directory.GetDirectories(root))
            {
                string file = Path.Combine(dir, ConfigName);
                if (File.Exists(file)) files.Add(file);
            }
            return files;
        }

        private static List<string> KitFiles(string asconRoot)
        {
            List<string> files = new List<string>();
            string root = Path.Combine(asconRoot ?? "", "KOMPAS-3D");
            if (!Directory.Exists(root)) return files;
            foreach (string dir in Directory.GetDirectories(root))
                foreach (string product in KnownProducts)
                {
                    string file = Path.Combine(dir, product + ".kit.config");
                    if (File.Exists(file)) files.Add(file);
                }
            return files;
        }

        private static bool SetKitEntry(string file, string dll)
        {
            byte[] original = File.ReadAllBytes(file);
            XDocument doc;
            using (MemoryStream ms = new MemoryStream(original)) doc = XDocument.Load(ms);
            XElement groups = doc.Root != null && doc.Root.Name == "Kit_Config" ? doc.Root.Element("Groups") : null;
            XElement group = null;
            if (groups != null)
                foreach (XElement candidate in groups.Elements("Group"))
                    if (candidate.Attribute("groupName") == null) { group = candidate; break; }
            if (group == null) throw new SetupException("Kit_Config root group missing");
            List<XElement> ours = new List<XElement>();
            foreach (XElement app in group.Elements("Application"))
                if (IsOurApp((string)app.Attribute("id")))
                    ours.Add(app);
            XElement desired = dll == null ? null : new XElement("Application",
                new XAttribute("libType", "lbt_rtw_ocx"),
                new XAttribute("id", EntryName), new XAttribute("path", Path.GetFullPath(dll)),
                new XAttribute("libName", "NormaCAD.PRO"),
                new XAttribute("displayName", "NormaCAD.PRO"),
                new XAttribute("autostart", "true"));
            if (desired == null && ours.Count == 0) return false;
            if (desired != null && ours.Count == 1 && XNode.DeepEquals(ours[0], desired)) return false;
            foreach (XElement app in ours) app.Remove();
            if (desired != null) group.Add(desired);
            Save(doc, file, original);
            return true;
        }

        /// <summary>
        /// dll != null — в каждом разделе Product ровно одна наша запись App с этим путём; null — наших записей нет.
        /// Чужие записи и их порядок не трогаются. Файл переписывается, только если что-то изменилось.
        /// </summary>
        private static bool SetEntry(string file, string dll)
        {
            byte[] original = File.ReadAllBytes(file);
            XDocument doc;
            using (MemoryStream ms = new MemoryStream(original)) doc = XDocument.Load(ms);
            if (doc.Root == null) return false;

            List<XElement> products = new List<XElement>(doc.Root.Elements("Product"));
            if (products.Count == 0)
            {
                if (dll == null) return false;
                string id = ProductOf(Path.GetDirectoryName(file));
                if (id == null) throw new SetupException("нет раздела Product, редакция КОМПАС неизвестна");
                XElement p = new XElement("Product", new XAttribute("id", id));
                doc.Root.Add(p);
                products.Add(p);
            }

            bool modified = false;
            foreach (XElement product in products)
            {
                List<XElement> ours = new List<XElement>();
                foreach (XElement app in product.Elements("App"))
                    if (IsOurApp((string)app.Attribute("id"))) ours.Add(app);
                // Уже стоит ровно то, что нужно, — раздел не трогаем
                if (dll != null && ours.Count == 1 && (string)ours[0].Attribute("id") == EntryName
                    && string.Equals((string)ours[0].Attribute("path"), dll, StringComparison.Ordinal))
                    continue;
                foreach (XElement e in ours) e.Remove();
                if (dll != null) product.Add(new XElement("App", new XAttribute("id", EntryName), new XAttribute("path", dll)));
                if (ours.Count > 0 || dll != null) modified = true;
            }
            if (!modified) return false;
            Save(doc, file, original);
            return true;
        }

        /// <summary>
        /// Запись в том же виде, что у КОМПАС: отступ в два пробела, та же кодировка и метка BOM, объявление
        /// &lt;?xml?&gt; — только если оно было. Через временный файл: КОМПАС не прочтёт файл, записанный наполовину.
        /// </summary>
        private static void Save(XDocument doc, string file, byte[] original)
        {
            Encoding encoding;
            if (original.Length >= 2 && original[0] == 0xFF && original[1] == 0xFE) encoding = new UnicodeEncoding(false, true);
            else if (original.Length >= 3 && original[0] == 0xEF && original[1] == 0xBB && original[2] == 0xBF)
                encoding = new UTF8Encoding(true);
            else encoding = new UTF8Encoding(false);

            XmlWriterSettings s = new XmlWriterSettings();
            s.Indent = true;
            s.IndentChars = "  ";
            s.Encoding = encoding;
            s.OmitXmlDeclaration = doc.Declaration == null;
            string tmp = file + ".normacad-new";
            try
            {
                using (XmlWriter w = XmlWriter.Create(tmp, s)) doc.Save(w);
                File.Replace(tmp, file, null, true);
            }
            finally
            {
                TryDelete(tmp);
            }
        }

        /// <summary>Редакция КОМПАС профиля (kStudy, KOMPAS, kHome): из version.xml, иначе по файлу настроек *.cfg.</summary>
        public static string ProductOf(string dir)
        {
            try
            {
                string v = Path.Combine(dir, "version.xml");
                if (File.Exists(v))
                {
                    XElement info = XDocument.Load(v).Root.Element("productInfo");
                    string name = info == null ? null : (string)info.Attribute("name");
                    if (!string.IsNullOrEmpty(name)) return name;
                }
            }
            catch (Exception) { }
            foreach (string p in KnownProducts)
                if (File.Exists(Path.Combine(dir, p + ".cfg"))) return p;
            return null;
        }

        private static bool SamePath(string a, string b)
        {
            try
            {
                return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception) { return false; }
        }
    }

    public static class SetupProgram
    {
        internal const string Title = "NormaCAD.PRO для КОМПАС-3D";
        private const int Ok = 0, Failed = 1, KompasRunning = 3;

        /// <summary>
        /// Без ключей — окно с одной кнопкой «Установить». /install, /uninstall — то же без окна, с итогом
        /// в сообщении; /quiet — без сообщений, итог в %TEMP%\NormaCAD-Setup.log.
        /// Код выхода: 0 — готово, 1 — ошибка, 3 — запущен КОМПАС (ничего не изменено).
        /// </summary>
        [STAThread]
        public static int Main(string[] args)
        {
            bool quiet = false, install = false, uninstall = false;
            foreach (string a in args)
            {
                string k = a.ToLowerInvariant();
                if (k == "/quiet" || k == "-quiet" || k == "/q") quiet = true;
                else if (k == "/install" || k == "-install") install = true;
                else if (k == "/uninstall" || k == "-uninstall") uninstall = true;
            }

            SetupOptions o = Installer.DefaultOptions(Assembly.GetExecutingAssembly().Location);
            if (quiet || install || uninstall)
                return RunCommand(o, !uninstall || install, quiet);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string deferred;
            using (SetupForm f = new SetupForm(o)) { Application.Run(f); deferred = f.Deferred; }
            Installer.RunDeferred(deferred);
            return Ok;
        }

        private static int RunCommand(SetupOptions o, bool install, bool quiet)
        {
            string deferred = null;
            string message;
            int code = Ok;
            while (true)
            {
                try
                {
                    if (install) message = SetupForm.Summary(Installer.Install(o));
                    else
                    {
                        UninstallResult r = Installer.Uninstall(o);
                        deferred = r.DeferredCommand;
                        message = SetupForm.Summary(r);
                    }
                }
                catch (KompasRunningException ex)
                {
                    if (!quiet && MessageBox.Show(ex.Message, Title, MessageBoxButtons.RetryCancel,
                        MessageBoxIcon.Warning) == DialogResult.Retry) continue;
                    message = "Отменено, ничего не изменено. " + ex.Message;
                    code = KompasRunning;
                }
                catch (Exception ex)
                {
                    message = "Ошибка: " + ex.Message;
                    code = Failed;
                }
                break;
            }
            if (quiet) WriteLog((install ? "установка" : "удаление") + ": " + message);
            else if (code != KompasRunning)
                MessageBox.Show(message, Title, MessageBoxButtons.OK,
                    code == Ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            Installer.RunDeferred(deferred);
            return code;
        }

        private static void WriteLog(string line)
        {
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "NormaCAD-Setup.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + line
                    + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception) { }
        }
    }

    internal class SetupForm : Form
    {
        private readonly SetupOptions options;
        private readonly Label state = new Label();
        private readonly Label result = new Label();
        private readonly Button primary = new Button();
        private readonly Button remove = new Button();
        private readonly Button close = new Button();

        public string Deferred;

        public SetupForm(SetupOptions o)
        {
            options = o;
            Text = SetupProgram.Title;
            Font = new Font("Segoe UI", 9f);
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(500, 270);

            Label title = new Label();
            title.Text = SetupProgram.Title;
            title.Font = new Font(Font.FontFamily, 13f, FontStyle.Bold);
            title.SetBounds(16, 14, 468, 28);

            state.SetBounds(16, 50, 468, 20);
            result.SetBounds(16, 76, 468, 92);

            Label hint = new Label();
            hint.Text = "Ставится для текущего пользователя Windows, права администратора не нужны. "
                + "Перед установкой и удалением закройте КОМПАС-3D. Удалить можно здесь "
                + "или в Windows: «Параметры» → «Приложения».";
            hint.ForeColor = Color.FromArgb(110, 110, 110);
            hint.SetBounds(16, 172, 468, 48);

            primary.SetBounds(16, 228, 140, 28);
            remove.SetBounds(166, 228, 100, 28);
            remove.Text = "Удалить";
            close.SetBounds(384, 228, 100, 28);
            close.Text = "Закрыть";
            close.DialogResult = DialogResult.Cancel;

            primary.Click += delegate { Run(true); };
            remove.Click += delegate { Run(false); };
            CancelButton = close;
            AcceptButton = primary;

            Controls.AddRange(new Control[] { title, state, result, hint, primary, remove, close });
            RefreshState();
        }

        private void RefreshState()
        {
            bool installed = Installer.IsInstalled(options);
            state.Text = (installed ? "Установлено, версия " + Installer.InstalledVersion(options) : "Не установлено")
                + ". Пользователь Windows: " + Environment.UserName + ".";
            primary.Text = installed ? "Переустановить" : "Установить";
            remove.Visible = installed;
        }

        private void Run(bool install)
        {
            result.Text = "";
            Cursor = Cursors.WaitCursor;
            try
            {
                while (true)
                {
                    try
                    {
                        if (install)
                        {
                            result.Text = Summary(Installer.Install(options));
                            Deferred = null;
                        }
                        else
                        {
                            UninstallResult r = Installer.Uninstall(options);
                            Deferred = r.DeferredCommand;
                            result.Text = Summary(r);
                        }
                        break;
                    }
                    catch (KompasRunningException ex)
                    {
                        if (MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.RetryCancel,
                            MessageBoxIcon.Warning) == DialogResult.Retry) continue;
                        result.Text = "Отменено, ничего не изменено. " + ex.Message;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                result.Text = "Ошибка: " + ex.Message;
            }
            finally
            {
                Cursor = Cursors.Default;
            }
            RefreshState();
        }

        internal static string Summary(InstallResult r)
        {
            string done = r.Previous == "" ? "Установлена версия " + r.Version + "."
                : r.Previous == r.Version ? "Версия " + r.Version + " переустановлена."
                : "Обновлено: версия " + r.Previous + " → " + r.Version + ".";
            string s;
            if (r.ConfigsReady > 0)
                s = done + " Запустите КОМПАС-3D: в меню «Приложения» появится «NormaCAD.PRO — проверка "
                    + "перед нормоконтролем» с командой «Проверить документ».";
            else
                s = done + " Но КОМПАС-3D под этой учётной записью Windows ещё не запускался, и пункта в меню "
                    + "«Приложения» нет. Запустите КОМПАС-3D, закройте его и запустите установку снова "
                    + "(или в КОМПАС: «Приложения» → «Добавить приложение…» → " + r.DllPath + ").";
            foreach (string w in r.Warnings) s += "\n" + w;
            return s;
        }

        internal static string Summary(UninstallResult r)
        {
            string s = "Расширение удалено." + (r.SignedOut == true ? " Вход на сервере закрыт." : "");
            foreach (string w in r.Warnings) s += "\n" + w;
            return s;
        }
    }
}
