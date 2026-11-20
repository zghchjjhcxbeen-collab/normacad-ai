// Окно проверки в своём потоке и связь окна с КОМПАС: по кнопке «Проверить» окно само выгружает
// активный документ, а COM-объект КОМПАС из чужого потока звать нельзя.
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace NormaCad.Kompas
{
    /// <summary>
    /// Держит окно проверки в отдельном потоке со своим циклом сообщений:
    /// КОМПАС не блокируется, инженер правит чертёж, глядя на замечания.
    /// </summary>
    internal static class Launcher
    {
        private static readonly object Gate = new object();
        private static CheckForm form;

        /// <summary>Без объекта КОМПАС: окно возьмёт объект приложения из ROT.</summary>
        public static void Open(ExportResult r)
        {
            Open(r, null);
        }

        /// <summary>
        /// Вызывать в потоке КОМПАС. r — выгруженный документ (показать имя, листы и форматы), null — окно
        /// открыто командой «Аккаунт». kompas — объект приложения (API5): через него окно по кнопке
        /// «Проверить» выгружает активный документ заново.
        /// </summary>
        public static void Open(ExportResult r, object kompas)
        {
            // В поток окна объект попадает только через таблицу интерфейсов COM
            int cookie = KompasLink.Register(kompas);
            lock (Gate)
            {
                CheckForm current = form;
                if (current != null && !current.IsDisposed && current.IsHandleCreated)
                {
                    try
                    {
                        current.BeginInvoke(new MethodInvoker(delegate
                        {
                            current.Activate();
                            current.AttachKompas(cookie);
                            current.Begin(r);
                        }));
                    }
                    catch (InvalidOperationException)
                    {
                        // Окно как раз закрывается
                        KompasLink.Revoke(cookie);
                    }
                    return;
                }

                ManualResetEvent ready = new ManualResetEvent(false);
                Thread thread = new Thread(delegate()
                {
                    try
                    {
                        Dpi.EnterThread();
                        Application.EnableVisualStyles();
                        CheckForm f = new CheckForm();
                        f.AttachKompas(cookie);
                        f.Shown += delegate { f.Begin(r); };
                        f.HandleCreated += delegate { form = f; ready.Set(); };
                        Application.Run(f);
                    }
                    catch (Exception)
                    {
                        // Сбой окна не должен уронить КОМПАС
                    }
                    finally
                    {
                        form = null;
                        ready.Set();
                    }
                });
                thread.SetApartmentState(ApartmentState.STA);
                thread.IsBackground = true;
                thread.Name = "NormaCAD check window";
                thread.Start();
                // Иначе повторный вызов команды успел бы открыть второе окно
                ready.WaitOne(5000);
            }
        }
    }

    /// <summary>
    /// Объект приложения КОМПАС для потока окна. COM-объект, полученный в потоке КОМПАС, из другого
    /// потока звать нельзя: в потоке КОМПАС он регистрируется в Global Interface Table, а поток окна
    /// берёт оттуда своего посредника — вызовы уходят в поток КОМПАС через его очередь сообщений.
    /// Не удалось зарегистрировать — объект приложения из ROT (KOMPAS.Application.5).
    /// </summary>
    internal static class KompasLink
    {
        [ComImport, Guid("00000146-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IGlobalInterfaceTable
        {
            void RegisterInterfaceInGlobal([MarshalAs(UnmanagedType.IUnknown)] object unknown, [In] ref Guid iid, out int cookie);
            void RevokeInterfaceFromGlobal(int cookie);
            void GetInterfaceFromGlobal(int cookie, [In] ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object unknown);
        }

        [ComImport, Guid("00000323-0000-0000-C000-000000000046")]
        private class StdGlobalInterfaceTable
        {
        }

        private static readonly Guid IDispatch = new Guid("00020400-0000-0000-C000-000000000046");

        internal const string ProgId = "KOMPAS.Application.5";

        private static IGlobalInterfaceTable Table()
        {
            return (IGlobalInterfaceTable)new StdGlobalInterfaceTable();
        }

        /// <summary>Зарегистрировать объект в потоке, которому он принадлежит. 0 — объекта нет или не вышло.</summary>
        public static int Register(object app)
        {
            if (app == null) return 0;
            try
            {
                Guid iid = IDispatch;
                int cookie;
                Table().RegisterInterfaceInGlobal(app, ref iid, out cookie);
                return cookie;
            }
            catch (Exception ex)
            {
                SupportLog.Write("КОМПАС: объект приложения не передан окну: " + ex.Message);
                return 0;
            }
        }

        /// <summary>Посредник объекта для текущего потока; cookie == 0 — объект приложения из ROT.</summary>
        public static object Get(int cookie)
        {
            if (cookie != 0)
            {
                try
                {
                    Guid iid = IDispatch;
                    object app;
                    Table().GetInterfaceFromGlobal(cookie, ref iid, out app);
                    return app;
                }
                catch (COMException ex)
                {
                    SupportLog.Write("КОМПАС: окно восстановит связь с приложением: " + ex.Message);
                }
                catch (InvalidCastException ex)
                {
                    SupportLog.Write("КОМПАС: окно восстановит связь с приложением: " + ex.Message);
                }
            }
            return Marshal.GetActiveObject(ProgId);
        }

        public static void Revoke(int cookie)
        {
            if (cookie == 0) return;
            try { Table().RevokeInterfaceFromGlobal(cookie); }
            catch (Exception) { }
        }
    }
}
