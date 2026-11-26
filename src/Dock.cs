﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MacDock
{
    enum Kind { App, Start, Clock, Downloads, Trash, Separator, Stack }

    class DockItem
    {
        public Kind Kind; public string Name = ""; public string Lnk; public string Target = ""; public string Args = "";
        public string Aumid;          // AppUserModelID (ventana / .lnk); sirve para agrupar ventanas de la misma app
        public string LauncherExe;    // Discord y otros Squirrel: Update.exe --processStart xx.exe
        public Bitmap Icon; public bool Pinned; public bool IsExplorer;
        public List<IntPtr> Windows = new List<IntPtr>();
        public DateTime LaunchAt = DateTime.MinValue;
        public int Badge;
        public float X, W, Scale = 1f, Cx;
        public bool Running { get { return Windows.Count > 0; } }
    }

    static class Program
    {
        public static bool Clock12;   // reloj en formato de 12 horas (config Clock12h)
        public static string TimeText(DateTime d, bool suffix)
        {
            // Cultura del sistema: en espanol el sufijo queda "a. m." / "p. m." como en Windows.
            var cul = System.Globalization.CultureInfo.CurrentCulture;
            if (!Clock12) return d.ToString("HH:mm", cul);
            return d.ToString(suffix ? "h:mm tt" : "h:mm", cul);
        }

        public static string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MacDock");

        // Recorta la memoria tras cerrar paneles o al quedar en reposo. Unico mecanismo del
        // programa: los caminos de la interfaz (dock, barra, Centro de Control) llegan aqui, sin
        // colecciones dobles ni repetidas gracias al cooldown. Seguro desde cualquier hilo: el
        // trabajo corre en el ThreadPool y no bloquea la interfaz.
        static long lastTrimTicks;
        static int trimBusy;
        public static void TrimMemory()
        {
            // cooldown: si otra operacion recorto hace menos de 2,5 s, esta no aporta nada nuevo
            long now = DateTime.UtcNow.Ticks;
            long last = System.Threading.Interlocked.Read(ref lastTrimTicks);
            if ((now - last) < 2500L * TimeSpan.TicksPerMillisecond) return;
            if (System.Threading.Interlocked.Exchange(ref trimBusy, 1) != 0) return;
            try
            {
                System.Threading.Interlocked.Exchange(ref lastTrimTicks, now);
                System.Threading.ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        GC.Collect();
                        N.SetProcessWorkingSetSize(N.GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));
                    }
                    catch { }
                    finally { System.Threading.Interlocked.Exchange(ref trimBusy, 0); }
                });
            }
            catch { System.Threading.Interlocked.Exchange(ref trimBusy, 0); }
        }

        public static void Log(object o)
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                string f = Path.Combine(DataDir, "error.log");
                var fi = new FileInfo(f);
                if (fi.Exists && fi.Length > 256 * 1024)
                {
                    string old = Path.Combine(DataDir, "error.log.old");
                    try { if (File.Exists(old)) File.Delete(old); } catch { }
                    try { File.Move(f, old); } catch { }
                }
                File.AppendAllText(f, DateTime.Now + "  " + o + "\r\n\r\n");
            }
            catch { }
        }

        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length > 0 && (args[0] == "--finder" || args[0] == "--spotlight"))
            {
                IntPtr h = N.FindWindow(null, "MacDockWindow");
                if (h != IntPtr.Zero) N.PostMessage(h, N.RegisterWindowMessage(args[0] == "--finder" ? "MacDockOpenFinder" : "MacDockOpenSpotlight"), IntPtr.Zero, IntPtr.Zero);
                else if (args[0] == "--finder") System.Diagnostics.Process.Start("explorer.exe");
                return;
            }
            if (args.Length > 0 && args[0] == "--exit")
            {
                IntPtr h = N.FindWindow(null, "MacDockWindow");
                if (h != IntPtr.Zero) N.PostMessage(h, 0x10, IntPtr.Zero, IntPtr.Zero);
                return;
            }
            if (args.Length > 0 && args[0] == "--trim")
            {
                IntPtr h = N.FindWindow(null, "MacDockWindow");
                if (h != IntPtr.Zero) N.PostMessage(h, N.RegisterWindowMessage("MacDockTrim"), IntPtr.Zero, IntPtr.Zero);
                return;
            }
            string exeName = Path.GetFileNameWithoutExtension(Application.ExecutablePath).ToLowerInvariant();
            bool uninstall = (args.Length > 0 && args[0] == "--uninstall") || exeName.Contains("desinstal") || exeName.Contains("uninstall");
            bool install = !uninstall && ((args.Length > 0 && args[0] == "--install") || exeName.Contains("instal") || exeName.Contains("setup"));
            if (uninstall || install)
            {
                try { N.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
                Application.EnableVisualStyles();
                if (uninstall) Setup.Uninstall(); else Setup.Install();
                return;
            }
            bool created;
            using (var mutex = new Mutex(true, "MacDock_Singleton_v1", out created))
            {
                if (!created) return;
                try { N.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.EnableVisualStyles();
                var form = new DockForm();
                Application.ThreadException += (s, e) => { Log(e.Exception); form.SafeRestore(); Application.Exit(); };
                AppDomain.CurrentDomain.UnhandledException += (s, e) => { Log(e.ExceptionObject); form.CriticalRestore(); };
                // ProcessExit no corre en el hilo de interfaz: solo restauraciones criticas y seguras.
                AppDomain.CurrentDomain.ProcessExit += (s, e) => form.CriticalRestore();
                Application.Run(form);
                form.SafeRestore();
            }
        }
    }

    class Config
    {
        public int IconSize = 48;
        public float Magnification = 1.8f;
        public bool ReserveSpace = false;
        public bool AutoHide = true;
        public bool AutoRefresh = false;
        public bool AppNap = false;
        public bool HotCorners = false;
        public bool MenuBar = true;
        public bool MenuBarAutoHide = false;
        public bool AutoTheme = false;
        public bool DynWall = false;
        public bool UseFinder = true;
        public bool SpotlightKey = true;
        public bool QuickLookExplorer = true;
        public bool DesktopMenu = true;
        public TimeSpan LightFrom = new TimeSpan(7, 0, 0);
        public TimeSpan DarkFrom = new TimeSpan(18, 30, 0);
        public bool HideTaskbar = true;
        public bool Clock12h = false;
        public bool AnimApertura  = false;   // legacy — solo para migracion a AnimVentanas
        public bool AnimVentanas  = false;   // interruptor general de animaciones de ventana
        public bool AnimAbrirScale  = true;  // apertura scale
        public bool AnimMinGenie    = true;  // minimizacion genie
        public int AnimGenieVel = -1;
        public bool VistaPrevia = true;
        public bool AnimCerrarScale = true;  // cierre scale+fade
        public bool Magnify = true;
        public bool Diagnostico = false;   // logs de diagnostico (duplicados, barra visible); 0 por defecto
        string path;

        public Config(string p)
        {
            path = p;
            if (!File.Exists(p)) { Save(); return; }
            bool haveMaster = false;
            foreach (var line in File.ReadAllLines(p))
            {
                var kv = line.Split(new[] { '=' }, 2); if (kv.Length < 2) continue;
                string k = kv[0].Trim().ToLowerInvariant(), v = kv[1].Trim();
                float f; int i;
                if (k == "iconsize" && int.TryParse(v, out i)) IconSize = Math.Max(24, Math.Min(96, i));
                else if (k == "magnification" && float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f)) Magnification = Math.Max(1f, Math.Min(2.5f, f));
                else if (k == "reservespace") ReserveSpace = v == "1";
                else if (k == "hidetaskbar") HideTaskbar = v == "1";
                else if (k == "clock12h") { Clock12h = v == "1"; Program.Clock12 = Clock12h; }
                else if (k == "animapertura")
                {
                    // Legacy: si no hay AnimVentanas todavia, migramos el valor
                    AnimApertura = v == "1";
                }
                else if (k == "animventanas") { AnimVentanas = v == "1"; haveMaster = true; }
                else if (k == "animabrirscale")  AnimAbrirScale  = v == "1";
                else if (k == "animmingenie")    AnimMinGenie    = v == "1";
                else if (k == "animgenievel" && int.TryParse(v, out i) && i >= 0 && i <= 2) AnimGenieVel = i;
                else if (k == "vistaprevia") VistaPrevia = v == "1";
                else if (k == "animcerrarscale") AnimCerrarScale = v == "1";
                else if (k == "animcerrascale")  AnimCerrarScale = v == "1";   // variante antigua mal escrita
                else if (k == "diagnostico") Diagnostico = v == "1";
                else if (k == "magnify") Magnify = v == "1";
                else if (k == "autohide") AutoHide = v == "1";
                else if (k == "autorefresh") AutoRefresh = v == "1";
                else if (k == "appnap") AppNap = v == "1";
                else if (k == "hotcorners") HotCorners = v == "1";
                else if (k == "menubar") MenuBar = v == "1";
                else if (k == "menubarautohide") MenuBarAutoHide = v == "1";
                else if (k == "autotheme") AutoTheme = v == "1";
                else if (k == "dynwall") DynWall = v == "1";
                else if (k == "usefinder") UseFinder = v == "1";
                else if (k == "spotlight") SpotlightKey = v == "1";
                else if (k == "quicklookexplorer") QuickLookExplorer = v == "1";
                // "macbuttons" ya no existe: se ignora sin error en configs antiguas.
                else if (k == "desktopmenu") DesktopMenu = v == "1";
                else if (k == "lightfrom") { TimeSpan t; if (TimeSpan.TryParse(v, out t)) LightFrom = t; }
                else if (k == "darkfrom") { TimeSpan t; if (TimeSpan.TryParse(v, out t)) DarkFrom = t; }
            }
            // Migracion: AnimApertura (legacy) -> AnimVentanas si no habia clave nueva
            if (!haveMaster && AnimApertura) AnimVentanas = true;
        }

        public void Save()
        {
            try
            {
                File.WriteAllText(path,
                    "# Configuracion del dock. Cambia un valor, guarda y reinicia el dock.\r\n" +
                    "IconSize=" + IconSize + "\r\n" +
                    "Magnification=" + Magnification.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\r\n" +
                    "Magnify=" + (Magnify ? 1 : 0) + "\r\n" +
                    "AutoHide=" + (AutoHide ? 1 : 0) + "\r\n" +
                    "AutoRefresh=" + (AutoRefresh ? 1 : 0) + "\r\n" +
                    "AppNap=" + (AppNap ? 1 : 0) + "\r\n" +
                    "HotCorners=" + (HotCorners ? 1 : 0) + "\r\n" +
                    "MenuBar=" + (MenuBar ? 1 : 0) + "\r\n" +
                    "MenuBarAutoHide=" + (MenuBarAutoHide ? 1 : 0) + "\r\n" +
                    "AutoTheme=" + (AutoTheme ? 1 : 0) + "\r\n" +
                    "LightFrom=" + LightFrom.ToString(@"hh\:mm") + "\r\n" +
                    "DarkFrom=" + DarkFrom.ToString(@"hh\:mm") + "\r\n" +
                    "DynWall=" + (DynWall ? 1 : 0) + "\r\n" +
                    "UseFinder=" + (UseFinder ? 1 : 0) + "\r\n" +
                    "Spotlight=" + (SpotlightKey ? 1 : 0) + "\r\n" +
                    "QuickLookExplorer=" + (QuickLookExplorer ? 1 : 0) + "\r\n" +
                    "DesktopMenu=" + (DesktopMenu ? 1 : 0) + "\r\n" +
                    "ReserveSpace=" + (ReserveSpace ? 1 : 0) + "\r\n" +
                    "HideTaskbar=" + (HideTaskbar ? 1 : 0) + "\r\n" +
                    "Clock12h=" + (Clock12h ? 1 : 0) + "\r\n" +
                    "AnimVentanas=" + (AnimVentanas ? 1 : 0) + "\r\n" +
                    "AnimAbrirScale=" + (AnimAbrirScale ? 1 : 0) + "\r\n" +
                    "AnimMinGenie=" + (AnimMinGenie ? 1 : 0) + "\r\n" +
                    (AnimGenieVel < 0 ? "" : "AnimGenieVel=" + AnimGenieVel + "\r\n") +
                    "VistaPrevia=" + (VistaPrevia ? 1 : 0) + "\r\n" +
                    "AnimCerrarScale=" + (AnimCerrarScale ? 1 : 0) + "\r\n" +
                    "Diagnostico=" + (Diagnostico ? 1 : 0) + "\r\n");
            }
            catch { }
        }
    }

    class DarkColors : ProfessionalColorTable
    {
        static Color bg = Color.FromArgb(44, 44, 48), sel = Color.FromArgb(10, 100, 220), bd = Color.FromArgb(70, 70, 76);
        public override Color ToolStripDropDownBackground { get { return bg; } }
        public override Color ImageMarginGradientBegin { get { return bg; } }
        public override Color ImageMarginGradientMiddle { get { return bg; } }
        public override Color ImageMarginGradientEnd { get { return bg; } }
        public override Color MenuBorder { get { return bd; } }
        public override Color MenuItemBorder { get { return sel; } }
        public override Color MenuItemSelected { get { return sel; } }
        public override Color SeparatorDark { get { return bd; } }
        public override Color SeparatorLight { get { return bg; } }
    }

    class DockForm : Form
    {
        Config cfg;
        string appsDir, orderFile;
        float s = 1f;
        DockItem startItem, clockItem, downloadsItem, trashItem, sepItem, stackItem;
        StackPopup stackPopup; DateTime stackClosedAt = DateTime.MinValue; Kind stackKind, closedKind;
        List<DockItem> pinned = new List<DockItem>();
        List<DockItem> transient = new List<DockItem>();
        List<DockItem> all = new List<DockItem>();
        Dictionary<string, Bitmap> iconCache = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);

        System.Windows.Forms.Timer fast = new System.Windows.Forms.Timer();
        System.Windows.Forms.Timer slow = new System.Windows.Forms.Timer();
        FileSystemWatcher watcher;

        Bitmap canvas;
        Graphics canvasG;
        // Pinceles, lapices y fuentes reutilizados: Render() corre hasta a 60 fps al ampliar y no
        // debe crear objetos GDI nuevos en cada fotograma.
        readonly Dictionary<int, SolidBrush> brushCache = new Dictionary<int, SolidBrush>();
        readonly Dictionary<long, Pen> penCache = new Dictionary<long, Pen>();
        readonly Dictionary<long, Font> fontCache = new Dictionary<long, Font>();
        StringFormat sfCenter;
        int winX, winY, W, H;
        float mag = 0f, lastMx = -1;
        long animStamp;                 // reloj monotono del ultimo fotograma (Stopwatch)
        int hoverIdx = -1;
        bool hiddenFull = false, light = false, restored = false, appBarOn = false, trashFull = false;
        bool savedAutoHide = false, haveSavedState = false;
        string lastClock = "";
        DateTime idleSince = DateTime.MaxValue;
        int appBarMsg, taskbarCreatedMsg;

        DockItem pressItem; Point pressPt; bool dragging;
        DockItem previewItem;
        static readonly UIntPtr PreviewTimerId = new UIntPtr(0x4D50);
        bool previewHover;
        long previewDeadline;
        volatile DockPreview preview;
        IntPtr previewHwnd;
        IntPtr previewForeground;
        Rectangle previewAnchor;
        long previewCrossingUntil;
        bool sessionWatch;
        Point? previewDismissedAt;
        Point lastCursor; float lastMag = -1; int stillFrames;
        float magX = -1f; bool wasInside;
        System.Windows.Forms.Timer edge = new System.Windows.Forms.Timer();
        bool fullApp, peeking, menuOpen; DateTime leaveSince = DateTime.MaxValue;
        Action<uint, IntPtr> winWatchHandler;
        Microsoft.Win32.UserPreferenceChangedEventHandler prefHandler;
        EventHandler dispHandler;
        Microsoft.Win32.PowerModeChangedEventHandler powerHandler;
        bool runRefreshQueued;
        float slide = 0f, slideTarget = 0f;
        static readonly uint myPid = (uint)Process.GetCurrentProcess().Id;
        PowerLineStatus lastPower = (PowerLineStatus)(-1);
        DateTime refreshCheckAt = DateTime.MinValue;
        DateTime trashCheckAt = DateTime.MinValue;
        DateTime fixAt = DateTime.MinValue;
        bool loweredRefresh;
        // Modo claro/oscuro automatico y fondo dinamico (se revisa cada 30 s; no hace nada si no toca cambiar)
        DateTime lookAt = DateTime.MinValue;
        void UpdateLook()
        {
            try
            {
                var now = DateTime.Now.TimeOfDay;
                if (cfg.AutoTheme)
                {
                    bool wantLight = cfg.LightFrom <= cfg.DarkFrom ? (now >= cfg.LightFrom && now < cfg.DarkFrom) : !(now >= cfg.DarkFrom && now < cfg.LightFrom);
                    using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", true))
                    {
                        if (k != null && Convert.ToInt32(k.GetValue("SystemUsesLightTheme", 0)) != (wantLight ? 1 : 0))
                        {
                            k.SetValue("SystemUsesLightTheme", wantLight ? 1 : 0, RegistryValueKind.DWord);
                            k.SetValue("AppsUseLightTheme", wantLight ? 1 : 0, RegistryValueKind.DWord);
                            N.BroadcastSetting("ImmersiveColorSet");
                        }
                    }
                }
                if (cfg.DynWall)
                {
                    string period = now < new TimeSpan(6, 0, 0) ? "noche" : now < new TimeSpan(9, 0, 0) ? "amanecer" :
                                    now < new TimeSpan(17, 0, 0) ? "dia" : now < new TimeSpan(19, 30, 0) ? "atardecer" : "noche";
                    string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "MacDock", period + ".png");
                    if (File.Exists(file))
                    {
                        string cur = "";
                        using (var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop")) if (k != null) cur = Convert.ToString(k.GetValue("WallPaper", ""));
                        if (!string.Equals(cur, file, StringComparison.OrdinalIgnoreCase)) N.SpiStr(0x14, 0, file, 3);
                    }
                }
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        // =====================  funciones tipo Mac: Spotlight, Quick Look, soltar en el dock, aviso de descargas  =====================
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, int mods, int vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
        delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, LowLevelKeyboardProc fn, IntPtr mod, uint thread);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int n, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);
        [StructLayout(LayoutKind.Sequential)] struct GUITHREADINFO { public int cbSize, flags; public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret; public N.RECT rcCaret; }
        [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint thread, ref GUITHREADINFO info);

        LowLevelKeyboardProc kbProc; IntPtr kbHook = IntPtr.Zero;
        int msgFinder = N.RegisterWindowMessage("MacDockOpenFinder"), msgSpot = N.RegisterWindowMessage("MacDockOpenSpotlight");
        int msgTrim = N.RegisterWindowMessage("MacDockTrim");
        FileSystemWatcher dlWatcher;

        void InitMacFeatures()
        {
            if (cfg.SpotlightKey) RegisterHotKey(Handle, 7, 0x0001 | 0x4000, 0x20);   // Alt + Espacio
            if (cfg.QuickLookExplorer) SetKeyHook(true);
            AllowDrop = true;
            SetDesktopMenu(cfg.DesktopMenu);
            WatchDownloads();
            WatchTaskbarPins();
        }

        // ---- clic derecho en el escritorio: menu estilo Mac (el de Windows sigue disponible en "Mas opciones de Windows") ----
        [StructLayout(LayoutKind.Sequential)] struct MSLLHOOKSTRUCT { public N.POINT pt; public int mouseData, flags, time; public IntPtr extra; }
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(N.POINT p);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, int flags);
        LowLevelKeyboardProc msProc; IntPtr msHook = IntPtr.Zero; bool eatRightUp; DateTime nativeUntil = DateTime.MinValue;

        void SetDesktopMenu(bool on)
        {
            OnHookThread(() =>
            {
                if (on && msHook == IntPtr.Zero) { msProc = MouseHook; msHook = SetWindowsHookEx(14, msProc, GetModuleHandle(null), 0); }
                else if (!on && msHook != IntPtr.Zero) { UnhookWindowsHookEx(msHook); msHook = IntPtr.Zero; }
            });
        }

        void SetKeyHook(bool on)
        {
            OnHookThread(() =>
            {
                if (on && kbHook == IntPtr.Zero) { kbProc = KeyboardHook; kbHook = SetWindowsHookEx(13, kbProc, GetModuleHandle(null), 0); }
                else if (!on && kbHook != IntPtr.Zero) { UnhookWindowsHookEx(kbHook); kbHook = IntPtr.Zero; }
            });
        }

        // Los ganchos de mouse y teclado viven en su propio hilo: Windows espera la respuesta en cada movimiento del
        // mouse, y si el hilo del dock esta dibujando, el cursor se traba. Aqui responden al instante.
        static Control hookCtl;
        static void OnHookThread(Action a)
        {
            if (hookCtl == null)
            {
                var ready = new System.Threading.ManualResetEvent(false);
                var th = new System.Threading.Thread(() =>
                {
                    var ctl = new Control(); ctl.CreateControl(); var hh = ctl.Handle; hookCtl = ctl; ready.Set();
                    Application.Run();
                });
                th.IsBackground = true; th.Name = "ganchos"; th.SetApartmentState(System.Threading.ApartmentState.STA); th.Start();
                ready.WaitOne(3000);
            }
            try { if (hookCtl != null) hookCtl.Invoke(a); } catch (Exception ex) { Program.Log(ex); }
        }

        static bool IsDesktopAt(N.POINT p)
        {
            IntPtr h = WindowFromPoint(p);
            if (h == IntPtr.Zero) return false;
            IntPtr root = GetAncestor(h, 2);
            string rc = N.ClassOf(root), hc = N.ClassOf(h);
            return (rc == "Progman" || rc == "WorkerW") && (hc == "SHELLDLL_DefView" || hc == "SysListView32" || hc == "WorkerW" || hc == "Progman");
        }

        IntPtr MouseHook(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                int msg = wParam.ToInt32();
                if (nCode >= 0 && (msg == 0x204 || msg == 0x205) && DateTime.Now > nativeUntil)
                {
                    var info = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                    if (msg == 0x204 && IsDesktopAt(info.pt) && DesktopIcons.Visible == false) { eatRightUp = true; return new IntPtr(1); }
                    if (msg == 0x205 && eatRightUp) { eatRightUp = false; var pt = new Point(info.pt.x, info.pt.y); BeginInvoke(new Action(() => DesktopMenuAt(pt))); return new IntPtr(1); }
                }
            }
            catch { }
            return CallNextHookEx(msHook, nCode, wParam, lParam);
        }

        ShellMenu lastShellMenu;
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        static IntPtr DesktopView()
        {
            IntPtr dv = IntPtr.Zero, w = IntPtr.Zero;
            IntPtr pm = N.FindWindow("Progman", null);
            dv = FindWindowEx(pm, IntPtr.Zero, "SHELLDLL_DefView", null);
            while (dv == IntPtr.Zero && (w = FindWindowEx(IntPtr.Zero, w, "WorkerW", null)) != IntPtr.Zero) dv = FindWindowEx(w, IntPtr.Zero, "SHELLDLL_DefView", null);
            return dv;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr p, IntPtr after, string cls, string title);

        void DesktopMenuAt(Point pt)
        {
            ClosePreview();
            string desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var m = NewMenu();
            // estilo Mac
            Add(m, "Nueva carpeta", () => { FileOps.NewFolder(desk); Finder.OpenOrFocus(light, s, desk); });
            Add(m, "Abrir el escritorio en Finder", () => Finder.OpenOrFocus(light, s, desk));
            Add(m, "Ver lo que hay en el escritorio (pila)", () => { if (stackItem != null) ToggleStack(stackItem); });
            Add(m, DesktopIcons.Visible ? "Ocultar iconos del escritorio" : "Mostrar iconos del escritorio", () => DesktopIcons.Toggle());
            Add(m, "Cambiar fondo de pantalla...", () => Process.Start("ms-settings:personalization-background"));
            m.Items.Add(new ToolStripSeparator());
            // Ordenar por / Actualizar (los mismos comandos que usa el escritorio de Windows)
            var sort = new ToolStripMenuItem("Ordenar por");
            string[] names = { "Nombre", "Tamaño", "Tipo de elemento", "Fecha de modificación" };
            for (int i = 0; i < names.Length; i++) { int cmd = 0x7602 + i; var si = new ToolStripMenuItem(names[i]); si.Click += (o, e) => { IntPtr dv = DesktopView(); if (dv != IntPtr.Zero) SendMessage(dv, 0x111, new IntPtr(cmd), IntPtr.Zero); }; sort.DropDownItems.Add(si); }
            m.Items.Add(sort);
            Add(m, "Actualizar", () => { IntPtr dv = DesktopView(); if (dv != IntPtr.Zero) SendMessage(dv, 0x111, new IntPtr(0x7103), IntPtr.Zero); });
            m.Items.Add(new ToolStripSeparator());
            // todas las opciones reales de Windows (Pegar, Nuevo, Git, NVIDIA, Configuracion de pantalla, Personalizar...)
            if (lastShellMenu != null) { lastShellMenu.Dispose(); lastShellMenu = null; }
            var sm = new ShellMenu();
            if (sm.AppendTo(m.Items, Handle)) lastShellMenu = sm; else sm.Dispose();
            while (m.Items.Count > 0 && m.Items[m.Items.Count - 1] is ToolStripSeparator) m.Items.RemoveAt(m.Items.Count - 1);
            menuOpen = true;
            m.Closed += (o, e) => { menuOpen = false; };
            N.SetForegroundWindow(Handle);
            m.Show(pt);
        }
        void StopMacFeatures()
        {
            try { UnregisterHotKey(Handle, 7); } catch { }
            try { SetKeyHook(false); } catch { }
            try { if (dlWatcher != null) dlWatcher.Dispose(); } catch { }
            try { SetDesktopMenu(false); } catch { }
        }

        // Espacio en el Explorador (lista de archivos) = Quick Look del archivo seleccionado. Se ignora si estas escribiendo.
        IntPtr KeyboardHook(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0 && (wParam.ToInt32() == 0x100) && Marshal.ReadInt32(lParam) == 0x20)
                {
                    bool mods = (GetAsyncKeyState(0x10) & 0x8000) != 0 || (GetAsyncKeyState(0x11) & 0x8000) != 0 || (GetAsyncKeyState(0x12) & 0x8000) != 0 || (GetAsyncKeyState(0x5B) & 0x8000) != 0;
                    if (!mods)
                    {
                        if (QuickLook.IsOpenAndFocused()) { BeginInvoke(new Action(() => QuickLook.Toggle(null, light))); return new IntPtr(1); }
                        IntPtr fg = N.GetForegroundWindow();
                        string cls = fg == IntPtr.Zero ? "" : N.ClassOf(fg);
                        if (cls == "CabinetWClass")
                        {
                            uint pid; uint tid = N.GetWindowThreadProcessId(fg, out pid);
                            var gi = new GUITHREADINFO(); gi.cbSize = Marshal.SizeOf(typeof(GUITHREADINFO));
                            if (GetGUIThreadInfo(tid, ref gi) && gi.hwndCaret == IntPtr.Zero)
                            {
                                string fc = gi.hwndFocus == IntPtr.Zero ? "" : N.ClassOf(gi.hwndFocus);
                                if (fc == "DirectUIHWND" || fc == "SysListView32")
                                {
                                    BeginInvoke(new Action(() => QuickLookExplorer(fg)));
                                    return new IntPtr(1);
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            return CallNextHookEx(kbHook, nCode, wParam, lParam);
        }

        void QuickLookExplorer(IntPtr explorerWnd)
        {
            try
            {
                var t = Type.GetTypeFromProgID("Shell.Application");
                dynamic sh = Activator.CreateInstance(t);
                foreach (dynamic w in sh.Windows())
                {
                    try
                    {
                        if (new IntPtr((long)w.HWND) != explorerWnd) continue;
                        dynamic items = w.Document.SelectedItems();
                        if (items.Count > 0) { string p = items.Item(0).Path; QuickLook.Toggle(p, light); }
                        break;
                    }
                    catch { }
                }
                Marshal.ReleaseComObject(sh);
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        // ---- aviso de descargas: rebote + numerito ----
        void WatchDownloads()
        {
            try
            {
                if (downloadsItem == null || !Directory.Exists(downloadsItem.Target)) return;
                dlWatcher = new FileSystemWatcher(downloadsItem.Target) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName };
                FileSystemEventHandler onNew = (o, e) => NewDownload(e.FullPath);
                dlWatcher.Created += onNew;
                dlWatcher.Renamed += (o, e) => NewDownload(e.FullPath);
                dlWatcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        static bool IsTempDownload(string p)
        {
            string n = Path.GetFileName(p).ToLowerInvariant();
            return n.StartsWith("~") || n.StartsWith(".") || n.EndsWith(".crdownload") || n.EndsWith(".part") || n.EndsWith(".tmp") || n.EndsWith(".download") || n.EndsWith(".partial") || n == "desktop.ini";
        }

        void NewDownload(string path)
        {
            if (IsTempDownload(path)) return;
            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (downloadsItem == null) return;
                    downloadsItem.Badge++;
                    downloadsItem.LaunchAt = DateTime.Now;
                    StartAnim(); Render();
                }));
            }
            catch { }
        }

        void DrawBadge(Graphics g, RectangleF icon, int n)
        {
            string t = n > 99 ? "99+" : n.ToString();
            var f = F(UiFont.Semi, Math.Max(9f, icon.Width * 0.2f), FontStyle.Regular);
            var sz = g.MeasureString(t, f);
            float h = sz.Height * 1.05f, w = Math.Max(h, sz.Width + h * 0.5f);
            var r = new RectangleF(icon.Right - w * 0.75f, icon.Top - h * 0.2f, w, h);
            using (var p = Round(r, h / 2)) g.FillPath(B(Color.FromArgb(255, 59, 48)), p);
            g.DrawString(t, f, Brushes.White, new RectangleF(r.X, r.Y + 0.5f, r.Width, r.Height), sfCenter);
        }

        static bool AllApps(string[] files)
        {
            return files.Length > 0 && files.All(f => { string x = Path.GetExtension(f).ToLowerInvariant(); return x == ".lnk" || x == ".exe" || x == ".url" || x == ".appref-ms"; });
        }

        // fija apps en el dock (copia el acceso directo a la carpeta del dock) en la posicion indicada
        void PinApps(string[] files, int at)
        {
            try
            {
                var order = pinned.Select(p => Path.GetFileName(p.Lnk)).ToList();
                foreach (var f in files)
                {
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    string name = Path.GetFileNameWithoutExtension(f);
                    if (ext == ".exe") { try { var vi = FileVersionInfo.GetVersionInfo(f); if (!string.IsNullOrEmpty(vi.FileDescription) && vi.FileDescription.Length < 40) name = vi.FileDescription; } catch { } }
                    name = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
                    string dst = Path.Combine(appsDir, name + (ext == ".exe" ? ".lnk" : ext == ".lnk" ? ".lnk" : ext));
                    if (File.Exists(dst)) { order.Remove(Path.GetFileName(dst)); }
                    else if (ext == ".exe") Shell.CreateLnk(dst, f);
                    else File.Copy(f, dst, true);
                    order.Insert(Math.Min(at, order.Count), Path.GetFileName(dst)); at++;
                }
                File.WriteAllLines(orderFile, order);
                ScheduleReload();
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        // lo que fijes en la barra de tareas de Windows aparece solo en el dock
        FileSystemWatcher pinWatcher;
        void WatchTaskbarPins()
        {
            try
            {
                string tb = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
                if (!Directory.Exists(tb)) return;
                pinWatcher = new FileSystemWatcher(tb, "*.lnk") { IncludeSubdirectories = false };
                FileSystemEventHandler h = (o, e) =>
                {
                    try
                    {
                        BeginInvoke(new Action(() =>
                        {
                            string t, a; Shell.ReadLnk(e.FullPath, out t, out a);
                            bool exists = pinned.Any(p => string.Equals(Path.GetFileName(p.Lnk), Path.GetFileName(e.FullPath), StringComparison.OrdinalIgnoreCase)
                                                       || (t != "" && string.Equals(p.Target, t, StringComparison.OrdinalIgnoreCase) && p.Args == a));
                            if (!exists) PinApps(new[] { e.FullPath }, pinned.Count);
                        }));
                    }
                    catch { }
                };
                pinWatcher.Created += h;
                pinWatcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        // ---- soltar archivos en el dock: Papelera = borrar, app = abrir con ella, carpeta = mover/copiar ----
        DockItem dropItem;

        static string[] DraggedFiles(DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
            return e.Data.GetData(DataFormats.FileDrop) as string[];
        }

        DragDropEffects DropEffectFor(DockItem it, DragEventArgs e, string[] files)
        {
            if (files == null || files.Length == 0) return DragDropEffects.None;
            if (AllApps(files)) return DragDropEffects.Link;   // fijar apps en el dock
            if (it == null) return DragDropEffects.None;
            switch (it.Kind)
            {
                case Kind.Trash: return DragDropEffects.Move;
                case Kind.Downloads:
                case Kind.Stack:
                    if ((e.KeyState & 8) != 0) return DragDropEffects.Copy;
                    return string.Equals(Path.GetPathRoot(files[0]), Path.GetPathRoot(it.Target), StringComparison.OrdinalIgnoreCase) ? DragDropEffects.Move : DragDropEffects.Copy;
                case Kind.App: return (it.IsExplorer || (it.Target != "" && File.Exists(it.Target))) ? DragDropEffects.Link : DragDropEffects.None;
            }
            return DragDropEffects.None;
        }

        protected override void OnDragEnter(DragEventArgs e) { base.OnDragEnter(e); StartAnim(); OnDragOver(e); }

        protected override void OnDragOver(DragEventArgs e)
        {
            base.OnDragOver(e);
            var pt = PointToClient(new Point(e.X, e.Y));
            int i = HitIndex(pt.X);
            dropItem = i >= 0 ? all[i] : null;
            e.Effect = DropEffectFor(dropItem, e, DraggedFiles(e));
            StartAnim();
        }

        protected override void OnDragLeave(EventArgs e) { base.OnDragLeave(e); dropItem = null; }

        protected override void OnDragDrop(DragEventArgs e)
        {
            base.OnDragDrop(e);
            var files = DraggedFiles(e);
            var it = dropItem; dropItem = null;
            if (files != null && AllApps(files))
            {
                var pt0 = PointToClient(new Point(e.X, e.Y));
                int at = pinned.Count;
                for (int i = 0; i < pinned.Count; i++) if (pt0.X < pinned[i].X + pinned[i].W / 2) { at = i; break; }
                BeginInvoke(new Action(() => PinApps(files, at)));
                return;
            }
            if (files == null || it == null) return;
            Keys mods = (e.KeyState & 8) != 0 ? Keys.Control : Keys.None;
            BeginInvoke(new Action(() =>
            {
                try
                {
                    switch (it.Kind)
                    {
                        case Kind.Trash: FileOps.Recycle(Handle, files); trashFull = N.RecycleBinFull(); trashItem.Icon = TrashIcon() ?? trashItem.Icon; Render(); break;
                        case Kind.Downloads:
                        case Kind.Stack: FileOps.Drop(Handle, files, it.Target, mods); break;
                        case Kind.App:
                            if (it.IsExplorer) { Finder.OpenOrFocus(light, s, Directory.Exists(files[0]) ? files[0] : Path.GetDirectoryName(files[0])); break; }
                            string args = string.Join(" ", files.Select(f => "\"" + f + "\"").ToArray());
                            if (!string.IsNullOrEmpty(it.Args)) args = it.Args + " " + args;
                            using (var p = Process.Start(new ProcessStartInfo(it.Target, args) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(it.Target) })) { }
                            it.LaunchAt = DateTime.Now; StartAnim();
                            break;
                    }
                }
                catch (Exception ex) { Program.Log(ex); }
            }));
        }

        // Algunas apps (navegadores basados en Chromium) se maximizan a su manera y quedan debajo de la barra superior.
        // Si la ventana activa ocupa todo el ancho y empieza encima del area de trabajo, se ajusta al espacio correcto.
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out N.RECT r, int size);
        HashSet<string> fixedOnce = new HashSet<string>();
        void FixUnderMenuBar()
        {
            try
            {
                if (bar == null || fullApp) return;
                IntPtr h = N.GetForegroundWindow();
                if (h == IntPtr.Zero || N.IsIconic(h)) return;
                uint pid; N.GetWindowThreadProcessId(h, out pid); if (pid == myPid) return;
                string cls = N.ClassOf(h);
                if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Windows.UI.Core.CoreWindow" || cls == "XamlExplorerHostIslandWindow") return;
                int style = N.GetWindowLong(h, -16);
                if ((style & 0x00040000) == 0 && (style & 0x00C00000) == 0) return;   // sin borde redimensionable ni titulo: probablemente pantalla completa o popup
                N.RECT r; if (DwmGetWindowAttribute(h, 9, out r, 16) != 0) N.GetWindowRect(h, out r);   // borde visible real
                var scr = Screen.FromHandle(h); var wa = scr.WorkingArea; var b = scr.Bounds;
                if (N.IsZoomed(h)) return;   // las ventanas maximizadas por Windows ya respetan la barra
                if (r.top >= wa.Top - 2 || r.left > b.Left + 2 || r.right < b.Right - 2) return;
                string key = h + "|" + r.top + "|" + r.bottom; if (fixedOnce.Contains(key)) return; fixedOnce.Add(key);
                N.SetWindowPos(h, IntPtr.Zero, wa.Left, wa.Top, wa.Width, wa.Height, 0x0004 | 0x0010);
            }
            catch { }
        }

        // Docker / WSL: su maquina virtual sigue gastando RAM y bateria aunque cierres la ventana de Docker.
        static readonly string[] DockerProcs = { "vmmem", "vmmemWSL", "Docker Desktop", "com.docker.backend", "wslservice", "wsl" };
        static bool DockerRunning()
        {
            foreach (var n in DockerProcs)
            {
                Process[] ps = null;
                try
                {
                    ps = Process.GetProcessesByName(n);
                    if (ps.Length > 0) return true;
                }
                finally { if (ps != null) foreach (var p in ps) p.Dispose(); }
            }
            return false;
        }

        void StopDocker()
        {
            if (MessageBox.Show("Se cerrara Docker Desktop y se apagara WSL (sus contenedores se detienen).\n\nGuarda lo que tengas corriendo ahi. Continuar?",
                "MacDock", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            var th = new Thread(() =>
            {
                try
                {
                    string docker = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Docker\Docker\resources\bin\docker.exe");
                    if (File.Exists(docker)) RunHidden(docker, "desktop stop", 40000);
                    foreach (var n in new[] { "Docker Desktop", "com.docker.backend", "com.docker.build" })
                    {
                        Process[] ps = null;
                        try
                        {
                            ps = Process.GetProcessesByName(n);
                            foreach (var p in ps) { try { p.Kill(); p.Dispose(); } catch { } }
                        }
                        finally { if (ps != null) foreach (var p in ps) { try { p.Dispose(); } catch { } } }
                    }
                    RunHidden(Path.Combine(Environment.SystemDirectory, "wsl.exe"), "--shutdown", 30000);
                    BeginInvoke(new Action(() => MessageBox.Show("Listo: Docker y WSL estan apagados. La RAM y la bateria que usaban ya quedaron libres.", "MacDock", MessageBoxButtons.OK, MessageBoxIcon.Information)));
                }
                catch (Exception ex) { Program.Log(ex); }
            });
            th.IsBackground = true; th.Start();
        }

        static void RunHidden(string exe, string args, int waitMs)
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
                if (p == null) return;
                try { p.WaitForExit(waitMs); } finally { p.Dispose(); }
            }
            catch { }
        }

        // Bandeja del sistema: abre el cuadro nativo de iconos ocultos junto al ^ de MacDock.
        // Sin temporizador de 350 ms: se espera al EVENT_OBJECT_SHOW del cuadro (maximo 1,5 s).
        N.WinEventProc trayShowProc, trayHideProc;
        IntPtr trayShowHook = IntPtr.Zero, trayHideHook = IntPtr.Zero, trayWnd = IntPtr.Zero;
        System.Windows.Forms.Timer trayWait, trayPos;
        Point trayAnchor;
        bool trayEnterSent;

        static bool IsOverflowClass(string c)
        {
            return c == "NotifyIconOverflowWindow" || c == "TopLevelWindowForOverflowXamlIsland";
        }

        static bool Cloaked(IntPtr h)
        {
            int c;
            return N.DwmGetWindowAttribute(h, 14, out c, 4) == 0 && c != 0;
        }

        static IntPtr FindOverflow(bool visibleOnly)
        {
            string[] classes = { "TopLevelWindowForOverflowXamlIsland", "NotifyIconOverflowWindow" };
            foreach (var c in classes)
            {
                IntPtr h = N.FindWindow(c, null);
                if (h == IntPtr.Zero || Cloaked(h)) continue;
                if (visibleOnly && !N.IsWindowVisible(h)) continue;
                return h;
            }
            return IntPtr.Zero;
        }

        void ShowTray(Point anchor)
        {
            ClosePreview();
            try
            {
                if (fullApp) return;
                if (cfg.Diagnostico) Program.Log("Tray: solicitud anchor=" + anchor);
                IntPtr open = FindOverflow(true);
                if (open != IntPtr.Zero)
                {
                    N.SetForegroundWindow(open);
                    N.PressKeys(0x1B);   // Esc cierra el cuadro
                    TrayClosed();
                    return;
                }
                IntPtr tray = N.FindWindow("Shell_TrayWnd", null);
                if (tray == IntPtr.Zero) return;
                trayAnchor = anchor;
                StopTrayWatch();
                StopTrayEnd();
                trayWnd = IntPtr.Zero;
                trayEnterSent = false;
                SaveTrayForeground();       // paso 0: recordar la ventana activa antes de Win+B
                RestoreTrayHiding();        // por si quedara algo de un cuadro anterior
                BeginTrayInvisible();       // paso 2: barra invisible mientras el cuadro esta abierto
                trayShowProc = OnTrayShow;
                trayShowHook = N.SetWinEventHook(0x8002, 0x8002, IntPtr.Zero, trayShowProc, 0, 0, 0);
                if (trayWait == null)
                {
                    trayWait = new System.Windows.Forms.Timer { Interval = 170 };
                    trayWait.Tick += OnTrayWait;
                }
                trayWait.Interval = 170;
                trayWait.Stop(); trayWait.Start();
                N.ShowWindow(tray, 5);
                N.SetForegroundWindow(tray);
                N.PressKeys(0x5B, 0x42);   // Win + B (Enter se envia 170 ms despues, en OnTrayWait)
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        void OnTrayShow(IntPtr hk, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time)
        {
            if (obj != 0 || child != 0 || hwnd == IntPtr.Zero || !IsOverflowClass(N.ClassOf(hwnd))) return;
            try { BeginInvoke(new Action<IntPtr>(TrayShown), hwnd); } catch { }
        }

        void OnTrayHide(IntPtr hk, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time)
        {
            if (hwnd != trayWnd) return;
            try { BeginInvoke(new Action(TrayClosed)); } catch { }
        }

        void OnTrayWait(object sender, EventArgs e)
        {
            if (trayWnd != IntPtr.Zero) return;
            if (!trayEnterSent)
            {
                trayEnterSent = true;
                trayWait.Interval = 1500;
                N.PressKeys(0x0D);   // Enter: abre el cuadro despues de que Windows procese Win+B
                return;
            }
            IntPtr h = FindOverflow(true);
            if (h != IntPtr.Zero) { TrayShown(h); return; }
            if (cfg.Diagnostico) Program.Log("Tray: timeout sin flyout, restaurando barra");
            StopTrayWatch();
            EndTray(true);
        }

        void TrayShown(IntPtr h)
        {
            if (trayWnd != IntPtr.Zero || h == IntPtr.Zero || !N.IsWindowVisible(h)) return;
            trayWnd = h;
            if (cfg.Diagnostico) Program.Log("Tray: flyout detectado hwnd=" + h);
            StopTrayWatch();
            PositionTray(h);
            RepeatTrayPos();
            trayHideProc = OnTrayHide;
            trayHideHook = N.SetWinEventHook(0x8003, 0x8003, IntPtr.Zero, trayHideProc, 0, 0, 0);
            // La barra ya quedo invisible en BeginTrayInvisible, antes de Win+B.
        }

        // Windows termina de medir el cuadro unos milisegundos despues de mostrarlo: se reajusta una vez.
        void RepeatTrayPos()
        {
            if (trayPos == null)
            {
                trayPos = new System.Windows.Forms.Timer { Interval = 80 };
                trayPos.Tick += (o, e) =>
                {
                    trayPos.Stop();
                    if (trayWnd != IntPtr.Zero) PositionTray(trayWnd);
                };
            }
            trayPos.Stop(); trayPos.Start();
        }

        void PositionTray(IntPtr h)
        {
            N.RECT r;
            if (!N.GetWindowRect(h, out r)) return;
            int w = r.right - r.left, hh = r.bottom - r.top;
            if (w <= 0 || hh <= 0) return;
            float dpi = 1f;
            try { uint d = N.GetDpiForWindow(h); if (d > 0) dpi = d / 96f; } catch { }
            var wa = Screen.PrimaryScreen.WorkingArea;
            int gap = (int)(4 * dpi);
            int x = trayAnchor.X - w / 2;
            int y = trayAnchor.Y + gap;
            x = Math.Max(wa.Left + 4, Math.Min(wa.Right - w - 4, x));
            if (y + hh > wa.Bottom) y = Math.Max(wa.Top, trayAnchor.Y - hh - gap);
            N.SetWindowPos(h, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010);   // sin tamano, z-order ni activacion
        }

        // Cierre del cuadro por cualquier via (Esc, clic fuera, otro clic en el ^, fallo al abrir).
        void TrayClosed() { EndTray(true); }

        void EndTray(bool refocus)
        {
            StopTrayEnd();
            StopTrayWatch();
            if (trayPos != null) trayPos.Stop();
            trayWnd = IntPtr.Zero; trayShowHook = IntPtr.Zero;
            // Orden para que la barra de Windows no se vea ni un fotograma al cerrar: primero el foco,
            // luego SW_HIDE mientras la barra sigue con alfa 0, y solo al final se restaura el estilo.
            if (refocus) RestoreTrayFocus();
            HideTaskbarNow();
            RestoreTrayHiding();
            if (cfg.Diagnostico) Program.Log("Tray: barra restaurada/ocultada");
            trayPrevFg = IntPtr.Zero;
        }

        // ---- Paso 0: foco ----
        IntPtr trayPrevFg = IntPtr.Zero;
        bool trayWasVisible;

        static bool IsTrayFocusClass(string c)
        {
            return c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd" || c == "XamlExplorerHostIslandWindow" || IsOverflowClass(c);
        }

        void SaveTrayForeground()
        {
            trayPrevFg = IntPtr.Zero;
            try
            {
                IntPtr fg = N.GetForegroundWindow();
                if (fg == IntPtr.Zero) return;
                uint pid; N.GetWindowThreadProcessId(fg, out pid);
                using (var me = Process.GetCurrentProcess()) if (pid == (uint)me.Id) return;
                if (ShellWindow(N.ClassOf(fg))) return;
                trayPrevFg = fg;
            }
            catch { }
        }

        // Si el foco sigue en la bandeja (Win+B), una barra en auto-ocultar se mantiene revelada: se devuelve a la ventana anterior.
        // Si el usuario ya paso a otra ventana (clic fuera), no se le quita.
        void RestoreTrayFocus()
        {
            try
            {
                IntPtr fg = N.GetForegroundWindow();
                if (fg != IntPtr.Zero && !IsTrayFocusClass(N.ClassOf(fg))) return;
                if (trayPrevFg != IntPtr.Zero && N.IsWindow(trayPrevFg) && N.IsWindowVisible(trayPrevFg)) { N.SetForegroundWindow(trayPrevFg); return; }
                IntPtr desk = N.FindWindow("Progman", null);
                if (desk != IntPtr.Zero) N.SetForegroundWindow(desk);
            }
            catch { }
        }

        void LogTrayVisible(string where)
        {
            if (!cfg.Diagnostico) return;
            try
            {
                IntPtr fg = N.GetForegroundWindow();
                Program.Log("Shell_TrayWnd visible (" + where + "): primer plano=" + (fg == IntPtr.Zero ? "-" : N.ClassOf(fg)) +
                            " trayWnd=0x" + trayWnd.ToInt64().ToString("X") + " trayShowHook=0x" + trayShowHook.ToInt64().ToString("X"));
            }
            catch { }
        }

        // ---- Paso 2: ocultar la barra sin SW_HIDE mientras el cuadro esta abierto ----
        class TrayState
        {
            public int Idx; public IntPtr H;
            public int Ex; public bool AddedLayered; public uint CrKey, Flags; public byte Alpha;
            public bool Styled;
            public string Device;   // monitor de la barra (\\.\DISPLAYn): identificador estable entre reinicios de Explorer
        }
        readonly List<TrayState> trayStates = new List<TrayState>();
        readonly object trayStateLock = new object();
        bool markerAutoHide;
        const int GWL_EXSTYLE = -20, WS_EX_LAYERED = 0x80000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct MONITORINFOEXW { public int cbSize; public N.RECT rcMonitor, rcWork; public uint dwFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfoW(IntPtr hmon, ref MONITORINFOEXW info);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);

        // Identificador estable del monitor de una barra (no cambia al reiniciar Explorer).
        string MonitorDeviceOf(IntPtr hwnd)
        {
            try
            {
                IntPtr m = MonitorFromWindow(hwnd, 2);
                if (m == IntPtr.Zero) return null;
                var mi = new MONITORINFOEXW(); mi.cbSize = Marshal.SizeOf(typeof(MONITORINFOEXW));
                if (!GetMonitorInfoW(m, ref mi) || string.IsNullOrEmpty(mi.szDevice)) return null;
                return mi.szDevice;
            }
            catch { return null; }
        }

        // Paso 2: antes de Win+B, la barra pasa a layered con alfa 0 (sigue "visible" para Windows).
        void BeginTrayInvisible()
        {
            if (!cfg.HideTaskbar) return;
            try
            {
                int i = 0;
                foreach (var t in Trays())
                {
                    var st = new TrayState { Idx = i++, H = t, Device = MonitorDeviceOf(t) };
                    st.Ex = N.GetWindowLong(t, GWL_EXSTYLE);
                    st.AddedLayered = (st.Ex & WS_EX_LAYERED) == 0;
                    if (!st.AddedLayered) N.GetLayeredWindowAttributes(t, out st.CrKey, out st.Alpha, out st.Flags);
                    st.Styled = true;
                    lock (trayStateLock) trayStates.Add(st);
                    WriteMarker();   // antes de tocar nada: si el proceso muere, RestoreStaleTaskbar lo deshace
                    try
                    {
                        N.SetWindowLong(t, GWL_EXSTYLE, st.Ex | WS_EX_LAYERED);
                        N.SetLayeredWindowAttributes(t, 0, 0, 2);
                    }
                    catch (Exception ex) { Program.Log(ex); }
                }
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        // Restaura exactamente lo guardado (quita WS_EX_LAYERED solo si lo puso MacDock). Idempotente.
        public void RestoreTrayHiding()
        {
            lock (trayStateLock)
            {
                foreach (var t in trayStates)
                {
                    if (!t.Styled) continue;
                    try
                    {
                        if (!N.IsWindow(t.H)) continue;
                        N.SetWindowLong(t.H, GWL_EXSTYLE, t.Ex);
                        if (!t.AddedLayered) N.SetLayeredWindowAttributes(t.H, t.CrKey, t.Alpha, t.Flags);
                    }
                    catch { }
                }
                trayStates.Clear();
            }
            WriteMarker();
        }

        // Explorer recreo la barra (TaskbarCreated): los HWND guardados ya no valen y la barra nueva trae su estilo original.
        void DiscardTrayState()
        {
            StopTrayEnd(); StopTrayWatch();
            trayWnd = IntPtr.Zero; trayShowHook = IntPtr.Zero; trayPrevFg = IntPtr.Zero;
            lock (trayStateLock) trayStates.Clear();
            trayCacheReady = false; trayMain = IntPtr.Zero; trayAll.Clear();
            WriteMarker();
        }

        // taskbar.marker v3: "A|1" = MacDock activo el auto-ocultar (el original era off);
        // "B|<monitor>|exOriginal|agregoLayered|crKey|alfa|flags" = estilo guardado de la barra de
        // ese monitor. Los formatos anteriores ("1", "R|idx|l|t|r|b", "S|idx|...") los sigue
        // entendiendo RestoreStaleTaskbar. Escritura atomica con flush a disco: nunca queda un
        // marker parcial si el proceso muere.
        void WriteMarker()
        {
            try
            {
                var txt = new System.Text.StringBuilder();
                if (markerAutoHide) txt.Append("A|1\r\n");
                lock (trayStateLock)
                {
                    foreach (var t in trayStates)
                    {
                        if (!t.Styled) continue;
                        txt.Append("B|").Append(t.Device ?? ("#" + t.Idx)).Append('|').Append(t.Ex).Append('|')
                           .Append(t.AddedLayered ? 1 : 0).Append('|').Append(t.CrKey).Append('|')
                           .Append(t.Alpha).Append('|').Append(t.Flags).Append("\r\n");
                    }
                }
                N.WriteMarkerFile(TaskbarMarker, txt.ToString());
            }
            catch { }
        }

        void StopTrayWatch()
        {
            if (trayWait != null) trayWait.Stop();
            if (trayShowHook != IntPtr.Zero) { try { N.UnhookWinEvent(trayShowHook); } catch { } trayShowHook = IntPtr.Zero; }
        }

        void StopTrayEnd()
        {
            if (trayHideHook != IntPtr.Zero) { try { N.UnhookWinEvent(trayHideHook); } catch { } trayHideHook = IntPtr.Zero; }
        }

        void HideTaskbarNow()
        {
            if (!cfg.HideTaskbar) return;
            try { foreach (var t in Trays()) if (N.IsWindowVisible(t)) N.ShowWindow(t, 0); } catch { }
        }

        // Boton sol/luna: cambia claro <-> oscuro al instante. Si estaba el modo automatico, se apaga (manda tu eleccion).
        void ToggleTheme()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", true))
                {
                    if (k == null) return;
                    int next = Convert.ToInt32(k.GetValue("SystemUsesLightTheme", 0)) == 1 ? 0 : 1;
                    k.SetValue("SystemUsesLightTheme", next, RegistryValueKind.DWord);
                    k.SetValue("AppsUseLightTheme", next, RegistryValueKind.DWord);
                }
                if (cfg.AutoTheme) { cfg.AutoTheme = false; cfg.Save(); }
                N.BroadcastSetting("ImmersiveColorSet");
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        MenuBar bar;
        void SetMenuBar(bool on)
        {
            if (on && bar == null)
            {
                bar = new MenuBar(s, light);
                bar.ToggleTheme = ToggleTheme;
                bar.ShowTray = ShowTray;
                bar.AddDockOptions = m =>
                {
                    m.Items.Add(MenuBarAutoHideItem());
                    m.Items.Add(IconSizeMenu());
                    m.Items.Add(new ToolStripSeparator());
                    m.Items.Add(BuildAnimMenu());
                };
                bar.Show();
                bar.SetAutoHide(cfg.MenuBarAutoHide);
                if (fullApp) bar.SetFull(true);
                UpdateEdgeTimer();
            }
            else if (!on && bar != null)
            {
                bar.Close(); bar.Dispose(); bar = null;
                UpdateEdgeTimer();
            }
        }
        AppNap nap = new AppNap();
        HotCorners corners = new HotCorners();
        HashSet<string> windowExes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        DateTime napAt = DateTime.MinValue;

        void UpdateNap()
        {
            try
            {
                bool battery = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline;
                if (!cfg.AppNap || !battery) { if (nap.Count > 0) nap.WakeAll(); return; }
                string fgExe = "";
                IntPtr fg = N.GetForegroundWindow();
                if (fg != IntPtr.Zero) { uint pid; N.GetWindowThreadProcessId(fg, out pid); fgExe = N.ProcessPath(pid); }
                nap.Update(windowExes, fgExe);
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        // Con bateria: pantalla a 60 Hz (ahorra mucha energia). Enchufado: la maxima (ej. 144 Hz).
        void ApplyRefresh()
        {
            try
            {
                var pw = SystemInformation.PowerStatus.PowerLineStatus;
                if (pw == lastPower && Display.Current(Screen.PrimaryScreen.DeviceName) != 0) return;
                lastPower = pw;
                if (!cfg.AutoRefresh) { if (loweredRefresh) RestoreRefresh(); return; }
                string dev = Screen.PrimaryScreen.DeviceName;
                var rates = Display.Rates(dev);
                if (rates.Count < 2) return;
                int cur = Display.Current(dev);
                int target;
                if (pw == PowerLineStatus.Offline)
                {
                    target = rates.Contains(60) ? 60 : rates.Where(r => r >= 48).DefaultIfEmpty(rates[0]).Min();
                    if (cur != target && Display.Set(dev, target)) loweredRefresh = true;
                }
                else
                {
                    target = rates.Max();
                    if (cur != target) Display.Set(dev, target);
                    loweredRefresh = false;
                }
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        void RestoreRefresh()
        {
            try
            {
                string dev = Screen.PrimaryScreen.DeviceName;
                var rates = Display.Rates(dev);
                if (rates.Count > 0) Display.Set(dev, rates.Max());
                loweredRefresh = false;
            }
            catch { }
        }

        public DockForm()
        {
            Text = "MacDockWindow";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(0, 0, 1, 1);
            Directory.CreateDirectory(Program.DataDir);
            DisplayBrightness.Init();
            ControlCenter.FullscreenBlock = () => fullApp;
            appsDir = Path.Combine(Program.DataDir, "Apps");
            orderFile = Path.Combine(Program.DataDir, "order.txt");
            cfg = new Config(Path.Combine(Program.DataDir, "config.ini"));
            ControlCenter.Diag = () => cfg.Diagnostico;
            PowerActions.Diag = () => cfg.Diagnostico;
            appBarMsg = N.RegisterWindowMessage("MacDockAppBarMsg");
            taskbarCreatedMsg = N.RegisterWindowMessage("TaskbarCreated");
            sfCenter = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            fast.Interval = 16; fast.Tick += FastTick;   // ~60 fps; solo mientras hay cambios visuales
            slow.Interval = 1000; slow.Tick += SlowTick;
            edge.Interval = 120; edge.Tick += EdgeTick;
        }

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x80000 | 0x80 | 0x8000000 | 0x8; return cp; }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        // ---------- metrics ----------
        static readonly int[] DockSizes = { 40, 48, 60 };
        float Ic { get { return cfg.IconSize * s; } }
        float IconUnit { get { return cfg.IconSize / 48f; } }
        float Gap { get { return 7 * s * IconUnit; } }
        float SepW { get { return 17 * s * IconUnit; } }
        float PadX { get { return 5 * s * IconUnit; } }
        float PadY { get { return 5 * s * IconUnit; } }
        float BarH { get { return Ic + PadY * 2 + 3 * s * IconUnit; } }
        float BottomM { get { return 5 * s; } }
        float MaxMag { get { return cfg.Magnify ? cfg.Magnification : 1f; } }
        float BarTop { get { return H - BottomM - BarH; } }
        float Slot(DockItem it) { return it.Kind == Kind.Separator ? SepW : Ic + Gap; }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try { s = N.GetDpiForWindow(Handle) / 96f; } catch { s = DeviceDpi / 96f; }
            if (s <= 0) s = 1f;
            ReadTheme();
            prefHandler = (o, a) => { ReadTheme(); Render(); if (bar != null) bar.SetTheme(light); };
            dispHandler = (o, a) => BeginInvoke(new Action(() => { ClosePreview(); UpdateDockScale(); Relayout(); if (appBarOn) SetAppBarPos(); if (bar != null) bar.OnDisplayChanged(); CheckFullscreen(); }));
            powerHandler = (o, a) => BeginInvoke(new Action(() => { lastPower = (PowerLineStatus)(-1); refreshCheckAt = DateTime.Now.AddSeconds(2); }));
            SystemEvents.UserPreferenceChanged += prefHandler;
            SystemEvents.DisplaySettingsChanged += dispHandler;
            BuildItems();
            SetupTaskbar();
            if (cfg.ReserveSpace) RegisterAppBar();
            RefreshRunning();
            Relayout();
            SetupWatcher();
            SystemEvents.PowerModeChanged += powerHandler;
            ApplyRefresh();
            if (cfg.MenuBar) SetMenuBar(true);
            InitMacFeatures();
            WindowAnimationManager.Init(this, GetDockIconCenter);
            SyncAnimConfig();
            winWatchHandler = OnWinWatch;
            WinWatch.Add(winWatchHandler);
            InstallLocHook();
            CheckFullscreen();
            slow.Start();
            UpdateEdgeTimer();
            idleSince = DateTime.Now;
        }

        void ReadTheme()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    light = k != null && Convert.ToInt32(k.GetValue("SystemUsesLightTheme", 0)) == 1;
            }
            catch { light = false; }
        }

        // ---------- items ----------
        int IconPx { get { int need = (int)Math.Ceiling(Ic * Math.Max(1f, MaxMag)); return need <= 128 ? 128 : 256; } }

        Bitmap IconFor(string parsingName)
        {
            Bitmap b;
            if (iconCache.TryGetValue(parsingName, out b)) return b;
            b = Shell.GetIcon(parsingName, IconPx);
            if (b != null) iconCache[parsingName] = b;
            return b;
        }

        void DropCached(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            Bitmap b;
            if (iconCache.TryGetValue(key, out b)) { iconCache.Remove(key); if (b != null) b.Dispose(); }
        }

        // Los iconos de apps cerradas y no fijadas no deben quedarse en memoria.
        void PruneIconCache()
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in pinned) if (!string.IsNullOrEmpty(p.Lnk)) used.Add(p.Lnk);
            foreach (var t in transient) if (!string.IsNullOrEmpty(t.Target)) used.Add(t.Target);
            var dead = new List<string>();
            foreach (var k in iconCache.Keys) if (!used.Contains(k)) dead.Add(k);
            foreach (var k in dead) DropCached(k);
        }

        void DisposeCanvas()
        {
            if (canvasG != null) { try { canvasG.Dispose(); } catch { } canvasG = null; }
            if (canvas != null) { try { canvas.Dispose(); } catch { } canvas = null; }
        }

        void TrimMemory()
        {
            try
            {
                // El canvas se conserva mientras el dock este visible: recrearlo cuesta GDI.
                if (!Visible || hiddenFull) DisposeCanvas();
            }
            catch { }
            Program.TrimMemory();   // politica unica: cooldown y trabajo fuera de la interfaz
        }

        int DockSizeIndex()
        {
            int best = 1, diff = int.MaxValue;
            for (int i = 0; i < DockSizes.Length; i++)
            {
                int d = Math.Abs(cfg.IconSize - DockSizes[i]);
                if (d < diff) { diff = d; best = i; }
            }
            return best;
        }

        void RefreshIcons()
        {
            var old = new List<Bitmap>(iconCache.Values);
            iconCache.Clear();
            foreach (var it in all)
            {
                if (it.Kind != Kind.App) continue;
                string key = it.Lnk ?? it.Target;
                it.Icon = string.IsNullOrEmpty(key) ? null : IconFor(key);
            }
            if (downloadsItem != null)
            {
                var b = Shell.GetIcon(downloadsItem.Target, IconPx);
                if (b != null) { if (downloadsItem.Icon != null) old.Add(downloadsItem.Icon); downloadsItem.Icon = b; }
            }
            if (stackItem != null)
            {
                var b = Shell.GetIcon(stackItem.Target, IconPx);
                if (b != null) { if (stackItem.Icon != null) old.Add(stackItem.Icon); stackItem.Icon = b; }
            }
            if (trashItem != null)
            {
                var b = TrashIcon();
                if (b != null) { if (trashItem.Icon != null) old.Add(trashItem.Icon); trashItem.Icon = b; }
            }
            foreach (var b in old) { try { b.Dispose(); } catch { } }
            idleSince = DateTime.Now;
            TrimMemory();
        }

        void SetDockSize(int px)
        {
            if (cfg.IconSize == px) return;
            cfg.IconSize = px; cfg.Save();
            RefreshIcons();
            Relayout();
            if (appBarOn) SetAppBarPos();
        }

        ToolStripMenuItem MenuBarAutoHideItem()
        {
            var mi = new ToolStripMenuItem("Ocultar barra superior automáticamente") { Checked = cfg.MenuBarAutoHide };
            mi.Click += (o, e) =>
            {
                try
                {
                    cfg.MenuBarAutoHide = !cfg.MenuBarAutoHide; cfg.Save();
                    if (bar != null) bar.SetAutoHide(cfg.MenuBarAutoHide);
                    UpdateEdgeTimer();
                }
                catch (Exception ex) { Program.Log(ex); }
            };
            return mi;
        }

        ToolStripMenuItem IconSizeMenu()
        {
            var sizes = new ToolStripMenuItem("Tamaño de los iconos");
            string[] sizeNames = { "Pequeño", "Mediano", "Grande" };
            int curSize = DockSizeIndex();
            for (int i = 0; i < DockSizes.Length; i++)
            {
                int px = DockSizes[i];
                var mi = new ToolStripMenuItem(sizeNames[i]) { Checked = i == curSize };
                mi.Click += (o, e) => { try { SetDockSize(px); } catch (Exception ex) { Program.Log(ex); } };
                sizes.DropDownItems.Add(mi);
            }
            return sizes;
        }

        void BuildItems()
        {
            Directory.CreateDirectory(appsDir);
            bool first = !File.Exists(orderFile);
            if (first) ImportTaskbarPins();

            var order = File.Exists(orderFile) ? File.ReadAllLines(orderFile).ToList() : new List<string>();
            var files = Directory.GetFiles(appsDir, "*.lnk").ToList();
            files = files.OrderBy(f => { int i = order.IndexOf(Path.GetFileName(f)); return i < 0 ? 10000 : i; })
                         .ThenBy(f => Path.GetFileName(f)).ToList();

            foreach (var p in pinned) if (p.Lnk != null) DropCached(p.Lnk);
            pinned.Clear();
            foreach (var f in files)
            {
                var it = new DockItem { Kind = Kind.App, Lnk = f, Pinned = true, Name = Path.GetFileNameWithoutExtension(f) };
                Shell.ReadLnk(f, out it.Target, out it.Args);
                string n = it.Name.ToLowerInvariant();
                string argsLower = (it.Args ?? "").ToLowerInvariant();
                it.Aumid = N.PathAumid(f);
                int af = argsLower.IndexOf("shell:appsfolder\\");
                if (string.IsNullOrEmpty(it.Aumid) && af >= 0)
                    it.Aumid = it.Args.Substring(af + "shell:appsfolder\\".Length).Trim();
                bool bundled = af >= 0;   // .lnk de app de la Store: explorer.exe shell:AppsFolder\<AUMID>
                it.IsExplorer = !bundled && (it.Target.ToLowerInvariant().EndsWith("\\explorer.exe") ||
                                (it.Target == "" && (n.Contains("explorer") || n.Contains("explorador"))) ||
                                string.Equals(it.Aumid, "Microsoft.Windows.Explorer", StringComparison.OrdinalIgnoreCase));
                // Squirrel (Discord, Slack...): el .lnk apunta a Update.exe --processStart App.exe
                // Tambien existe --processStartAndWait; el valor puede ir con espacio o "=" y con comillas.
                var launch = Regex.Match(it.Args ?? "", "--process(?:startandwait|start)(?:\\s*=\\s*|\\s+)(?:\"([^\"]+)\"|(\\S+))",
                                         RegexOptions.IgnoreCase);
                if (launch.Success)
                    it.LauncherExe = Path.GetFileName(launch.Groups[1].Success ? launch.Groups[1].Value : launch.Groups[2].Value);
                if (it.IsExplorer) it.Name = "Explorador de archivos";
                it.Icon = IconFor(f);
                pinned.Add(it);
            }
            if (first) SaveOrder();

            if (startItem == null)
            {
                startItem = new DockItem { Kind = Kind.Start, Name = "Inicio" };
                clockItem = new DockItem { Kind = Kind.Clock, Name = "Centro de control" };
                string dl = N.Downloads();
                downloadsItem = new DockItem { Kind = Kind.Downloads, Name = "Descargas", Target = dl, Icon = Shell.GetIcon(dl, IconPx) };
                trashFull = N.RecycleBinFull();
                trashItem = new DockItem { Kind = Kind.Trash, Name = "Papelera", Icon = TrashIcon() };
                sepItem = new DockItem { Kind = Kind.Separator };
                string desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                stackItem = new DockItem { Kind = Kind.Stack, Name = "Escritorio", Target = desk, Icon = Shell.GetIcon(desk, IconPx) };
            }
            Compose();
            TrimMemory();
        }

        Bitmap TrashIcon() { return Shell.GetIcon("::{645FF040-5081-101B-9F08-00AA002F954E}", IconPx); }

        void ImportTaskbarPins()
        {
            string src = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var names = new List<string>();
            if (Directory.Exists(src))
            {
                var lnks = Directory.GetFiles(src, "*.lnk")
                    .OrderBy(f => { var n = Path.GetFileName(f).ToLowerInvariant(); return (n.Contains("explorer") || n.Contains("explorador")) ? 0 : 1; })
                    .ThenBy(f => f).ToList();
                foreach (var f in lnks)
                {
                    string t, a; Shell.ReadLnk(f, out t, out a);
                    string key = (t + "|" + a).ToLowerInvariant();
                    if (t != "" && !seen.Add(key)) continue;
                    try { File.Copy(f, Path.Combine(appsDir, Path.GetFileName(f)), true); names.Add(Path.GetFileName(f)); } catch { }
                }
            }
            try { File.WriteAllLines(orderFile, names); } catch { }
        }

        void SaveOrder()
        {
            try { File.WriteAllLines(orderFile, pinned.Select(p => Path.GetFileName(p.Lnk)).ToArray()); } catch { }
        }

        // Diagnostico de elementos repetidos en el dock (iconos pequenos sobre el reloj). Se activa con Diagnostico=1.
        string lastDupSig = "";
        string lastDupRaw = "";

        static bool SameApp(DockItem a, DockItem b)
        {
            if (a == b || a.Kind != Kind.App || b.Kind != Kind.App) return false;
            if (!string.IsNullOrEmpty(a.Aumid) && string.Equals(a.Aumid, b.Aumid, StringComparison.OrdinalIgnoreCase)) return true;
            if (a.Target != "" && b.Target != "" && !a.IsExplorer && !b.IsExplorer && NormPath(a.Target) == NormPath(b.Target)) return true;
            // El nombre solo cuenta si ninguna tiene AUMID y ademas coincide el .exe: dos apps distintas
            // (Java, Launcher, Setup...) pueden compartir descripcion y no deben fusionarse.
            if (!string.IsNullOrEmpty(a.Aumid) || !string.IsNullOrEmpty(b.Aumid)) return false;
            string ea = ExeNameOf(a), eb = ExeNameOf(b);
            if (ea == "" || eb == "") return false;
            return string.Equals(ea, eb, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        }

        static string ExeNameOf(DockItem d)
        {
            if (!string.IsNullOrEmpty(d.LauncherExe)) return d.LauncherExe;
            return string.IsNullOrEmpty(d.Target) ? "" : Path.GetFileName(d.Target);
        }

        // Una sola linea por cambio (no por tick): nombre, ruta, AUMID, X y ancho de cada elemento repetido.
        void LogDuplicates(string where)
        {
            if (!cfg.Diagnostico) return;
            try
            {
                // Firma barata (cantidad + nombres) antes del escaneo O(n^2): en reposo no cambia nada
                // y asi no se recorre la lista cada refresco.
                var raw = new StringBuilder();
                raw.Append(all.Count);
                for (int i = 0; i < all.Count; i++) raw.Append('|').Append(all[i].Name);
                string rawSig = raw.ToString();
                if (rawSig == lastDupRaw) return;
                lastDupRaw = rawSig;

                var dups = new List<DockItem>();
                for (int i = 0; i < all.Count; i++)
                    for (int j = i + 1; j < all.Count; j++)
                        if (SameApp(all[i], all[j])) { if (!dups.Contains(all[i])) dups.Add(all[i]); if (!dups.Contains(all[j])) dups.Add(all[j]); }
                string line = "";
                foreach (var d in dups)
                    line += (line == "" ? "" : " | ") + (d.Pinned ? "fijo" : "transitorio") + " nombre=" + d.Name + " ruta=" + (d.Lnk ?? d.Target) +
                            " aumid=" + d.Aumid + " X=" + (int)d.X + " W=" + (int)d.W + " ventanas=" + d.Windows.Count;
                if (line == lastDupSig) return;
                lastDupSig = line;
                if (line != "") Program.Log("Dock: elementos repetidos (" + where + "): " + line);
            }
            catch { }
        }

        // Un elemento transitorio que coincide con uno fijo (p. ej. se fijo una app que ya estaba abierta) no debe
        // dibujarse aparte: sus ventanas pasan al fijo.
        void MergeTransientIntoPinned()
        {
            for (int ti = transient.Count - 1; ti >= 0; ti--)
            {
                var t = transient[ti];
                for (int pi = 0; pi < pinned.Count; pi++)
                {
                    var p = pinned[pi];
                    if (!SameApp(t, p)) continue;
                    foreach (var w in t.Windows) if (!p.Windows.Contains(w)) p.Windows.Add(w);
                    transient.RemoveAt(ti);
                    break;
                }
            }
        }

        void Compose()
        {
            MergeTransientIntoPinned();
            all = new List<DockItem>();
            all.Add(startItem);
            all.AddRange(pinned);
            all.AddRange(transient);
            all.Add(sepItem);
            all.Add(clockItem);
            all.Add(stackItem);
            all.Add(downloadsItem);
            all.Add(trashItem);
        }

        void SetupWatcher()
        {
            try
            {
                watcher = new FileSystemWatcher(appsDir, "*.lnk");
                watcher.IncludeSubdirectories = false;
                FileSystemEventHandler h = (o, e) => BeginInvoke(new Action(ScheduleReload));
                watcher.Created += h; watcher.Deleted += h; watcher.Changed += h;
                watcher.Renamed += (o, e) => BeginInvoke(new Action(ScheduleReload));
                watcher.EnableRaisingEvents = true;
            }
            catch { }
        }

        System.Windows.Forms.Timer reloadTimer;
        void ScheduleReload()
        {
            if (reloadTimer == null)
            {
                reloadTimer = new System.Windows.Forms.Timer { Interval = 600 };
                reloadTimer.Tick += (o, e) => { reloadTimer.Stop(); BuildItems(); RefreshRunning(); Relayout(); LogDuplicates("Recarga"); idleSince = DateTime.Now; };
            }
            reloadTimer.Stop(); reloadTimer.Start();
        }

        // ---------- running windows ----------
        // Por ventana se guardan pid, ruta, clase y AUMID; la ruta tambien se guarda por pid.
        // La entrada se invalida si la ventana se destruye (se poda al no volver a enumerarse)
        // o si el pid de la ventana cambia. La agrupacion resultante es la misma de antes.
        struct WinInfo { public uint Pid; public string Path; public string Cls; public string Aumid; }
        readonly Dictionary<IntPtr, WinInfo> winInfoCache = new Dictionary<IntPtr, WinInfo>();
        readonly Dictionary<uint, string> pidPathCache = new Dictionary<uint, string>();

        // Reintentos de AUMID: algunas ventanas (sobre todo ApplicationFrameWindow) aun no lo exponen cuando
        // se ven por primera vez. Espera progresiva (0,5 s, 1 s, 2 s... hasta 32 s) con tope total de 60 s;
        // se cancela si la ventana desaparece, cambia de pid (el cache se reinicia antes) o el AUMID aparece.
        struct AumidRetry { public int Count; public DateTime FirstSeen; public DateTime Last; }
        readonly Dictionary<IntPtr, AumidRetry> aumidRetries = new Dictionary<IntPtr, AumidRetry>();
        bool aumidRetryPending;
        static readonly int[] AumidDelays = { 500, 1000, 2000, 4000, 8000, 16000, 32000 };

        // true si toca releer el AUMID ahora; pending informa de que queda algun reintento para el SlowTick.
        bool ShouldRetryAumid(IntPtr h, WinInfo wi, out bool pending)
        {
            pending = false;
            if (wi.Aumid != "" || wi.Path == "") return false;
            DateTime now = DateTime.Now;
            if (!N.IsWindow(h)) { aumidRetries.Remove(h); return false; }   // la ventana se cerro
            AumidRetry st;
            if (!aumidRetries.TryGetValue(h, out st))
            {
                st = new AumidRetry { Count = 0, FirstSeen = now, Last = now };
                aumidRetries[h] = st;
            }
            if (st.Count >= AumidDelays.Length || (now - st.FirstSeen).TotalSeconds > 60) return false;
            if ((now - st.Last).TotalMilliseconds < AumidDelays[st.Count]) { pending = true; return false; }
            return true;
        }

        void RefreshRunning()
        {
            aumidRetryPending = false;
            var wins = new List<KeyValuePair<IntPtr, WinInfo>>();
            var seen = new HashSet<IntPtr>();
            IntPtr me = Handle;
            N.EnumWindows((h, l) =>
            {
                if (h == me || !N.IsWindowVisible(h)) return true;
                uint wpid; N.GetWindowThreadProcessId(h, out wpid); if (wpid == myPid) return true;
                if (N.GetWindow(h, 4) != IntPtr.Zero) return true;
                int ex = N.GetWindowLong(h, -20);
                if ((ex & 0x80) != 0) return true;
                if (N.GetWindowTextLength(h) == 0) return true;
                int cloaked; N.DwmGetWindowAttribute(h, 14, out cloaked, 4);
                if (cloaked != 0) return true;
                uint pid; N.GetWindowThreadProcessId(h, out pid);
                WinInfo wi;
                if (!winInfoCache.TryGetValue(h, out wi) || wi.Pid != pid)
                {
                    string path;
                    if (!pidPathCache.TryGetValue(pid, out path)) { path = N.ProcessPath(pid); pidPathCache[pid] = path; }
                    wi = new WinInfo { Pid = pid, Path = path, Cls = N.ClassOf(h), Aumid = path != "" ? (N.WindowAumid(h) ?? "") : "" };
                    winInfoCache[h] = wi;
                    aumidRetries.Remove(h);   // ventana nueva o pid distinto: el conteo empieza de cero
                    if (wi.Aumid == "" && wi.Path != "")
                    {
                        // Primera lectura vacia: deja el reintento programado para que SlowTick lo active.
                        aumidRetries[h] = new AumidRetry { Count = 0, FirstSeen = DateTime.Now, Last = DateTime.Now };
                        aumidRetryPending = true;
                    }
                }
                else
                {
                    bool pending;
                    if (ShouldRetryAumid(h, wi, out pending))
                    {
                        var st = aumidRetries[h];
                        st.Count++; st.Last = DateTime.Now;
                        aumidRetries[h] = st;
                        string again = N.WindowAumid(h) ?? "";
                        if (again != "")
                        {
                            wi.Aumid = again;
                            winInfoCache[h] = wi;
                            aumidRetries.Remove(h);
                        }
                        else if (st.Count < 3) pending = true;
                    }
                    if (pending && aumidRetries.ContainsKey(h)) aumidRetryPending = true;
                }
                seen.Add(h);
                if (wi.Path.EndsWith("\\explorer.exe", StringComparison.OrdinalIgnoreCase) && wi.Cls != "CabinetWClass") return true;
                wins.Add(new KeyValuePair<IntPtr, WinInfo>(h, wi));
                return true;
            }, IntPtr.Zero);

            if (winInfoCache.Count > seen.Count)
            {
                var dead = new List<IntPtr>();
                foreach (var k in winInfoCache.Keys) if (!seen.Contains(k)) dead.Add(k);
                foreach (var k in dead) winInfoCache.Remove(k);
                foreach (var k in dead) aumidRetries.Remove(k);
                if (dead.Count > 0)
                {
                    var live = new HashSet<uint>();
                    foreach (var v in winInfoCache.Values) live.Add(v.Pid);
                    var drop = new List<uint>();
                    foreach (var k in pidPathCache.Keys) if (!live.Contains(k)) drop.Add(k);
                    foreach (var k in drop) pidPathCache.Remove(k);
                }
            }

            windowExes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var w in wins) if (w.Value.Path != "") windowExes.Add(w.Value.Path);
            var before = new int[all.Count];
            for (int i = 0; i < all.Count; i++) before[i] = all[i].Windows.Count;
            int beforeCount = all.Count;
            foreach (var p in pinned) p.Windows.Clear();
            foreach (var t in transient) t.Windows.Clear();
            DockItem explorerItem = null;
            foreach (var p in pinned) if (p.IsExplorer) { explorerItem = p; break; }
            if (explorerItem != null && Finder.Current != null && !Finder.Current.IsDisposed && Finder.Current.Visible) explorerItem.Windows.Add(Finder.Current.Handle);

            foreach (var kv in wins)
            {
                IntPtr h = kv.Key;
                string path = kv.Value.Path;
                if (path == "") continue;
                bool explorerWin = path.EndsWith("\\explorer.exe", StringComparison.OrdinalIgnoreCase);
                if (explorerWin)
                {
                    if (explorerItem != null) explorerItem.Windows.Add(h);
                    continue;
                }
                string aumid = kv.Value.Aumid ?? "";
                string winCls = kv.Value.Cls;

                // 1) AppUserModelID: agrupa todas las ventanas de la misma app (Store/UWP, Squirrel...).
                DockItem it = ByAumid(pinned, aumid);
                if (it == null) it = ByAumid(transient, aumid);
                // 2) Ruta normalizada (misma app aunque cambie la carpeta de version).
                if (it == null)
                {
                    string np = NormPath(path);
                    foreach (var p in pinned)
                        if (!p.IsExplorer && p.Target != "" && NormPath(p.Target) == np) { it = p; break; }
                    if (it == null)
                        foreach (var t in transient)
                            if (t.Target != "" && NormPath(t.Target) == np) { it = t; break; }
                }
                // 3) Nombre del .exe y --processStart de Squirrel (Update.exe -> Discord.exe).
                if (it == null)
                {
                    string exe = Path.GetFileName(path);
                    it = ByExe(pinned, exe);
                }
                if (it == null)
                {
                    // Sin AUMID no se pueden agrupar las ventanas de ApplicationFrameHost.
                    bool frame = string.Equals(winCls, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase) ||
                                 path.EndsWith("\\ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase);
                    if (aumid == "" && frame) continue;
                    // Solo las apps empaquetadas (AUMID con "!") o alojadas en ApplicationFrameHost se abren
                    // con shell:AppsFolder. Las demas usan su .exe y guardan el AUMID solo para agrupar.
                    bool packed = aumid != "" && (aumid.IndexOf('!') >= 0 || frame);
                    string target = packed ? "shell:AppsFolder\\" + aumid : path, name;
                    if (packed) name = NameFromAumid(aumid);
                    else
                    {
                        name = Path.GetFileNameWithoutExtension(path);
                        try { var vi = FileVersionInfo.GetVersionInfo(path); if (!string.IsNullOrEmpty(vi.FileDescription)) name = vi.FileDescription; } catch { }
                    }
                    it = new DockItem { Kind = Kind.App, Target = target, Name = name, Aumid = aumid, Icon = IconFor(target) };
                    transient.Add(it);
                }
                it.Windows.Add(h);
            }
            transient.RemoveAll(t => t.Windows.Count == 0);
            PruneIconCache();
            Compose();

            bool changed = all.Count != beforeCount;
            if (!changed) for (int i = 0; i < all.Count; i++) if ((all[i].Windows.Count > 0) != (before[i] > 0)) { changed = true; break; }
            if (changed) { if (all.Count != beforeCount) Relayout(); else Render(); }
            LogDuplicates("RefreshRunning");
        }

        static string NormPath(string p)
        {
            if (string.IsNullOrEmpty(p)) return "";
            try { p = Path.GetFullPath(p); } catch { }
            return p.TrimEnd('\\').ToLowerInvariant();
        }

        static DockItem ByAumid(List<DockItem> list, string aumid)
        {
            if (string.IsNullOrEmpty(aumid)) return null;
            foreach (var it in list)
                if (!string.IsNullOrEmpty(it.Aumid) && string.Equals(it.Aumid, aumid, StringComparison.OrdinalIgnoreCase)) return it;
            return null;
        }

        // Por nombre de .exe solo si no hay ambiguedad: dos apps distintas pueden llamarse igual.
        static DockItem ByExe(List<DockItem> pinned, string exe)
        {
            if (string.IsNullOrEmpty(exe)) return null;
            DockItem hit = null; int n = 0;
            foreach (var p in pinned)
            {
                if (p.IsExplorer) continue;
                bool m = string.Equals(Path.GetFileName(p.Target), exe, StringComparison.OrdinalIgnoreCase) ||
                         (!string.IsNullOrEmpty(p.LauncherExe) && string.Equals(p.LauncherExe, exe, StringComparison.OrdinalIgnoreCase));
                if (m) { hit = p; n++; }
            }
            return n == 1 ? hit : null;
        }

        static string NameFromAumid(string aumid)
        {
            string s = aumid ?? "";
            int i = s.IndexOf('!'); if (i > 0) s = s.Substring(0, i);
            i = s.IndexOf('_'); if (i > 0) s = s.Substring(0, i);
            i = s.LastIndexOf('.'); if (i >= 0 && i + 1 < s.Length) s = s.Substring(i + 1);
            return s;
        }

        // ---------- timers ----------
        void SlowTick(object sender, EventArgs e)
        {
            try
            {
                if (!fullApp) HideTaskbarIfShown();
                if (aumidRetryPending) { aumidRetryPending = false; RefreshRunning(); }
                if (trayWnd != IntPtr.Zero && (!N.IsWindow(trayWnd) || !N.IsWindowVisible(trayWnd))) TrayClosed();
                if (DateTime.Now >= refreshCheckAt) { refreshCheckAt = DateTime.Now.AddSeconds(5); ApplyRefresh(); }
                if (DateTime.Now >= napAt) { napAt = DateTime.Now.AddSeconds(3); UpdateNap(); }
                if (DateTime.Now >= lookAt) { lookAt = DateTime.Now.AddSeconds(30); UpdateLook(); }
                if (DateTime.Now >= fixAt) { fixAt = DateTime.Now.AddSeconds(2); FixUnderMenuBar(); }
                if (!hiddenFull && !fast.Enabled && mag > 0f && Cursor.Position != lastCursor) StartAnim();
                if (DateTime.Now >= trashCheckAt) { trashCheckAt = DateTime.Now.AddSeconds(5); bool tf = N.RecycleBinFull(); if (tf != trashFull) { trashFull = tf; trashItem.Icon = TrashIcon() ?? trashItem.Icon; Render(); } }
                string clk = Program.TimeText(DateTime.Now, true) + SystemInformation.PowerStatus.BatteryLifePercent;
                if (clk != lastClock) { lastClock = clk; if (!fast.Enabled) Render(); }
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        void StartAnim()
        {
            if (!fast.Enabled)
            {
                animStamp = Stopwatch.GetTimestamp();
                fast.Start();
            }
            idleSince = DateTime.MaxValue;
        }

        static double BounceSeconds(DockItem it)
        {
            return it.Kind == Kind.Downloads ? 0.9 : 1.6;
        }

        bool AnyBouncing()
        {
            DateTime now = DateTime.Now;
            foreach (var it in all) if (it.LaunchAt != DateTime.MinValue)
                {
                    if (it.Running || (now - it.LaunchAt).TotalSeconds > BounceSeconds(it)) it.LaunchAt = DateTime.MinValue;
                    else return true;
                }
            return false;
        }

        void FastTick(object sender, EventArgs e)
        {
            if (hiddenFull) { fast.Stop(); return; }

            // Interpolacion dependiente del tiempo real transcurrido (no de un numero fijo de
            // fotogramas): el movimiento es igual de suave a 60 Hz que si Windows entrega menos
            // ticks. Se limita dt para que una pausa del hilo no produzca un salto.
            long stamp = Stopwatch.GetTimestamp();
            double dt = (stamp - animStamp) / (double)Stopwatch.Frequency;
            animStamp = stamp;
            if (dt <= 0.0 || dt > 0.10) dt = 0.016;

            bool sliding = slide != slideTarget;
            if (sliding)
            {
                slide += (slideTarget - slide) * (float)(1.0 - Math.Exp(-dt / 0.050));
                if (Math.Abs(slideTarget - slide) < 0.015f) slide = slideTarget;
                if (slide >= 1f && slideTarget >= 1f)
                {
                    hiddenFull = true; mag = 0f; hoverIdx = -1; fast.Stop();
                    N.ShowWindow(Handle, 0); idleSince = DateTime.Now;
                    TrimMemory();   // una sola operacion al terminar la interaccion, no por fotograma
                    return;
                }
            }
            Point c = Cursor.Position;
            if (preview != null && preview.IsUnderPointer(c))
            {
                previewCrossingUntil = 0;
                fast.Stop();
                return;
            }
            if (preview != null && InPreviewApproach(c))
            {
                if (N.WindowFromPoint(new N.POINT { x = c.X, y = c.Y }) == Handle)
                {
                    previewCrossingUntil = 0;
                    fast.Stop();
                    return;
                }
                if (previewCrossingUntil == 0)
                    previewCrossingUntil = stamp + Stopwatch.Frequency / 5;
                if (stamp < previewCrossingUntil) return;
                previewDismissedAt = c;
                ClosePreview();
            }
            float mx = c.X - winX, my = c.Y - (winY + SlidePx);
            float left = all[0].X - PadX, right = all[all.Count - 1].X + all[all.Count - 1].W + PadX;
            float topZone = BarTop - (mag > 0.05f ? Ic * (MaxMag - 1f) : 0f);
            bool inside = mx >= left - 2 && mx <= right + 2 && my >= topZone && my <= H + 2;
            if (dragging) inside = true;
            float prevMx = lastMx;
            float target = inside ? 1f : 0f;
            mag += (target - mag) * (float)(1.0 - Math.Exp(-dt / 0.060));
            if (Math.Abs(target - mag) < 0.01f) mag = target;
            if (inside)
            {
                if (!wasInside || magX < 0f) magX = mx;
                else magX += (mx - magX) * (float)(1.0 - Math.Exp(-dt / 0.022));
                lastMx = magX;
            }
            wasInside = inside;
            hoverIdx = inside ? HitIndex(mx) : -1;
            bool bouncing = AnyBouncing();
            bool same = c == lastCursor && mag == lastMag && Math.Abs(magX - prevMx) < 0.01f && !bouncing && !dragging && !sliding;
            lastCursor = c; lastMag = mag;
            if (same)
            {
                // Ni con el mouse encima hace falta seguir a 60 fps si nada se mueve: se apaga
                // tras unos fotogramas y OnMouseMove/OnMouseLeave lo vuelven a arrancar.
                if (++stillFrames > 10) { fast.Stop(); stillFrames = 0; idleSince = DateTime.Now; }
                return;
            }
            stillFrames = 0;
            DoLayout(lastMx, mag);
            if (cfg.VistaPrevia) TrackPreview(c);
            Render();
            if (!inside && mag == 0f && !bouncing && !dragging && slide == slideTarget)
            {
                fast.Stop(); hoverIdx = -1; Render(); idleSince = DateTime.Now;
            }
        }

        // ---------- layout ----------
        void Relayout()
        {
            Screen scr = Screen.PrimaryScreen;
            float restTotal = 0; foreach (var i in all) restTotal += Slot(i);
            W = (int)Math.Min(scr.Bounds.Width, restTotal + PadX * 2 + Ic * (MaxMag - 1f) * 3f + 240 * s);
            H = (int)(BottomM + BarH + Ic * Math.Max(MaxMag - 1f, 0.6f) + 50 * s);
            winX = scr.Bounds.X + (scr.Bounds.Width - W) / 2;
            winY = scr.Bounds.Bottom - H;
            if (canvas != null && (canvas.Width != W || canvas.Height != H)) DisposeCanvas();
            DoLayout(lastMx, mag);
            Render();
        }

        void DoLayout(float mx, float m)
        {
            float restTotal = 0; foreach (var it in all) restTotal += Slot(it);
            float restStart = (W - restTotal) / 2f;
            float cx = restStart;
            foreach (var it in all) { float sl = Slot(it); it.Cx = cx + sl / 2f; cx += sl; }
            float total = 0;
            float spread = Ic * 2.7f;
            foreach (var it in all)
            {
                it.Scale = 1f;
                if (m > 0 && it.Kind != Kind.Separator && MaxMag > 1f)
                {
                    float d = Math.Abs(mx - it.Cx) / spread;
                    if (d < 1f) it.Scale = 1f + (MaxMag - 1f) * m * (float)((Math.Cos(Math.PI * d) + 1) / 2);
                }
                it.W = Slot(it) + (it.Kind == Kind.Separator ? 0 : Ic * (it.Scale - 1f));
                total += it.W;
            }
            float t = restTotal > 0 ? Math.Max(0f, Math.Min(1f, (mx - restStart) / restTotal)) : 0.5f;
            float start = restStart - (total - restTotal) * t;
            float x = start;
            foreach (var it in all) { it.X = x; x += it.W; }
        }

        int HitIndex(float mx)
        {
            for (int i = 0; i < all.Count; i++)
                if (mx >= all[i].X && mx < all[i].X + all[i].W) return all[i].Kind == Kind.Separator ? -1 : i;
            return -1;
        }

        // ---------- rendering ----------
        static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath(); float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }

        float Bounce(DockItem it)
        {
            if (it.LaunchAt == DateTime.MinValue) return 0;
            double t = (DateTime.Now - it.LaunchAt).TotalSeconds;
            double dur = BounceSeconds(it);
            if (t <= 0 || t > dur) return 0;
            // Rebote amortiguado: tres saltos decrecientes en vez de una onda de amplitud fija.
            double amp = Ic * (it.Kind == Kind.Downloads ? 0.22 : 0.34);
            double decay = Math.Exp(-3.6 * (t / dur));
            double phase = t * Math.PI / (dur * 0.28);
            return (float)(amp * Math.Abs(Math.Sin(phase)) * decay);
        }

        // ---- recursos de dibujo reutilizados ----
        SolidBrush B(Color c)
        {
            int k = c.ToArgb(); SolidBrush b;
            if (!brushCache.TryGetValue(k, out b)) { b = new SolidBrush(c); brushCache[k] = b; }
            return b;
        }

        Pen P(Color c, float w)
        {
            long k = ((long)(uint)c.ToArgb() << 24) | (long)Math.Round(w * 16f);
            Pen p;
            if (!penCache.TryGetValue(k, out p)) { p = new Pen(c, w); penCache[k] = p; }
            return p;
        }

        // Identificador estable por familia (nombre -> id). Antes solo habia 3 ids fijos y cualquier
        // familia nueva caia en 0, pudiendo compartir entrada de cache con otra distinta.
        readonly Dictionary<string, int> famIds = new Dictionary<string, int>(StringComparer.Ordinal);
        int nextFamId;

        int FamId(string fam)
        {
            if (fam == null) fam = "";
            int id;
            if (famIds.TryGetValue(fam, out id)) return id;
            id = nextFamId++;
            famIds[fam] = id;
            return id;
        }

        Font F(string fam, float px, FontStyle style)
        {
            if (px <= 0) px = 1;
            int bucket = (int)Math.Round(px * 4f);
            long k = ((long)FamId(fam) << 32) | ((long)bucket << 4) | ((long)style & 0xF);
            Font f;
            if (!fontCache.TryGetValue(k, out f)) { f = new Font(fam, bucket / 4f, style, GraphicsUnit.Pixel); fontCache[k] = f; }
            return f;
        }

        void DisposeRenderCache()
        {
            foreach (var b in brushCache.Values) { try { b.Dispose(); } catch { } }
            foreach (var p in penCache.Values) { try { p.Dispose(); } catch { } }
            foreach (var f in fontCache.Values) { try { f.Dispose(); } catch { } }
            brushCache.Clear(); penCache.Clear(); fontCache.Clear();
            if (sfCenter != null) { try { sfCenter.Dispose(); } catch { } sfCenter = null; }
        }

        void Render()
        {
            if (W <= 0 || H <= 0 || all.Count == 0 || hiddenFull) return;
            if (canvas == null) { canvas = new Bitmap(W, H, PixelFormat.Format32bppArgb); canvasG = Graphics.FromImage(canvas); }
            var g = canvasG;
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // Alta calidad solo al ampliar; en reposo, bilineal (igual de nitido y mas barato).
            g.InterpolationMode = mag > 0.01f ? InterpolationMode.HighQualityBicubic : InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.CompositingQuality = CompositingQuality.HighQuality;

                float left = all[0].X - PadX, right = all[all.Count - 1].X + all[all.Count - 1].W + PadX;
                float top = BarTop, bottom = H - BottomM;
                DrawDockBackground(g, left, top, right, bottom);

                DockItem hover = hoverIdx >= 0 && hoverIdx < all.Count ? all[hoverIdx] : null;
                float hoverTop = 0;
                foreach (var it in all)
                {
                    if (it.Kind == Kind.Separator)
                    {
                        float sx = it.X + it.W / 2f;
                        float sy0 = top + PadY + 7 * s * IconUnit;
                        float sy1 = bottom - PadY - 7 * s * IconUnit;
                        g.DrawLine(P(light ? Color.FromArgb(46, 0, 0, 0) : Color.FromArgb(58, 255, 255, 255),
                                     Math.Max(1f, s * 0.8f)), sx, sy0, sx, sy1);
                        g.DrawLine(P(light ? Color.FromArgb(60, 255, 255, 255) : Color.FromArgb(24, 255, 255, 255),
                                     Math.Max(1f, s * 0.7f)), sx + Math.Max(1f, s), sy0, sx + Math.Max(1f, s), sy1);
                        continue;
                    }
                    float size = Ic * it.Scale;
                    float ix = it.X + (it.W - size) / 2f;
                    float iy = top + PadY + Ic - size - Bounce(it);
                    var r = new RectangleF(ix, iy, size, size);
                    if (it.Kind == Kind.Start) DrawStart(g, r);
                    else if (it.Kind == Kind.Clock) DrawClock(g, r);
                    else if (it.Icon != null) g.DrawImage(it.Icon, r);
                    else DrawPlaceholder(g, r, it.Name);
                    if (it == hover) hoverTop = iy; if (it.Badge > 0) DrawBadge(g, r, it.Badge);

                    if (it.Running)
                    {
                        float dr = 2.2f * s * IconUnit;
                        float dcx = it.X + it.W / 2f;
                        g.FillEllipse(B(light ? Color.FromArgb(200, 30, 30, 30) : Color.FromArgb(220, 255, 255, 255)),
                            dcx - dr, bottom - PadY / 2f - dr - 0.5f * s, dr * 2, dr * 2);
                    }
                }

                if (hover != null && mag > 0.35f && !dragging)
                {
                    string label = hover.Name;
                    if (hover.Kind == Kind.Clock) label = DateTime.Now.ToString("dddd d 'de' MMMM, ") + Program.TimeText(DateTime.Now, true);
                    var font = F(UiFont.Reg, 12.5f * s, FontStyle.Regular);
                    {
                        var ts = g.MeasureString(label, font);
                        float lw = ts.Width + 18 * s, lh = ts.Height + 8 * s;
                        float lx = hover.X + hover.W / 2 - lw / 2, ly = hoverTop - lh - 9 * s;
                        lx = Math.Max(2, Math.Min(W - lw - 2, lx)); ly = Math.Max(1, ly);
                        int a = (int)(255 * Math.Min(1f, (mag - 0.35f) / 0.4f));
                        a = (a / 8) * 8;   // pasos de 8: evita un pincel distinto por fotograma
                        using (var p = Round(new RectangleF(lx, ly, lw, lh), 6 * s))
                        {
                            int ca = a * 235 / 255;
                            g.FillPath(B(light ? Color.FromArgb(ca, 250, 250, 252) : Color.FromArgb(ca, 40, 40, 44)), p);
                            g.DrawPath(P(light ? Color.FromArgb(a / 5, 0, 0, 0) : Color.FromArgb(a / 4, 255, 255, 255), Math.Max(1f, s * 0.8f)), p);
                        }
                        g.DrawString(label, font, B(light ? Color.FromArgb(a, 20, 20, 20) : Color.FromArgb(a, 240, 240, 242)), lx + 9 * s, ly + 4 * s);
                    }
                }
            Push();
        }

        // Fondo del dock: sombra suave, material translucido, luz superior y borde fino. No usa
        // capturas de pantalla ni blur: conserva el escritorio real y evita halos oscuros.
        void DrawDockBackground(Graphics g, float left, float top, float right, float bottom)
        {
            float w = right - left, h = bottom - top;
            if (w <= 2 || h <= 2) return;
            float radius = Math.Min(Ic * 0.34f, h / 2f);

            // Sombra en dos capas muy tenues, desplazada hacia abajo. Alpha bajo para que no
            // aparezca un halo negro alrededor del dock.
            using (var p = Round(new RectangleF(left + 1 * s, top + 3 * s, w - 2 * s, h + 1 * s), radius + 1 * s))
                g.FillPath(B(Color.FromArgb(light ? 24 : 34, 0, 0, 0)), p);
            using (var p = Round(new RectangleF(left - 2 * s, top + 7 * s, w + 4 * s, h), radius + 3 * s))
                g.FillPath(B(Color.FromArgb(light ? 12 : 18, 0, 0, 0)), p);

            var bar = new RectangleF(left, top, w, h);
            using (var p = Round(bar, radius))
            {
                g.FillPath(B(light ? Color.FromArgb(194, 248, 248, 250) : Color.FromArgb(182, 30, 30, 36)), p);

                // Luz cenital y sombra inferior, recortadas al contorno redondeado: dan la
                // sensacion de material esmerilado sin tocar el contenido del escritorio.
                g.SetClip(p, CombineMode.Intersect);
                g.FillRectangle(B(Color.FromArgb(light ? 30 : 24, 255, 255, 255)), left, top, w, h * 0.42f);
                g.FillRectangle(B(Color.FromArgb(light ? 12 : 30, 0, 0, 0)), left, bottom - h * 0.30f, w, h * 0.30f);
                g.ResetClip();

                // Borde exterior discreto + linea interior clara en el borde superior.
                g.DrawPath(P(light ? Color.FromArgb(40, 0, 0, 0) : Color.FromArgb(54, 255, 255, 255),
                             Math.Max(1f, s * 0.8f)), p);
            }
            g.DrawLine(P(light ? Color.FromArgb(70, 255, 255, 255) : Color.FromArgb(34, 255, 255, 255),
                         Math.Max(1f, s * 0.7f)),
                       left + radius, top + Math.Max(1f, s * 0.8f),
                       right - radius, top + Math.Max(1f, s * 0.8f));
        }

        void DrawStart(Graphics g, RectangleF r)
        {
            using (var p = Round(r, r.Width * 0.225f))
            {
                using (var b = new LinearGradientBrush(r, Color.FromArgb(77, 163, 255), Color.FromArgb(16, 92, 214), 90f))
                    g.FillPath(b, p);
            }
            float q = r.Width * 0.19f, gap = r.Width * 0.035f;
            float ox = r.X + (r.Width - q * 2 - gap) / 2, oy = r.Y + (r.Height - q * 2 - gap) / 2;
            var w = B(Color.FromArgb(245, 255, 255, 255));
            g.FillRectangle(w, ox, oy, q, q); g.FillRectangle(w, ox + q + gap, oy, q, q);
            g.FillRectangle(w, ox, oy + q + gap, q, q); g.FillRectangle(w, ox + q + gap, oy + q + gap, q, q);
        }

        void DrawClock(Graphics g, RectangleF r)
        {
            using (var p = Round(r, r.Width * 0.225f))
            {
                g.FillPath(B(Color.FromArgb(252, 252, 252)), p);
                g.DrawPath(P(Color.FromArgb(40, 0, 0, 0), Math.Max(1f, s * 0.6f)), p);
            }
            var now = DateTime.Now;
            var sf = sfCenter;
            string[] dias = { "DOM", "LUN", "MAR", "MIÉ", "JUE", "VIE", "SÁB" };
            string day = dias[(int)now.DayOfWeek] + " " + now.Day + (Program.Clock12 ? (now.Hour < 12 ? " AM" : " PM") : "");
            g.DrawString(day, F(UiFont.Semi, r.Width * 0.155f, FontStyle.Regular), B(Color.FromArgb(255, 59, 48)),
                new RectangleF(r.X, r.Y + r.Height * 0.08f, r.Width, r.Height * 0.22f), sf);
            g.DrawString(Program.TimeText(now, false), F(UiFont.Semi, r.Width * 0.27f, FontStyle.Regular), B(Color.FromArgb(28, 28, 30)),
                new RectangleF(r.X, r.Y + r.Height * 0.3f, r.Width, r.Height * 0.36f), sf);

            var ps = SystemInformation.PowerStatus;
            float pct = ps.BatteryLifePercent;
            if (pct >= 0 && pct <= 1 && ps.BatteryChargeStatus != BatteryChargeStatus.NoSystemBattery)
            {
                float bw = r.Width * 0.24f, bh = r.Height * 0.11f;
                float bx = r.X + r.Width * 0.2f, by = r.Y + r.Height * 0.73f;
                using (var bp = Round(new RectangleF(bx, by, bw, bh), bh * 0.25f))
                    g.DrawPath(P(Color.FromArgb(140, 0, 0, 0), Math.Max(1f, r.Width * 0.018f)), bp);
                Color fill = pct <= 0.2f ? Color.FromArgb(255, 59, 48) : (ps.PowerLineStatus == PowerLineStatus.Online ? Color.FromArgb(52, 199, 89) : Color.FromArgb(60, 60, 64));
                g.FillRectangle(B(fill), bx + bw * 0.1f, by + bh * 0.22f, (bw * 0.8f) * pct, bh * 0.56f);
                g.FillRectangle(B(Color.FromArgb(140, 0, 0, 0)), bx + bw, by + bh * 0.3f, bw * 0.07f, bh * 0.4f);
                g.DrawString((int)Math.Round(pct * 100) + "%", F(UiFont.Reg, r.Width * 0.13f, FontStyle.Regular), B(Color.FromArgb(90, 90, 96)),
                    bx + bw * 1.15f, by - r.Height * 0.035f);
            }
        }

        void DrawPlaceholder(Graphics g, RectangleF r, string name)
        {
            using (var p = Round(r, r.Width * 0.225f))
                g.FillPath(B(Color.FromArgb(120, 120, 128)), p);
            g.DrawString(name.Length > 0 ? name.Substring(0, 1).ToUpperInvariant() : "?",
                F(UiFont.Semi, r.Width * 0.42f, FontStyle.Regular), Brushes.White, r, sfCenter);
        }

        void Push()
        {
            IntPtr screenDc = N.GetDC(IntPtr.Zero);
            IntPtr memDc = N.CreateCompatibleDC(screenDc);
            IntPtr hBmp = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                hBmp = canvas.GetHbitmap(Color.FromArgb(0));
                old = N.SelectObject(memDc, hBmp);
                var size = new N.SIZE { cx = W, cy = H };
                var src = new N.POINT();
                var dst = new N.POINT { x = winX, y = winY + (int)SlidePx };
                var blend = new N.BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
                N.UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, 2);
            }
            finally
            {
                N.ReleaseDC(IntPtr.Zero, screenDc);
                if (hBmp != IntPtr.Zero) { N.SelectObject(memDc, old); N.DeleteObject(hBmp); }
                N.DeleteDC(memDc);
            }
        }

        // ---------- mouse ----------
        bool PreviewBlocked()
        {
            return restored || !cfg.VistaPrevia || fullApp || !DockShown || slide > 0.01f ||
                menuOpen || (bar != null && bar.MenuOpen) || trayWnd != IntPtr.Zero ||
                (trayWait != null && trayWait.Enabled) || stackPopup != null ||
                ControlCenter.Current != null || dragging || pressItem != null;
        }

        Rectangle PreviewIcon(DockItem item)
        {
            float size = Ic * item.Scale;
            return Rectangle.Round(new RectangleF(winX + item.X + (item.W - size) / 2,
                winY + SlidePx + BarTop + PadY + Ic - size - Bounce(item), size, size));
        }

        DockItem PreviewHit(Point cursor)
        {
            if (PreviewBlocked()) return null;
            int i = HitIndex(cursor.X - winX);
            if (i < 0) return null;
            var item = all[i];
            return item.Kind == Kind.App && item.Running && PreviewIcon(item).Contains(cursor) ? item : null;
        }

        void TrackPreview(Point cursor)
        {
            if (!cfg.VistaPrevia) return;
            if (preview != null && (preview.IsUnderPointer(cursor) || InPreviewApproach(cursor))) return;
            if (previewDismissedAt == cursor) return;
            previewDismissedAt = null;
            var item = PreviewHit(cursor);
            if (item == previewItem) return;
            int delay = preview != null ? 120 : 400;
            ClosePreview();
            if (item == null || N.WindowFromPoint(new N.POINT { x = cursor.X, y = cursor.Y }) != Handle) return;
            previewItem = item;
            previewForeground = N.GetForegroundWindow();
            previewDeadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * delay / 1000;
            previewHover = N.SetTimer(Handle, PreviewTimerId, (uint)delay, IntPtr.Zero) != UIntPtr.Zero;
            if (!previewHover) previewItem = null;
        }

        void StopPreviewHover()
        {
            if (!previewHover) return;
            previewHover = false;
            if (IsHandleCreated) N.KillTimer(Handle, PreviewTimerId);
        }

        void ShowPreview()
        {
            StopPreviewHover();
            Point cursor = Cursor.Position;
            if (previewItem == null || PreviewHit(cursor) != previewItem ||
                N.WindowFromPoint(new N.POINT { x = cursor.X, y = cursor.Y }) != Handle ||
                N.GetForegroundWindow() != previewForeground || FindOverflow(true) != IntPtr.Zero)
            {
                ClosePreview();
                return;
            }
            try
            {
                previewAnchor = PreviewIcon(previewItem);
                preview = new DockPreview(previewItem, previewAnchor, s, h =>
                {
                    ClosePreview();
                    Focus(h);
                }, () => { ClosePreview(); StartAnim(); }, p =>
                {
                    StartAnim();
                    return InPreviewApproach(p) || (previewItem != null && PreviewHit(p) == previewItem);
                });
                if (preview.IsEmpty) ClosePreview();
                else
                {
                    if (!sessionWatch) sessionWatch = N.WTSRegisterSessionNotification(Handle, 0);
                    Interlocked.Exchange(ref previewHwnd, preview.Handle);
                    preview.Show();
                    if (cfg.Diagnostico) Program.Log("Vista previa: visible " + preview.Text +
                        " " + preview.Bounds + " foco=" + (previewForeground == N.GetForegroundWindow()));
                }
            }
            catch (Exception ex) { ClosePreview(); Program.Log(ex); }
        }

        void ClosePreview()
        {
            StopPreviewHover();
            previewItem = null;
            previewCrossingUntil = 0;
            var card = preview;
            if (card == null) return;
            preview = null;
            card.Dispose();
        }

        void UpdateDockScale()
        {
            float current;
            try { current = N.GetDpiForWindow(Handle) / 96f; } catch { current = DeviceDpi / 96f; }
            if (current > 0) s = current;
        }

        bool InPreviewApproach(Point p)
        {
            return preview != null && p.X >= previewAnchor.Left && p.X < previewAnchor.Right &&
                p.Y >= preview.Bounds.Bottom - 3 && p.Y < previewAnchor.Top + Math.Max(4, (int)(20 * s));
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            if (sessionWatch)
            {
                N.WTSUnRegisterSessionNotification(Handle);
                sessionWatch = false;
            }
            base.OnHandleDestroyed(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            StartAnim();
            TrackPreview(Cursor.Position);
            if (pressItem != null && e.Button == MouseButtons.Left && pressItem.Pinned)
            {
                if (!dragging && (Math.Abs(e.X - pressPt.X) > 8 * s || Math.Abs(e.Y - pressPt.Y) > 8 * s)) dragging = true;
                if (dragging)
                {
                    int cur = pinned.IndexOf(pressItem);
                    int target = cur;
                    for (int i = 0; i < pinned.Count; i++)
                        if (e.X >= pinned[i].X && e.X < pinned[i].X + pinned[i].W) { target = i; break; }
                    if (target != cur && cur >= 0) { pinned.RemoveAt(cur); pinned.Insert(target, pressItem); Compose(); }
                }
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            Point cursor = Cursor.Position;
            if (preview == null || (!preview.IsUnderPointer(cursor) && !InPreviewApproach(cursor))) ClosePreview();
            // El timer puede haberse apagado con la lupa ya abierta: al salir hay que reactivarlo
            // para que la magnificacion vuelva a 1 sin esperar al timer de 120 ms.
            StartAnim();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            ClosePreview();
            int i = HitIndex(e.X);
            pressItem = i >= 0 ? all[i] : null;
            pressPt = e.Location;
            dragging = false;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            var item = pressItem; pressItem = null;
            if (dragging) { dragging = false; SaveOrder(); return; }
            int i = HitIndex(e.X);
            var it = i >= 0 ? all[i] : null;
            try
            {
                // Se usa el elemento pulsado, no el que este bajo el cursor al soltar: la lupa mueve los
                // iconos entre el clic y el soltar y antes se perdia el primer clic. Solo se activa si el
                // cursor no se alejo mas de medio icono y no hubo arrastre.
                bool near = Math.Abs(e.X - pressPt.X) <= Ic / 2f && Math.Abs(e.Y - pressPt.Y) <= Ic / 2f;
                if (e.Button == MouseButtons.Left && item != null && near) Activate(item);
                else if (e.Button == MouseButtons.Right) ShowMenu(it);
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        void Launch(DockItem it)
        {
            var psi = new ProcessStartInfo(it.Lnk ?? it.Target) { UseShellExecute = true };
            if (it.Lnk == null && it.Target != "") psi.WorkingDirectory = Path.GetDirectoryName(it.Target);
            using (var p = Process.Start(psi)) { }
            if (!it.IsExplorer) { it.LaunchAt = DateTime.Now; StartAnim(); }
        }

        // ---------- animaciones de ventanas estilo macOS ----------
        void SyncAnimConfig()
        {
            var wac = new WindowAnimationConfig
            {
                AnimVentanas = cfg.AnimVentanas,
                AnimAbrirScale = cfg.AnimAbrirScale,
                AnimMinGenie = cfg.AnimMinGenie,
                AnimGenieVel = cfg.AnimGenieVel,
                AnimCerrarScale = cfg.AnimCerrarScale,
                Diagnostico = cfg.Diagnostico
            };
            WindowAnimationManager.ApplyConfig(wac);
        }

        ToolStripMenuItem BuildAnimMenu()
        {
            var main = new ToolStripMenuItem("Animaciones de ventanas") { Checked = cfg.AnimVentanas };
            var abrir = new ToolStripMenuItem("Apertura — Scale") { Checked = cfg.AnimAbrirScale };
            var genie = new ToolStripMenuItem("Minimización — Genie") { Checked = cfg.AnimMinGenie };
            var cerrar = new ToolStripMenuItem("Cierre — Scale + Fade") { Checked = cfg.AnimCerrarScale };

            main.Click += (o, e) =>
            {
                cfg.AnimVentanas = !cfg.AnimVentanas;
                cfg.Save();
                main.Checked = cfg.AnimVentanas;
                SyncAnimConfig();
            };

            abrir.Click += (o, e) =>
            {
                cfg.AnimAbrirScale = !cfg.AnimAbrirScale;
                cfg.Save();
                abrir.Checked = cfg.AnimAbrirScale;
                SyncAnimConfig();
            };

            genie.Click += (o, e) =>
            {
                cfg.AnimMinGenie = !cfg.AnimMinGenie;
                cfg.Save();
                genie.Checked = cfg.AnimMinGenie;
                SyncAnimConfig();
            };

            cerrar.Click += (o, e) =>
            {
                cfg.AnimCerrarScale = !cfg.AnimCerrarScale;
                cfg.Save();
                cerrar.Checked = cfg.AnimCerrarScale;
                SyncAnimConfig();
            };

            main.DropDownItems.Add(abrir);
            main.DropDownItems.Add(genie);
            var speed = new ToolStripMenuItem(cfg.AnimGenieVel < 0
                ? "Velocidad del Genie (original: 0,29 s)" : "Velocidad del Genie");
            var labels = new[] { "Normal (0,50 s)", "Rápida (0,35 s)", "Muy rápida (0,25 s)" };
            for (int i = 0; i < labels.Length; i++)
            {
                int value = i;
                var option = new ToolStripMenuItem(labels[i]) { Checked = cfg.AnimGenieVel == i };
                option.Click += (o, e) =>
                {
                    cfg.AnimGenieVel = value;
                    cfg.Save();
                    speed.Text = "Velocidad del Genie";
                    foreach (ToolStripMenuItem entry in speed.DropDownItems) entry.Checked = entry == option;
                    SyncAnimConfig();
                };
                speed.DropDownItems.Add(option);
            }
            main.DropDownItems.Add(speed);
            main.DropDownItems.Add(cerrar);
            var previews = new ToolStripMenuItem("Vista previa al pasar el ratón") { Checked = cfg.VistaPrevia };
            previews.Click += (o, e) =>
            {
                cfg.VistaPrevia = !cfg.VistaPrevia;
                ClosePreview();
                previews.Checked = cfg.VistaPrevia;
                cfg.Save();
            };
            main.DropDownItems.Add(previews);
            return main;
        }

        Point? GetDockIconCenter(IntPtr hwnd, uint pid)
        {
            try
            {
                DockItem found = null;
                // 1. Buscar en listas de ventanas conocidas de cada item
                foreach (var it in all)
                {
                    if (it.Windows.Contains(hwnd)) { found = it; break; }
                }

                // 2. Si no, correlacionar por PID y ejecutable / AUMID
                if (found == null)
                {
                    string path = N.ProcessPath(pid);
                    string aumid = N.WindowAumid(hwnd);
                    string name = Path.GetFileName(path ?? "");

                    foreach (var it in all)
                    {
                        if (it.Kind != Kind.App) continue;
                        if (!string.IsNullOrEmpty(aumid) && string.Equals(it.Aumid, aumid, StringComparison.OrdinalIgnoreCase)) { found = it; break; }
                        if (!string.IsNullOrEmpty(path) && string.Equals(it.Target, path, StringComparison.OrdinalIgnoreCase)) { found = it; break; }
                        if (!string.IsNullOrEmpty(it.LauncherExe) && string.Equals(it.LauncherExe, name, StringComparison.OrdinalIgnoreCase)) { found = it; break; }
                        if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(it.Target) && string.Equals(Path.GetFileName(it.Target), name, StringComparison.OrdinalIgnoreCase)) { found = it; break; }
                    }
                }

                if (found == null) return null;

                int cx = winX + (int)(found.X + found.W / 2);
                int iy = winY + (int)(SlidePx + BarTop + BarH / 2);
                return new Point(cx, iy);
            }
            catch { return null; }
        }

        void AnimOpenStop()
        {
            WindowAnimationManager.Shutdown();
        }

        void Activate(DockItem it)
        {
            switch (it.Kind)
            {
                case Kind.Start: N.PressKeys(0x5B); return;
                case Kind.Clock:
                    {
                        var ccAnchor = new Point(winX + (int)(it.X + it.W / 2), winY + (int)(SlidePx + BarTop - 6 * s));
                        var scr = Screen.FromPoint(new Point(winX + (int)(it.X + it.W / 2), winY + (int)(SlidePx + BarTop)));
                        ControlCenter.Toggle(s, light, ccAnchor, scr.Bounds, false, ToggleTheme);
                        return;
                    }
                case Kind.Downloads: it.Badge = 0; Render(); ToggleStack(it); return;
                case Kind.Stack: ToggleStack(it); return;
                case Kind.Trash: Process.Start("explorer.exe", "shell:RecycleBinFolder"); return;
            }
            RefreshRunning();
            var ws = it.Windows;
            if (ws.Count == 0) { if (it.IsExplorer && cfg.UseFinder) { Finder.OpenOrFocus(light, s); return; } Launch(it); return; }
            IntPtr fg = N.GetForegroundWindow();
            if (fg != IntPtr.Zero && Owns(ws, fg))
            {
                foreach (var h in ws) if (!N.IsIconic(h)) N.ShowWindow(h, 6);
                return;
            }
            // EnumWindows devuelve las ventanas en orden Z: la primera visible es la ultima usada.
            IntPtr target = IntPtr.Zero;
            foreach (var h in ws) if (!N.IsIconic(h)) { target = h; break; }
            if (target == IntPtr.Zero) target = ws[0];
            Focus(target);
        }

        static IntPtr RootOf(IntPtr h)
        {
            if (h == IntPtr.Zero) return h;
            IntPtr root = N.GetAncestor(h, 2);   // GA_ROOT
            if (root != IntPtr.Zero) h = root;
            IntPtr owner = N.GetAncestor(h, 3);  // GA_ROOTOWNER
            return owner != IntPtr.Zero ? owner : h;
        }

        static bool Owns(List<IntPtr> ws, IntPtr h)
        {
            IntPtr r = RootOf(h);
            foreach (var w in ws) if (w == h || w == r || RootOf(w) == r) return true;
            return false;
        }

        void ToggleStack(DockItem it)
        {
            if (stackPopup != null && !stackPopup.IsDisposed)
            {
                bool same = stackKind == it.Kind;
                stackPopup.Close();
                if (same) return;
                stackClosedAt = DateTime.MinValue;
            }
            stackKind = it.Kind;
            // el clic que cierra el panel (al perder foco) no debe volver a abrirlo
            if ((DateTime.Now - stackClosedAt).TotalMilliseconds < 300 && closedKind == it.Kind) return;
            var anchor = new Point(winX + (int)(it.X + it.W / 2), winY + (int)(SlidePx + BarTop - 8 * s));
            stackPopup = it.Kind == Kind.Downloads
                ? new StackPopup(s, light, anchor, Screen.PrimaryScreen.Bounds, "Descargas", new[] { it.Target }, true)
                : new StackPopup(s, light, anchor, Screen.PrimaryScreen.Bounds);
            stackPopup.FormClosed += (o, e) => { stackClosedAt = DateTime.Now; closedKind = stackKind; stackPopup = null; };
            stackPopup.Show();
            N.SetForegroundWindow(stackPopup.Handle);
        }

        void Focus(IntPtr h)
        {
            if (h == IntPtr.Zero) return;
            IntPtr fg = N.GetForegroundWindow();
            uint tid = 0, ftid = 0;
            N.GetWindowThreadProcessId(h, out tid);
            if (fg != IntPtr.Zero) N.GetWindowThreadProcessId(fg, out ftid);
            bool attach = tid != 0 && ftid != 0 && tid != ftid;
            if (attach) N.AttachThreadInput(ftid, tid, true);
            try
            {
                if (N.IsIconic(h)) N.ShowWindow(h, 9);
                N.BringWindowToTop(h);
                N.SetForegroundWindow(h);
                if (N.GetForegroundWindow() != h) N.SwitchToThisWindow(h, true);
            }
            finally { if (attach) N.AttachThreadInput(ftid, tid, false); }
        }

        // ---------- context menu ----------
        ContextMenuStrip NewMenu()
        {
            return MenuStyle.New(light);
        }

        void Add(ContextMenuStrip m, string text, Action a, bool bold = false)
        {
            var mi = new ToolStripMenuItem(text);
            if (bold) mi.Font = new Font(m.Font, FontStyle.Bold);
            mi.Click += (o, e) => { try { a(); } catch (Exception ex) { Program.Log(ex); } };
            m.Items.Add(mi);
        }

        void ShowMenu(DockItem it)
        {
            ClosePreview();
            var m = NewMenu();
            if (it != null && it.Kind == Kind.App)
            {
                foreach (var h in it.Windows.Take(10))
                {
                    var hh = h; string t = N.TitleOf(h); if (t.Length > 60) t = t.Substring(0, 57) + "...";
                    Add(m, t, () => Focus(hh));
                }
                if (it.Windows.Count > 0) m.Items.Add(new ToolStripSeparator());
                if ((it.Target + it.Name).IndexOf("docker", StringComparison.OrdinalIgnoreCase) >= 0 && DockerRunning())
                {
                    Add(m, "Apagar Docker y WSL (libera RAM y bateria)", () => StopDocker(), true);
                    m.Items.Add(new ToolStripSeparator());
                }
                Add(m, it.Running ? "Nueva ventana" : "Abrir", () => Launch(it), !it.Running);
                if (it.Pinned)
                    Add(m, "Quitar del dock", () => { try { File.Delete(it.Lnk); } catch { } });
                else if (it.Target != "")
                    Add(m, "Mantener en el dock", () =>
                    {
                        string name = string.Join("_", it.Name.Split(Path.GetInvalidFileNameChars()));
                        string lnk = Path.Combine(appsDir, name + ".lnk");
                        Shell.CreateLnk(lnk, it.Target);
                        var order = pinned.Select(p => Path.GetFileName(p.Lnk)).ToList(); order.Add(Path.GetFileName(lnk));
                        try { File.WriteAllLines(orderFile, order); } catch { }
                    });
                if (it.Running)
                {
                    m.Items.Add(new ToolStripSeparator());
                    Add(m, it.Windows.Count > 1 ? "Cerrar todas las ventanas" : "Cerrar", () => { foreach (var h in it.Windows) N.PostMessage(h, 0x10, IntPtr.Zero, IntPtr.Zero); });
                }
                m.Items.Add(new ToolStripSeparator());
            }
            else if (it != null && it.Kind == Kind.Trash)
            {
                Add(m, "Abrir", () => Activate(it), true);
                Add(m, "Vaciar papelera...", () => { N.SHEmptyRecycleBin(Handle, null, 0); });
                m.Items.Add(new ToolStripSeparator());
            }
            else if (it != null && it.Kind == Kind.Clock)
            {
                Add(m, "Wi-Fi, volumen y bateria", () => N.PressKeys(0x5B, 0x41), true);
                Add(m, "Notificaciones y calendario", () => N.PressKeys(0x5B, 0x4E));
                Add(m, "Configuracion de Windows", () => Process.Start("ms-settings:"));
                m.Items.Add(new ToolStripSeparator());
            }
            else if (it != null && it.Kind == Kind.Stack)
            {
                Add(m, "Ver lo que hay en el escritorio", () => ToggleStack(it), true);
                Add(m, DesktopIcons.Visible ? "Ocultar iconos del escritorio" : "Mostrar iconos del escritorio", () => DesktopIcons.Toggle());
                Add(m, "Abrir en el Explorador", () => Process.Start("explorer.exe", "\"" + it.Target + "\""));
                m.Items.Add(new ToolStripSeparator());
            }
            else if (it != null && it.Kind == Kind.Downloads)
            {
                Add(m, "Ver descargas", () => ToggleStack(it), true);
                Add(m, "Abrir en el Explorador", () => Process.Start("explorer.exe", "\"" + it.Target + "\""));
                m.Items.Add(new ToolStripSeparator());
            }

            var opts = new ToolStripMenuItem("Opciones del dock");
            Action<string, bool, Action> addOpt = (text, chk, act) =>
            {
                var mi = new ToolStripMenuItem(text) { Checked = chk };
                mi.Click += (o, e) => { try { act(); } catch (Exception ex) { Program.Log(ex); } };
                opts.DropDownItems.Add(mi);
            };
            addOpt("Efecto lupa", cfg.Magnify, () => { cfg.Magnify = !cfg.Magnify; cfg.Save(); Relayout(); });
            addOpt("Ocultar cuando una ventana lo tape", cfg.AutoHide && !cfg.ReserveSpace, () =>
            {
                cfg.AutoHide = !(cfg.AutoHide && !cfg.ReserveSpace);
                if (cfg.AutoHide && cfg.ReserveSpace) { cfg.ReserveSpace = false; UnregisterAppBar(); }
                cfg.Save();
                UpdateEdgeTimer();
            });
            addOpt("Pantalla a 60 Hz con bateria (ahorro)", cfg.AutoRefresh, () =>
            {
                cfg.AutoRefresh = !cfg.AutoRefresh; cfg.Save();
                lastPower = (PowerLineStatus)(-1); ApplyRefresh();
            });
            addOpt("Menu de Mac al hacer clic derecho en el escritorio", cfg.DesktopMenu, () => { cfg.DesktopMenu = !cfg.DesktopMenu; cfg.Save(); SetDesktopMenu(cfg.DesktopMenu); });
            addOpt("Abrir Finder en vez del Explorador", cfg.UseFinder, () => { cfg.UseFinder = !cfg.UseFinder; cfg.Save(); });
            addOpt("Spotlight con Alt + Espacio", cfg.SpotlightKey, () =>
            {
                cfg.SpotlightKey = !cfg.SpotlightKey; cfg.Save();
                if (cfg.SpotlightKey) RegisterHotKey(Handle, 7, 0x0001 | 0x4000, 0x20); else UnregisterHotKey(Handle, 7);
            });
            addOpt("Vista rapida con Espacio en el Explorador", cfg.QuickLookExplorer, () =>
            {
                cfg.QuickLookExplorer = !cfg.QuickLookExplorer; cfg.Save();
                if (cfg.QuickLookExplorer) SetKeyHook(true);
                else SetKeyHook(false);
            });
            addOpt("Modo claro de dia, oscuro de noche", cfg.AutoTheme, () =>
            {
                cfg.AutoTheme = !cfg.AutoTheme; cfg.Save(); lookAt = DateTime.MinValue;
            });
            addOpt("Fondo dinamico segun la hora", cfg.DynWall, () =>
            {
                cfg.DynWall = !cfg.DynWall; cfg.Save(); lookAt = DateTime.MinValue;
            });
            addOpt("Barra superior", cfg.MenuBar, () =>
            {
                cfg.MenuBar = !cfg.MenuBar; cfg.Save(); SetMenuBar(cfg.MenuBar);
            });
            addOpt("Escritorio limpio (ocultar iconos)", !DesktopIcons.Visible, () => DesktopIcons.Toggle());
            addOpt("App Nap: apps sin usar en modo eficiencia (con bateria)", cfg.AppNap, () =>
            {
                cfg.AppNap = !cfg.AppNap; cfg.Save(); UpdateNap();
            });
            addOpt("Esquinas activas (arriba-izq: ventanas, abajo-izq: escritorio)", cfg.HotCorners, () =>
            {
                cfg.HotCorners = !cfg.HotCorners; cfg.Save();
                UpdateEdgeTimer();
            });
            addOpt("Reservar espacio (las ventanas no lo tapan)", cfg.ReserveSpace, () =>
            {
                cfg.ReserveSpace = !cfg.ReserveSpace; cfg.Save();
                if (cfg.ReserveSpace) RegisterAppBar(); else UnregisterAppBar();
                UpdateEdgeTimer();
            });
            addOpt("Reloj de 12 horas (AM/PM)", cfg.Clock12h, () => { cfg.Clock12h = !cfg.Clock12h; Program.Clock12 = cfg.Clock12h; cfg.Save(); lastClock = ""; Render(); if (bar != null) bar.Invalidate(); });
            opts.DropDownItems.Add(BuildAnimMenu());
            addOpt("Editar apps del dock (carpeta)", false, () => Process.Start("explorer.exe", "\"" + appsDir + "\""));
            addOpt("Importar de nuevo apps de la barra", false, () => { ImportTaskbarPins(); ScheduleReload(); });
            Add(m, "Abrir Finder", () => Finder.OpenOrFocus(light, s));
            Add(m, "Buscar con Spotlight (Alt + Espacio)", () => Spotlight.Toggle(light, s));
            if (DockerRunning()) Add(m, "Apagar Docker y WSL (libera RAM y bateria)", () => StopDocker());
            m.Items.Add(opts);
            Add(m, "Cerrar dock y volver a la barra de Windows", () => Close());

            menuOpen = true;
            m.Closed += (o, e) => { menuOpen = false; };
            N.SetForegroundWindow(Handle);
            m.Show(Cursor.Position);
        }

        // ---------- taskbar / appbar ----------
        // Los handles de la barra se buscan una vez y se refrescan solo si Explorer los recrea
        // (TaskbarCreated) o si un handle deja de ser ventana. Antes se llamaba a FindWindow y
        // EnumWindows en cada SlowTick y en cada cierre del cuadro de la bandeja.
        IntPtr trayMain;
        readonly List<IntPtr> trayAll = new List<IntPtr>();
        bool trayCacheReady;

        void RefreshTrayCache()
        {
            trayMain = N.FindWindow("Shell_TrayWnd", null);
            trayAll.Clear();
            if (trayMain != IntPtr.Zero) trayAll.Add(trayMain);
            N.EnumWindows((h, l) => { if (N.ClassOf(h) == "Shell_SecondaryTrayWnd") trayAll.Add(h); return true; }, IntPtr.Zero);
            trayCacheReady = true;
        }

        bool TrayCacheValid()
        {
            if (!trayCacheReady) return false;
            if (trayMain != IntPtr.Zero && !N.IsWindow(trayMain)) return false;
            foreach (var t in trayAll) if (!N.IsWindow(t)) return false;
            return true;
        }

        List<IntPtr> Trays()
        {
            if (!TrayCacheValid()) RefreshTrayCache();
            return trayAll;
        }

        string TaskbarMarker { get { return Path.Combine(Program.DataDir, "taskbar.marker"); } }

        // Si el proceso anterior murio sin restaurar (Procesos > Finalizar), devuelve la barra de Windows
        // al arrancar (y tambien en TaskbarCreated). Se ejecuta siempre ANTES de volver a modificar la
        // barra. Entiende el marker v3 ("A|1", "B|<monitor>|...") y los antiguos ("1",
        // "R|idx|l|t|r|b", "S|idx|ex|add|cr|al|fl"). El marker solo se borra cuando toda la
        // restauracion termino bien; si algo fallo se conserva para reintentar.
        void RestoreStaleTaskbar()
        {
            try
            {
                if (!File.Exists(TaskbarMarker)) return;
                string txt = File.ReadAllText(TaskbarMarker);
                IntPtr tray = N.FindWindow("Shell_TrayWnd", null);
                if (tray == IntPtr.Zero) { Program.Log("taskbar.marker pendiente: la barra de Windows todavia no existe; se conserva para reintentar"); return; }
                bool restoreAutoHide = txt.Trim() == "1";   // formato muy antiguo: "1" = autohide activo por MacDock
                bool ok = true;
                var trays = Trays().ToList();
                var byDev = new Dictionary<string, IntPtr>();
                foreach (var t in trays) { string d = MonitorDeviceOf(t); if (d != null && !byDev.ContainsKey(d)) byDev[d] = t; }
                foreach (var raw in txt.Split('\n'))
                {
                    string line = raw.TrimEnd('\r');
                    if (line.Length == 0) continue;
                    string[] p = line.Split('|');
                    int idx;
                    try
                    {
                        if (p[0] == "A" && p.Length >= 2 && p[1] == "1") restoreAutoHide = true;
                        else if (p[0] == "B" && p.Length >= 7)
                        {
                            IntPtr h;
                            if (p[1].StartsWith("#"))
                            {
                                if (!int.TryParse(p[1].Substring(1), out idx) || idx < 0 || idx >= trays.Count) continue;
                                h = trays[idx];
                            }
                            else if (!byDev.TryGetValue(p[1], out h)) continue;   // esa barra ya no existe: no hay estilo que deshacer
                            int ex, add; uint cr, fl; byte al;
                            if (!int.TryParse(p[2], out ex) || !int.TryParse(p[3], out add) || !uint.TryParse(p[4], out cr) ||
                                !byte.TryParse(p[5], out al) || !uint.TryParse(p[6], out fl)) { ok = false; continue; }
                            N.SetWindowLong(h, GWL_EXSTYLE, ex);
                            if (add == 0) N.SetLayeredWindowAttributes(h, cr, al, fl);
                        }
                        else if (p[0] == "S" && p.Length >= 7 && int.TryParse(p[1], out idx) && idx < trays.Count)   // v2 por indice
                        {
                            int ex, add; uint cr, fl; byte al;
                            if (!int.TryParse(p[2], out ex) || !int.TryParse(p[3], out add) || !uint.TryParse(p[4], out cr) ||
                                !byte.TryParse(p[5], out al) || !uint.TryParse(p[6], out fl)) { ok = false; continue; }
                            N.SetWindowLong(trays[idx], GWL_EXSTYLE, ex);
                            if (add == 0) N.SetLayeredWindowAttributes(trays[idx], cr, al, fl);
                        }
                        else if (p[0] == "R" && p.Length >= 6 && int.TryParse(p[1], out idx) && idx < trays.Count)
                        {
                            int l, t;
                            if (int.TryParse(p[2], out l) && int.TryParse(p[3], out t))
                                N.SetWindowPos(trays[idx], IntPtr.Zero, l, t, 0, 0, 0x0001 | 0x0004 | 0x0010);
                        }
                    }
                    catch (Exception ex2) { ok = false; Program.Log(ex2); }
                }
                N.ShowWindow(tray, 5);
                if (restoreAutoHide)
                {
                    var abd = new N.APPBARDATA(); abd.cbSize = Marshal.SizeOf(typeof(N.APPBARDATA)); abd.hWnd = tray;
                    int state = (int)N.SHAppBarMessage(4, ref abd);
                    abd.lParam = new IntPtr(state & ~1);
                    N.SHAppBarMessage(10, ref abd);
                }
                markerAutoHide = false;
                if (ok) File.Delete(TaskbarMarker);
                else Program.Log("taskbar.marker: restauracion incompleta; el marker se conserva para reintentar");
            }
            catch { }
        }

        void SetupTaskbar()
        {
            RestoreStaleTaskbar();
            if (!cfg.HideTaskbar) return;
            var abd = new N.APPBARDATA(); abd.cbSize = Marshal.SizeOf(typeof(N.APPBARDATA));
            abd.hWnd = N.FindWindow("Shell_TrayWnd", null);
            int state = (int)N.SHAppBarMessage(4, ref abd);
            savedAutoHide = (state & 1) != 0; haveSavedState = true;
            if (!savedAutoHide)
            {
                abd.lParam = new IntPtr(state | 1); N.SHAppBarMessage(10, ref abd);
                markerAutoHide = true; WriteMarker();
            }
            HideTaskbarIfShown();
        }

        void HideTaskbarIfShown()
        {
            if (!cfg.HideTaskbar) return;
            var trays = Trays();
            IntPtr mainTray = trayMain;
            bool trayVis = mainTray != IntPtr.Zero && N.IsWindowVisible(mainTray);
            if (trayVis && !trayWasVisible) LogTrayVisible("SlowTick");
            trayWasVisible = trayVis;
            // No ocultar mientras se espera el cuadro de la bandeja o mientras esta abierto:
            // Win+B necesita la barra y el cuadro se cerraria solo.
            if (trayWnd != IntPtr.Zero || trayShowHook != IntPtr.Zero) return;
            IntPtr fg = N.GetForegroundWindow();
            string fc = fg != IntPtr.Zero ? N.ClassOf(fg) : "";
            if (fc == "Windows.UI.Core.CoreWindow" || fc == "Shell_TrayWnd" || fc == "Shell_SecondaryTrayWnd" ||
                fc == "XamlExplorerHostIslandWindow" || fc == "TopLevelWindowForOverflowXamlIsland" || fc == "NotifyIconOverflowWindow")
                return;
            foreach (var t in trays) if (N.IsWindowVisible(t)) N.ShowWindow(t, 0);
        }

        void RegisterAppBar()
        {
            if (appBarOn) return;
            var abd = new N.APPBARDATA(); abd.cbSize = Marshal.SizeOf(typeof(N.APPBARDATA));
            abd.hWnd = Handle; abd.uCallbackMessage = appBarMsg;
            N.SHAppBarMessage(0, ref abd);
            appBarOn = true;
            SetAppBarPos();
        }

        void SetAppBarPos()
        {
            if (!appBarOn) return;
            var b = Screen.PrimaryScreen.Bounds;
            var abd = new N.APPBARDATA(); abd.cbSize = Marshal.SizeOf(typeof(N.APPBARDATA));
            abd.hWnd = Handle; abd.uEdge = 3;
            abd.rc.left = b.Left; abd.rc.right = b.Right; abd.rc.bottom = b.Bottom;
            abd.rc.top = b.Bottom - (int)Math.Ceiling(BottomM + BarH + 2 * s);
            N.SHAppBarMessage(2, ref abd);
            abd.rc.top = abd.rc.bottom - (int)Math.Ceiling(BottomM + BarH + 2 * s);
            N.SHAppBarMessage(3, ref abd);
        }

        void UnregisterAppBar()
        {
            if (!appBarOn) return;
            var abd = new N.APPBARDATA(); abd.cbSize = Marshal.SizeOf(typeof(N.APPBARDATA)); abd.hWnd = Handle;
            N.SHAppBarMessage(1, ref abd);
            appBarOn = false;
        }

        static bool ShellWindow(string c)
        {
            return c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd" ||
                   c == "Windows.UI.Core.CoreWindow" || c == "XamlExplorerHostIslandWindow" || c == "#32768" ||
                   c == "TopLevelWindowForOverflowXamlIsland" || c == "MultitaskingViewFrame" ||
                   c == "ForegroundStaging" || c == "ApplicationManager_DesktopShellWindow" || c == "TaskListThumbnailWnd";
        }

        bool fsCheckQueued;
        N.WinEventProc locProc; IntPtr locHook = IntPtr.Zero; uint locPid;

        // El primer plano puede cambiar a otra ventana del mismo proceso (video, F11) sin EVENT_SYSTEM_FOREGROUND.
        // Un hook de LOCATIONCHANGE limitado al pid en primer plano detecta esos cambios sin temporizadores.
        void InstallLocHook()
        {
            IntPtr fg = N.GetForegroundWindow();
            uint pid = 0;
            if (fg != IntPtr.Zero) N.GetWindowThreadProcessId(fg, out pid);
            if (pid == myPid) pid = 0;
            if (locHook != IntPtr.Zero)
            {
                if (pid == locPid) return;
                try { N.UnhookWinEvent(locHook); } catch { } locHook = IntPtr.Zero;
            }
            locPid = pid;
            if (pid == 0) return;
            locProc = OnLocationChanged;
            locHook = N.SetWinEventHook(0x800B, 0x800B, IntPtr.Zero, locProc, pid, 0, 0);
        }

        void OnLocationChanged(IntPtr hk, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time)
        {
            if (obj != 0 || child != 0) return;
            // Este hook ya esta limitado al proceso en primer plano: sirve para refrescar la
            // captura de esa ventana sin escuchar los cambios de posicion de todo el sistema.
            try { WindowAnimationManager.OnLocationChanged(hwnd); } catch { }
        }

        void QueueFullscreenCheck()
        {
            if (fsCheckQueued) return;
            fsCheckQueued = true;
            try
            {
                BeginInvoke(new Action(() =>
                {
                    fsCheckQueued = false;
                    CheckFullscreen();
                }));
            }
            catch { fsCheckQueued = false; }
        }

        void OnWinWatch(uint ev, IntPtr hwnd)
        {
            if (hwnd == Interlocked.CompareExchange(ref previewHwnd, IntPtr.Zero, IntPtr.Zero))
            {
                uint pid;
                N.GetWindowThreadProcessId(hwnd, out pid);
                if (pid == myPid || (pid == 0 && ev == 0x8001)) return;
            }
            var card = preview;
            bool interaction = ev == 0x0004 || ev == 0x0014; // MENUSTART / SWITCHSTART
            if (card != null && (interaction ||
                ((ev == 0x8000 || ev == 0x8001 || ev == 0x8003 || ev == 0x0016 || ev == 0x0017)
                    && card.AffectsSource(hwnd))))
            {
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (preview != card) return;
                        previewDismissedAt = Cursor.Position;
                        ClosePreview();
                        StartAnim();
                    }));
                }
                catch { }
            }
            if (ev == 0x0003)
            {
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (previewItem != null && (N.GetForegroundWindow() != previewForeground ||
                            FindOverflow(true) != IntPtr.Zero))
                        {
                            previewDismissedAt = Cursor.Position;
                            ClosePreview();
                            StartAnim();
                        }
                        InstallLocHook();
                        if (trayWnd != IntPtr.Zero && hwnd != trayWnd)
                        {
                            string c = N.ClassOf(hwnd);
                            if (c != "Shell_TrayWnd" && c != "Shell_SecondaryTrayWnd") TrayClosed();
                        }
                        QueueFullscreenCheck();
                        WindowAnimationManager.OnForegroundChanged(hwnd);
                    }));
                }
                catch { }
                return;
            }
            if (ev == 0x800B)
            {
                // Cambio de posicion/tamano: refresca la captura de la ventana en primer plano.
                // Es barato y no toca la interfaz, asi que se atiende directamente.
                try { WindowAnimationManager.OnLocationChanged(hwnd); } catch { }
                return;
            }
            if (ev == 0x000A || ev == 0x000B || ev == 0x0016 || ev == 0x0017 ||
                ev == 0x8000 || ev == 0x8001 || ev == 0x8002 || ev == 0x8003)
            {
                QueueRefreshRunning();
                if (ev == 0x0016 || ev == 0x0017 || ev == 0x8000 || ev == 0x8001 || ev == 0x8002 || ev == 0x8003)
                {
                    try
                    {
                        BeginInvoke(new Action(() =>
                        {
                            WindowAnimationManager.OnWindowEvent(ev, hwnd);
                        }));
                    }
                    catch { }
                }
            }
        }

        void QueueRefreshRunning()
        {
            if (runRefreshQueued) return;
            runRefreshQueued = true;
            try
            {
                BeginInvoke(new Action(() =>
                {
                    runRefreshQueued = false;
                    RefreshRunning();
                }));
            }
            catch { runRefreshQueued = false; }
        }

        // Pantalla completa: SHQueryUserNotificationState y, de respaldo, la ventana en primer plano
        // que ocupa todo el monitor del dock. Solo se revisa cuando cambia el primer plano (WinEvent).
        void CheckFullscreen()
        {
            bool full = false;
            try
            {
                IntPtr fg = N.GetForegroundWindow();
                if (fg != IntPtr.Zero && fg != Handle && !N.IsIconic(fg) && N.IsWindowVisible(fg))
                {
                    uint pid; N.GetWindowThreadProcessId(fg, out pid);
                    string cls = N.ClassOf(fg);
                    bool skip = pid == myPid || ShellWindow(cls);
                    if (!skip)
                    {
                        string path = N.ProcessPath(pid);
                        if (path.EndsWith("\\explorer.exe", StringComparison.OrdinalIgnoreCase)) skip = true;
                    }
                    if (!skip)
                    {
                        int state = 0;
                        bool quns = N.SHQueryUserNotificationState(out state) == 0;
                        bool d3d = quns && (state == 3 || state == 4);       // D3D fullscreen / presentacion
                        bool busy = quns && state == 2;                       // ocupado (juego, video...)
                        var mi = new N.MONITORINFO(); mi.cbSize = Marshal.SizeOf(typeof(N.MONITORINFO));
                        IntPtr mon = N.MonitorFromWindow(Handle, 2);          // MONITOR_DEFAULTTONEAREST
                        if (mon != IntPtr.Zero && N.GetMonitorInfo(mon, ref mi))
                        {
                            N.RECT r;
                            if (N.GetWindowRect(fg, out r))
                            {
                                int tol = Math.Max(2, (int)(s * 2));
                                bool covers = r.left <= mi.rcMonitor.left + tol && r.top <= mi.rcMonitor.top + tol &&
                                              r.right >= mi.rcMonitor.right - tol && r.bottom >= mi.rcMonitor.bottom - tol;
                                bool caption = (N.GetWindowLong(fg, -16) & 0x00C00000) == 0x00C00000;
                                full = covers && (!caption || busy || d3d);
                            }
                        }
                        if (!full && d3d) full = true;
                    }
                }
            }
            catch (Exception ex) { Program.Log(ex); }
            if (full == fullApp) return;
            fullApp = full;
            if (bar != null) bar.SetFull(full);
            if (full) HideForFullscreen(); else ShowAfterFullscreen();
        }

        void HideForFullscreen()
        {
            ClosePreview();
            try
            {
                ControlCenter.CloseIfOpen();
                peeking = false; leaveSince = DateTime.MaxValue;
                if (!hiddenFull)
                {
                    hiddenFull = true; slide = 0f; slideTarget = 0f; mag = 0f; hoverIdx = -1;
                    fast.Stop();
                    N.SetWindowPos(Handle, new IntPtr(-2), 0, 0, 0, 0, 0x0013);   // deja de ser TopMost
                    N.ShowWindow(Handle, 0);
                    idleSince = DateTime.Now;
                }
                if (appBarOn) UnregisterAppBar();
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        void ShowAfterFullscreen()
        {
            try
            {
                N.SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0013);       // vuelve a ser TopMost
                if (cfg.ReserveSpace) RegisterAppBar();
                if (!(cfg.AutoHide && !cfg.ReserveSpace && Covered())) ShowDock();
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        float SlidePx { get { return slide * (BottomM + BarH + 8 * s); } }
        bool DockShown { get { return !hiddenFull && slideTarget == 0f; } }

        // Esconde el dock deslizandolo hacia abajo (como el "ocultar automaticamente" de Mac).
        void HideDock()
        {
            ClosePreview();
            peeking = false;
            if (hiddenFull || slideTarget == 1f) return;
            slideTarget = 1f; StartAnim();
        }

        void ShowDock()
        {
            if (hiddenFull)
            {
                hiddenFull = false; slide = 1f;
                N.ShowWindow(Handle, 8);
                N.SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
            }
            slideTarget = 0f;
            magX = -1f; wasInside = false;
            DoLayout(lastMx, mag);
            StartAnim();
        }

        // Una ventana (que no es el escritorio) toca la zona del dock.
        bool Covered()
        {
            IntPtr fg = N.GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == Handle || N.IsIconic(fg) || !N.IsWindowVisible(fg)) return false;
            string c = N.ClassOf(fg);
            if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd" ||
                c == "Windows.UI.Core.CoreWindow" || c == "XamlExplorerHostIslandWindow" || c == "#32768" ||
                c == "TopLevelWindowForOverflowXamlIsland" || c.StartsWith("WindowsForms10")) return false;
            uint pid; N.GetWindowThreadProcessId(fg, out pid);
            if (pid == myPid) return false;
            N.RECT r; N.GetWindowRect(fg, out r);
            float left = winX + all[0].X - PadX, right = winX + all[all.Count - 1].X + all[all.Count - 1].W + PadX;
            float top = winY + BarTop;
            return r.bottom > top && r.top < winY + H && r.right > left && r.left < right;
        }

        // El timer de 120 ms solo hace falta si hay algo que vigilar: auto-ocultar, esquinas o barra.
        void UpdateEdgeTimer()
        {
            if (restored) return;
            bool need = cfg.HotCorners || (cfg.AutoHide && !cfg.ReserveSpace) || (cfg.MenuBarAutoHide && bar != null);
            if (need) { if (!edge.Enabled) edge.Start(); }
            else if (edge.Enabled) edge.Stop();
        }

        // Cada 120 ms: decide si el dock se ve, se esconde o se asoma al llevar el mouse al borde inferior.
        void EdgeTick(object sender, EventArgs e)
        {
            if (restored || all.Count == 0) return;
            if (fullApp) return;
            if (menuOpen || dragging || stackPopup != null ||
                (preview != null && preview.IsUnderPointer(Cursor.Position))) return;
            if (cfg.HotCorners && !fullApp) corners.Tick(Screen.PrimaryScreen, bar != null);
            bool hide = fullApp || (cfg.AutoHide && !cfg.ReserveSpace && Covered());
            Point c = Cursor.Position;
            var b = Screen.PrimaryScreen.Bounds;
            if (bar != null && cfg.MenuBarAutoHide && !fullApp) bar.AutoTick(c, b);
            bool inX = c.X >= winX && c.X <= winX + W;
            float zoneTop = winY + BarTop - (mag > 0.05f ? Ic * (MaxMag - 1f) : 0f) - 10 * s;
            bool overZone = inX && c.Y >= zoneTop;

            if (!hide)
            {
                peeking = false;
                if (!DockShown) ShowDock();
                return;
            }
            if (!DockShown)
            {
                if (inX && c.Y >= b.Bottom - 2) { peeking = true; leaveSince = DateTime.MaxValue; ShowDock(); }
                return;
            }
            if (overZone) { peeking = true; leaveSince = DateTime.MaxValue; return; }
            if (leaveSince == DateTime.MaxValue) { leaveSince = DateTime.Now; return; }
            if ((DateTime.Now - leaveSince).TotalMilliseconds > (peeking ? 600 : 0)) { leaveSince = DateTime.MaxValue; HideDock(); }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x02B1 || m.Msg == 0x02E0 || m.Msg == 0x007E)
            {
                previewDismissedAt = Cursor.Position;
                ClosePreview();
                if (m.Msg == 0x02E0 && IsHandleCreated && !restored)
                    BeginInvoke(new Action(() => { UpdateDockScale(); Relayout(); }));
            }
            if (m.Msg == 0x0113 && m.WParam.ToInt64() == (long)PreviewTimerId.ToUInt64())
            {
                if (previewHover)
                {
                    long remaining = previewDeadline - Stopwatch.GetTimestamp();
                    if (remaining <= 0) ShowPreview();
                    else if (N.SetTimer(Handle, PreviewTimerId,
                        (uint)Math.Max(10, (remaining * 1000 + Stopwatch.Frequency - 1) / Stopwatch.Frequency),
                        IntPtr.Zero) == UIntPtr.Zero) ClosePreview();
                }
                return;
            }
            if (m.Msg == 0x0312 && m.WParam.ToInt32() == 7) { Spotlight.Toggle(light, s); return; }
            if (msgFinder != 0 && m.Msg == msgFinder) { Finder.OpenOrFocus(light, s); return; }
            if (msgSpot != 0 && m.Msg == msgSpot) { Spotlight.Toggle(light, s); return; }
            if (msgTrim != 0 && m.Msg == msgTrim)
            {
                Program.TrimMemory();
                return;
            }
            if (m.Msg == appBarMsg && appBarMsg != 0)
            {
                if (m.WParam.ToInt32() == 1) SetAppBarPos();
            }
            else if (m.Msg == taskbarCreatedMsg && taskbarCreatedMsg != 0)
            {
                BeginInvoke(new Action(() =>
                {
                    DiscardTrayState();
                    RestoreStaleTaskbar();
                    if (!fullApp) HideTaskbarIfShown();
                    if (appBarOn) { appBarOn = false; RegisterAppBar(); }
                    if (bar != null) bar.ReRegister();
                }));
            }
            else if (m.Msg == 0x0011 || m.Msg == 0x0016)
            {
                SafeRestore();
            }
            base.WndProc(ref m);
        }

        // HWND del dock cacheado para el camino critico: CriticalRestore nunca accede a la
        // propiedad Handle (puede crear el handle o colgarse desde un hilo que no es de UI).
        IntPtr cachedHwnd;
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            cachedHwnd = Handle;
        }

        // Restauraciones criticas, seguras desde cualquier hilo (ProcessExit / UnhandledException):
        // solo P/Invoke directos y datos cacheados con antelacion (HWND del dock, HWND de las barras
        // en trayMain/trayAll, estado original del autohide en savedAutoHide). No toca formularios,
        // controles, timers ni propiedades de WinForms; no enumera ventanas. Cada paso va en su
        // propio try/catch y se ignora su error. Idempotente via Interlocked: estado 2 = completa;
        // si algo falla vuelve a 0 para permitir reintentar la restauracion incompleta.
        int criticalState;
        public void CriticalRestore()
        {
            if (System.Threading.Interlocked.CompareExchange(ref criticalState, 1, 0) != 0) return;
            bool ok = true;
            try { if (!TrayCritical()) ok = false; } catch { ok = false; }
            try { DisplayBrightness.RestoreGamma(); } catch { ok = false; }
            try { if (cachedHwnd != IntPtr.Zero) UnregisterHotKey(cachedHwnd, 7); } catch { ok = false; }
            try { if (locHook != IntPtr.Zero) { N.UnhookWinEvent(locHook); locHook = IntPtr.Zero; } } catch { ok = false; }
            try { if (trayShowHook != IntPtr.Zero) { N.UnhookWinEvent(trayShowHook); trayShowHook = IntPtr.Zero; } } catch { ok = false; }
            try { if (trayHideHook != IntPtr.Zero) { N.UnhookWinEvent(trayHideHook); trayHideHook = IntPtr.Zero; } } catch { ok = false; }
            try { if (!UnregisterAppBarCritical()) ok = false; } catch { ok = false; }
            criticalState = ok ? 2 : 0;
        }

        // Devuelve la barra de Windows desde el camino critico, con datos cacheados (sin Trays()).
        bool TrayCritical()
        {
            bool ok = true;
            // estilos guardados por barra: datos ya cacheados, solo se comprueba que el HWND siga vivo
            lock (trayStateLock)
            {
                foreach (var t in trayStates)
                {
                    if (!t.Styled) continue;
                    try
                    {
                        if (!N.IsWindow(t.H)) continue;
                        N.SetWindowLong(t.H, GWL_EXSTYLE, t.Ex);
                        if (!t.AddedLayered) N.SetLayeredWindowAttributes(t.H, t.CrKey, t.Alpha, t.Flags);
                    }
                    catch { ok = false; }
                }
                trayStates.Clear();
            }
            WriteMarker();
            // autohide: estado original guardado en SetupTaskbar
            try
            {
                if (haveSavedState && !savedAutoHide)
                {
                    var abd = new N.APPBARDATA(); abd.cbSize = Marshal.SizeOf(typeof(N.APPBARDATA));
                    abd.hWnd = N.FindWindow("Shell_TrayWnd", null);
                    if (abd.hWnd != IntPtr.Zero)
                    {
                        int state = (int)N.SHAppBarMessage(4, ref abd);
                        abd.lParam = new IntPtr(state & ~1); N.SHAppBarMessage(10, ref abd);
                    }
                    markerAutoHide = false;
                    WriteMarker();
                }
            }
            catch { ok = false; }
            // barras visibles otra vez, con la lista cacheada de HWND (sin enumerar ventanas)
            try
            {
                if (trayMain != IntPtr.Zero && N.IsWindow(trayMain)) N.ShowWindow(trayMain, 5);
                lock (trayStateLock) { foreach (var h in trayAll) if (N.IsWindow(h)) N.ShowWindow(h, 5); }
            }
            catch { ok = false; }
            return ok;
        }

        bool UnregisterAppBarCritical()
        {
            if (!appBarOn) return true;
            IntPtr h = cachedHwnd;
            if (h == IntPtr.Zero || !N.IsWindow(h)) { appBarOn = false; return true; }
            var abd = new N.APPBARDATA(); abd.cbSize = Marshal.SizeOf(typeof(N.APPBARDATA)); abd.hWnd = h;
            N.SHAppBarMessage(1, ref abd);
            appBarOn = false;
            return true;
        }

        // Cierre normal en el hilo de interfaz: formularios, recursos y todo lo de WinForms.
        public void SafeRestore()
        {
            if (restored) return;
            restored = true;
            ClosePreview();
            try { AnimOpenStop(); } catch { }
            try { slow.Stop(); fast.Stop(); edge.Stop(); } catch { }
            if (winWatchHandler != null) { try { WinWatch.Remove(winWatchHandler); } catch { } winWatchHandler = null; }
            if (prefHandler != null) { try { SystemEvents.UserPreferenceChanged -= prefHandler; } catch { } prefHandler = null; }
            if (dispHandler != null) { try { SystemEvents.DisplaySettingsChanged -= dispHandler; } catch { } dispHandler = null; }
            if (powerHandler != null) { try { SystemEvents.PowerModeChanged -= powerHandler; } catch { } powerHandler = null; }
            try { if (watcher != null) { watcher.EnableRaisingEvents = false; watcher.Dispose(); watcher = null; } } catch { }
            try { if (pinWatcher != null) { pinWatcher.EnableRaisingEvents = false; pinWatcher.Dispose(); pinWatcher = null; } } catch { }
            if (locHook != IntPtr.Zero) { try { N.UnhookWinEvent(locHook); } catch { } locHook = IntPtr.Zero; }
            StopTrayWatch(); StopTrayEnd(); trayWnd = IntPtr.Zero;
            RestoreTrayHiding();
            if (trayWait != null) { try { trayWait.Stop(); trayWait.Dispose(); } catch { } trayWait = null; }
            if (trayPos != null) { try { trayPos.Stop(); trayPos.Dispose(); } catch { } trayPos = null; }
            if (loweredRefresh) RestoreRefresh();
            try { nap.WakeAll(); } catch { }
            try { StopMacFeatures(); } catch { }
            try { if (bar != null) { bar.Close(); bar = null; } } catch { }
            try { UnregisterAppBar(); } catch { }
            try
            {
                if (haveSavedState && !savedAutoHide)
                {
                    var abd = new N.APPBARDATA(); abd.cbSize = Marshal.SizeOf(typeof(N.APPBARDATA));
                    abd.hWnd = N.FindWindow("Shell_TrayWnd", null);
                    int state = (int)N.SHAppBarMessage(4, ref abd);
                    abd.lParam = new IntPtr(state & ~1); N.SHAppBarMessage(10, ref abd);
                }
            }
            catch { }
            try { if (File.Exists(TaskbarMarker)) File.Delete(TaskbarMarker); } catch { }
            try { foreach (var t in Trays()) N.ShowWindow(t, 5); } catch { }
            DisposeCanvas();
            DisposeRenderCache();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SafeRestore();
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) ClosePreview();
            base.Dispose(disposing);
        }
    }
}
