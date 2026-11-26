// WindowAnimations.cs � MacDock: motor central de animaciones de ventana inspiradas en macOS.
// Tres efectos independientes: Apertura Scale, Minimizacion Genie, Cierre Scale+Fade.
// C# 5 / .NET Framework 4 / x64. Sin dependencias nuevas.
//
// ARQUITECTURA:
//   WindowAnimationManager � singleton central que gestiona capturas, overlays, timers y estado.
//   WindowAnimationConfig  � configuracion sincronizada con Config (AnimVentanas, AnimAbrirScale,
//                            AnimMinimizarGenie, AnimCerrarScale).
//   AnimOverlay            � formulario superpuesto click-through sin foco ni Alt+Tab.
//   AnimState              � estado de una animacion activa (una por HWND y generacion).
//
// RESTRICCIONES RESPETADAS:
//   - No modifica ventanas externas (tamano, estilos, transparencia, estado).
//   - No usa SetParent sobre ventanas externas.
//   - No inyecta DLL.
//   - No usa Application.DoEvents().
//   - Timer compartido ~16 ms solo mientras hay animaciones activas.
//   - Maximas 2 animaciones simultaneas.
//   - Captura en segundo plano, dibujo en hilo de UI.
//   - Si la captura o el overlay no son limpios, cancela y deja que Windows opere normalmente.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace MacDock
{
    // -------------------------------------------------------------------------
    // Configuracion de animaciones
    // -------------------------------------------------------------------------
    class WindowAnimationConfig
    {
        public bool AnimVentanas    = false;   // interruptor general (apagado por defecto)
        public bool AnimAbrirScale  = true;    // apertura scale
        public bool AnimMinGenie    = true;    // minimizacion genie
        public int AnimGenieVel = -1;
        public int GenieDurationMs
        {
            get
            {
                switch (AnimGenieVel)
                {
                    case 0: return 500;
                    case 1: return 350;
                    case 2: return 250;
                    default: return 290;
                }
            }
        }
        public bool AnimCerrarScale = true;    // cierre scale+fade
        public bool Diagnostico     = false;   // logs temporales del motor

        public bool AbrirEnabled   { get { return AnimVentanas && AnimAbrirScale;  } }
        public bool GenieEnabled   { get { return AnimVentanas && AnimMinGenie;    } }
        public bool CerrarEnabled  { get { return AnimVentanas && AnimCerrarScale; } }
    }

    // -------------------------------------------------------------------------
    // Tipo de animacion
    // -------------------------------------------------------------------------
    enum AnimKind { Open, Genie, Close }

    // -------------------------------------------------------------------------
    // Estado de una animacion activa
    // -------------------------------------------------------------------------
    class AnimState : IDisposable
    {
        public IntPtr   Hwnd;
        public uint     Pid;
        public long     Generation;   // para detectar reutilizacion de HWND
        public AnimKind Kind;
        public Stopwatch Clock;      // tiempo monotono: no depende del reloj del sistema
        public bool     Finished;

        // Para Genie: destino en pantalla (centro del icono del dock)
        public Point    GenieTarget;
        public int      GenieDurationMs;

        // Overlay y bitmap de la animacion
        public AnimOverlay Overlay;
        public Bitmap      Snapshot;   // captura de la ventana; propiedad de AnimState
        public Bitmap      Frame;      // lienzo reutilizable del overlay
        public Graphics    FrameG;

        // Dimensiones originales de la ventana al iniciar la animacion
        public Rectangle OrigBounds;

        // Devuelve el lienzo reutilizable, limpio y con el tamano pedido.
        // Evita crear y destruir un Bitmap/Graphics en cada fotograma.
        public Bitmap BeginFrame(int w, int h)
        {
            if (w <= 0 || h <= 0) return null;
            if (Frame == null || Frame.Width != w || Frame.Height != h)
            {
                if (FrameG != null) { try { FrameG.Dispose(); } catch { } FrameG = null; }
                if (Frame != null) { try { Frame.Dispose(); } catch { } Frame = null; }
                Frame = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
                FrameG = Graphics.FromImage(Frame);
                FrameG.InterpolationMode = InterpolationMode.HighQualityBicubic;
                FrameG.SmoothingMode     = SmoothingMode.HighQuality;
                FrameG.PixelOffsetMode   = PixelOffsetMode.HighQuality;
            }
            FrameG.Clear(Color.Transparent);
            return Frame;
        }

        public void Dispose()
        {
            try { if (Overlay  != null) { Overlay.SafeClose(); Overlay = null; } } catch { }
            try { if (Snapshot != null) { Snapshot.Dispose(); Snapshot = null; } } catch { }
            try { if (FrameG   != null) { FrameG.Dispose(); FrameG = null; } } catch { }
            try { if (Frame    != null) { Frame.Dispose(); Frame = null; } } catch { }
            try { if (Clock    != null) { Clock.Stop(); Clock = null; } } catch { }
        }
    }

    // -------------------------------------------------------------------------
    // Metadatos de elegibilidad calculados al capturar (clase, estilos, PID...).
    // Se guardan con el snapshot para poder clasificar un cierre cuando la ventana
    // ya no es visible y no se puede volver a consultar.
    // -------------------------------------------------------------------------
    class WinMeta
    {
        public bool   Eligible;
        public bool   HasCaption;
        public bool   HasOwner;
        public bool   Fullscreen;
        public bool   Popup;
        public string Class = "";
        public string Reason = "";
    }

    // -------------------------------------------------------------------------
    // Cierre pendiente: copia del snapshot tomada al ocultarse/destruirse la
    // ventana. Se confirma a los ~80 ms o cuando el HWND deja de existir.
    // -------------------------------------------------------------------------
    class PendingClose
    {
        public IntPtr    Hwnd;
        public long      Gen;
        public uint      Pid;
        public Rectangle Bounds;
        public Bitmap    Image;
        public AnimOverlay Overlay;    // preview mostrado desde el HIDE (evita el hueco)
        public DateTime  At;
        public bool      FromHide;
    }

    // -------------------------------------------------------------------------
    // Formulario overlay: click-through, sin foco, sin Alt+Tab, sin activacion
    // -------------------------------------------------------------------------
    class AnimOverlay : Form
    {
        // Usamos UpdateLayeredWindow para transparencia por pixel
        const int WS_EX_LAYERED    = 0x80000;
        const int WS_EX_TRANSPARENT= 0x20;
        const int WS_EX_NOACTIVATE = 0x8000000;
        const int WS_EX_TOOLWINDOW = 0x80;
        const int WS_EX_TOPMOST    = 0x8;

        bool _closed;

        public AnimOverlay()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition   = FormStartPosition.Manual;
            ShowInTaskbar   = false;
            BackColor       = Color.Black;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE |
                              WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        // Pinta el bitmap con transparencia por pixel usando UpdateLayeredWindow.
        // bmp debe ser Format32bppPArgb. alpha es 0-255 (opacidad global).
        public void UpdateFrame(Bitmap bmp, Rectangle bounds, byte alpha)
        {
            if (_closed || bmp == null) return;
            try
            {
                IntPtr screenDc = N.GetDC(IntPtr.Zero);
                if (screenDc == IntPtr.Zero) return;
                IntPtr memDc = N.CreateCompatibleDC(screenDc);
                if (memDc == IntPtr.Zero) { N.ReleaseDC(IntPtr.Zero, screenDc); return; }

                IntPtr hbmp = IntPtr.Zero;
                IntPtr hOld = IntPtr.Zero;
                try
                {
                    hbmp = bmp.GetHbitmap(Color.FromArgb(0));
                    hOld = N.SelectObject(memDc, hbmp);

                    var pDst  = new N.POINT { x = bounds.X, y = bounds.Y };
                    var pSrc  = new N.POINT { x = 0, y = 0 };
                    var size  = new N.SIZE  { cx = bounds.Width, cy = bounds.Height };
                    var blend = new N.BLENDFUNCTION
                    {
                        BlendOp             = 0,   // AC_SRC_OVER
                        BlendFlags          = 0,
                        SourceConstantAlpha = alpha,
                        AlphaFormat         = 1    // AC_SRC_ALPHA
                    };
                    N.UpdateLayeredWindow(Handle, screenDc, ref pDst, ref size, memDc, ref pSrc, 0, ref blend, 2);
                }
                finally
                {
                    if (hOld  != IntPtr.Zero) N.SelectObject(memDc, hOld);
                    if (hbmp  != IntPtr.Zero) N.DeleteObject(hbmp);
                    N.DeleteDC(memDc);
                    N.ReleaseDC(IntPtr.Zero, screenDc);
                }
            }
            catch { }
        }

        public void SafeClose()
        {
            if (_closed) return;
            _closed = true;
            try { Hide(); }  catch { }
            try { Close(); } catch { }
            try { Dispose(); } catch { }
        }
    }

    // -------------------------------------------------------------------------
    // WindowAnimationManager � singleton central
    // -------------------------------------------------------------------------
    static class WindowAnimationManager
    {
        // ----- API adicional necesaria -----
        [DllImport("dwmapi.dll")]
        static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int val, int size);
        [DllImport("dwmapi.dll")]
        static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out N.RECT val, int size);
        [DllImport("user32.dll")]
        static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);
        [DllImport("user32.dll")]
        static extern IntPtr GetWindowDC(IntPtr hwnd);
        [DllImport("user32.dll")]
        static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("gdi32.dll")]
        static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);
        [DllImport("gdi32.dll")]
        static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")]
        static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")]
        static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")]
        static extern bool DeleteObject(IntPtr obj);
        [DllImport("user32.dll")]
        static extern bool GetClientRect(IntPtr hwnd, out N.RECT rect);
        [DllImport("gdi32.dll")]
        static extern bool StretchBlt(IntPtr hdcDest, int nXOriginDest, int nYOriginDest, int nWidthDest, int nHeightDest, IntPtr hdcSrc, int nXOriginSrc, int nYOriginSrc, int nWidthSrc, int nHeightSrc, uint dwRop);
        [DllImport("gdi32.dll")]
        static extern int SetStretchBltMode(IntPtr hdc, int iStretchMode);
        [DllImport("user32.dll")]
        static extern int GetWindowLong(IntPtr hwnd, int idx);
        const uint SRCCOPY = 0x00CC0020;
        const int HALFTONE = 4;

        const int DWMWA_CLOAKED          = 14;
        const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        const int GWL_STYLE              = -16;
        const int GWL_EXSTYLE            = -20;
        const int WS_CHILD               = 0x40000000;
        const int WS_POPUP               = unchecked((int)0x80000000);
        const int WS_CAPTION             = 0x00C00000;
        const int WS_EX_TOOLWINDOW       = 0x80;
        const int WS_EX_TRANSPARENT      = 0x20;
        const int WS_EX_NOACTIVATE       = 0x8000000;
        static readonly uint _myPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
        const int MAX_CONCURRENT         = 2;
        const int TIMER_INTERVAL_MS      = 16;

        // Duraciones de las animaciones (ms)
        const double DUR_OPEN  = 200.0;
        const double DUR_CLOSE = 160.0;

        // Parametros de apertura
        const float OPEN_SCALE_START   = 0.88f;
        const float OPEN_ALPHA_START   = 210f;    // ~82 %
        // Parametros de cierre
        const float CLOSE_SCALE_END    = 0.92f;
        // Segmentos Genie
        const int   GENIE_SEGMENTS     = 30;
        // Cache de captura: tamano maximo y tiempo maximo de reutilizacion (ms)
        const int   SNAP_MAX_W         = 960;
        const int   SNAP_MAX_H         = 540;
        const double SNAP_MAX_AGE_MS   = 2000.0;
        const double SNAP_REFRESH_MS   = 500.0;   // refresco maximo con la misma ventana

        // ----- Estado global -----
        static readonly object _lock = new object();
        static WindowAnimationConfig _cfg = new WindowAnimationConfig();
        static readonly List<AnimState> _active = new List<AnimState>(MAX_CONCURRENT);
        static System.Windows.Forms.Timer _timer;
        static readonly HashSet<System.Windows.Forms.Timer> _deferredTimers = new HashSet<System.Windows.Forms.Timer>();
        static Control _uiControl;   // cualquier control del hilo de UI para BeginInvoke

        // ----- Cache de captura de la ventana de primer plano -----
        static IntPtr   _snapHwnd;
        static uint     _snapPid;
        static Bitmap   _snapBmp;
        static DateTime _snapAt = DateTime.MinValue;
        static Rectangle _snapBounds;
        static long     _snapGen;
        static WinMeta  _snapMeta;

        // Cierres pendientes de confirmar (HWND -> copia del snapshot + metadatos)
        static readonly Dictionary<IntPtr, PendingClose> _pendingClose = new Dictionary<IntPtr, PendingClose>();
        const int PENDING_CLOSE_MS  = 400;      // confirmacion de cierre: si sigue viva (bandeja) se cancela
        const int PENDING_MAX_MS    = 3000;    // vida maxima de una entrada

        // Contador de generacion por HWND (para detectar reutilizacion)
        static readonly Dictionary<IntPtr, long> _generations = new Dictionary<IntPtr, long>();
        static long _genCounter;

        // Seguimiento para distinguir minimizacion de cierre
        // hwnd -> tiempo del ultimo MINIMIZESTART
        static readonly Dictionary<IntPtr, DateTime> _minimizeStart = new Dictionary<IntPtr, DateTime>();

        // Thread de captura: solo existe mientras AnimVentanas esta activo.
        static Thread _captureThread;
        static volatile int _captureGen;
        static AutoResetEvent _captureRequest;
        static int _captureWorkers;
        static IntPtr _captureTarget;                 // peticion puntual (show/location)
        static IntPtr _foreground;                    // ultima ventana en primer plano
        static bool   _fgEligible;                    // elegibilidad cacheada de _foreground
        static int    _fgRetries;                     // reintentos de elegibilidad al arrancar
        const int     FG_MAX_RETRIES = 6;
        static DateTime _lastCaptureAt = DateTime.MinValue;

        // Referencia al Dock para obtener info de iconos
        static Func<IntPtr, uint, Point?> _getIconCenter;   // hwnd,pid -> punto central del icono en pantalla

        // -------------------------------------------------------------------------
        // Inicializacion y configuracion
        // -------------------------------------------------------------------------
        public static void Init(Control uiControl, Func<IntPtr, uint, Point?> getIconCenter)
        {
            _uiControl      = uiControl;
            _getIconCenter  = getIconCenter;
            // El hilo de captura no se crea aqui: ApplyConfig lo arranca solo si AnimVentanas
            // esta activo, y lo detiene al desactivarlo.
        }

        public static WindowAnimationConfig Config { get { return _cfg; } }

        public static void ApplyConfig(WindowAnimationConfig cfg)
        {
            lock (_lock)
            {
                _cfg = cfg;
                if (!NeedsForegroundSnapshot(cfg) && cfg.AnimVentanas) DropSnapshot();
            }
            if (cfg.AnimVentanas)
            {
                StartCaptureThread();
                // Sembrar el primer plano actual para no depender de un cambio de ventana.
                IntPtr fg = N.GetForegroundWindow();
                if (fg != IntPtr.Zero && NeedsForegroundSnapshot(cfg))
                {
                    _foreground = fg;
                    _fgEligible = false;
                    _fgRetries = 0;
                    try { _fgEligible = EvaluateWindow(fg).Eligible; } catch { }
                    if (_fgEligible) RequestCapture(fg);
                }
                WakeCapture();
            }
            else
            {
                StopCaptureThread();
                CancelAll();
                lock (_lock) { DropSnapshot(); _foreground = IntPtr.Zero; _fgEligible = false; }
            }
            LogLifecycle("config");
        }

        public static void Shutdown()
        {
            CancelAll();
            StopCaptureThread();
            lock (_lock) { DropSnapshot(); _foreground = IntPtr.Zero; _fgEligible = false; }
            LogLifecycle("cierre");
        }

        static void LogLifecycle(string action)
        {
            if (!_cfg.Diagnostico) return;
            lock (_lock)
            {
                Program.Log("Anim: " + action + " hiloCaptura=" + _captureWorkers +
                    " timers=" + ((_timer != null ? 1 : 0) + _deferredTimers.Count) +
                    " snapshot=" + (_snapBmp != null ? 1 : 0) + " overlays=" + (_active.Count + _pendingClose.Count));
            }
        }

        static bool NeedsForegroundSnapshot(WindowAnimationConfig cfg)
        {
            return cfg.GenieEnabled || cfg.CerrarEnabled;
        }

        // -------------------------------------------------------------------------
        // Punto de entrada: evento de WinWatch
        // -------------------------------------------------------------------------
        // Llamar desde el hilo de UI (via BeginInvoke) con el evento y el HWND.
        public static void OnWindowEvent(uint ev, IntPtr hwnd)
        {
            try
            {
                var cfg = _cfg;
                if (!cfg.AnimVentanas) return;
                if (hwnd == IntPtr.Zero) return;

                switch (ev)
                {
                    // Apertura / restauracion
                    case 0x8002:   // EVENT_OBJECT_SHOW
                        CancelPendingClose(hwnd);          // reaparecer cancela un cierre pendiente
                        if (cfg.AbrirEnabled) HandleShow(hwnd);
                        break;
                    case 0x0017:   // EVENT_SYSTEM_MINIMIZEEND (restaurar)
                        CancelPendingClose(hwnd);
                        if (cfg.AbrirEnabled) HandleRestored(hwnd);
                        break;

                    // Minimizacion
                    case 0x0016:   // EVENT_SYSTEM_MINIMIZESTART
                        // Registrar que esta ventana inicio minimizacion
                        lock (_lock) { _minimizeStart[hwnd] = DateTime.UtcNow; }
                        CancelPendingClose(hwnd);
                        if (cfg.GenieEnabled) HandleMinimize(hwnd);
                        break;

                    // Cierre / destruccion
                    case 0x8003:   // EVENT_OBJECT_HIDE
                        if (cfg.CerrarEnabled) HandleHide(hwnd);
                        break;
                    case 0x8001:   // EVENT_OBJECT_DESTROY
                        HandleDestroy(hwnd);
                        break;
                    case 0x8000:   // EVENT_OBJECT_CREATE
                        // El HWND puede reutilizarse: una creacion invalida pendientes
                        CancelPendingClose(hwnd);
                        BumpGen(hwnd);
                        break;
                }
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        // -------------------------------------------------------------------------
        // Primer plano y cambios de geometria adelantan el refresco periodico.
        // -------------------------------------------------------------------------
        public static void OnForegroundChanged(IntPtr hwnd)
        {
            _foreground = hwnd;
            _fgEligible = false;
            _fgRetries  = 0;
            if (hwnd != IntPtr.Zero && NeedsForegroundSnapshot(_cfg))
            {
                WinMeta m = null;
                try { m = EvaluateWindow(hwnd); } catch { }
                _fgEligible = m != null && m.Eligible;
                if (_cfg.Diagnostico)
                    Program.Log("Anim: primer plano hwnd=" + hwnd + " cls=" + N.ClassOf(hwnd) +
                                " elegible=" + _fgEligible + " motivo=" + (m != null ? m.Reason : "?"));
                if (_fgEligible) RequestCapture(hwnd);
            }
            WakeCapture();
        }

        // Cambio de posicion/tamano de una ventana (EVENT_OBJECT_LOCATIONCHANGE).
        public static void OnLocationChanged(IntPtr hwnd)
        {
            if (!NeedsForegroundSnapshot(_cfg)) return;
            if (hwnd == IntPtr.Zero || hwnd != _foreground || !_fgEligible) return;
            RequestCapture(hwnd);
        }

        static void RequestCapture(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            lock (_lock)
            {
                if (_captureRequest == null) return;
                _captureTarget = hwnd;
                _captureRequest.Set();
            }
        }

        static void WakeCapture()
        {
            lock (_lock) { if (_captureRequest != null) _captureRequest.Set(); }
        }

        static void StartCaptureThread()
        {
            lock (_lock)
            {
                if (_captureThread != null) return;
                int gen = ++_captureGen;
                var signal = new AutoResetEvent(false);
                _captureRequest = signal;
                _captureThread = new Thread(() =>
                {
                    try { CaptureLoop(gen, signal); }
                    finally
                    {
                        lock (_lock)
                        {
                            _captureWorkers--;
                            if (_captureThread == Thread.CurrentThread)
                            {
                                _captureThread = null;
                                _captureRequest = null;
                            }
                            signal.Dispose();
                        }
                        LogLifecycle("hilo terminado");
                    }
                }) { IsBackground = true, Name = "MacDock-Capture", Priority = ThreadPriority.BelowNormal };
                _captureWorkers++;
                _captureThread.Start();
            }
        }

        static void StopCaptureThread()
        {
            lock (_lock)
            {
                _captureGen++;
                _captureTarget = IntPtr.Zero;
                if (_captureRequest != null) _captureRequest.Set();
                _captureRequest = null;
                _captureThread = null;
            }
        }

        // Una ventana puede cambiar su contenido sin cambiar de posicion o de foco.
        // Se conserva una copia reciente para los eventos que llegan despues de ocultarla.
        static void CaptureLoop(int gen, AutoResetEvent signal)
        {
            while (gen == _captureGen)
            {
                IntPtr target;
                lock (_lock)
                {
                    if (gen != _captureGen) break;
                    target = _captureTarget;
                    _captureTarget = IntPtr.Zero;
                }

                bool active  = _cfg.AnimVentanas;
                bool overlay = AnyOverlayActive();
                if (target != IntPtr.Zero && active && !overlay)
                {
                    bool ok = false;
                    try { ok = CaptureWindow(target, gen); } catch { }
                    if (gen == _captureGen && target == _foreground)
                    {
                        _lastCaptureAt = DateTime.UtcNow;
                        _fgEligible = ok;
                        if (ok) _fgRetries = 0;
                    }
                }

                int wait = Timeout.Infinite;
                if (active && overlay)
                    wait = 200;                       // reanudar en cuanto termine el overlay
                else if (NeedsForegroundSnapshot(_cfg) && _foreground != IntPtr.Zero)
                {
                    if (_fgEligible)
                    {
                        int since = _lastCaptureAt == DateTime.MinValue
                                    ? int.MaxValue
                                    : (int)(DateTime.UtcNow - _lastCaptureAt).TotalMilliseconds;
                        if (since >= (int)SNAP_REFRESH_MS)
                        {
                            // El refresco periodico de la ventana usa SNAP_REFRESH_MS.
                            lock (_lock)
                            {
                                if (gen != _captureGen) break;
                                if (_captureTarget == IntPtr.Zero) _captureTarget = _foreground;
                            }
                            continue;
                        }
                        wait = Math.Max(10, (int)SNAP_REFRESH_MS - since);
                    }
                    else if (_fgRetries < FG_MAX_RETRIES)
                    {
                        // La ventana pudo no estar lista al llegar el evento (sin tamano, sin
                        // mostrar): se reintenta unas pocas veces y luego se duerme sin plazo.
                        _fgRetries++;
                        lock (_lock)
                        {
                            if (gen != _captureGen) break;
                            if (_captureTarget == IntPtr.Zero) _captureTarget = _foreground;
                        }
                        wait = 500;
                    }
                }

                signal.WaitOne(wait);
            }
        }

        static bool CaptureWindow(IntPtr hwnd, int gen)
        {
            if (!N.IsWindow(hwnd) || !N.IsWindowVisible(hwnd) || N.IsIconic(hwnd)) return false;

            // Comprobar que no esta cloaked
            int cloaked = 0;
            DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out cloaked, sizeof(int));
            if (cloaked != 0) return false;

            // Obtener limites en el mismo espacio de coordenadas que este proceso.
            // GetWindowRect respeta el DPI virtualizado; DWM puede devolver pixeles fisicos.
            N.RECT wr;
            Rectangle bounds;
            if (N.GetWindowRect(hwnd, out wr))
                bounds = new Rectangle(wr.left, wr.top, wr.right - wr.left, wr.bottom - wr.top);
            else
            {
                N.RECT fr;
                if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out fr, Marshal.SizeOf(typeof(N.RECT))) != 0) return false;
                bounds = new Rectangle(fr.left, fr.top, fr.right - fr.left, fr.bottom - fr.top);
            }

            if (bounds.Width <= 0 || bounds.Height <= 0) return false;

            uint pid;
            N.GetWindowThreadProcessId(hwnd, out pid);

            // Metadatos de elegibilidad: se guardan con la captura para poder clasificar un
            // cierre posterior aunque la ventana ya no sea visible ni exista.
            WinMeta meta = EvaluateWindow(hwnd);
            if (!meta.Eligible) return false;

            // Escalar si es mayor que el maximo
            int w = bounds.Width, h = bounds.Height;
            if (w > SNAP_MAX_W || h > SNAP_MAX_H)
            {
                float scale = Math.Min((float)SNAP_MAX_W / w, (float)SNAP_MAX_H / h);
                w = Math.Max(1, (int)(w * scale));
                h = Math.Max(1, (int)(h * scale));
            }

            Bitmap bmp = null;
            bool ok = false;

            // Intentar capturar desde el DC de pantalla con StretchBlt: escala la ventana
            // completa al tamano w x h sin crear un bitmap intermedio a tamano completo.
            try
            {
                IntPtr screenDc = N.GetDC(IntPtr.Zero);
                if (screenDc != IntPtr.Zero)
                {
                    try
                    {
                        bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
                        using (var g = Graphics.FromImage(bmp))
                        {
                            IntPtr destDc = g.GetHdc();
                            try
                            {
                                SetStretchBltMode(destDc, HALFTONE);
                                ok = StretchBlt(destDc, 0, 0, w, h, screenDc, bounds.X, bounds.Y,
                                                bounds.Width, bounds.Height, SRCCOPY);
                            }
                            finally { g.ReleaseHdc(destDc); }
                        }
                        if (!ok) { bmp.Dispose(); bmp = null; }
                    }
                    finally { N.ReleaseDC(IntPtr.Zero, screenDc); }
                }
            }
            catch
            {
                if (bmp != null) { bmp.Dispose(); bmp = null; }
                ok = false;
            }

            // Fallback: PrintWindow si CopyFromScreen falla
            if (!ok)
            {
                try
                {
                    var tmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppPArgb);
                    using (var g = Graphics.FromImage(tmp))
                    {
                        IntPtr hdc = g.GetHdc();
                        bool pw = PrintWindow(hwnd, hdc, 2);  // PW_RENDERFULLCONTENT
                        g.ReleaseHdc(hdc);
                        if (pw)
                        {
                            if (w != bounds.Width || h != bounds.Height)
                            {
                                bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
                                using (var gs = Graphics.FromImage(bmp))
                                {
                                    gs.InterpolationMode = InterpolationMode.HighQualityBicubic;
                                    gs.DrawImage(tmp, 0, 0, w, h);
                                }
                                tmp.Dispose();
                            }
                            else { bmp = tmp; tmp = null; }
                            ok = true;
                        }
                        else { tmp.Dispose(); }
                    }
                }
                catch
                {
                    if (bmp != null) { bmp.Dispose(); bmp = null; }
                }
            }

            if (!ok || bmp == null) return false;
            if (LooksBlank(bmp)) { bmp.Dispose(); return false; }

            lock (_lock)
            {
                if (gen != _captureGen || !_cfg.AnimVentanas || !N.IsWindow(hwnd) || N.IsIconic(hwnd))
                {
                    bmp.Dispose();
                    return false;
                }
                DropSnapshot();
                _snapHwnd   = hwnd;
                _snapPid    = pid;
                _snapBmp    = bmp;
                _snapAt     = DateTime.UtcNow;
                _snapBounds = new Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height);
                _snapGen    = GetGen(hwnd);
                _snapMeta   = meta;
            }
            if (_cfg.Diagnostico) Program.Log("Anim: captura hwnd=" + hwnd + " " + bounds.Width + "x" + bounds.Height);
            return true;
        }

        // Rechaza capturas totalmente negras (PrintWindow en apps aceleradas por GPU,
        // ventanas protegidas o minimizadas a medias).
        static bool LooksBlank(Bitmap bmp)
        {
            try
            {
                int lit = 0, total = 0;
                for (int y = 0; y < 6; y++)
                    for (int x = 0; x < 6; x++)
                    {
                        int px = Math.Min(bmp.Width - 1, (x + 1) * bmp.Width / 7);
                        int py = Math.Min(bmp.Height - 1, (y + 1) * bmp.Height / 7);
                        Color c = bmp.GetPixel(px, py);
                        total++;
                        if (c.R + c.G + c.B > 24) lit++;
                    }
                return lit * 100 < total * 3;
            }
            catch { return false; }
        }

        // Obtiene (y crea si necesario) el numero de generacion de un HWND
        static long GetGen(IntPtr hwnd)
        {
            long g;
            if (!_generations.TryGetValue(hwnd, out g)) { g = ++_genCounter; _generations[hwnd] = g; }
            return g;
        }

        static void BumpGen(IntPtr hwnd)
        {
            lock (_lock) { _generations[hwnd] = ++_genCounter; }
        }

        static void DropSnapshot()
        {
            if (_snapBmp != null) { try { _snapBmp.Dispose(); } catch { } _snapBmp = null; }
            _snapHwnd = IntPtr.Zero; _snapAt = DateTime.MinValue;
        }

        // Obtiene una copia del snapshot valida para el HWND dado (dentro del tiempo maximo)
        static Bitmap CloneSnapshot(IntPtr hwnd, out Rectangle bounds, out uint pid, out long gen, out WinMeta meta)
        {
            bounds = Rectangle.Empty; pid = 0; gen = 0; meta = null;
            lock (_lock)
            {
                if (_snapHwnd != hwnd || _snapBmp == null) return null;
                if ((DateTime.UtcNow - _snapAt).TotalMilliseconds > SNAP_MAX_AGE_MS) return null;
                try
                {
                    bounds = _snapBounds;
                    pid    = _snapPid;
                    gen    = _snapGen;
                    meta   = _snapMeta;
                    return (Bitmap)_snapBmp.Clone();
                }
                catch { return null; }
            }
        }

        static Bitmap CloneSnapshot(IntPtr hwnd, out Rectangle bounds)
        {
            uint pid; long gen; WinMeta meta;
            return CloneSnapshot(hwnd, out bounds, out pid, out gen, out meta);
        }

        // -------------------------------------------------------------------------
        // Handlers de eventos
        // -------------------------------------------------------------------------
        static void HandleShow(IntPtr hwnd)
        {
            if (!_cfg.AbrirEnabled) return;
            // Descartar eventos de ventanas del sistema, propios del dock, etc.
            if (!IsAnimatableWindow(hwnd)) return;
            // No duplicar con una animacion de restauracion (MINIMIZEEND)
            if (AlreadyAnimating(hwnd)) return;
            if (_active.Count >= MAX_CONCURRENT) return;

            // Solicitar captura en segundo plano
            uint pid;
            N.GetWindowThreadProcessId(hwnd, out pid);
            RequestCapture(hwnd);

            // Esperar brevemente la captura antes de crear el overlay (maximo ~80ms sin bloquear UI)
            // Lo hacemos diferido: programamos la animacion tras un pequeno retardo
            var uiCtl = _uiControl;
            if (uiCtl == null || uiCtl.IsDisposed) return;

            var hwndCopy = hwnd;
            var pidCopy  = pid;
            ScheduleOnce(60, () => LaunchOpenAnim(hwndCopy, pidCopy));
        }

        static void HandleRestored(IntPtr hwnd)
        {
            if (!_cfg.AnimVentanas) return;
            if (!IsAnimatableWindow(hwnd)) return;
            if (AlreadyAnimating(hwnd)) return;
            if (_active.Count >= MAX_CONCURRENT) return;

            uint pid;
            N.GetWindowThreadProcessId(hwnd, out pid);
            RequestCapture(hwnd);

            var uiCtl = _uiControl;
            if (uiCtl == null || uiCtl.IsDisposed) return;
            var hwndCopy = hwnd; var pidCopy = pid;
            ScheduleOnce(60, () => LaunchOpenAnim(hwndCopy, pidCopy));
        }

        static void LaunchOpenAnim(IntPtr hwnd, uint pid)
        {
            try
            {
                if (!_cfg.AbrirEnabled) return;
                if (!N.IsWindow(hwnd) || !N.IsWindowVisible(hwnd)) return;
                if (AlreadyAnimating(hwnd)) return;
                if (_active.Count >= MAX_CONCURRENT) return;

                // Obtener limites reales
                Rectangle origBounds = GetWindowBounds(hwnd);
                if (origBounds.Width < 10 || origBounds.Height < 10) return;

                // Sin captura valida no hay animacion: un lienzo opaco produciria un
                // rectangulo negro sobre la ventana real.
                Rectangle snapBounds;
                Bitmap snap = CloneSnapshot(hwnd, out snapBounds);
                if (snap == null) return;

                // Crear overlay
                AnimOverlay overlay;
                try { overlay = new AnimOverlay(); }
                catch { snap.Dispose(); return; }

                overlay.Bounds = origBounds;

                var st = new AnimState
                {
                    Hwnd       = hwnd,
                    Pid        = pid,
                    Generation = GetOrBumpGen(hwnd),
                    Kind       = AnimKind.Open,
                    Clock      = Stopwatch.StartNew(),
                    OrigBounds = origBounds,
                    Overlay    = overlay,
                    Snapshot   = snap
                };

                try { overlay.Show(); } catch { st.Dispose(); return; }

                lock (_lock) { _active.Add(st); }
                EnsureTimer();
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        static void HandleMinimize(IntPtr hwnd)
        {
            if (!_cfg.GenieEnabled) return;
            if (_cfg.Diagnostico) Program.Log("Anim: MINIMIZESTART hwnd=" + hwnd + " cls=" + N.ClassOf(hwnd));
            if (AlreadyAnimating(hwnd)) return;
            if (_active.Count >= MAX_CONCURRENT) return;

            // La ventana ya esta minimizada cuando llega MINIMIZESTART: no se puede volver a
            // consultar su visibilidad. Se usan la copia y los metadatos tomados al capturarla.
            Rectangle snapBounds; uint pid; long gen; WinMeta meta;
            Bitmap snap = CloneSnapshot(hwnd, out snapBounds, out pid, out gen, out meta);
            if (snap == null) { if (_cfg.Diagnostico) Program.Log("Anim: genie sin snapshot valido (hwnd=" + hwnd + ")"); return; }
            if (meta == null || !meta.Eligible) { snap.Dispose(); if (_cfg.Diagnostico) Program.Log("Anim: genie descartado (no elegible)"); return; }

            // Buscar el icono de destino en el dock
            Point? iconCenter = null;
            if (_getIconCenter != null)
            {
                try { iconCenter = _getIconCenter(hwnd, pid); } catch { }
            }
            if (!iconCenter.HasValue) { snap.Dispose(); return; }

            // Obtener limites originales de la ventana
            Rectangle origBounds = snapBounds;
            if (origBounds.Width < 10) { snap.Dispose(); return; }

            AnimOverlay overlay;
            try { overlay = new AnimOverlay(); }
            catch { snap.Dispose(); return; }

            overlay.Bounds = origBounds;

            var st = new AnimState
            {
                Hwnd        = hwnd,
                Pid         = pid,
                Generation  = GetOrBumpGen(hwnd),
                Kind        = AnimKind.Genie,
                GenieDurationMs = _cfg.GenieDurationMs,
                Clock       = Stopwatch.StartNew(),
                OrigBounds  = origBounds,
                GenieTarget = iconCenter.Value,
                Overlay     = overlay,
                Snapshot    = snap
            };

            try { overlay.Show(); } catch { st.Dispose(); return; }

            lock (_lock) { _active.Add(st); }
            EnsureTimer();
        }

        // La ventana se ha ocultado. No se puede volver a consultar su visibilidad: se toma
        // una copia del ultimo snapshot de ese mismo HWND y se guarda como cierre pendiente.
        static void HandleHide(IntPtr hwnd)
        {
            if (!_cfg.CerrarEnabled) return;
            if (WasRecentlyMinimized(hwnd)) return;       // es Genie, no cierre
            if (AlreadyAnimating(hwnd)) return;
            if (AlreadyPendingClose(hwnd)) return;

            PendingClose pc = MakePendingClose(hwnd, true);
            if (pc == null) { if (_cfg.Diagnostico) Program.Log("Anim: HIDE sin copia valida hwnd=" + hwnd); return; }                        // sin captura valida: sin animacion

            // Mostrar ya la copia a opacidad completa: la ventana real acaba de desaparecer y
            // el overlay ocupa su lugar sin dejar un hueco visible mientras se confirma.
            pc.Overlay = ShowClosePreview(pc);

            lock (_lock) { _pendingClose[hwnd] = pc; }
            ConsolidatePending();
            if (_cfg.Diagnostico) Program.Log("Anim: cierre pendiente hwnd=" + hwnd + " overlay=" + (pc.Overlay != null));

            ScheduleOnce(PENDING_CLOSE_MS, () =>
            {
                if (AlreadyPendingClose(hwnd)) ConfirmClose(hwnd, false);
            });
        }

        static void HandleDestroy(IntPtr hwnd)
        {
            // Cancelar animaciones de apertura/genie de esta ventana, pero no un cierre ya
            // en curso (es justo lo que queremos conservar).
            CancelAnimOf(hwnd, true);

            bool pending = AlreadyPendingClose(hwnd);
            if (!pending && _cfg.CerrarEnabled && !WasRecentlyMinimized(hwnd))
            {
                // Destruccion sin HIDE previo: tambien cuenta como posible cierre.
                PendingClose pc = MakePendingClose(hwnd, false);
                if (pc != null) { lock (_lock) { _pendingClose[hwnd] = pc; } pending = true; }
            }
            if (pending) ConfirmClose(hwnd, true);

            // El HWND puede reutilizarse: nueva generacion para la proxima ventana.
            BumpGen(hwnd);
            lock (_lock) { _minimizeStart.Remove(hwnd); }
            lock (_lock) { if (_snapHwnd == hwnd) DropSnapshot(); }
        }

        // Crea una copia independiente del snapshot para clasificarlo como cierre. No exige
        // que la ventana siga visible (en HIDE/DESTROY ya no lo esta): usa los metadatos de
        // elegibilidad guardados al capturar y comprueba HWND, generacion y antiguedad.
        static PendingClose MakePendingClose(IntPtr hwnd, bool fromHide)
        {
            Rectangle bounds; uint pid; long gen; WinMeta meta;
            Bitmap copy = CloneSnapshot(hwnd, out bounds, out pid, out gen, out meta);
            if (copy == null) return null;
            if (meta == null || !meta.Eligible) { copy.Dispose(); return null; }
            return new PendingClose
            {
                Hwnd     = hwnd,
                Gen      = gen,
                Pid      = pid,
                Bounds   = bounds,
                Image    = copy,
                At       = DateTime.UtcNow,
                FromHide = fromHide
            };
        }

        // Confirma el cierre: la ventana desaparecio o sigue oculta sin minimizacion previa.
        static void ConfirmClose(IntPtr hwnd, bool destroyed)
        {
            PendingClose pc = null;
            lock (_lock)
            {
                if (_pendingClose.TryGetValue(hwnd, out pc)) _pendingClose.Remove(hwnd);
            }
            if (pc == null) return;

            bool animate = true;
            try
            {
                if (!_cfg.CerrarEnabled) animate = false;
                if (WasRecentlyMinimized(hwnd)) animate = false;
                if (AlreadyAnimating(hwnd)) animate = false;
                // Si reaparecio o sigue viva oculta (bandeja/hide temporal): cancelar sin animar.
                if (N.IsWindow(hwnd) && (!destroyed || N.IsWindowVisible(hwnd))) animate = false;
                // HWND reutilizado: la generacion ya no coincide.
                if (!destroyed && GetGen(hwnd) != pc.Gen) animate = false;
                if (_active.Count >= MAX_CONCURRENT) animate = false;

                if (_cfg.Diagnostico) Program.Log("Anim: confirmar cierre hwnd=" + hwnd + " destroy=" + destroyed + " animar=" + animate);
                if (animate) StartCloseAnim(pc);
            }
            finally
            {
                if (pc.Image != null) { try { pc.Image.Dispose(); } catch { } pc.Image = null; }
                if (pc.Overlay != null) { try { pc.Overlay.SafeClose(); } catch { } pc.Overlay = null; }
            }
        }

        // Crea y muestra el overlay con la copia a opacidad completa. Devuelve null si no se
        // pudo crear (en ese caso se animara igualmente creandolo al confirmar).
        static AnimOverlay ShowClosePreview(PendingClose pc)
        {
            try
            {
                var overlay = new AnimOverlay();
                overlay.Bounds = pc.Bounds;
                overlay.Show();
                overlay.UpdateFrame(pc.Image, pc.Bounds, 255);
                return overlay;
            }
            catch { return null; }
        }

        static void StartCloseAnim(PendingClose pc)
        {
            AnimOverlay overlay = pc.Overlay;
            if (overlay == null || overlay.IsDisposed)
            {
                try { overlay = new AnimOverlay(); }
                catch { return; }
                overlay.Bounds = pc.Bounds;
                try { overlay.Show(); } catch { return; }
                overlay.UpdateFrame(pc.Image, pc.Bounds, 255);
            }
            pc.Overlay = null;

            var st = new AnimState
            {
                Hwnd       = pc.Hwnd,
                Pid        = pc.Pid,
                Generation = pc.Gen,
                Kind       = AnimKind.Close,
                Clock      = Stopwatch.StartNew(),
                OrigBounds = pc.Bounds,
                Overlay    = overlay,
                Snapshot   = pc.Image
            };
            pc.Image = null;   // la propiedad pasa a AnimState

            lock (_lock) { _active.Add(st); }
            EnsureTimer();
        }

        static bool AlreadyPendingClose(IntPtr hwnd)
        {
            lock (_lock) { return _pendingClose.ContainsKey(hwnd); }
        }

        // Hay overlays visibles (animacion o preview de cierre): no se captura nada.
        static bool AnyOverlayActive()
        {
            lock (_lock) { return _active.Count > 0 || _pendingClose.Count > 0; }
        }

        static void CancelPendingClose(IntPtr hwnd)
        {
            PendingClose pc = null;
            lock (_lock)
            {
                if (_pendingClose.TryGetValue(hwnd, out pc)) _pendingClose.Remove(hwnd);
            }
            if (pc != null && _cfg.Diagnostico) Program.Log("Anim: cierre cancelado hwnd=" + hwnd);
            if (pc != null)
            {
                if (pc.Overlay != null) { try { pc.Overlay.SafeClose(); } catch { } }
                if (pc.Image != null) { try { pc.Image.Dispose(); } catch { } }
            }
        }

        // Libera copias caducadas para que la tabla no crezca nunca.
        static void ConsolidatePending()
        {
            List<PendingClose> dead = null;
            DateTime now = DateTime.UtcNow;
            lock (_lock)
            {
                foreach (var kv in _pendingClose)
                {
                    if ((now - kv.Value.At).TotalMilliseconds > PENDING_MAX_MS)
                    {
                        if (dead == null) dead = new List<PendingClose>();
                        dead.Add(kv.Value);
                    }
                }
                if (dead != null) foreach (var pc in dead) _pendingClose.Remove(pc.Hwnd);
            }
            if (dead != null)
                foreach (var pc in dead)
                {
                    try { if (pc.Overlay != null) pc.Overlay.SafeClose(); } catch { }
                    try { if (pc.Image != null) pc.Image.Dispose(); } catch { }
                }
        }

        // -------------------------------------------------------------------------
        // Timer de animacion (~16 ms por tick)
        // -------------------------------------------------------------------------
        static void EnsureTimer()
        {
            if (!_cfg.AnimVentanas || _timer != null) return;
            _timer = new System.Windows.Forms.Timer { Interval = TIMER_INTERVAL_MS };
            _timer.Tick += OnTick;
            _timer.Start();
        }

        static void ScheduleOnce(int interval, Action action)
        {
            var timer = new System.Windows.Forms.Timer { Interval = interval };
            _deferredTimers.Add(timer);
            timer.Tick += (s, e) =>
            {
                if (!_deferredTimers.Remove(timer)) return;
                timer.Dispose();
                action();
            };
            timer.Start();
        }

        static void OnTick(object sender, EventArgs e)
        {
            List<AnimState> copy;
            lock (_lock) { copy = new List<AnimState>(_active); }

            bool any = false;
            foreach (var st in copy)
            {
                if (st.Finished) continue;
                try { TickAnim(st); } catch (Exception ex) { Program.Log(ex); st.Finished = true; }
                if (!st.Finished) any = true;
            }

            // Eliminar terminadas
            lock (_lock)
            {
                for (int i = _active.Count - 1; i >= 0; i--)
                {
                    if (_active[i].Finished)
                    {
                        try { _active[i].Dispose(); } catch { }
                        _active.RemoveAt(i);
                    }
                }
                any = _active.Count > 0;
            }

            if (!any)
            {
                if (_timer != null) { _timer.Stop(); _timer.Dispose(); _timer = null; }
            }
        }

        static void TickAnim(AnimState st)
        {
            if (st.Clock == null) { st.Finished = true; return; }
            double elapsed = st.Clock.Elapsed.TotalMilliseconds;

            switch (st.Kind)
            {
                case AnimKind.Open:  TickOpen(st, elapsed);  break;
                case AnimKind.Genie: TickGenie(st, elapsed); break;
                case AnimKind.Close: TickClose(st, elapsed); break;
            }
        }

        // ---- Apertura Scale ----
        static void TickOpen(AnimState st, double elapsed)
        {
            if (elapsed >= DUR_OPEN) { st.Finished = true; return; }
            if (st.Overlay == null || st.Overlay.IsDisposed) { st.Finished = true; return; }
            if (st.Snapshot == null) { st.Finished = true; return; }

            float t = (float)(elapsed / DUR_OPEN);
            float ease = EaseOut(t);

            float scale = OPEN_SCALE_START + (1f - OPEN_SCALE_START) * ease;
            byte  alpha = (byte)(OPEN_ALPHA_START + (255 - OPEN_ALPHA_START) * ease);

            Rectangle orig = st.OrigBounds;
            int w = Math.Max(1, (int)(orig.Width  * scale));
            int h = Math.Max(1, (int)(orig.Height * scale));
            int x = (orig.Width  - w) / 2;
            int y = (orig.Height - h) / 2;

            // El overlay conserva el tamano original: el escalado se dibuja centrado
            // sobre un lienzo reutilizable con margenes transparentes.
            Bitmap frame = st.BeginFrame(orig.Width, orig.Height);
            if (frame == null) { st.Finished = true; return; }

            st.FrameG.DrawImage(st.Snapshot, new Rectangle(x, y, w, h));
            st.Overlay.UpdateFrame(frame, orig, alpha);
        }

        // ---- Cierre Scale + Fade ----
        static void TickClose(AnimState st, double elapsed)
        {
            if (elapsed >= DUR_CLOSE) { st.Finished = true; return; }
            if (st.Overlay == null || st.Overlay.IsDisposed) { st.Finished = true; return; }
            if (st.Snapshot == null) { st.Finished = true; return; }

            float t    = (float)(elapsed / DUR_CLOSE);
            float ease = EaseOut(t);

            float scale = 1f - (1f - CLOSE_SCALE_END) * ease;
            byte  alpha = (byte)(255 * (1f - ease));

            Rectangle orig = st.OrigBounds;
            int w = Math.Max(1, (int)(orig.Width  * scale));
            int h = Math.Max(1, (int)(orig.Height * scale));
            int x = (orig.Width  - w) / 2;
            int y = (orig.Height - h) / 2;

            Bitmap frame = st.BeginFrame(orig.Width, orig.Height);
            if (frame == null) { st.Finished = true; return; }

            st.FrameG.DrawImage(st.Snapshot, new Rectangle(x, y, w, h));
            st.Overlay.UpdateFrame(frame, orig, alpha);
        }

        // ---- Minimizacion Genie ----
        static void TickGenie(AnimState st, double elapsed)
        {
            if (elapsed >= st.GenieDurationMs)
            {
                st.Finished = true;
                if (_cfg.Diagnostico) Program.Log("Anim: Genie objetivo=" + st.GenieDurationMs +
                    " ms, Stopwatch=" + st.Clock.ElapsedMilliseconds + " ms");
                return;
            }
            if (st.Overlay == null || st.Overlay.IsDisposed) { st.Finished = true; return; }
            if (st.Snapshot == null) { st.Finished = true; return; }

            float t    = (float)(elapsed / st.GenieDurationMs);
            float ease = EaseInOut(t);

            byte alpha = t < 0.8f ? (byte)255 : (byte)(255 * (1f - (t - 0.8f) / 0.2f));

            Rectangle orig = st.OrigBounds;
            Point dest     = st.GenieTarget;

            // Dimensiones del frame de salida: igual que el original para que el overlay cubra
            int fw = orig.Width;
            int fh = orig.Height;

            Bitmap frame = st.BeginFrame(fw, fh);
            if (frame == null) { st.Finished = true; return; }
            if (!RenderGenie(st.FrameG, st.Snapshot, fw, fh, orig, dest, ease))
            { st.Finished = true; return; }

            st.Overlay.UpdateFrame(frame, orig, alpha);
        }

        // -------------------------------------------------------------------------
        // Renderizado
        // -------------------------------------------------------------------------
        // Render de la deformacion Genie con malla de segmentos horizontales.
        // Cada segmento se deforma hacia el punto de destino (iconCenter).
        static bool RenderGenie(Graphics g, Bitmap src, int fw, int fh, Rectangle orig, Point dest, float ease)
        {
            if (g == null || src == null || fw <= 0 || fh <= 0) return false;
            try
            {
                // Centro del icono en coordenadas relativas al overlay
                float cx = dest.X - orig.X;
                float cy = dest.Y - orig.Y;

                // Anchura del haz en el icono: ~la anchura del icono (60 px aprox)
                float destW = 60f;

                int seg = GENIE_SEGMENTS;
                float segH = (float)src.Height / seg;
                float dstSegH = (float)fh / seg;

                for (int i = 0; i < seg; i++)
                {
                    // Progreso de este segmento: los inferiores se contraen antes
                    float segProgress = ease + (1f - ease) * ((float)i / seg);
                    if (segProgress > 1f) segProgress = 1f;

                    // Rectangulo fuente en el bitmap original
                    var srcRect = new RectangleF(
                        0, i * segH,
                        src.Width, segH);

                    // Rectangulo destino sin deformar
                    float dstY0 = i * dstSegH;
                    float dstY1 = dstY0 + dstSegH;

                    // Anchura actual del segmento: se contrae hacia el destino
                    float curW = fw * (1f - segProgress) + destW * segProgress;
                    float curX = cx - curW / 2f;

                    // Desplazamiento vertical hacia el icono
                    float curY0 = dstY0 * (1f - segProgress) + cy * segProgress;
                    float curY1 = dstY1 * (1f - segProgress) + cy * segProgress;

                    if (curW < 1f) curW = 1f;
                    if (curY1 <= curY0) curY1 = curY0 + 1f;

                    var dstRect = new RectangleF(curX, curY0, curW, curY1 - curY0);

                    // Dibujar segmento
                    g.DrawImage(src,
                        dstRect,
                        srcRect,
                        GraphicsUnit.Pixel);
                }
                return true;
            }
            catch { return false; }
        }

        // -------------------------------------------------------------------------
        // Curvas de suavizado
        // -------------------------------------------------------------------------
        static float EaseOut(float t)
        {
            // Ease-out cuadratico: 1 - (1-t)^2
            float u = 1f - t;
            return 1f - u * u;
        }

        static float EaseInOut(float t)
        {
            // Ease-in-out seno
            return (float)(0.5 - 0.5 * Math.Cos(Math.PI * t));
        }

        // -------------------------------------------------------------------------
        // Utilidades de ventana
        // -------------------------------------------------------------------------
        static bool IsAnimatableWindow(IntPtr hwnd)
        {
            return EvaluateWindow(hwnd).Eligible;
        }

        // Evalua si una ventana normal puede animarse. No toca GDI ni captura nada.
        // Requiere: nivel superior, visible, con marco (WS_CAPTION), sin dueno, sin
        // estilos de herramienta/click-through, PID y ruta reales, no cloaked, dentro
        // de su monitor y sin ocupar la pantalla completa.
        static WinMeta EvaluateWindow(IntPtr hwnd)
        {
            var meta = new WinMeta();
            if (hwnd == IntPtr.Zero || !N.IsWindow(hwnd)) { meta.Reason = "no-window"; return meta; }

            meta.Class = N.ClassOf(hwnd);
            if (IsSystemClass(meta.Class)) { meta.Reason = "clase-sistema"; return meta; }
            if (!N.IsWindowVisible(hwnd)) { meta.Reason = "oculta"; return meta; }
            if (N.IsIconic(hwnd)) { meta.Reason = "minimizada"; return meta; }
            if (N.GetAncestor(hwnd, 2) != hwnd) { meta.Reason = "no-raiz"; return meta; }   // GA_ROOT

            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            if ((ex & WS_EX_TOOLWINDOW) != 0) { meta.Reason = "toolwindow"; return meta; }
            if ((ex & WS_EX_TRANSPARENT) != 0) { meta.Reason = "transparente"; return meta; }
            if ((ex & WS_EX_NOACTIVATE) != 0) { meta.Reason = "noactivate"; return meta; }

            int style = GetWindowLong(hwnd, GWL_STYLE);
            if ((style & WS_CHILD) != 0) { meta.Reason = "hija"; return meta; }
            meta.HasCaption = (style & WS_CAPTION) == WS_CAPTION;
            meta.Popup      = (style & WS_POPUP) != 0 && !meta.HasCaption;
            if (!meta.HasCaption) { meta.Reason = "sin-caption"; return meta; }    // menus, splash, tooltips
            if (meta.Popup) { meta.Reason = "popup"; return meta; }
            if (N.GetWindow(hwnd, 4) != IntPtr.Zero) { meta.HasOwner = true; meta.Reason = "con-dueno"; return meta; }  // GW_OWNER

            uint pid;
            N.GetWindowThreadProcessId(hwnd, out pid);
            if (pid == 0) { meta.Reason = "sin-pid"; return meta; }
            if (pid == _myPid) { meta.Reason = "propia"; return meta; }
            string path = N.ProcessPath(pid);
            if (path == "") { meta.Reason = "sin-ruta"; return meta; }
            if (path.EndsWith("\\MacDock.exe", StringComparison.OrdinalIgnoreCase)) { meta.Reason = "macdock"; return meta; }

            int cloaked = 0;
            DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out cloaked, sizeof(int));
            if (cloaked != 0) { meta.Reason = "cloaked"; return meta; }

            Rectangle b = GetWindowBounds(hwnd);
            if (b.Width < 120 || b.Height < 80) { meta.Reason = "pequena " + b.Width + "x" + b.Height; return meta; }

            IntPtr mon = N.MonitorFromWindow(hwnd, 2);
            if (mon != IntPtr.Zero)
            {
                var mi = new N.MONITORINFO();
                mi.cbSize = Marshal.SizeOf(typeof(N.MONITORINFO));
                if (N.GetMonitorInfo(mon, ref mi))
                {
                    var mb = Rectangle.FromLTRB(mi.rcMonitor.left, mi.rcMonitor.top,
                                                mi.rcMonitor.right, mi.rcMonitor.bottom);
                    meta.Fullscreen = b.Width >= mb.Width - 2 && b.Height >= mb.Height - 2 && !N.IsZoomed(hwnd);
                    if (meta.Fullscreen) { meta.Reason = "pantalla-completa"; return meta; }
                    if (!mb.Contains(b)) { meta.Reason = "fuera-monitor"; return meta; }
                }
            }

            meta.Eligible = true;
            meta.Reason = "ok";
            return meta;
        }

        static bool IsSystemClass(string cls)
        {
            if (string.IsNullOrEmpty(cls)) return true;
            switch (cls)
            {
                case "Progman":
                case "WorkerW":
                case "Shell_TrayWnd":
                case "Shell_SecondaryTrayWnd":
                case "NotifyIconOverflowWindow":
                case "Windows.UI.Core.CoreWindow":
                case "#32768":   // menus
                case "#32769":   // escritorio
                case "tooltips_class32":
                case "SysShadow":
                case "MultitaskingViewFrame":
                case "XamlExplorerHostIslandWindow":
                    return true;
            }
            return cls.StartsWith("ToolTip", StringComparison.OrdinalIgnoreCase);
        }

        static Rectangle GetWindowBounds(IntPtr hwnd)
        {
            // GetWindowRect siempre devuelve coordenadas en el mismo espacio que usa este
            // proceso (DPI virtualizado). DWMWA_EXTENDED_FRAME_BOUNDS puede devolver pixeles
            // fisicos en ventanas per-monitor DPI aware (Chrome, Explorer) y descuadrar el overlay.
            N.RECT wr;
            if (N.GetWindowRect(hwnd, out wr))
                return new Rectangle(wr.left, wr.top, wr.right - wr.left, wr.bottom - wr.top);
            N.RECT fr;
            if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out fr, Marshal.SizeOf(typeof(N.RECT))) == 0)
                return new Rectangle(fr.left, fr.top, fr.right - fr.left, fr.bottom - fr.top);
            return Rectangle.Empty;
        }

        static bool AlreadyAnimating(IntPtr hwnd)
        {
            lock (_lock)
            {
                foreach (var st in _active)
                    if (st.Hwnd == hwnd && !st.Finished) return true;
            }
            return false;
        }

        static bool WasRecentlyMinimized(IntPtr hwnd)
        {
            lock (_lock)
            {
                DateTime t;
                if (!_minimizeStart.TryGetValue(hwnd, out t)) return false;
                return (DateTime.UtcNow - t).TotalMilliseconds < 1500;
            }
        }

        static void CancelAnimOf(IntPtr hwnd, bool keepClose)
        {
            lock (_lock)
            {
                foreach (var st in _active)
                    if (st.Hwnd == hwnd && !(keepClose && st.Kind == AnimKind.Close)) st.Finished = true;
            }
        }

        static void CancelAll()
        {
            if (_timer != null) { _timer.Dispose(); _timer = null; }
            foreach (var timer in _deferredTimers) timer.Dispose();
            _deferredTimers.Clear();
            List<AnimState> active;
            lock (_lock)
            {
                active = new List<AnimState>(_active);
                _active.Clear();
            }
            foreach (var st in active) st.Dispose();
            List<IntPtr> pending;
            lock (_lock) { pending = new List<IntPtr>(_pendingClose.Keys); }
            foreach (var h in pending) CancelPendingClose(h);
        }

        static long GetOrBumpGen(IntPtr hwnd)
        {
            lock (_lock) { return GetGen(hwnd); }
        }
    }
}
