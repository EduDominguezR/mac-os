using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace MacDock
{
    sealed class DockPreview : Form
    {
        sealed class Entry
        {
            public IntPtr Window, Thumbnail;
            public uint Pid, Tid;
            public string Title;
            public bool Minimized;
            public Rectangle Tile;

            public bool IsCurrent()
            {
                uint pid;
                int cloaked;
                return N.GetWindowThreadProcessId(Window, out pid) == Tid && pid == Pid &&
                    N.IsWindowVisible(Window) && (N.DwmGetWindowAttribute(Window, 14, out cloaked, 4) != 0 || cloaked == 0);
            }
        }

        readonly List<Entry> entries = new List<Entry>(4);
        readonly HashSet<IntPtr> sources = new HashSet<IntPtr>();
        readonly Image fallback;
        readonly Font caption;
        readonly SolidBrush tileBrush;
        readonly Pen outline;
        readonly int titleIconSize, titlePadding;
        readonly Action<IntPtr> activate;
        readonly Action dismiss;
        readonly Func<Point, bool> overIcon;
        readonly int pad, titleHeight, imageHeight, tileWidth, gap, counterWidth;
        int hovered = -1;
        volatile bool invalidated;
        public bool IsEmpty { get { return entries.Count == 0; } }
        public bool IsUnderPointer(Point p)
        {
            return IsHandleCreated && Bounds.Contains(p) && Handle == N.WindowFromPoint(new N.POINT { x = p.X, y = p.Y });
        }
        public bool AffectsSource(IntPtr hwnd)
        {
            if (!sources.Contains(hwnd)) return false;
            invalidated = true;
            return true;
        }

        public DockPreview(DockItem item, Rectangle icon, float scale, Action<IntPtr> activate,
            Action dismiss, Func<Point, bool> overIcon)
        {
            this.activate = activate;
            this.dismiss = dismiss;
            this.overIcon = overIcon;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            string appName = string.IsNullOrWhiteSpace(item.Name) ? Path.GetFileNameWithoutExtension(item.Target) : item.Name;
            Text = "Vista previa de " + appName;
            BackColor = Color.FromArgb(32, 32, 36);
            Opacity = 0.97;

            foreach (var hwnd in item.Windows)
            {
                uint pid;
                uint tid = N.GetWindowThreadProcessId(hwnd, out pid);
                if (tid == 0 || !N.IsWindowVisible(hwnd) || !sources.Add(hwnd)) continue;
                if (entries.Count == 4) continue;
                var title = N.TitleOf(hwnd);
                entries.Add(new Entry { Window = hwnd, Tid = tid, Pid = pid, Minimized = N.IsIconic(hwnd),
                    Title = title.Length == 0 ? appName : title });
            }
            if (IsEmpty) return;

            Rectangle work = Screen.FromRectangle(icon).WorkingArea;
            pad = (int)Math.Round(10 * scale);
            gap = (int)Math.Round(8 * scale);
            titleHeight = (int)Math.Round(27 * scale);
            imageHeight = (int)Math.Round(88 * scale);
            counterWidth = sources.Count > entries.Count ? (int)Math.Round(42 * scale) : 0;
            int spacing = 2 * pad + gap * (entries.Count - 1) + counterWidth;
            tileWidth = Math.Min((int)Math.Round(152 * scale), Math.Max(1, (work.Width - spacing) / entries.Count));
            int width = spacing + tileWidth * entries.Count;
            int height = 2 * pad + titleHeight + imageHeight;
            int x = Math.Max(work.Left, Math.Min(work.Right - width, icon.Left + (icon.Width - width) / 2));
            Bounds = new Rectangle(x, Math.Max(work.Top, icon.Top + 2 - height), width, height);
            int radius = Math.Max(2, (int)Math.Round(12 * scale));
            using (var path = new GraphicsPath())
            {
                int d = 2 * radius;
                path.AddArc(0, 0, d, d, 180, 90);
                path.AddArc(width - d, 0, d, d, 270, 90);
                path.AddArc(width - d, height - d, d, d, 0, 90);
                path.AddArc(0, height - d, d, d, 90, 90);
                path.CloseFigure();
                Region = new Region(path);
            }
            caption = new Font("Segoe UI", 12 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            tileBrush = new SolidBrush(Color.FromArgb(18, 18, 21));
            outline = new Pen(Color.FromArgb(168, 168, 178));
            titleIconSize = Math.Max(12, (int)Math.Round(14 * scale));
            titlePadding = Math.Max(4, (int)Math.Round(6 * scale));
            int iconSize = Math.Max(24, (int)Math.Round(30 * scale));
            if (item.Icon != null) fallback = new Bitmap(item.Icon, new Size(iconSize, iconSize));
            for (int i = 0; i < entries.Count; i++)
                entries[i].Tile = new Rectangle(pad + i * (tileWidth + gap), pad, tileWidth, titleHeight + imageHeight);
        }

        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle = (cp.ExStyle & ~0x40000) | 0x80 | 0x08000000; // TOOLWINDOW sin APPWINDOW
                return cp;
            }
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (!Visible) { ReleaseThumbnails(); return; }
            foreach (var entry in entries) RegisterThumbnail(entry);
        }

        void RegisterThumbnail(Entry entry)
        {
            if (!entry.IsCurrent() || N.IsIconic(entry.Window)) return;
            IntPtr thumb;
            if (N.DwmRegisterThumbnail(Handle, entry.Window, out thumb) != 0) return;
            try
            {
                N.SIZE size;
                if (N.DwmQueryThumbnailSourceSize(thumb, out size) != 0 || size.cx < 2 || size.cy < 2) return;
                double factor = Math.Min((tileWidth - 8.0) / size.cx, (imageHeight - 8.0) / size.cy);
                int w = Math.Max(1, (int)(factor * size.cx)), h = Math.Max(1, (int)(factor * size.cy));
                int x = entry.Tile.Left + (tileWidth - w) / 2, y = entry.Tile.Top + (imageHeight - h) / 2;
                var props = new N.DWM_THUMBNAIL_PROPERTIES {
                    dwFlags = N.DWM_TNP_RECTDESTINATION | N.DWM_TNP_OPACITY | N.DWM_TNP_VISIBLE,
                    rcDestination = new N.RECT { left = x, top = y, right = x + w, bottom = y + h },
                    opacity = 255, fVisible = true
                };
                if (N.DwmUpdateThumbnailProperties(thumb, ref props) == 0)
                {
                    entry.Thumbnail = thumb;
                    thumb = IntPtr.Zero;
                }
            }
            finally { if (thumb != IntPtr.Zero) N.DwmUnregisterThumbnail(thumb); }
        }

        void ReleaseThumbnails()
        {
            foreach (var entry in entries)
            {
                if (entry.Thumbnail == IntPtr.Zero) continue;
                N.DwmUnregisterThumbnail(entry.Thumbnail);
                entry.Thumbnail = IntPtr.Zero;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (IsEmpty) return;
            const TextFormatFlags flags = TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis |
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix;
            const TextFormatFlags centered = flags | TextFormatFlags.HorizontalCenter;
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                var r = entry.Tile;
                e.Graphics.FillRectangle(tileBrush, r.X + 1, r.Y + 1, r.Width - 2, imageHeight - 1);
                if (hovered == i) e.Graphics.DrawRectangle(outline, r.X, r.Y, r.Width - 1, r.Height - 1);
                if (entry.Thumbnail == IntPtr.Zero)
                {
                    int size = titleIconSize * 2;
                    int y = r.Y + (imageHeight - size - (entry.Minimized ? titleHeight : 0)) / 2;
                    DrawIcon(e.Graphics, new Rectangle(r.X + (r.Width - size) / 2, y, size, size));
                    if (entry.Minimized)
                        TextRenderer.DrawText(e.Graphics, "Minimizada", caption,
                            new Rectangle(r.X, y + size, r.Width, titleHeight), Color.Gainsboro, centered);
                }
                int titleX = r.X + titlePadding;
                DrawIcon(e.Graphics, new Rectangle(titleX, r.Y + imageHeight + (titleHeight - titleIconSize) / 2,
                    titleIconSize, titleIconSize));
                titleX += titleIconSize + titlePadding;
                TextRenderer.DrawText(e.Graphics, entry.Title, caption,
                    new Rectangle(titleX, r.Y + imageHeight, Math.Max(1, r.Right - titleX - titlePadding), titleHeight),
                    Color.White, flags);
            }
            if (counterWidth != 0)
                TextRenderer.DrawText(e.Graphics, "+" + (sources.Count - entries.Count), caption,
                    new Rectangle(Width - pad - counterWidth, pad, counterWidth, imageHeight), Color.White, centered);
        }

        void DrawIcon(Graphics g, Rectangle rect)
        {
            if (fallback != null) g.DrawImage(fallback, rect);
            else g.DrawIcon(SystemIcons.Application, rect);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int hit = entries.FindIndex(entry => entry.Tile.Contains(e.Location));
            if (hit != hovered) { hovered = hit; Invalidate(); }
            Cursor = hit >= 0 ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (!overIcon(Cursor.Position)) dismiss();
            else if (hovered != -1) { hovered = -1; Invalidate(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            var entry = entries.Find(candidate => candidate.Tile.Contains(e.Location));
            if (entry == null) return;
            if (!invalidated && entry.IsCurrent()) activate(entry.Window); else dismiss();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x21) { m.Result = new IntPtr(3); return; } // WM_MOUSEACTIVATE / MA_NOACTIVATE
            base.WndProc(ref m);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ReleaseThumbnails();
                if (caption != null) caption.Dispose();
                if (tileBrush != null) tileBrush.Dispose();
                if (outline != null) outline.Dispose();
                if (fallback != null) fallback.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
