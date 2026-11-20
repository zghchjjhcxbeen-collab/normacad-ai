// Hosted acceptance: the installed library runs inside an owned KOMPAS process.
// No application classes or export hooks are linked into this executable.
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Automation;

class HostedWorkflowTest
{
    static object Call(object o, string n, params object[] a) { return o.GetType().InvokeMember(n, BindingFlags.GetProperty | BindingFlags.InvokeMethod, null, o, a); }
    static void Set(object o, string n, object v) { o.GetType().InvokeMember(n, BindingFlags.SetProperty, null, o, new object[] { v }); }
    static readonly string[] ProcessNames = { "kStudy", "KOMPAS", "kHome" };
    static string token, marker, output, sourceName;
    static int submissions, polls;
    static volatile bool accepting;
    static volatile string serverError;
    static HttpListener server;
    static AutomationElement Find(AutomationElement root, string name) { return root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, name)); }
    static AutomationElement Window(int pid) { return AutomationElement.RootElement.FindFirst(TreeScope.Children, new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty, pid), new PropertyCondition(AutomationElement.NameProperty, "NormaCAD.PRO — проверка перед нормоконтролем"))); }
    static bool Running() { foreach (string name in ProcessNames) foreach (Process p in Process.GetProcessesByName(name)) { p.Dispose(); return true; } return false; }
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    static string StartMock()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); int port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        string url = "http://127.0.0.1:" + port;
        server = new HttpListener(); server.Prefixes.Add(url + "/"); server.Start();
        new Thread(delegate() {
            while (server.IsListening) {
                HttpListenerContext c; try { c = server.GetContext(); } catch { return; }
                try {
                    Require(c.Request.Headers["Authorization"] == "Bearer " + token, "Unexpected credentials: isolated settings were not used");
                    string path = c.Request.Url.AbsolutePath, body;
                    if (path == "/api/auth/me") body = "{\"email\":\"hosted@example.invalid\",\"full_name\":\"Hosted acceptance\"}";
                    else if (path == "/api/users/me/access") body = "{\"plan\":\"trial\",\"expired\":false,\"can_check\":true,\"remaining\":999,\"checks_limit\":999,\"used\":0}";
                    else if (path == "/api/drawings/jobs" && c.Request.HttpMethod == "POST") {
                        Require(accepting, "Submission outside the single authorized check");
                        Require(Interlocked.Increment(ref submissions) == 1, "Duplicate submission rejected");
                        using (var bytes = new MemoryStream()) {
                            c.Request.InputStream.CopyTo(bytes); byte[] data = bytes.ToArray();
                            string wire = Encoding.GetEncoding(28591).GetString(data);
                            Require(c.Request.ContentType.StartsWith("multipart/form-data;"), "Not multipart");
                            Require(wire.Contains("name=\"file\"; filename=\"") && wire.Contains("name=\"domain\""), "Missing upload fields");
                            Require(wire.Contains("%PDF-") || wire.Contains("\u0089PNG\r\n\u001a\n"), "No real PDF/PNG export in submitted body");
                            Require(data.Length > 256, "Empty export");
                            int header = wire.IndexOf("name=\"file\"; filename=\"", StringComparison.Ordinal);
                            int nameStart = header + "name=\"file\"; filename=\"".Length;
                            int nameEnd = wire.IndexOf('"', nameStart);
                            string submittedName = Encoding.UTF8.GetString(data, nameStart, nameEnd - nameStart);
                            string expectedStem = Path.GetFileNameWithoutExtension(sourceName);
                            Require(submittedName == expectedStem + ".pdf" || submittedName == expectedStem + ".png", "Wrong document submitted: " + submittedName + "; expected " + sourceName);
                            File.AppendAllText(Path.Combine(output, "submissions.txt"), sourceName + " -> " + submittedName + "\r\n", Encoding.UTF8);
                            int begin = wire.IndexOf("\r\n\r\n", header, StringComparison.Ordinal) + 4;
                            string boundary = c.Request.ContentType.Substring(c.Request.ContentType.IndexOf("boundary=", StringComparison.Ordinal) + 9).Trim('"');
                            int end = wire.IndexOf("\r\n--" + boundary, begin, StringComparison.Ordinal);
                            Require(begin > 3 && end > begin, "Malformed upload file part");
                            byte[] uploaded = new byte[end - begin]; Array.Copy(data, begin, uploaded, 0, uploaded.Length);
                            string uploadDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "hosted-uploads")); Directory.CreateDirectory(uploadDir);
                            File.WriteAllBytes(Path.Combine(uploadDir, sourceName + (wire.Substring(begin).StartsWith("%PDF-") ? ".pdf" : ".png")), uploaded);
                        }
                        body = "{\"job_id\":\"" + marker + "\",\"status\":\"queued\",\"progress\":0}";
                    }
                    else if (path == "/api/drawings/jobs/" + marker && c.Request.HttpMethod == "GET") {
                        Interlocked.Increment(ref polls);
                        body = "{\"job_id\":\"" + marker + "\",\"status\":\"done\",\"progress\":100,\"report\":{\"verdict\":\"" + marker + "\",\"score\":100,\"pages_total\":1,\"pages_checked\":1,\"check_id\":42,\"stats\":{\"critical\":0,\"warnings\":1,\"ok\":0},\"violations\":[{\"n\":1,\"severity\":\"warning\",\"title\":\"" + marker + "\",\"standard\":\"Acceptance fixture\",\"detail\":\"Mock transport report\",\"fixes\":[],\"page\":1}]}}";
                    } else throw new Exception("Unexpected endpoint " + c.Request.HttpMethod + " " + path);
                    byte[] response = Encoding.UTF8.GetBytes(body); c.Response.ContentType = "application/json; charset=utf-8"; c.Response.OutputStream.Write(response, 0, response.Length); c.Response.Close();
                } catch (Exception e) { serverError = e.ToString(); try { c.Response.StatusCode = 500; c.Response.Close(); } catch { } }
            }
        }) { IsBackground = true }.Start();
        return url;
    }
    static void CopyTree(string source, string destination) {
        Directory.CreateDirectory(destination);
        foreach (string f in Directory.GetFiles(source)) File.Copy(f, Path.Combine(destination, Path.GetFileName(f)), true);
        foreach (string d in Directory.GetDirectories(source)) CopyTree(d, Path.Combine(destination, Path.GetFileName(d)));
    }
    static void AssertOwned(int pid) {
        foreach (string n in ProcessNames) foreach (Process p in Process.GetProcessesByName(n)) using (p) Require(p.Id == pid, "Another KOMPAS process appeared; refusing to attach");
    }
    static object FindCheckCommand(object app, string path)
    {
        object libraries = Call(Call(app, "LibraryManager"), "ProceduresLibraries");
        for (int i = 0; i < (int)Call(libraries, "Count"); i++)
        {
            object library = Call(libraries, "Item", i);
            if (library == null || !string.Equals("" + Call(library, "PathName"), path, StringComparison.OrdinalIgnoreCase)) continue;
            object commands = Call(library, "Procedures");
            int count = (int)Call(commands, "Count");
            bool attached = (bool)Call(library, "Attach");
            Console.WriteLine("NormaCAD: attached=" + attached + ", commands=" + count);
            if (!attached) continue;
            for (int j = 0; j < count; j++)
            {
                object command = Call(commands, "Item", j);
                string name = "" + Call(command, "Name");
                int id = Convert.ToInt32(Call(command, "Id"));
                if (name == "Проверить документ" && id == 1)
                {
                    Console.WriteLine("Executing " + id + ": " + name);
                    return command;
                }
            }
        }
        return null;
    }
    [STAThread] static int Main(string[] args)
    {
        if (args.Length < 3) { Console.WriteLine("HostedWorkflowTest.exe <KOMPAS exe> <fixture folder> <output folder> [installed DLL]"); return 2; }
        Process owned = null; object app = null, doc = null; AutomationElement win = null;
        try {
            Require(!Running(), "An existing KOMPAS session belongs to the user; refusing to attach");
            output = Path.GetFullPath(args[2]); Directory.CreateDirectory(output);
            string cfg = Path.Combine(output, "isolated.cfg"); token = "hosted-" + Guid.NewGuid().ToString("N");
            string url = StartMock();
            string protectedToken = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser));
            File.WriteAllLines(cfg, new[] { "server=" + url, "email=hosted@example.invalid", "token=" + protectedToken, "domain=eskd", "topmost=0" }, Encoding.UTF8);
            var start = new ProcessStartInfo(Path.GetFullPath(args[0])); start.UseShellExecute = false; start.WindowStyle = ProcessWindowStyle.Hidden;
            start.EnvironmentVariables["NORMACAD_KOMPAS_CFG"] = cfg;
            start.EnvironmentVariables["NORMACAD_KOMPAS_LOG"] = Path.Combine(output, "isolated.log");
            owned = Process.Start(start);
            object k = null;
            for (int i = 0; i < 120 && k == null; i++) { Thread.Sleep(500); Require(!owned.HasExited, "Owned KOMPAS exited before COM registration"); try { k = Marshal.GetActiveObject("KOMPAS.Application.5"); } catch (COMException) { } }
            Require(k != null, "Owned KOMPAS did not register in ROT");
            foreach (string n in ProcessNames) foreach (Process p in Process.GetProcessesByName(n)) using (p) Require(p.Id == owned.Id, "Another KOMPAS process appeared; refusing to attach");
            app = Call(k, "ksGetApplication7"); Set(app, "HideMessage", 1); Set(k, "Visible", true);
            Require((int)Call(Call(app, "Documents"), "Count") == 0, "Owned process has pre-existing documents");
            string dll = args.Length > 3 ? Path.GetFullPath(args[3]) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"NormaCAD\Kompas\NormaCadKompas.rtw");
            string copiedRoot = Path.Combine(output, "inputs"); CopyTree(Path.GetFullPath(args[1]), copiedRoot);
            int count = 0, failed = 0; string stage = "";
            string[] fixtures = Directory.GetFiles(Path.GetFullPath(args[1])); Array.Sort(fixtures, StringComparer.OrdinalIgnoreCase);
            foreach (string file in fixtures) {
                string ext = Path.GetExtension(file).ToLowerInvariant(); if (Array.IndexOf(new[] { ".cdw", ".spw", ".kdw", ".frw" }, ext) < 0) continue;
                sourceName = Path.GetFileName(file);
                try {
                stage = "ownership"; Require(!owned.HasExited, "Owned host exited"); AssertOwned(owned.Id);
                stage = "prepare"; Require(Window(owned.Id) == null, "Previous extension window is still closing"); Require((int)Call(Call(app, "Documents"), "Count") == 0, "Previous document cleanup incomplete");
                marker = "HOSTED_" + (++count) + "_" + Guid.NewGuid().ToString("N"); submissions = polls = 0; serverError = null;
                string copy = Path.Combine(copiedRoot, Path.GetFileName(file));
                stage = "open";
                doc = Call(Call(app, "Documents"), "Open", copy, true, false); Require(doc != null, "Could not open " + file);
                stage = "verify-active-document";
                Require(string.Equals("" + Call(Call(app, "ActiveDocument"), "PathName"), copy, StringComparison.OrdinalIgnoreCase), "Opened file is not the active document");
                stage = "execute-labeled-command";
                object command = FindCheckCommand(app, dll);
                Require(command != null, "Installed command 'Проверить документ' missing: " + dll);
                Call(command, "Execute", new DispatchWrapper(null), true);
                stage = "wait-window";
                win = null; for (int i = 0; i < 120 && win == null; i++) { Thread.Sleep(500); win = Window(owned.Id); }
                Require(win != null, "Hosted extension window did not appear for " + file);
                AutomationElement button = null; for (int i = 0; i < 60 && button == null; i++) { Thread.Sleep(500); button = Find(win, "Проверить"); }
                Require(button != null, "Check button did not appear");
                AutomationElement identity = null;
                for (int i = 0; i < 60 && identity == null; i++) { identity = Find(win, "hosted@example.invalid"); if (identity == null) Thread.Sleep(250); }
                Require(identity != null, "Isolated test account missing; upload stopped");
                stage = "verify-document-before-check";
                Require(string.Equals("" + Call(Call(app, "ActiveDocument"), "PathName"), copy, StringComparison.OrdinalIgnoreCase), "Active document changed before upload");
                stage = "press-check"; accepting = true;
                ((InvokePattern)button.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                stage = "wait-correlated-report";
                bool complete = false;
                for (int i = 0; i < 180; i++) { Thread.Sleep(500); Require(serverError == null, "Mock failed: " + serverError); if (Find(win, "Проверить снова") != null && Find(win, marker) != null) { complete = true; break; } }
                Require(complete && submissions == 1 && polls > 0, "No completed correlated report: submissions=" + submissions + ", polls=" + polls);
                File.AppendAllText(Path.Combine(output, "results.txt"), "PASS " + Path.GetFileName(file) + " " + marker + " submission=1 polled=true report=true\r\n", Encoding.UTF8);
                Console.WriteLine("PASS " + Path.GetFileName(file));
                } catch (Exception e) {
                    failed++; string failure = "FAIL " + sourceName + " stage=" + stage + " submissions=" + submissions + " polls=" + polls + " " + e;
                    Console.WriteLine(failure); File.AppendAllText(Path.Combine(output, "results.txt"), failure + "\r\n", Encoding.UTF8);
                } finally {
                    accepting = false;
                    // Wait for disposal: Launcher clears its static form asynchronously after Close.
                    if (win != null) try { ((WindowPattern)win.GetCurrentPattern(WindowPattern.Pattern)).Close(); } catch { }
                    win = null;
                    for (int i = 0; i < 40 && !owned.HasExited && Window(owned.Id) != null; i++) Thread.Sleep(100);
                    Thread.Sleep(300);
                    if (doc != null) { try { Call(doc, "Close", 0); } catch (Exception close) { File.AppendAllText(Path.Combine(output, "results.txt"), "CLEANUP " + sourceName + " " + close + "\r\n"); } doc = null; }
                }
                if (owned.HasExited) { Console.WriteLine("Owned host exited; cannot continue"); return 1; }
            }
            Require(count > 0, "No supported fixture files"); return failed == 0 ? 0 : 1;
        } catch (Exception e) { Console.WriteLine(e); return 1; }
        finally {
            if (win != null) try { ((WindowPattern)win.GetCurrentPattern(WindowPattern.Pattern)).Close(); } catch { }
            if (doc != null) try { Call(doc, "Close", 0); } catch { }
            if (app != null) try { Call(app, "Quit"); } catch { }
            if (owned != null) { if (!owned.WaitForExit(15000)) owned.Kill(); owned.Dispose(); }
            if (server != null) server.Close();
        }
    }
}


