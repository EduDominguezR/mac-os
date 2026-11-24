using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MacDock
{
    // Barra superior estilo Mac: logo (menu del sistema), app activa, y a la derecha Wi-Fi, volumen, bateria y fecha/hora.
    // Se dibuja solo cuando algo cambia (app activa, minuto, bateria, red): casi cero CPU.
    class MenuBar : Form
    {
        float s; bool light;
        int W, H, X, Y;
        bool full, appBarOn, autoHide, autoHidden, menuOpen;
        float slide = 0f, slideTarget = 0f;
        int appBarMsg;
        DateTime autoLeaveAt = DateTime.MaxValue;
        Action<uint, IntPtr> fgHandler; bool fgOn;
        System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        System.Windows.Forms.Timer anim = new System.Windows.Forms.Timer();
        NetworkAvailabilityChangedEventHandler netAvail;
        NetworkAddressChangedEventHandler netAddr;
        string appName = "Escritorio", title = "", lastKey = "";
        IntPtr fgWnd = IntPtr.Zero;
        RectangleF rLogo, rApp, rWifi, rVol, rBat, rClock, rTheme, rTray, rCc;
        public Action ToggleTheme;   // lo conecta el dock: cambia claro/oscuro
        public Action<Point> ShowTray;   // lo conecta el dock: abre/cierra los iconos ocultos junto al ^
        public Action<ContextMenuStrip> AddDockOptions;   // lo conecta el dock: opciones de barra y tamano de iconos
        Dictionary<string, string> nameCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static readonly uint myPid = (uint)Process.GetCurrentProcess().Id;

        // Recursos de dibujo persistentes: la barra se redibuja al cambiar de app, tema, hover...
        // y no debe crear fuentes, pinceles, bitmap ni Graphics en cada pasada.
        int hoverPart = -1;
        Bitmap barCanvas; Graphics barG;
        Font fBold, fReg, fIcon;
        SolidBrush fgB, dimB, hoverB, sheenB, shadeB;
        Pen borderP;
        StringFormat sfTitle;
        string resKey = "";

        public MenuBar(float scale, bool lightTheme)
        {
            s = scale; light = lightTheme;
            Text = "MacDockMenuBar";
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(0, 0, 1, 1);
            appBarMsg = N.RegisterWindowMessage("MacDockMenuBarMsg");
            timer.Interval = 10000; timer.Tick += (o, e) => Redraw(false);
            anim.Interval = 15; anim.Tick += AnimTick;
        }

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x80000 | 0x80 | 0x8000000 | 0x8; return cp; }
        }
        protected override bool ShowWithoutActivation { get { return true; } }

        public int BarHeight { get { return (int)Math.Round(26 * s); } }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            var b = Screen.PrimaryScreen.Bounds;
            W = b.Width; H = BarHeight; X = b.Left; Y = b.Top;
            slide = 0f; slideTarget = 0f;
            RegisterAppBar();
            fgHandler = OnWinEvent;
            SubscribeFg();
            netAvail = (o, a) => { if (autoHidden) return; try { BeginInvoke(new Action(() => Redraw(true))); } catch { } };
            netAddr = (o, a) => { if (autoHidden) return; try { BeginInvoke(new Action(() => Redraw(true))); } catch { } };
            NetworkChange.NetworkAvailabilityChanged += netAvail;
            NetworkChange.NetworkAddressChanged += netAddr;
            UpdateActive(Win32Fg());
            timer.Start();
        }

        static IntPtr Win32Fg() { return N.GetForegroundWindow(); }

        void SubscribeFg()
        {
            if (fgOn || IsDisposed) return;
            if (fgHandler == null) fgHandler = OnWinEvent;
            fgOn = true;
            WinWatch.Add(fgHandler);
        }

        void UnsubscribeFg()
        {
            if (!fgOn) return;
            fgOn = false;
            if (fgHandler != null) WinWatch.Remove(fgHandler);
        }

        void OnWinEvent(uint ev, IntPtr hwnd)
        {
            if (ev != 0x0003) return;
            try { BeginInvoke(new Action(() => { if (!IsDisposed) UpdateActive(hwnd); })); } catch { }
        }

        void UpdateActive(IntPtr h)
        {
            try
            {
                if (h == IntPtr.Zero || h == Handle) return;
                uint pid; N.GetWindowThreadProcessId(h, out pid);
                if (pid == myPid) return;
                string cls = N.ClassOf(h);
                if (cls == "Shell_TrayWnd" || cls == "Windows.UI.Core.CoreWindow" || cls == "XamlExplorerHostIslandWindow" || cls == "#32768" ||
                    cls == "TopLevelWindowForOverflowXamlIsland" || cls == "NotifyIconOverflowWindow") return;
                fgWnd = h;
                string path = N.ProcessPath(pid);
                if (cls == "Progman" || cls == "WorkerW") { appName = "Escritorio"; title = ""; }
                else if (path.EndsWith("\\explorer.exe", StringComparison.OrdinalIgnoreCase)) { appName = "Explorador"; title = N.TitleOf(h); }
                else { appName = NiceName(path); title = N.TitleOf(h); }
                Redraw(false);
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        string NiceName(string path)
        {
            if (string.IsNullOrEmpty(path)) return "App";
            string n;
            if (nameCache.TryGetValue(path, out n)) return n;
            n = Path.GetFileNameWithoutExtension(path);
            try { var vi = FileVersionInfo.GetVersionInfo(path); if (!string.IsNullOrEmpty(vi.FileDescription) && vi.FileDescription.Length < 40) n = vi.FileDescription; } catch { }
            nameCache[path] = n;
            return n;
        }

        public void SetFull(bool f)
        {
            if (f == full) return;
            full = f;
            if (full)
            {
                ControlCenter.CloseIfOpen();
                anim.Stop();
                timer.Stop();
                N.SetWindowPos(Handle, new IntPtr(-2), 0, 0, 0, 0, 0x0013);   // deja de ser TopMost
                N.ShowWindow(Handle, 0);
            }
            else if (autoHide)
            {
                slide = 1f; slideTarget = 1f;
                if (!autoHidden) FinishHide();
            }
            else
            {
                autoHidden = false; slide = 0f; slideTarget = 0f;
                if (!appBarOn) RegisterAppBar();
                SubscribeFg();
                timer.Start();
                N.ShowWindow(Handle, 8); N.SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x13);
                UpdateActive(Win32Fg()); Redraw(true);
            }
        }

        public void SetTheme(bool lightTheme) { light = lightTheme; Redraw(true); }

        // ---------- ocultar automaticamente (como el dock con el borde inferior) ----------
        public void SetAutoHide(bool on)
        {
            if (on == autoHide) return;
            autoHide = on;
            autoLeaveAt = DateTime.MaxValue;
            if (!autoHide && (autoHidden || slideTarget > 0f || slide > 0f)) ShowAuto();
        }

        public void AutoTick(Point c, Rectangle scr)
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (!autoHide) { if (autoHidden || slideTarget > 0f || slide > 0f) ShowAuto(); return; }
            if (full) return;
            bool inX = c.X >= scr.Left && c.X < scr.Right;
            bool atTop = inX && c.Y <= scr.Top + 1;
            bool onBar = !autoHidden && inX && c.Y >= scr.Top && c.Y <= scr.Top + BarHeight + (int)(4 * s);
            if (autoHidden) { if (atTop) ShowAuto(); return; }
            if (menuOpen || atTop || onBar)
            {
                autoLeaveAt = DateTime.MaxValue;
                if (slideTarget > 0f) ShowAuto();
                return;
            }
            if (autoLeaveAt == DateTime.MaxValue) { autoLeaveAt = DateTime.Now; return; }
            if ((DateTime.Now - autoLeaveAt).TotalMilliseconds > 500) HideAuto();
        }

        void HideAuto()
        {
            if (autoHidden || IsDisposed) return;
            autoLeaveAt = DateTime.MaxValue;
            slideTarget = 1f;
            if (!anim.Enabled) anim.Start();
        }

        void ShowAuto()
        {
            if (IsDisposed || !IsHandleCreated || full) return;
            autoLeaveAt = DateTime.MaxValue;
            bool wasHidden = autoHidden;
            if (!wasHidden && slideTarget == 0f && slide == 0f) return;
            autoHidden = false;
            if (!appBarOn) RegisterAppBar();
            SubscribeFg();
            timer.Start();
            if (wasHidden || slide >= 1f)
            {
                slide = 1f;
                MoveBar();
                N.ShowWindow(Handle, 8);
                UpdateActive(Win32Fg());
                Redraw(true);
            }
            slideTarget = 0f;
            if (!anim.Enabled) anim.Start();
        }

        void AnimTick(object sender, EventArgs e)
        {
            if (IsDisposed || !IsHandleCreated) { anim.Stop(); return; }
            if (menuOpen && slideTarget > 0f) slideTarget = 0f;
            slide += (slideTarget - slide) * 0.34f;
            if (Math.Abs(slideTarget - slide) < 0.01f) slide = slideTarget;
            MoveBar();
            if (slide == slideTarget)
            {
                anim.Stop();
                if (slide >= 1f) FinishHide();
            }
        }

        void MoveBar()
        {
            int y = Y - (int)Math.Round(slide * H);
            N.SetWindowPos(Handle, new IntPtr(-1), X, y, 0, 0, 0x0001 | 0x0010);
        }

        void FinishHide()
        {
            autoHidden = true; autoLeaveAt = DateTime.MaxValue;
            slide = 1f; slideTarget = 1f;
            MoveBar();
            try { Unregister(); } catch { }
            try { timer.Stop(); } catch { }
            UnsubscribeFg();
            N.ShowWindow(Handle, 0);
        }

        // ---------- dibujo ----------
        void EnsureResources()
        {
            string key = light + "|" + s.ToString("0.###");
            if (resKey == key && fBold != null) return;
            DisposeResources();
            fBold = new Font(UiFont.Semi, 13f * s, FontStyle.Regular, GraphicsUnit.Pixel);
            fReg = new Font(UiFont.Reg, 13f * s, FontStyle.Regular, GraphicsUnit.Pixel);
            fIcon = new Font("Segoe MDL2 Assets", 13f * s, FontStyle.Regular, GraphicsUnit.Pixel);
            fgB = new SolidBrush(light ? Color.FromArgb(255, 20, 20, 22) : Color.FromArgb(255, 245, 245, 247));
            dimB = new SolidBrush(light ? Color.FromArgb(150, 20, 20, 22) : Color.FromArgb(160, 245, 245, 247));
            hoverB = new SolidBrush(light ? Color.FromArgb(18, 0, 0, 0) : Color.FromArgb(22, 255, 255, 255));
            sheenB = new SolidBrush(light ? Color.FromArgb(30, 255, 255, 255) : Color.FromArgb(14, 255, 255, 255));
            shadeB = new SolidBrush(light ? Color.FromArgb(10, 0, 0, 0) : Color.FromArgb(30, 0, 0, 0));
            borderP = new Pen(light ? Color.FromArgb(30, 0, 0, 0) : Color.FromArgb(40, 255, 255, 255),
                              Math.Max(1f, s * 0.8f));
            sfTitle = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            resKey = key;
        }

        void DisposeResources()
        {
            if (fBold != null) { fBold.Dispose(); fBold = null; }
            if (fReg != null) { fReg.Dispose(); fReg = null; }
            if (fIcon != null) { fIcon.Dispose(); fIcon = null; }
            if (fgB != null) { fgB.Dispose(); fgB = null; }
            if (dimB != null) { dimB.Dispose(); dimB = null; }
            if (hoverB != null) { hoverB.Dispose(); hoverB = null; }
            if (sheenB != null) { sheenB.Dispose(); sheenB = null; }
            if (shadeB != null) { shadeB.Dispose(); shadeB = null; }
            if (borderP != null) { borderP.Dispose(); borderP = null; }
            if (sfTitle != null) { sfTitle.Dispose(); sfTitle = null; }
            resKey = "";
        }

        void EnsureCanvas()
        {
            if (barCanvas != null && barCanvas.Width == W && barCanvas.Height == H) return;
            if (barG != null) { try { barG.Dispose(); } catch { } barG = null; }
            if (barCanvas != null) { try { barCanvas.Dispose(); } catch { } barCanvas = null; }
            barCanvas = new Bitmap(W, H, PixelFormat.Format32bppArgb);
            barG = Graphics.FromImage(barCanvas);
        }

        void DrawHover(Graphics g, RectangleF r)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            var rr = RectangleF.Inflate(r, -5 * s, -4 * s);
            using (var p = Round(rr, 6 * s)) g.FillPath(hoverB, p);
        }

        void Redraw(bool force)
        {
            if (full || autoHidden || W <= 0 || IsDisposed) return;
            var ps = SystemInformation.PowerStatus;
            bool net = false; try { net = NetworkInterface.GetIsNetworkAvailable(); } catch { }
            string clock = ClockText();
            string key = appName + "|" + title + "|" + clock + "|" + (int)(ps.BatteryLifePercent * 100) + ps.PowerLineStatus + "|" + net + "|" + light;
            if (!force && key == lastKey) return;
            lastKey = key;

            EnsureResources();
            EnsureCanvas();
            var g = barG;
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            Color bg = light ? Color.FromArgb(190, 246, 246, 248) : Color.FromArgb(150, 18, 18, 22);
            using (var b = new SolidBrush(bg)) g.FillRectangle(b, 0, 0, W, H);
            g.FillRectangle(sheenB, 0, 0, W, H * 0.55f);
            g.FillRectangle(shadeB, 0, H * 0.70f, W, H * 0.30f);
            g.DrawLine(borderP, 0, H - 1, W, H - 1);

            // Fondos de hover con los rectangulos del ultimo fotograma (el layout no cambia).
            DrawHover(g, rLogo); DrawHover(g, rApp); DrawHover(g, rWifi); DrawHover(g, rVol);
            DrawHover(g, rBat); DrawHover(g, rClock); DrawHover(g, rTheme); DrawHover(g, rTray); DrawHover(g, rCc);

            float cy = H / 2f;
            // izquierda: logo + app activa + titulo
            float x = 14 * s;
            float lq = 5.5f * s, lg = 1.4f * s, ly = cy - lq - lg / 2;
            rLogo = new RectangleF(x - 8 * s, 0, lq * 2 + lg + 16 * s, H);
            g.FillRectangle(fgB, x, ly, lq, lq); g.FillRectangle(fgB, x + lq + lg, ly, lq, lq);
            g.FillRectangle(fgB, x, ly + lq + lg, lq, lq); g.FillRectangle(fgB, x + lq + lg, ly + lq + lg, lq, lq);
            x += lq * 2 + lg + 20 * s;

            var an = g.MeasureString(appName, fBold);
            rApp = new RectangleF(x - 8 * s, 0, an.Width + 16 * s, H);
            g.DrawString(appName, fBold, fgB, x, cy - an.Height / 2);
            x += an.Width + 16 * s;

            // derecha: reloj, bateria, volumen, wifi (de derecha a izquierda)
            float rx = W - 14 * s;
            var cs = g.MeasureString(clock, fReg);
            rx -= cs.Width;
            g.DrawString(clock, fReg, fgB, rx, cy - cs.Height / 2);
            rClock = new RectangleF(rx - 8 * s, 0, cs.Width + 16 * s, H);
            rx -= 16 * s;

            // icono Centro de Control estilo macOS (dos interruptores tipo pildora)
            float ccW = 15 * s, ccH = 12 * s;
            rx -= ccW;
            DrawControlCenterIcon(g, rx, cy - ccH / 2, ccW, ccH, fgB.Color, s);
            rCc = new RectangleF(rx - 6 * s, 0, ccW + 12 * s, H);
            rx -= 16 * s;

            float pct = ps.BatteryLifePercent;
            if (pct >= 0 && pct <= 1 && ps.BatteryChargeStatus != BatteryChargeStatus.NoSystemBattery)
            {
                string pt = (int)Math.Round(pct * 100) + "%";
                float bw = 22 * s, bh = 11 * s;
                rx -= bw + 2 * s;
                float by = cy - bh / 2;
                using (var pen = new Pen(dimB.Color, Math.Max(1f, 1.1f * s)))
                using (var path = Round(new RectangleF(rx, by, bw, bh), 3 * s)) g.DrawPath(pen, path);
                g.FillRectangle(dimB, rx + bw + 0.8f * s, cy - 2.2f * s, 1.6f * s, 4.4f * s);
                Color fill = pct <= 0.2f ? Color.FromArgb(255, 69, 58) : (ps.PowerLineStatus == PowerLineStatus.Online ? Color.FromArgb(52, 199, 89) : fgB.Color);
                using (var fb = new SolidBrush(fill))
                using (var path = Round(new RectangleF(rx + 2 * s, by + 2 * s, Math.Max(1.5f * s, (bw - 4 * s) * pct), bh - 4 * s), 1.5f * s)) g.FillPath(fb, path);
                var pts = g.MeasureString(pt, fReg);
                rx -= pts.Width + 3 * s;
                g.DrawString(pt, fReg, fgB, rx, cy - pts.Height / 2);
                rBat = new RectangleF(rx - 6 * s, 0, pts.Width + bw + 18 * s, H);
                rx -= 18 * s;
            }
            else rBat = RectangleF.Empty;

            string vol = "";
            var vs = g.MeasureString(vol, fIcon); rx -= vs.Width;
            g.DrawString(vol, fIcon, fgB, rx, cy - vs.Height / 2);
            rVol = new RectangleF(rx - 6 * s, 0, vs.Width + 12 * s, H);
            rx -= 16 * s;

            string wifi = "";
            var ws = g.MeasureString(wifi, fIcon); rx -= ws.Width;
            g.DrawString(wifi, fIcon, net ? fgB : dimB, rx, cy - ws.Height / 2);
            if (!net) using (var pen = new Pen(dimB.Color, 1.4f * s)) g.DrawLine(pen, rx, cy + ws.Height / 3, rx + ws.Width, cy - ws.Height / 3);
            rWifi = new RectangleF(rx - 6 * s, 0, ws.Width + 12 * s, H);
            rx -= 16 * s;

            // sol / luna: en oscuro muestra la luna, en claro el sol. Un clic cambia el modo.
            string th = light ? "" : "";
            var ts = g.MeasureString(th, fIcon); rx -= ts.Width;
            g.DrawString(th, fIcon, fgB, rx, cy - ts.Height / 2);
            rTheme = new RectangleF(rx - 6 * s, 0, ts.Width + 12 * s, H);
            rx -= 16 * s;

            // ^ : iconos de la bandeja del sistema (apps en segundo plano)
            string tr = "\uE70E";
            var trs = g.MeasureString(tr, fIcon); rx -= trs.Width;
            g.DrawString(tr, fIcon, fgB, rx, cy - trs.Height / 2);
            rTray = new RectangleF(rx - 6 * s, 0, trs.Width + 12 * s, H);
            rx -= 24 * s;

            // titulo de la ventana, atenuado, en el espacio que sobra
            if (title != "" && title != appName && rx - x > 80 * s)
            {
                float maxW = Math.Min(rx - x, W * 0.45f);
                g.DrawString(title, fReg, dimB,
                    new RectangleF(x, cy - fReg.Height / 2f - 1, maxW, fReg.Height + 2), sfTitle);
            }

            Push(barCanvas);
        }

        // Icono del Centro de Control (dos interruptores tipo pildora), adaptado de MacDock (MIT).
        static void DrawControlCenterIcon(Graphics g, float x, float y, float w, float h, Color fg, float s)
        {
            float ph = h * 0.42f;
            using (var pen = new Pen(fg, Math.Max(1f, 1.15f * s)))
            using (var b = new SolidBrush(fg))
            {
                using (var p1 = Round(new RectangleF(x, y, w, ph), ph / 2)) g.DrawPath(pen, p1);
                using (var p2 = Round(new RectangleF(x, y + h - ph, w, ph), ph / 2)) g.DrawPath(pen, p2);
                float d = ph - 1.6f * s;
                g.FillEllipse(b, x + w - d - 0.8f * s, y + 0.8f * s, d, d);
                g.FillEllipse(b, x + 0.8f * s, y + h - ph + 0.8f * s, d, d);
            }
        }

        static string ClockText()
        {
            var n = DateTime.Now;
            string[] d = { "dom", "lun", "mar", "mié", "jue", "vie", "sáb" };
            string[] m = { "ene", "feb", "mar", "abr", "may", "jun", "jul", "ago", "sept", "oct", "nov", "dic" };
            return d[(int)n.DayOfWeek] + " " + n.Day + " " + m[n.Month - 1] + "   " + Program.TimeText(n, true);
        }

        static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath(); float dd = rad * 2;
            p.AddArc(r.X, r.Y, dd, dd, 180, 90); p.AddArc(r.Right - dd, r.Y, dd, dd, 270, 90);
            p.AddArc(r.Right - dd, r.Bottom - dd, dd, dd, 0, 90); p.AddArc(r.X, r.Bottom - dd, dd, dd, 90, 90);
            p.CloseFigure(); return p;
        }

        void Push(Bitmap bmp)
        {
            IntPtr screenDc = N.GetDC(IntPtr.Zero), memDc = N.CreateCompatibleDC(screenDc), hb = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                hb = bmp.GetHbitmap(Color.FromArgb(0)); old = N.SelectObject(memDc, hb);
                var size = new N.SIZE { cx = W, cy = H }; var src = new N.POINT(); var dst = new N.POINT { x = X, y = Y - (int)Math.Round(slide * H) };
                var bl = new N.BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
                N.UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref bl, 2);
            }
            finally
            {
                N.ReleaseDC(IntPtr.Zero, screenDc);
                if (hb != IntPtr.Zero) { N.SelectObject(memDc, old); N.DeleteObject(hb); }
                N.DeleteDC(memDc);
            }
        }

        // ---------- clics ----------
        int HitPart(Point p)
        {
            if (rLogo.Contains(p)) return 0;
            if (rApp.Contains(p)) return 1;
            if (rTheme.Contains(p)) return 2;
            if (rTray.Contains(p)) return 3;
            if (rWifi.Contains(p)) return 4;
            if (rVol.Contains(p)) return 5;
            if (rBat.Contains(p)) return 6;
            if (rCc.Contains(p)) return 7;
            if (rClock.Contains(p)) return 8;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int hp = HitPart(e.Location);
            if (hp == hoverPart) return;
            hoverPart = hp;
            Redraw(true);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hoverPart == -1) return;
            hoverPart = -1;
            Redraw(true);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            try
            {
                if (rTheme.Contains(e.Location)) { if (ToggleTheme != null) ToggleTheme(); }
                else if (rTray.Contains(e.Location)) { if (ShowTray != null) ShowTray(new Point(X + (int)(rTray.Left + rTray.Width / 2), Y + H)); }
                else if (rLogo.Contains(e.Location)) SystemMenu();
                else if (rApp.Contains(e.Location)) AppMenu();
                else if (rWifi.Contains(e.Location) || rVol.Contains(e.Location) || rBat.Contains(e.Location) || rCc.Contains(e.Location))
                {
                    Point anchor = new Point(X + W - (int)(12 * s), Y + H + (int)(6 * s));
                    ControlCenter.Toggle(s, light, anchor, Screen.PrimaryScreen.Bounds, true, ToggleTheme);
                }
                else if (rClock.Contains(e.Location)) N.PressKeys(0x5B, 0x4E);
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        ContextMenuStrip NewMenu()
        {
            return MenuStyle.New(light);
        }

        static void Item(ContextMenuStrip m, string text, Action a)
        {
            var mi = new ToolStripMenuItem(text);
            mi.Click += (o, e) => { try { a(); } catch (Exception ex) { Program.Log(ex); } };
            m.Items.Add(mi);
        }

        static bool Confirm(string what)
        {
            return MessageBox.Show("¿Seguro que quieres " + what + "? Guarda tu trabajo antes.", "MacDock", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        }

        static void DoSuspend()
        {
            PowerActions.Suspend();
        }

        void SystemMenu()
        {
            var m = NewMenu();
            Item(m, "Menú Inicio", () => N.PressKeys(0x5B));
            Item(m, "Acerca de este equipo", () => Process.Start("ms-settings:about"));
            m.Items.Add(new ToolStripSeparator());
            Item(m, "Centro de Control...", () => ControlCenter.Toggle(s, light, new Point(X + (int)(200 * s), Y + H + (int)(6 * s)), Screen.PrimaryScreen.Bounds, true, ToggleTheme));
            Item(m, "Configuración", () => Process.Start("ms-settings:"));
            Item(m, "Administrador de tareas", () => Process.Start("taskmgr.exe"));
            Item(m, "Ajustes de Windows (MacDock)", () =>
            {
                string ps = Path.Combine(Program.DataDir, "ajustes.ps1");
                if (File.Exists(ps)) Process.Start(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"), "-NoProfile -ExecutionPolicy Bypass -File \"" + ps + "\"");
            });
            if (AddDockOptions != null)
            {
                m.Items.Add(new ToolStripSeparator());
                AddDockOptions(m);
            }
            m.Items.Add(new ToolStripSeparator());
            Item(m, "Suspender", DoSuspend);
            Item(m, "Reiniciar...", () => { if (Confirm("reiniciar")) Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 0") { CreateNoWindow = true, UseShellExecute = false }); });
            Item(m, "Apagar...", () => { if (Confirm("apagar")) Process.Start(new ProcessStartInfo("shutdown.exe", "/s /t 0") { CreateNoWindow = true, UseShellExecute = false }); });
            m.Items.Add(new ToolStripSeparator());
            Item(m, "Bloquear pantalla", () => N.PressKeys(0x5B, 0x4C));
            Item(m, "Cerrar sesión...", () => { if (Confirm("cerrar sesión")) Process.Start(new ProcessStartInfo("shutdown.exe", "/l") { CreateNoWindow = true, UseShellExecute = false }); });
            Show(m, rLogo);
        }

        void AppMenu()
        {
            IntPtr h = fgWnd;
            if (h == IntPtr.Zero || appName == "Escritorio") return;
            var m = NewMenu();
            Item(m, "Minimizar", () => N.ShowWindow(h, 6));
            Item(m, N.IsZoomed(h) ? "Restaurar tamaño" : "Maximizar", () => N.ShowWindow(h, N.IsZoomed(h) ? 9 : 3));
            m.Items.Add(new ToolStripSeparator());
            Item(m, "Cerrar ventana", () => N.PostMessage(h, 0x10, IntPtr.Zero, IntPtr.Zero));
            Show(m, rApp);
        }

        void Show(ContextMenuStrip m, RectangleF r)
        {
            N.SetForegroundWindow(Handle);
            menuOpen = true;
            m.Closed += (o, e) => { menuOpen = false; autoLeaveAt = DateTime.MaxValue; };
            m.Show(new Point(X + (int)r.X, Y + H));
        }

        // ---------- reservar el espacio arriba (como Mac) ----------
        void RegisterAppBar()
        {
            var abd = new N.APPBARDATA(); abd.cbSize = Marshal.SizeOf(typeof(N.APPBARDATA));
            abd.hWnd = Handle; abd.uCallbackMessage = appBarMsg;
            N.SHAppBarMessage(0, ref abd);
            appBarOn = true;
            SetPos();
        }

        void SetPos()
        {
            if (!appBarOn) return;
            var b = Screen.PrimaryScreen.Bounds;
            var abd = new N.APPBARDATA(); abd.cbSize = Marshal.SizeOf(typeof(N.APPBARDATA));
            abd.hWnd = Handle; abd.uEdge = 1;
            abd.rc.left = b.Left; abd.rc.right = b.Right; abd.rc.top = b.Top; abd.rc.bottom = b.Top + BarHeight;
            N.SHAppBarMessage(2, ref abd);
            abd.rc.bottom = abd.rc.top + BarHeight;
            N.SHAppBarMessage(3, ref abd);
        }

        public void ReRegister() { if (autoHidden) return; if (appBarOn) { Unregister(); } RegisterAppBar(); Redraw(true); }

        void Unregister()
        {
            if (!appBarOn) return;
            var abd = new N.APPBARDATA(); abd.cbSize = Marshal.SizeOf(typeof(N.APPBARDATA)); abd.hWnd = Handle;
            N.SHAppBarMessage(1, ref abd);
            appBarOn = false;
        }

        public void OnDisplayChanged()
        {
            var b = Screen.PrimaryScreen.Bounds;
            W = b.Width; H = BarHeight; X = b.Left; Y = b.Top;
            SetPos(); Redraw(true);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == appBarMsg && appBarMsg != 0 && m.WParam.ToInt32() == 1) SetPos();
            base.WndProc(ref m);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            try
            {
                DisposeResources();
                if (barG != null) { barG.Dispose(); barG = null; }
                if (barCanvas != null) { barCanvas.Dispose(); barCanvas = null; }
                anim.Stop(); anim.Dispose();
                if (netAvail != null) NetworkChange.NetworkAvailabilityChanged -= netAvail;
                if (netAddr != null) NetworkChange.NetworkAddressChanged -= netAddr;
                netAvail = null; netAddr = null;
                UnsubscribeFg(); Unregister();
                timer.Stop(); timer.Dispose();
            }
            catch { }
            base.OnFormClosing(e);
        }
    }

    // Centro de Control con el diseno modular Bento de macOS Sonoma / Sequoia.
    // Adaptado del Centro de Control de MacDock (MIT). Ver LICENSE.
    // El constructor no consulta el sistema: todo se lee en un hilo de fondo al crear la ventana.
    // Fuentes, pinceles, lapices y trazados se cachean; OnPaint no crea objetos GDI nuevos.
    class ControlCenter : Form
    {
        public static ControlCenter Current;
        public static Func<bool> FullscreenBlock;   // lo conecta el dock: true = app a pantalla completa
        static DateTime closedAt = DateTime.MinValue;

        public static void CloseIfOpen()
        {
            ControlCenter c = Current;
            if (c != null && !c.IsDisposed) { try { c.Close(); } catch { } }
        }

        public static void Toggle(float scale, bool light, Point anchor, Rectangle screenBounds, bool fromTop, Action toggleThemeAction)
        {
            if (FullscreenBlock != null && FullscreenBlock()) return;
            if (Current != null && !Current.IsDisposed) { Current.Close(); return; }
            if ((DateTime.Now - closedAt).TotalMilliseconds < 250) return;
            Current = new ControlCenter(scale, light, anchor, screenBounds, fromTop, toggleThemeAction);
            Current.openedAt = DateTime.Now.Ticks;
            Current.FormClosed += (o, e) => { closedAt = DateTime.Now; Current = null; };
            Current.Show();
            Current.Activate();
            N.SetForegroundWindow(Current.Handle);
        }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);

        float s; bool light;
        Action onToggleTheme;
        float volume = 0.5f, brightness = 0.8f;
        bool muted, netOn = true, isEthernet, dndOn, isDesktop = true, highPerfPlan;
        int wifiState = -2, btState = -2;            // -2 = leyendo, -1 = no disponible, 0 = off, 1 = on
        bool shareOn, busyWifi, busyBt, shareAvailable, dndAvailable = true;
        string wifiName, btDevice, netLabel = "Conectado";
        string mediaTitle = "Reproductor multimedia", mediaApp = "Ahora suena";
        bool mediaPlaying;
        Bitmap art; string artKey;
        // Reintentos de caratula: un unico temporizador de la interfaz con esperas 1,5 s / 3 s / 6 s
        // (maximo 3). La clave guardada hace de generacion: si cambia la cancion o se cierra el panel,
        // el reintento pendiente deja de ser valido y no se aplica nada.
        System.Windows.Forms.Timer artRetryTimer;
        int artRetryStep; string artRetryKey;
        readonly List<Snap> pendingSnaps = new List<Snap>();
        int cpuPct = 8, ramPct = 35;
        float ramUsedGb = 6f, ramTotalGb = 16f;
        float batteryPct; bool charging, hasBattery;
        int dragSlider, hoverPart;
        int refreshFast, refreshSlow;
        long openedAt, firstPaintAt, slowAppliedAt;   // tiempos de presentacion (con Diagnostico)

        RectangleF rConnCard, rWifiRow, rBtRow, rShareRow;
        RectangleF rDarkCard, rDndCard, rDispCard;
        RectangleF rBrightCard, rBrightTrack;
        RectangleF rSoundCard, rSoundTrack, rMuteBtn, rAudioOutBtn;
        RectangleF rMediaCard, rMediaIcon, rPrevBtn, rPlayBtn, rNextBtn;
        RectangleF rFootPower, rFootSnip;

        Font fTitle, fSub, fMini, fIcon;
        StringFormat sfCenter, sfEllipsis;
        readonly Dictionary<int, SolidBrush> brushes = new Dictionary<int, SolidBrush>();
        readonly Dictionary<int, Pen> pens = new Dictionary<int, Pen>();
        GraphicsPath pConnCard, pDarkCard, pDndCard, pDispCard, pBrightCard, pSoundCard, pMediaCard;
        GraphicsPath pBrightTrack, pSoundTrack, pHover1, pHover2, pHover3, pMediaIcon, pFootPower, pFootSnip;

        // Ultimo estado conocido del panel: la siguiente apertura lo muestra al instante (< 100 ms)
        // y actualiza los valores reales por detras. Solo campos simples (sin bitmaps ni GDI).
        sealed class KnownState
        {
            public float Volume = 0.5f, Brightness = 0.8f; public bool Muted;
            public int WifiState = -2, BtState = -2; public string WifiName, BtDevice, NetLabel;
            public bool NetOn, IsEth, ShareAvail, ShareOn, DndOn, DndAvail = true, IsDesktop = true, HighPerf;
            public int Cpu = 8, RamPct = 35; public float RamUsed = 6f, RamTotal = 16f;
            public float BatteryPct; public bool Charging, HasBattery;
            public string MediaTitle, MediaApp; public bool MediaPlaying;
        }
        static readonly KnownState Known = new KnownState();
        public static Func<bool> Diag;   // conecta cfg.Diagnostico: registra tiempos de presentacion

        void LoadKnown()
        {
            volume = Known.Volume; brightness = Known.Brightness; muted = Known.Muted;
            wifiState = Known.WifiState; btState = Known.BtState;
            wifiName = Known.WifiName; btDevice = Known.BtDevice; netLabel = Known.NetLabel;
            netOn = Known.NetOn; isEthernet = Known.IsEth;
            shareAvailable = Known.ShareAvail; shareOn = Known.ShareOn;
            dndOn = Known.DndOn; dndAvailable = Known.DndAvail;
            isDesktop = Known.IsDesktop; highPerfPlan = Known.HighPerf;
            cpuPct = Known.Cpu; ramPct = Known.RamPct; ramUsedGb = Known.RamUsed; ramTotalGb = Known.RamTotal;
            batteryPct = Known.BatteryPct; charging = Known.Charging; hasBattery = Known.HasBattery;
            if (Known.MediaTitle != null) { mediaTitle = Known.MediaTitle; mediaApp = Known.MediaApp; mediaPlaying = Known.MediaPlaying; }
        }

        void SaveKnown()
        {
            Known.Volume = volume; Known.Brightness = brightness; Known.Muted = muted;
            Known.WifiState = wifiState; Known.BtState = btState;
            Known.WifiName = wifiName; Known.BtDevice = btDevice; Known.NetLabel = netLabel;
            Known.NetOn = netOn; Known.IsEth = isEthernet;
            Known.ShareAvail = shareAvailable; Known.ShareOn = shareOn;
            Known.DndOn = dndOn; Known.DndAvail = dndAvailable;
            Known.IsDesktop = isDesktop; Known.HighPerf = highPerfPlan;
            Known.Cpu = cpuPct; Known.RamPct = ramPct; Known.RamUsed = ramUsedGb; Known.RamTotal = ramTotalGb;
            Known.BatteryPct = batteryPct; Known.Charging = charging; Known.HasBattery = hasBattery;
            Known.MediaTitle = mediaTitle; Known.MediaApp = mediaApp; Known.MediaPlaying = mediaPlaying;
        }

        ControlCenter(float scale, bool lightTheme, Point anchor, Rectangle screen, bool fromTop, Action toggleTheme)
        {
            s = scale; light = lightTheme; onToggleTheme = toggleTheme;
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; KeyPreview = true;
            StartPosition = FormStartPosition.Manual; DoubleBuffered = true;
            BackColor = light ? Color.FromArgb(236, 236, 242) : Color.FromArgb(30, 30, 36);

            int w = (int)(332 * s), h = (int)(430 * s);
            int x = fromTop ? Math.Min(screen.Right - w - (int)(10 * s), Math.Max(screen.Left + (int)(10 * s), anchor.X - w))
                            : Math.Min(screen.Right - w - (int)(10 * s), Math.Max(screen.Left + (int)(10 * s), anchor.X - w / 2));
            int y = fromTop ? anchor.Y : Math.Max(screen.Top + (int)(10 * s), anchor.Y - h - (int)(8 * s));
            Bounds = new Rectangle(x, y, w, h);

            LoadKnown();          // sin lectura de hardware: estado conocido al instante
            CreateResources();
            LayoutCards();
        }

        // ---- recursos GDI cacheados ----
        void CreateResources()
        {
            fTitle = new Font(UiFont.Semi, 12f * s, FontStyle.Regular, GraphicsUnit.Pixel);
            fSub = new Font(UiFont.Reg, 10.5f * s, FontStyle.Regular, GraphicsUnit.Pixel);
            fMini = new Font(UiFont.Semi, 10.5f * s, FontStyle.Regular, GraphicsUnit.Pixel);
            fIcon = new Font("Segoe MDL2 Assets", 13.5f * s, FontStyle.Regular, GraphicsUnit.Pixel);
            sfCenter = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            sfEllipsis = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        }

        SolidBrush B(Color c)
        {
            int k = c.ToArgb(); SolidBrush b;
            if (!brushes.TryGetValue(k, out b)) { b = new SolidBrush(c); brushes[k] = b; }
            return b;
        }

        Pen P(Color c)
        {
            int k = c.ToArgb(); Pen p;
            if (!pens.TryGetValue(k, out p)) { p = new Pen(c, 1f); pens[k] = p; }
            return p;
        }

        void ResetColors()
        {
            foreach (var b in brushes.Values) b.Dispose();
            foreach (var p in pens.Values) p.Dispose();
            brushes.Clear(); pens.Clear();
        }

        void DisposePaths()
        {
            var paths = new GraphicsPath[] { pConnCard, pDarkCard, pDndCard, pDispCard, pBrightCard, pSoundCard, pMediaCard,
                pBrightTrack, pSoundTrack, pHover1, pHover2, pHover3, pMediaIcon, pFootPower, pFootSnip };
            foreach (var p in paths) if (p != null) p.Dispose();
            pConnCard = pDarkCard = pDndCard = pDispCard = pBrightCard = pSoundCard = pMediaCard = null;
            pBrightTrack = pSoundTrack = pHover1 = pHover2 = pHover3 = pMediaIcon = pFootPower = pFootSnip = null;
        }

        void DisposeResources()
        {
            DisposePaths();
            ResetColors();
            if (fTitle != null) { fTitle.Dispose(); fTitle = null; }
            if (fSub != null) { fSub.Dispose(); fSub = null; }
            if (fMini != null) { fMini.Dispose(); fMini = null; }
            if (fIcon != null) { fIcon.Dispose(); fIcon = null; }
            if (sfCenter != null) { sfCenter.Dispose(); sfCenter = null; }
            if (sfEllipsis != null) { sfEllipsis.Dispose(); sfEllipsis = null; }
        }

        // ---- colores del tema (structs: no asignan memoria) ----
        Color CardBg { get { return light ? Color.FromArgb(250, 250, 253) : Color.FromArgb(44, 44, 52); } }
        Color CardBorder { get { return light ? Color.FromArgb(22, 0, 0, 0) : Color.FromArgb(32, 255, 255, 255); } }
        Color FgCol { get { return light ? Color.FromArgb(24, 24, 28) : Color.FromArgb(242, 242, 246); } }
        Color SubCol { get { return light ? Color.FromArgb(118, 118, 128) : Color.FromArgb(156, 156, 168); } }
        Color BlueCol { get { return Color.FromArgb(10, 132, 255); } }
        Color GreenCol { get { return Color.FromArgb(48, 209, 88); } }
        Color PurpleCol { get { return Color.FromArgb(94, 92, 230); } }
        Color OffCol { get { return light ? Color.FromArgb(220, 220, 228) : Color.FromArgb(68, 68, 78); } }
        Color HoverCol { get { return light ? Color.FromArgb(18, 0, 0, 0) : Color.FromArgb(24, 255, 255, 255); } }

        void LayoutCards()
        {
            float p = 12 * s, gap = 9 * s;
            float colW = (Width - p * 2 - gap) / 2f;
            float topH = 134 * s;

            rConnCard = new RectangleF(p, p, colW, topH);
            float rh = topH / 3f;
            rWifiRow = new RectangleF(rConnCard.X, rConnCard.Y, colW, rh);
            rBtRow = new RectangleF(rConnCard.X, rConnCard.Y + rh, colW, rh);
            rShareRow = new RectangleF(rConnCard.X, rConnCard.Y + rh * 2, colW, rh);

            float rx = p + colW + gap;
            float halfH = (topH - gap) / 2f;
            rDarkCard = new RectangleF(rx, p, colW, halfH);
            if (dndAvailable)
            {
                float miniW = (colW - gap) / 2f;
                rDndCard = new RectangleF(rx, p + halfH + gap, miniW, halfH);
                rDispCard = new RectangleF(rx + miniW + gap, p + halfH + gap, miniW, halfH);
            }
            else
            {
                rDndCard = RectangleF.Empty;
                rDispCard = new RectangleF(rx, p + halfH + gap, colW, halfH);
            }

            float y = p + topH + gap;
            float slCardH = 66 * s;
            rBrightCard = new RectangleF(p, y, Width - p * 2, slCardH);
            rBrightTrack = new RectangleF(rBrightCard.X + 12 * s, rBrightCard.Y + 31 * s, rBrightCard.Width - 24 * s, 24 * s);

            y += slCardH + gap;
            rSoundCard = new RectangleF(p, y, Width - p * 2, slCardH);
            rSoundTrack = new RectangleF(rSoundCard.X + 12 * s, rSoundCard.Y + 31 * s, rSoundCard.Width - 54 * s, 24 * s);
            rMuteBtn = new RectangleF(rSoundTrack.X, rSoundTrack.Y, 28 * s, rSoundTrack.Height);
            rAudioOutBtn = new RectangleF(rSoundCard.Right - 34 * s, rSoundTrack.Y, 24 * s, 24 * s);

            y += slCardH + gap;
            float mediaH = 58 * s;
            rMediaCard = new RectangleF(p, y, Width - p * 2, mediaH);
            float btnSz = 28 * s, btnY = rMediaCard.Y + (mediaH - btnSz) / 2f;
            rNextBtn = new RectangleF(rMediaCard.Right - 10 * s - btnSz, btnY, btnSz, btnSz);
            rPlayBtn = new RectangleF(rNextBtn.X - 4 * s - btnSz, btnY, btnSz, btnSz);
            rPrevBtn = new RectangleF(rPlayBtn.X - 4 * s - btnSz, btnY, btnSz, btnSz);
            float d = 36 * s;
            rMediaIcon = new RectangleF(rMediaCard.X + 11 * s, rMediaCard.Y + (mediaH - d) / 2f, d, d);

            y += mediaH + gap;
            rFootPower = new RectangleF(Width - p - 154 * s, y + 1 * s, 88 * s, 25 * s);
            rFootSnip = new RectangleF(Width - p - 60 * s, y + 1 * s, 60 * s, 25 * s);

            BuildPaths();
        }

        void BuildPaths()
        {
            DisposePaths();
            float rad = 12 * s;
            pConnCard = RoundPath(rConnCard, rad);
            pDarkCard = RoundPath(rDarkCard, rad);
            if (rDndCard.Width > 0) pDndCard = RoundPath(rDndCard, rad);
            pDispCard = RoundPath(rDispCard, rad);
            pBrightCard = RoundPath(rBrightCard, rad);
            pSoundCard = RoundPath(rSoundCard, rad);
            pMediaCard = RoundPath(rMediaCard, rad);
            pBrightTrack = RoundPath(rBrightTrack, rBrightTrack.Height / 2f);
            pSoundTrack = RoundPath(rSoundTrack, rSoundTrack.Height / 2f);
            pHover1 = RoundPath(RectangleF.Inflate(rWifiRow, -4 * s, -2 * s), 8 * s);
            pHover2 = RoundPath(RectangleF.Inflate(rBtRow, -4 * s, -2 * s), 8 * s);
            pHover3 = RoundPath(RectangleF.Inflate(rShareRow, -4 * s, -2 * s), 8 * s);
            pMediaIcon = RoundPath(rMediaIcon, 8 * s);
            pFootPower = RoundPath(rFootPower, rFootPower.Height / 2f);
            pFootSnip = RoundPath(rFootSnip, rFootSnip.Height / 2f);
        }

        static GraphicsPath RoundPath(RectangleF r, float rad)
        {
            var p = new GraphicsPath(); float d = rad * 2;
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }

        // ---- lectura del sistema en segundo plano ----
        sealed class Snap
        {
            public bool Fast, Slow, Media;   // que fase relleno este bloque (Apply es parcial)
            public bool ArtOnly;             // solo actualiza la caratula (reintento en segundo plano)
            public bool Desktop, NetOn, IsEth, Muted, Dnd, DndAvail, Share, ShareAvail, Playing, High, Charging, HasBattery;
            public float Volume, Brightness, RamUsed, RamTotal, BatteryPct;
            public int Wifi, Bt, Cpu, RamPct;
            public string Label, Ssid, BtDev, Title, App, ArtKey;
            public Bitmap Art;
        }

        bool PostSnap(Snap q)
        {
            bool posted = false;
            try
            {
                if (IsHandleCreated)
                {
                    lock (pendingSnaps) pendingSnaps.Add(q);
                    BeginInvoke(new Action<Snap>(Apply), q);
                    posted = true;
                }
            }
            catch { }
            finally
            {
                if (!posted && q.Art != null) { try { q.Art.Dispose(); } catch { } q.Art = null; }
            }
            return posted;
        }

        // Fase rapida (volumen, brillo, red, CPU/bateria) y fase lenta (antenas, SSID, Bluetooth, multimedia).
        void RefreshAllAsync()
        {
            if (System.Threading.Interlocked.CompareExchange(ref refreshFast, 1, 0) == 0)
            {
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    var q = new Snap { Fast = true };
                    try
                    {
                        q.Desktop = DeviceProfile.IsDesktop;
                        bool net, eth; string lbl;
                        SystemStats.GetNetworkInfo(out net, out eth, out lbl);
                        q.NetOn = net; q.IsEth = eth; q.Label = lbl;
                        bool mut;
                        q.Volume = AudioSessions.GetMasterVolume(out mut); q.Muted = mut;
                        q.Brightness = DisplayBrightness.Get();
                        if (q.Desktop)
                        {
                            int cpu, ramPct; float used, total;
                            SystemStats.SampleCpuAndRam(out cpu, out ramPct, out used, out total);
                            q.Cpu = cpu; q.RamPct = ramPct; q.RamUsed = used; q.RamTotal = total;
                            q.High = SystemStats.IsHighPerformancePlan();
                        }
                        else
                        {
                            var ps = SystemInformation.PowerStatus;
                            q.BatteryPct = ps.BatteryLifePercent;
                            q.Charging = ps.PowerLineStatus == PowerLineStatus.Online;
                            q.HasBattery = ps.BatteryChargeStatus != BatteryChargeStatus.NoSystemBattery && ps.BatteryLifePercent >= 0f && ps.BatteryLifePercent <= 1f;
                        }
                    }
                    catch (Exception ex) { Program.Log(ex); }
                    finally
                    {
                        PostSnap(q);
                        System.Threading.Interlocked.Exchange(ref refreshFast, 0);
                        RefreshSlowPhase();
                    }
                });
            }
        }

        void RefreshSlowPhase()
        {
            if (System.Threading.Interlocked.CompareExchange(ref refreshSlow, 1, 0) != 0) return;
            var q = new Snap { Slow = true };
            try
            {
                q.Wifi = SysToggles.GetRadio(SysToggles.WiFi);
                q.Bt = SysToggles.GetRadio(SysToggles.Bluetooth);
                q.Ssid = q.Wifi == 1 ? SysToggles.GetWifiName() : null;
                var bts = q.Bt == 1 ? SysToggles.GetBluetoothConnected() : new List<string>();
                q.BtDev = bts.Count == 0 ? null : bts.Count == 1 ? bts[0] : bts[0] + " +" + (bts.Count - 1);
                q.ShareAvail = SysToggles.NearShareAvailable;
                q.Share = q.ShareAvail && SysToggles.GetNearShare();
                q.Dnd = SysToggles.GetDnd();
                q.DndAvail = SysToggles.DndAvailable;

                string mt, ma; bool playing;
                string ak; Bitmap ab;
                MediaControl.GetArtwork(72, artKey, out ak, out mt, out ma, out playing, out ab);
                if (string.IsNullOrEmpty(mt)) MediaControl.GetNowPlaying(out mt, out ma, out playing);
                else if (ab == null) MaybeRetryArtwork(ak);
                q.Title = mt; q.App = ma; q.Playing = playing; q.ArtKey = ak; q.Art = ab;
            }
            catch (Exception ex) { Program.Log(ex); }
            finally { PostSnap(q); System.Threading.Interlocked.Exchange(ref refreshSlow, 0); }
        }

        // Spotify publica el titulo antes que la caratula. En vez de dormir 1,5 s dentro de la fase
        // lenta (retrasaba antenas y el resto), se programa un temporizador unico de la interfaz que
        // solo trae la caratula. Se puede llamar desde cualquier hilo: aqui se pasa al hilo del panel.
        void MaybeRetryArtwork(string key)
        {
            if (string.IsNullOrEmpty(key) || IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(new Action(() => ScheduleArtworkRetry(key))); } catch { }
        }

        void ScheduleArtworkRetry(string key)
        {
            if (IsDisposed || !IsHandleCreated || string.IsNullOrEmpty(key)) return;
            if (artRetryTimer != null && artRetryKey == key) return;   // ya hay uno pendiente para esta clave
            StopArtworkRetry();
            artRetryKey = key; artRetryStep = 0;
            artRetryTimer = new System.Windows.Forms.Timer { Interval = 1500 };
            artRetryTimer.Tick += ArtworkRetryTick;
            artRetryTimer.Start();
        }

        void StopArtworkRetry()
        {
            if (artRetryTimer != null) { try { artRetryTimer.Stop(); artRetryTimer.Dispose(); } catch { } artRetryTimer = null; }
            artRetryStep = 0; artRetryKey = null;
        }

        void ArtworkRetryTick(object sender, EventArgs e)
        {
            if (IsDisposed || artRetryKey == null || artRetryKey != artKey) { StopArtworkRetry(); return; }
            artRetryStep++;
            if (artRetryStep > 3 || art != null) { StopArtworkRetry(); return; }
            artRetryTimer.Interval = artRetryStep == 1 ? 3000 : 6000;   // esperas 1,5 -> 3 -> 6 s
            string gen = artRetryKey;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                // Sin Sleep: la espera la marca el temporizador; aqui solo se lee el sistema y se aplica.
                try
                {
                    string ak, mt, ma; bool playing; Bitmap ab;
                    MediaControl.GetArtwork(72, null, out ak, out mt, out ma, out playing, out ab);
                    if (ab == null) return;
                    if (ak != gen || IsDisposed || !IsHandleCreated) { try { ab.Dispose(); } catch { } return; }
                    PostSnap(new Snap { Media = true, ArtOnly = true, ArtKey = ak, Art = ab });
                }
                catch (Exception ex) { Program.Log(ex); }
            });
        }

        // Un solo punto de aplicacion: cada bloque actualiza solo los campos que trae.
        void Apply(Snap q)
        {
            lock (pendingSnaps) pendingSnaps.Remove(q);
            if (IsDisposed) { if (q.Art != null) { q.Art.Dispose(); q.Art = null; } return; }
            if (q.Fast)
            {
                isDesktop = q.Desktop;
                netOn = q.NetOn; isEthernet = q.IsEth; if (q.Label != null) netLabel = q.Label;
                muted = q.Muted; volume = q.Volume;
                brightness = q.Brightness;
                if (q.Desktop)
                {
                    cpuPct = q.Cpu; ramPct = q.RamPct; ramUsedGb = q.RamUsed; ramTotalGb = q.RamTotal;
                    highPerfPlan = q.High;
                }
                else { batteryPct = q.BatteryPct; charging = q.Charging; hasBattery = q.HasBattery; }
            }
            if (q.Slow)
            {
                if (!busyWifi) { wifiState = q.Wifi; wifiName = q.Ssid; }
                if (!busyBt) { btState = q.Bt; btDevice = q.BtDev; }
                shareAvailable = q.ShareAvail; shareOn = q.Share;
                if (dndAvailable != q.DndAvail)
                {
                    dndAvailable = q.DndAvail;
                    LayoutCards();
                }
                dndOn = q.Dnd;
            }
            if (q.Slow || q.Media)
            {
                if (!q.ArtOnly) { mediaTitle = q.Title; mediaApp = q.App; mediaPlaying = q.Playing; }
                if (q.ArtKey != artKey)
                {
                    artKey = q.ArtKey;
                    if (art != null) { art.Dispose(); art = null; }
                }
                if (q.Art != null) { if (art != null) art.Dispose(); art = q.Art; q.Art = null; }
            }
            SaveKnown();
            if (slowAppliedAt == 0 && (q.Slow || q.Media))
            {
                slowAppliedAt = DateTime.Now.Ticks;
                if (Diag != null && Diag()) Program.Log("CC: presentacion inicial " + ((firstPaintAt - openedAt) / (float)TimeSpan.TicksPerMillisecond).ToString("0") + " ms; actualizacion completa " + ((slowAppliedAt - openedAt) / (float)TimeSpan.TicksPerMillisecond).ToString("0") + " ms tras abrir");
            }
            Invalidate();
        }

        // Al encender/apagar antenas solo se relee lo necesario.
        void ReadRadiosAsync()
        {
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                int w = SysToggles.GetRadio(SysToggles.WiFi), b = SysToggles.GetRadio(SysToggles.Bluetooth);
                bool n, eth; string lbl;
                SystemStats.GetNetworkInfo(out n, out eth, out lbl);
                string ssid = w == 1 ? SysToggles.GetWifiName() : null;
                var bts = b == 1 ? SysToggles.GetBluetoothConnected() : new List<string>();
                string btName = bts.Count == 0 ? null : bts.Count == 1 ? bts[0] : bts[0] + " +" + (bts.Count - 1);
                try
                {
                    if (!IsHandleCreated) return;
                    BeginInvoke(new Action(() =>
                    {
                        if (IsDisposed) return;
                        if (!busyWifi) { wifiState = w; wifiName = ssid; }
                        if (!busyBt) { btState = b; btDevice = btName; }
                        netOn = n; isEthernet = eth; if (lbl != null) netLabel = lbl;
                        Invalidate();
                    }));
                }
                catch { }
            });
        }

        void ToggleRadio(int kind)
        {
            bool isWifi = kind == SysToggles.WiFi;
            int cur = isWifi ? wifiState : btState;
            if (cur < 0) return;
            bool want = cur != 1;
            if (isWifi) { wifiState = want ? 1 : 0; busyWifi = true; } else { btState = want ? 1 : 0; busyBt = true; }
            Invalidate();
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                bool ok = SysToggles.SetRadio(kind, want);
                if (want) System.Threading.Thread.Sleep(isWifi ? 2500 : 3500);
                try
                {
                    if (!IsHandleCreated) return;
                    BeginInvoke(new Action(() =>
                    {
                        if (IsDisposed) return;
                        busyWifi = false; busyBt = false;
                        if (!ok) MessageBox.Show(this, "Windows no dejo cambiar " + (isWifi ? "el Wi-Fi" : "el Bluetooth") + ".\nPuede que este bloqueado por el modo avion o por la configuracion del equipo.", "MacDock", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        ReadRadiosAsync();
                    }));
                }
                catch { }
            });
        }

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x80; cp.ClassStyle |= 0x20000; return cp; }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyDwmTheme();
            RefreshAllAsync();
        }

        void ApplyDwmTheme()
        {
            try
            {
                int round = 2; DwmSetWindowAttribute(Handle, 33, ref round, 4);
                int dark = light ? 0 : 1; DwmSetWindowAttribute(Handle, 20, ref dark, 4);
                int border = light ? 0x00CFCFD6 : 0x00484852; DwmSetWindowAttribute(Handle, 34, ref border, 4);
            }
            catch { }
        }

        protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); Close(); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Close(); base.OnKeyDown(e); }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            lock (pendingSnaps)
            {
                foreach (var q in pendingSnaps) if (q.Art != null) { try { q.Art.Dispose(); } catch { } q.Art = null; }
                pendingSnaps.Clear();
            }
            if (art != null) { try { art.Dispose(); } catch { } art = null; }
            StopArtworkRetry();
            DisposeResources();
            if (mediaSoonTimer != null) { try { mediaSoonTimer.Stop(); mediaSoonTimer.Dispose(); } catch { } mediaSoonTimer = null; }
            // El gestor GSMTC y sus RCWs no quedan referenciados fuera del panel abierto; el recorte
            // deja que el runtime suelte ensamblados y memoria que solo viven con el panel.
            try { MediaControl.ReleaseGs(); } catch { }
            try { Program.TrimMemory(); } catch { }
            base.OnFormClosed(e);
        }

        void DrawCard(Graphics g, GraphicsPath p, bool hov)
        {
            if (p == null) return;
            g.FillPath(B(CardBg), p);
            if (hov) g.FillPath(B(HoverCol), p);
            g.DrawPath(P(CardBorder), p);
        }

        void DrawCircleIconBtn(Graphics g, RectangleF r, string glyph, bool hov)
        {
            if (hov) g.FillEllipse(B(HoverCol), r);
            g.DrawString(glyph, fIcon, B(FgCol), r, sfCenter);
        }

        void DrawConnRow(Graphics g, RectangleF r, GraphicsPath hover, string glyph, string title, string status,
                         bool on, bool enabled, Color onCol, bool hov)
        {
            if (hov && enabled && hover != null) g.FillPath(B(HoverCol), hover);
            float d = 28 * s, cx = r.X + 10 * s, cy = r.Y + (r.Height - d) / 2f;
            Color circle = enabled ? (on ? onCol : OffCol) : OffCol;
            g.FillEllipse(B(circle), cx, cy, d, d);
            Color iconCol = enabled ? (on ? Color.White : FgCol) : SubCol;
            g.DrawString(glyph, fIcon, B(iconCol), new RectangleF(cx, cy, d, d), sfCenter);
            g.DrawString(title, fTitle, B(enabled ? FgCol : SubCol), cx + d + 9 * s, cy - 1 * s);
            g.DrawString(status, fSub, B(SubCol), cx + d + 9 * s, cy + 14 * s);
        }

        void DrawMiniCard(Graphics g, GraphicsPath p, RectangleF r, string glyph, string label, bool on, Color activeCol, bool hov)
        {
            if (p == null) return;
            if (on) { g.FillPath(B(activeCol), p); g.DrawPath(P(CardBorder), p); }
            else DrawCard(g, p, hov);
            if (hov && on) g.FillPath(B(HoverCol), p);
            Brush b = B(on ? Color.White : FgCol);
            g.DrawString(glyph, fIcon, b, new RectangleF(r.X, r.Y + 6 * s, r.Width, 24 * s), sfCenter);
            g.DrawString(label, fMini, b, new RectangleF(r.X, r.Bottom - 22 * s, r.Width, 18 * s), sfCenter);
        }

        void DrawPillSlider(Graphics g, RectangleF r, GraphicsPath track, float val, string glyph)
        {
            g.FillPath(B(light ? Color.FromArgb(222, 222, 230) : Color.FromArgb(64, 64, 74)), track);
            float fw = Math.Max(r.Height, r.Width * Math.Max(0f, Math.Min(1f, val)));
            var st = g.Save();
            g.SetClip(track);
            g.FillRectangle(B(light ? Color.FromArgb(255, 255, 255) : Color.FromArgb(240, 240, 245)), r.X, r.Y, fw, r.Height);
            g.Restore(st);
            g.DrawPath(P(light ? Color.FromArgb(35, 0, 0, 0) : Color.FromArgb(40, 255, 255, 255)), track);
            g.DrawString(glyph, fIcon, B(Color.FromArgb(95, 95, 105)), new RectangleF(r.X + 4 * s, r.Y, 24 * s, r.Height), sfCenter);
        }

        void DrawPillButton(Graphics g, GraphicsPath p, RectangleF r, string text, Color bg, Color border, Brush fg, Font f, bool hov)
        {
            g.FillPath(B(bg), p);
            if (hov) g.FillPath(B(HoverCol), p);
            g.DrawPath(P(border), p);
            g.DrawString(text, f, fg, r, sfCenter);
        }

        protected override void OnPaint(PaintEventArgs pe)
        {
            if (Width <= 0 || fTitle == null) return;
            if (firstPaintAt == 0) firstPaintAt = DateTime.Now.Ticks;
            var g = pe.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            Brush fgB = B(FgCol), subB = B(SubCol);

            // Brillo superior muy leve: da volumen al panel sin difuminado falso.
            g.FillRectangle(B(light ? Color.FromArgb(18, 255, 255, 255) : Color.FromArgb(10, 255, 255, 255)),
                            0, 0, Width, 64 * s);

            // 1) conectividad
            DrawCard(g, pConnCard, false);
            bool showEth = wifiState == -1 && isEthernet;
            string connTitle = showEth ? "Ethernet" : "Wi-Fi";
            string connGlyph = showEth ? "\uE839" : "\uE701";
            string wifiSub = showEth ? netLabel
                           : wifiState == -2 ? "..."
                           : wifiState == -1 ? "No disponible"
                           : wifiState == 0 ? "Desactivado"
                           : busyWifi ? "Conectando..."
                           : wifiName ?? "Activado, sin conexion";
            bool wifiOn = showEth ? netOn : wifiState == 1;
            bool wifiEnabled = wifiState >= 0 || showEth;
            string btSub = btState == -2 ? "..." : btState == -1 ? "No disponible" : btState == 0 ? "Desactivado"
                         : busyBt ? "Activando..." : btDevice ?? "Activado";
            DrawConnRow(g, rWifiRow, pHover1, connGlyph, connTitle, wifiSub, wifiOn, wifiEnabled, BlueCol, hoverPart == 1);
            DrawConnRow(g, rBtRow, pHover2, "\uE702", "Bluetooth", btSub, btState == 1, btState >= 0, BlueCol, hoverPart == 2);
            string shareSub = !shareAvailable ? "No disponible" : shareOn ? "Mis dispositivos" : "Desactivado";
            DrawConnRow(g, rShareRow, pHover3, "\uE72D", "Compartir", shareSub, shareOn, shareAvailable, BlueCol, hoverPart == 3);

            // 2) modo claro / oscuro
            DrawCard(g, pDarkCard, hoverPart == 7);
            {
                float d = 30 * s, cx = rDarkCard.X + 12 * s, cy = rDarkCard.Y + (rDarkCard.Height - d) / 2f;
                g.FillEllipse(B(!light ? BlueCol : Color.FromArgb(255, 159, 10)), cx, cy, d, d);
                g.DrawString(light ? "\uE706" : "\uE708", fIcon, Brushes.White, new RectangleF(cx, cy, d, d), sfCenter);
                g.DrawString(light ? "Modo claro" : "Modo oscuro", fTitle, fgB, cx + d + 10 * s, cy + 1 * s);
                g.DrawString("Cambiar tema", fSub, subB, cx + d + 10 * s, cy + 16 * s);
            }

            // 3) mini tarjetas
            if (dndAvailable) DrawMiniCard(g, pDndCard, rDndCard, "\uE708", "Enfoque", dndOn, PurpleCol, hoverPart == 8);
            DrawMiniCard(g, pDispCard, rDispCard, "\uE7F4", "Pantallas", false, BlueCol, hoverPart == 9);

            // 4) brillo
            DrawCard(g, pBrightCard, false);
            g.DrawString(isDesktop ? "Pantalla (Monitor)" : "Pantalla", fTitle, fgB, rBrightCard.X + 12 * s, rBrightCard.Y + 8 * s);
            g.DrawString((int)Math.Round(brightness * 100) + "%", fSub, subB, rBrightCard.Right - 42 * s, rBrightCard.Y + 9 * s);
            DrawPillSlider(g, rBrightTrack, pBrightTrack, brightness, "\uE706");

            // 5) sonido
            DrawCard(g, pSoundCard, false);
            g.DrawString("Sonido", fTitle, fgB, rSoundCard.X + 12 * s, rSoundCard.Y + 8 * s);
            g.DrawString(muted ? "Silenciado" : ((int)Math.Round(volume * 100) + "%"), fSub, subB, rSoundCard.Right - (muted ? 68 * s : 42 * s), rSoundCard.Y + 9 * s);
            string vGlyph = (muted || volume <= 0.01f) ? "\uE74F" : (volume < 0.45f ? "\uE993" : "\uE767");
            DrawPillSlider(g, rSoundTrack, pSoundTrack, muted ? 0f : volume, vGlyph);
            g.FillEllipse(B(OffCol), rAudioOutBtn);
            if (hoverPart == 10) g.FillEllipse(B(HoverCol), rAudioOutBtn);
            g.DrawString("\uE7F5", fIcon, fgB, rAudioOutBtn, sfCenter);

            // 6) ahora suena
            DrawCard(g, pMediaCard, false);
            if (art != null)
            {
                var stArt = g.Save();
                g.SetClip(pMediaIcon);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(art, rMediaIcon);
                g.Restore(stArt);
            }
            else
            {
                g.FillPath(B(mediaPlaying ? Color.FromArgb(255, 55, 95) : OffCol), pMediaIcon);
                g.DrawString("\uEC4F", fIcon, mediaPlaying ? Brushes.White : fgB, rMediaIcon, sfCenter);
            }
            float tx = rMediaIcon.Right + 10 * s, maxTw = Math.Max(40 * s, rPrevBtn.X - tx - 6 * s);
            g.DrawString(mediaTitle, fTitle, fgB, new RectangleF(tx, rMediaIcon.Y + 2 * s, maxTw, 18 * s), sfEllipsis);
            g.DrawString(mediaApp, fSub, subB, new RectangleF(tx, rMediaIcon.Y + 19 * s, maxTw, 16 * s), sfEllipsis);
            DrawCircleIconBtn(g, rPrevBtn, "\uE892", hoverPart == 4);
            DrawCircleIconBtn(g, rPlayBtn, mediaPlaying ? "\uE769" : "\uE768", hoverPart == 5);
            DrawCircleIconBtn(g, rNextBtn, "\uE893", hoverPart == 6);

            // 7) pie
            if (isDesktop)
            {
                g.DrawString("CPU " + cpuPct + "% · RAM " + ramUsedGb.ToString("0.0") + "/" + ramTotalGb.ToString("0") + " GB", fSub, subB, 14 * s, rFootSnip.Y + 5 * s);
                DrawPillButton(g, pFootPower, rFootPower, highPerfPlan ? "⚡ Turbo ON" : "Equilibrado",
                               highPerfPlan ? GreenCol : CardBg, CardBorder, highPerfPlan ? Brushes.White : fgB, fSub, hoverPart == 11);
            }
            else
            {
                string batTxt = hasBattery ? ("Batería " + (int)Math.Round(batteryPct * 100) + "% · " + (charging ? "Cargando" : "Batería")) : "Modo Laptop";
                g.DrawString(batTxt, fSub, subB, 14 * s, rFootSnip.Y + 5 * s);
                DrawPillButton(g, pFootPower, rFootPower, "Win + A", CardBg, CardBorder, fgB, fSub, hoverPart == 11);
            }
            DrawPillButton(g, pFootSnip, rFootSnip, "Captura", CardBg, CardBorder, fgB, fSub, hoverPart == 12);
        }

        void SetSliderFromMouse(int which, int mx)
        {
            var tr = which == 1 ? rBrightTrack : rSoundTrack;
            float v = Math.Max(0f, Math.Min(1f, (mx - tr.X) / tr.Width));
            if (which == 1)
            {
                brightness = Math.Max(0.10f, v);
                DisplayBrightness.Set(brightness);
            }
            else
            {
                volume = v;
                muted = v <= 0.005f;
                AudioSessions.SetMasterVolume(volume, muted);
            }
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (rMuteBtn.Contains(e.Location))
            {
                muted = !muted;
                AudioSessions.SetMasterVolume(volume, muted);
                Invalidate();
                return;
            }
            if (rBrightTrack.Contains(e.Location)) { dragSlider = 1; Capture = true; SetSliderFromMouse(1, e.X); return; }
            if (rSoundTrack.Contains(e.Location)) { dragSlider = 2; Capture = true; SetSliderFromMouse(2, e.X); return; }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragSlider != 0) { SetSliderFromMouse(dragSlider, e.X); return; }
            int hp = rWifiRow.Contains(e.Location) ? 1 : rBtRow.Contains(e.Location) ? 2 : rShareRow.Contains(e.Location) ? 3
                   : rPrevBtn.Contains(e.Location) ? 4 : rPlayBtn.Contains(e.Location) ? 5 : rNextBtn.Contains(e.Location) ? 6
                   : rDarkCard.Contains(e.Location) ? 7 : (dndAvailable && rDndCard.Contains(e.Location)) ? 8
                   : rDispCard.Contains(e.Location) ? 9 : rAudioOutBtn.Contains(e.Location) ? 10
                   : rFootPower.Contains(e.Location) ? 11 : rFootSnip.Contains(e.Location) ? 12 : 0;
            if (hp == 1 && !(wifiState >= 0 || isEthernet)) hp = 0;
            if (hp == 2 && btState < 0) hp = 0;
            if (hp == 3 && !shareAvailable) hp = 0;
            if (hp != hoverPart) { hoverPart = hp; Invalidate(); }
            bool hand = hp > 0 || rDarkCard.Contains(e.Location) || (dndAvailable && rDndCard.Contains(e.Location)) || rDispCard.Contains(e.Location)
                     || rBrightTrack.Contains(e.Location) || rSoundTrack.Contains(e.Location) || rAudioOutBtn.Contains(e.Location)
                     || rFootPower.Contains(e.Location) || rFootSnip.Contains(e.Location);
            Cursor = hand ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (dragSlider != 0) { dragSlider = 0; Capture = false; return; }
            if (e.Button != MouseButtons.Left) return;
            try
            {
                if (rWifiRow.Contains(e.Location))
                {
                    if (wifiState >= 0) ToggleRadio(SysToggles.WiFi);
                    else if (wifiState == -1) { Close(); Process.Start(isEthernet ? "ms-settings:network-ethernet" : "ms-settings:network-wifi"); }
                }
                else if (rBtRow.Contains(e.Location))
                {
                    if (btState >= 0) ToggleRadio(SysToggles.Bluetooth);
                    else if (btState == -1) { Close(); Process.Start("ms-settings:bluetooth"); }
                }
                else if (rShareRow.Contains(e.Location))
                {
                    if (shareAvailable)
                    {
                        bool want = !shareOn;
                        shareOn = want; Invalidate();   // respuesta visual inmediata
                        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                        {
                            bool ok = SysToggles.SetNearShare(want);
                            try { BeginInvoke(new Action(() => { if (!IsDisposed) { shareAvailable = ok ? shareAvailable : SysToggles.NearShareAvailable; Invalidate(); } })); } catch { }
                        });
                    }
                }
                else if (rDarkCard.Contains(e.Location))
                {
                    if (onToggleTheme != null) onToggleTheme();
                    light = !light;
                    BackColor = light ? Color.FromArgb(236, 236, 242) : Color.FromArgb(30, 30, 36);
                    ResetColors();
                    ApplyDwmTheme();
                    Invalidate();
                }
                else if (dndAvailable && rDndCard.Contains(e.Location))
                {
                    bool want = !dndOn;
                    dndOn = want; Invalidate();   // respuesta visual inmediata
                    System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                    {
                        bool ok = SysToggles.SetDnd(want);
                        try { BeginInvoke(new Action(() => { if (!IsDisposed) { if (!ok) { dndAvailable = SysToggles.DndAvailable; if (!dndAvailable) LayoutCards(); } Invalidate(); } })); } catch { }
                    });
                }
                else if (rDispCard.Contains(e.Location))
                {
                    Close();
                    try { Process.Start("DisplaySwitch.exe"); } catch { Process.Start("ms-settings:display"); }
                }
                else if (rAudioOutBtn.Contains(e.Location))
                {
                    Close();
                    // Selector nativo de salida (Windows 11). En versiones anteriores, ajustes de sonido.
                    if (Environment.OSVersion.Version.Build >= 22000) N.PressKeys(0x5B, 0x11, 0x56);
                    else try { Process.Start("ms-settings:sound"); } catch { }
                }
                else if (rPrevBtn.Contains(e.Location)) { MediaControl.PrevTrack(); RefreshMediaSoon(); }
                else if (rPlayBtn.Contains(e.Location)) { MediaControl.PlayPause(); mediaPlaying = !mediaPlaying; Invalidate(); RefreshMediaSoon(); }
                else if (rNextBtn.Contains(e.Location)) { MediaControl.NextTrack(); RefreshMediaSoon(); }
                else if (rFootPower.Contains(e.Location))
                {
                    if (isDesktop)
                    {
                        bool want = !highPerfPlan;
                        highPerfPlan = want; Invalidate();   // respuesta visual inmediata
                        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                        {
                            bool now = SystemStats.ToggleHighPerformancePlan();   // devuelve el estado nuevo
                            try { BeginInvoke(new Action(() => { if (!IsDisposed) { highPerfPlan = now; Invalidate(); } })); } catch { }
                        });
                    }
                    else { Close(); N.PressKeys(0x5B, 0x41); }
                }
                else if (rFootSnip.Contains(e.Location))
                {
                    Hide();
                    var snipTimer = new System.Windows.Forms.Timer { Interval = 180 };
                    snipTimer.Tick += (s2, a2) => { snipTimer.Stop(); snipTimer.Dispose(); Close(); N.PressKeys(0x5B, 0x10, 0x53); };
                    snipTimer.Start();
                }
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        // Un solo timer pendiente aunque se pulse varias veces: la relectura multimedia se hace
        // 250 ms despues del ultimo clic, no una cola por cada evento.
        System.Windows.Forms.Timer mediaSoonTimer;
        void RefreshMediaSoon()
        {
            if (mediaSoonTimer != null) return;
            mediaSoonTimer = new System.Windows.Forms.Timer { Interval = 250 };
            mediaSoonTimer.Tick += (o, a) =>
            {
                mediaSoonTimer.Stop(); mediaSoonTimer.Dispose(); mediaSoonTimer = null;
                if (IsDisposed) return;
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    string mt, ma; bool playing;
                    string ak; Bitmap ab;
                    MediaControl.GetArtwork(72, artKey, out ak, out mt, out ma, out playing, out ab);
                    if (string.IsNullOrEmpty(mt)) MediaControl.GetNowPlaying(out mt, out ma, out playing);
                    else if (ab == null) MaybeRetryArtwork(ak);
                    var q = new Snap { Media = true, Title = mt, App = ma, Playing = playing, ArtKey = ak, Art = ab };
                    PostSnap(q);
                });
            };
            mediaSoonTimer.Start();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            float step = (e.Delta > 0 ? 0.05f : -0.05f);
            if (rBrightCard.Contains(e.Location))
            {
                brightness = Math.Max(0.10f, Math.Min(1f, brightness + step));
                DisplayBrightness.Set(brightness);
                Invalidate();
            }
            else if (rSoundCard.Contains(e.Location))
            {
                volume = Math.Max(0f, Math.Min(1f, volume + step));
                muted = volume <= 0.005f;
                AudioSessions.SetMasterVolume(volume, muted);
                Invalidate();
            }
        }
    }
}
