using System;
using System.Windows.Forms;

namespace MacDock
{
    // Un solo hook de eventos de ventana para todo MacDock (primer plano, crear/destruir/mostrar y
    // movimiento). Corre en su propio hilo STA para no cargar el hilo de la interfaz.
    // Antes vivia en MacButtons.cs; se conserva porque Dock y MenuBar tambien lo usan.
    static class WinWatch
    {
        static readonly object Lock = new object();
        static Action<uint, IntPtr> handlers;
        static N.WinEventProc proc;
        static bool started;

        public static void Add(Action<uint, IntPtr> h)
        {
            lock (Lock)
            {
                handlers += h;
                if (started) return;
                started = true;
                Start();
            }
        }

        public static void Remove(Action<uint, IntPtr> h)
        {
            lock (Lock) { handlers -= h; }
        }

        static void Start()
        {
            var th = new System.Threading.Thread(() =>
            {
                IntPtr sys = IntPtr.Zero, obj = IntPtr.Zero;
                try
                {
                    proc = Dispatch;
                    sys = N.SetWinEventHook(0x0003, 0x0017, IntPtr.Zero, proc, 0, 0, 0);
                    obj = N.SetWinEventHook(0x8000, 0x8003, IntPtr.Zero, proc, 0, 0, 0);
                    // EVENT_OBJECT_LOCATIONCHANGE no se engancha aqui: llega para todas las
                    // ventanas del sistema y en reposo solo genera trabajo. El dock mantiene un
                    // hook limitado al proceso en primer plano (InstallLocHook) para la captura.
                    Application.Run();
                }
                catch (Exception ex) { Program.Log(ex); }
                finally
                {
                    if (sys != IntPtr.Zero) N.UnhookWinEvent(sys);
                    if (obj != IntPtr.Zero) N.UnhookWinEvent(obj);
                }
            });
            th.IsBackground = true;
            th.SetApartmentState(System.Threading.ApartmentState.STA);
            th.Start();
        }

        static void Dispatch(IntPtr hk, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time)
        {
            if (obj != 0 || child != 0 || hwnd == IntPtr.Zero) return;
            Action<uint, IntPtr> hs;
            lock (Lock) hs = handlers;
            if (hs == null) return;
            try { hs(ev, hwnd); } catch { }
        }
    }
}
