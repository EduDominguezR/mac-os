using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MacDock
{
    // "App Nap" estilo Mac: con bateria, las apps que no estas usando pasan a modo eficiencia
    // (EcoQoS de Windows 11). La app en primer plano y las que estan sonando nunca se frenan.
    class AppNap
    {
        [StructLayout(LayoutKind.Sequential)]
        struct PROCESS_POWER_THROTTLING_STATE { public uint Version, ControlMask, StateMask; }

        [DllImport("kernel32.dll")] static extern bool SetProcessInformation(IntPtr h, int cls, ref PROCESS_POWER_THROTTLING_STATE info, int size);

        static readonly HashSet<string> NeverNap = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "explorer.exe", "macdock.exe", "spotify.exe", "obs64.exe", "obs32.exe", "zoom.exe", "teams.exe", "ms-teams.exe",
            "discord.exe", "vlc.exe", "wmplayer.exe", "music.ui.exe", "audacity.exe", "steam.exe", "steamwebhelper.exe",
            "windowsterminal.exe", "openconsole.exe", "conhost.exe", "cmd.exe", "powershell.exe", "pwsh.exe",
            "docker desktop.exe", "com.docker.backend.exe", "vmmem.exe", "surfshark.exe", "taskmgr.exe", "dwm.exe"
        };

        // pid -> exe que dejamos en modo eficiencia
        Dictionary<int, string> napped = new Dictionary<int, string>();

        static bool SetEco(int pid, bool on)
        {
            IntPtr h = N.OpenProcess(0x0200 | 0x1000, false, (uint)pid);
            if (h == IntPtr.Zero) return false;
            try
            {
                var s = new PROCESS_POWER_THROTTLING_STATE { Version = 1, ControlMask = 1, StateMask = on ? 1u : 0u };
                return SetProcessInformation(h, 4, ref s, Marshal.SizeOf(typeof(PROCESS_POWER_THROTTLING_STATE)));
            }
            finally { N.CloseHandle(h); }
        }

        // appExes: exes de las apps con ventana (las que muestra el dock). fgExe: la app que estas usando.
        public void Update(ICollection<string> appExes, string fgExe)
        {
            var audible = AudioSessions.ActivePids();
            var audibleExes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var all = Process.GetProcesses();
            var pathOf = new Dictionary<int, string>();
            foreach (var p in all)
            {
                string path = "";
                try { path = N.ProcessPath((uint)p.Id); } catch { }
                pathOf[p.Id] = path;
                if (audible.Contains((uint)p.Id) && path != "") audibleExes.Add(path);
                p.Dispose();
            }

            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var exe in appExes)
            {
                if (string.IsNullOrEmpty(exe)) continue;
                if (NeverNap.Contains(Path.GetFileName(exe))) continue;
                if (string.Equals(exe, fgExe, StringComparison.OrdinalIgnoreCase)) continue;
                if (audibleExes.Contains(exe)) continue;
                targets.Add(exe);
            }

            var keep = new Dictionary<int, string>();
            foreach (var kv in pathOf)
            {
                if (kv.Value == "" || !targets.Contains(kv.Value)) continue;
                if (!napped.ContainsKey(kv.Key)) SetEco(kv.Key, true);
                keep[kv.Key] = kv.Value;
            }
            foreach (var kv in napped)
                if (!keep.ContainsKey(kv.Key) && pathOf.ContainsKey(kv.Key)) SetEco(kv.Key, false);
            napped = keep;
        }

        public void WakeAll()
        {
            foreach (var pid in napped.Keys) { try { SetEco(pid, false); } catch { } }
            napped.Clear();
        }

        public int Count { get { return napped.Count; } }
    }

    // Que procesos estan reproduciendo sonido ahora mismo (Core Audio).
    static class AudioSessions
    {
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCo { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int flow, int mask, out IntPtr devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice dev);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o);
        }

        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionManager2
        {
            [PreserveSig] int GetAudioSessionControl(IntPtr g, int f, out IntPtr c);
            [PreserveSig] int GetSimpleAudioVolume(IntPtr g, int f, out IntPtr v);
            [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator e);
        }

        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionEnumerator
        {
            [PreserveSig] int GetCount(out int n);
            [PreserveSig] int GetSession(int i, out IAudioSessionControl2 s);
        }

        [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionControl2
        {
            [PreserveSig] int GetState(out int state);
            [PreserveSig] int GetDisplayName(out IntPtr s);
            [PreserveSig] int SetDisplayName(IntPtr s, IntPtr g);
            [PreserveSig] int GetIconPath(out IntPtr s);
            [PreserveSig] int SetIconPath(IntPtr s, IntPtr g);
            [PreserveSig] int GetGroupingParam(out Guid g);
            [PreserveSig] int SetGroupingParam(IntPtr g, IntPtr c);
            [PreserveSig] int RegisterAudioSessionNotification(IntPtr n);
            [PreserveSig] int UnregisterAudioSessionNotification(IntPtr n);
            [PreserveSig] int GetSessionIdentifier(out IntPtr s);
            [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr s);
            [PreserveSig] int GetProcessId(out uint pid);
        }

        public static HashSet<uint> ActivePids()
        {
            var set = new HashSet<uint>();
            IMMDeviceEnumerator en = null; IMMDevice dev = null; IAudioSessionManager2 mgr = null; IAudioSessionEnumerator se = null;
            try
            {
                en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();
                if (en.GetDefaultAudioEndpoint(0, 1, out dev) != 0 || dev == null) return set;
                Guid iid = typeof(IAudioSessionManager2).GUID; object o;
                if (dev.Activate(ref iid, 23, IntPtr.Zero, out o) != 0) return set;
                mgr = (IAudioSessionManager2)o;
                if (mgr.GetSessionEnumerator(out se) != 0 || se == null) return set;
                int n; se.GetCount(out n);
                for (int i = 0; i < n; i++)
                {
                    IAudioSessionControl2 s;
                    if (se.GetSession(i, out s) != 0 || s == null) continue;
                    int st; uint pid;
                    if (s.GetState(out st) == 0 && st == 1 && s.GetProcessId(out pid) == 0 && pid != 0) set.Add(pid);
                    Marshal.ReleaseComObject(s);
                }
            }
            catch { }
            finally
            {
                if (se != null) Marshal.ReleaseComObject(se);
                if (mgr != null) Marshal.ReleaseComObject(mgr);
                if (dev != null) Marshal.ReleaseComObject(dev);
                if (en != null) Marshal.ReleaseComObject(en);
            }
            return set;
        }

        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioEndpointVolume
        {
            [PreserveSig] int RegisterControlChangeNotify(IntPtr pNotify);
            [PreserveSig] int UnregisterControlChangeNotify(IntPtr pNotify);
            [PreserveSig] int GetChannelCount(out uint pnChannelCount);
            [PreserveSig] int SetMasterVolumeLevel(float fLevelDB, ref Guid pguidEventContext);
            [PreserveSig] int SetMasterVolumeLevelScalar(float fLevel, ref Guid pguidEventContext);
            [PreserveSig] int GetMasterVolumeLevel(out float pfLevelDB);
            [PreserveSig] int GetMasterVolumeLevelScalar(out float pfLevel);
            [PreserveSig] int SetChannelVolumeLevel(uint nChannel, float fLevelDB, ref Guid pguidEventContext);
            [PreserveSig] int SetChannelVolumeLevelScalar(uint nChannel, float fLevel, ref Guid pguidEventContext);
            [PreserveSig] int GetChannelVolumeLevel(uint nChannel, out float pfLevelDB);
            [PreserveSig] int GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid pguidEventContext);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
        }

        public static float GetMasterVolume(out bool muted)
        {
            muted = false;
            IMMDeviceEnumerator en = null; IMMDevice dev = null; IAudioEndpointVolume ep = null;
            try
            {
                en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();
                if (en.GetDefaultAudioEndpoint(0, 1, out dev) != 0 || dev == null) return 0.5f;
                Guid iid = typeof(IAudioEndpointVolume).GUID; object o;
                if (dev.Activate(ref iid, 23, IntPtr.Zero, out o) != 0 || o == null) return 0.5f;
                ep = (IAudioEndpointVolume)o;
                float lvl = 0.5f;
                ep.GetMasterVolumeLevelScalar(out lvl);
                ep.GetMute(out muted);
                return Math.Max(0f, Math.Min(1f, lvl));
            }
            catch { return 0.5f; }
            finally
            {
                if (ep != null) Marshal.ReleaseComObject(ep);
                if (dev != null) Marshal.ReleaseComObject(dev);
                if (en != null) Marshal.ReleaseComObject(en);
            }
        }

        // Volumen: trabajador serializado con intervalo ~50 ms y ultimo valor ganador. El slider
        // responde al instante (solo toca el campo y repinta) y la operacion COM va en segundo
        // plano: los objetos COM se crean y se liberan en el propio hilo del trabajador, sin
        // cruzar hilos sin marshaling. Nunca se crea un hilo nuevo por evento del usuario.
        static readonly object VolSync = new object();
        static System.Threading.AutoResetEvent volWake;
        static System.Threading.Thread volWorker;
        static float volPending;
        static bool? volMutePending;
        static bool volHasPending;
        static DateTime volLastApply = DateTime.MinValue;

        public static void SetMasterVolume(float level, bool? mute = null)
        {
            lock (VolSync)
            {
                volPending = Math.Max(0f, Math.Min(1f, level));
                volMutePending = mute;
                volHasPending = true;
                if (volWorker == null || !volWorker.IsAlive)
                {
                    volWake = new System.Threading.AutoResetEvent(false);
                    volWorker = new System.Threading.Thread(VolumeLoop);
                    volWorker.IsBackground = true; volWorker.Name = "MacDockVolume"; volWorker.Start();
                }
            }
            try { volWake.Set(); } catch { }
        }

        static void VolumeLoop()
        {
            while (true)
            {
                volWake.WaitOne();
                while (true)
                {
                    int wait = 50 - (int)(DateTime.Now - volLastApply).TotalMilliseconds;
                    if (wait > 0) System.Threading.Thread.Sleep(wait);
                    float lvl; bool? mut;
                    lock (VolSync)
                    {
                        lvl = volPending; mut = volMutePending; volHasPending = false;
                    }
                    ApplyVolume(lvl, mut);
                    volLastApply = DateTime.Now;
                    lock (VolSync) { if (!volHasPending) break; }
                }
            }
        }

        static void ApplyVolume(float level, bool? mute)
        {
            IMMDeviceEnumerator en = null; IMMDevice dev = null; IAudioEndpointVolume ep = null;
            try
            {
                en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();
                if (en.GetDefaultAudioEndpoint(0, 1, out dev) != 0 || dev == null) return;
                Guid iid = typeof(IAudioEndpointVolume).GUID; object o;
                if (dev.Activate(ref iid, 23, IntPtr.Zero, out o) != 0 || o == null) return;
                ep = (IAudioEndpointVolume)o;
                Guid ctx = Guid.Empty;
                if (level >= 0f) ep.SetMasterVolumeLevelScalar(Math.Max(0f, Math.Min(1f, level)), ref ctx);
                if (mute.HasValue) ep.SetMute(mute.Value, ref ctx);
            }
            catch { }
            finally
            {
                if (ep != null) Marshal.ReleaseComObject(ep);
                if (dev != null) Marshal.ReleaseComObject(dev);
                if (en != null) Marshal.ReleaseComObject(en);
            }
        }
    }

    // Brillo real de pantalla. Adaptado del Centro de Control de MacDock (MIT). Ver LICENSE.
    // 1) Laptop: driver \\.\LCD. 2) Escritorio: DDC/CI. 3) Respaldo: rampa gamma de la GPU.
    // Los cambios se agrupan: el ultimo valor gana y nunca se llama al hardware mas de una vez cada 50 ms.
    static class DisplayBrightness
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(IntPtr dev, uint code, byte[] inBuf, int inSize, byte[] outBuf, int outSize, out int ret, IntPtr ovl);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        struct PHYSICAL_MONITOR
        {
            public IntPtr hPhysicalMonitor;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szPhysicalMonitorDescription;
        }
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(N.POINT pt, uint flags);
        [DllImport("dxva2.dll", SetLastError = true)] static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMon, out uint num);
        [DllImport("dxva2.dll", SetLastError = true)] static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMon, uint num, [Out] PHYSICAL_MONITOR[] mons);
        [DllImport("dxva2.dll", SetLastError = true)] static extern bool DestroyPhysicalMonitors(uint num, [In] PHYSICAL_MONITOR[] mons);
        [DllImport("dxva2.dll", SetLastError = true)] static extern bool GetMonitorBrightness(IntPtr hPhysicalMonitor, out uint min, out uint cur, out uint max);
        [DllImport("dxva2.dll", SetLastError = true)] static extern bool SetMonitorBrightness(IntPtr hPhysicalMonitor, uint brightness);
        [DllImport("gdi32.dll")] static extern bool SetDeviceGammaRamp(IntPtr hdc, ref RAMP lpRamp);
        [DllImport("gdi32.dll")] static extern bool GetDeviceGammaRamp(IntPtr hdc, [Out] out RAMP lpRamp);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateDCW(string driver, string device, string port, IntPtr devmode);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public uint StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplayDevices(string device, uint num, ref DISPLAY_DEVICE dd, uint flags);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        struct RAMP
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public ushort[] Red;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public ushort[] Green;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public ushort[] Blue;
        }

        static readonly object Sync = new object();
        static readonly object GammaLock = new object();
        static readonly System.Threading.AutoResetEvent Wake = new System.Threading.AutoResetEvent(false);
        static System.Threading.Thread worker;
        static float pending = -1f, lastVal = 1f;
        static DateTime lastApply = DateTime.MinValue;
        static bool initialised, gammaActive;

        static string Marker { get { return Path.Combine(Program.DataDir, "gamma.marker"); } }

        // Se llama al arrancar la app. Si el proceso anterior murio con gamma aplicada, la deshace.
        public static void Init()
        {
            if (initialised) return;
            initialised = true;
            RestoreGamma();
            try { AppDomain.CurrentDomain.ProcessExit += (o, e) => RestoreGamma(); } catch { }
            try { AppDomain.CurrentDomain.UnhandledException += (o, e) => RestoreGamma(); } catch { }
        }

        public static float Get()
        {
            float v;
            if (TryGetHardware(out v)) { lastVal = Clamp(v); return lastVal; }
            return lastVal;
        }

        // Ultimo valor gana: la UI puede llamar en cada pixel del arrastre sin saturar el monitor.
        public static void Set(float val)
        {
            val = Clamp(val);
            lock (Sync)
            {
                lastVal = val; pending = val;
                if (worker == null || !worker.IsAlive)
                {
                    worker = new System.Threading.Thread(Loop);
                    worker.IsBackground = true; worker.Name = "MacDockBrightness"; worker.Start();
                }
            }
            Wake.Set();
        }

        static float Clamp(float v) { return Math.Max(0.10f, Math.Min(1f, v)); }

        static void Loop()
        {
            while (true)
            {
                Wake.WaitOne();
                while (true)
                {
                    int wait = 50 - (int)(DateTime.Now - lastApply).TotalMilliseconds;
                    if (wait > 0) System.Threading.Thread.Sleep(wait);
                    float v;
                    lock (Sync) { v = pending; pending = -1f; }
                    if (v < 0f) break;
                    lastApply = DateTime.Now;
                    Apply(v);
                    lock (Sync) { if (pending < 0f) break; }
                }
            }
        }

        static void Apply(float val)
        {
            if (TrySetHardware(val))
            {
                // Si antes hizo falta gamma, se devuelve la tabla original en cuanto DDC/CI responde.
                if (gammaActive || File.Exists(Marker)) RestoreGamma();
                return;
            }
            ApplyGamma(val);
        }

        // 1) panel interno del laptop (\\.\LCD), 2) DDC/CI del monitor bajo el cursor
        static bool TryGetHardware(out float value)
        {
            value = lastVal;
            IntPtr h = CreateFile(@"\\.\LCD", 0x80000000 | 0x40000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h != IntPtr.Zero && h != new IntPtr(-1))
            {
                try
                {
                    var buf = new byte[3]; int ret;
                    if (DeviceIoControl(h, 0x230498, null, 0, buf, 3, out ret, IntPtr.Zero) && ret >= 3)
                    {
                        byte b = System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Offline ? buf[2] : buf[1];
                        value = b / 100f; return true;
                    }
                }
                catch { }
                finally { N.CloseHandle(h); }
            }
            try
            {
                var cp = System.Windows.Forms.Cursor.Position;
                IntPtr hMon = MonitorFromPoint(new N.POINT { x = cp.X, y = cp.Y }, 2);
                uint count;
                if (hMon != IntPtr.Zero && GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out count) && count > 0)
                {
                    var mons = new PHYSICAL_MONITOR[count];
                    if (GetPhysicalMonitorsFromHMONITOR(hMon, count, mons))
                    {
                        try
                        {
                            uint min, cur, max;
                            if (GetMonitorBrightness(mons[0].hPhysicalMonitor, out min, out cur, out max) && max > min)
                            {
                                value = (float)(cur - min) / (max - min); return true;
                            }
                        }
                        finally { DestroyPhysicalMonitors(count, mons); }
                    }
                }
            }
            catch { }
            return false;
        }

        static bool TrySetHardware(float val)
        {
            byte b = (byte)Math.Round(Clamp(val) * 100f);
            IntPtr h = CreateFile(@"\\.\LCD", 0x80000000 | 0x40000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h != IntPtr.Zero && h != new IntPtr(-1))
            {
                try
                {
                    var buf = new byte[] { 3, b, b }; int ret;
                    if (DeviceIoControl(h, 0x23049C, buf, 3, null, 0, out ret, IntPtr.Zero)) return true;
                }
                catch { }
                finally { N.CloseHandle(h); }
            }
            bool ok = false;
            try
            {
                var cp = System.Windows.Forms.Cursor.Position;
                IntPtr hMon = MonitorFromPoint(new N.POINT { x = cp.X, y = cp.Y }, 2);
                uint count;
                if (hMon != IntPtr.Zero && GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out count) && count > 0)
                {
                    var mons = new PHYSICAL_MONITOR[count];
                    if (GetPhysicalMonitorsFromHMONITOR(hMon, count, mons))
                    {
                        try
                        {
                            for (int i = 0; i < count; i++)
                            {
                                uint min = 0, cur = 50, max = 100;
                                GetMonitorBrightness(mons[i].hPhysicalMonitor, out min, out cur, out max);
                                if (max <= min) { min = 0; max = 100; }
                                uint target = (uint)Math.Round(min + (max - min) * Clamp(val));
                                if (SetMonitorBrightness(mons[i].hPhysicalMonitor, target)) ok = true;
                            }
                        }
                        finally { DestroyPhysicalMonitors(count, mons); }
                    }
                }
            }
            catch { }
            return ok;
        }

        // ---- gamma por pantalla ----
        // gamma.marker v2: primera linea "V2" y una linea por pantalla "<dispositivo>|<rampa en
        // base64>" con la tabla ORIGINAL leida con GetDeviceGammaRamp antes de la primera
        // modificacion. El marcador antiguo (solo fecha) no guarda la tabla: no se puede
        // reconstruir la original, asi que no se inventa una (ver RestoreGamma).
        // Cada pantalla se lee y se aplica con su propio contexto de dispositivo.
        static readonly HashSet<string> gammaOff = new HashSet<string>();   // pantallas sin tabla fiable
        static bool gammaBlocked;   // marcador legacy irrecuperable: no tocar la gamma en esta sesion

        static List<string> DisplayDevices()
        {
            var list = new List<string>();
            try
            {
                for (uint i = 0; ; i++)
                {
                    var dd = new DISPLAY_DEVICE(); dd.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
                    if (!EnumDisplayDevices(null, i, ref dd, 0)) break;
                    if ((dd.StateFlags & 0x1) != 0) list.Add(dd.DeviceName);   // ATTACHED_TO_DESKTOP
                }
            }
            catch { }
            return list;
        }

        // DC de la pantalla indicada (o del escritorio virtual si no se pudo identificar).
        static IntPtr DcOf(string dev)
        {
            if (dev == null) return N.GetDC(IntPtr.Zero);
            IntPtr h = CreateDCW(dev, null, null, IntPtr.Zero);
            return h != IntPtr.Zero ? h : N.GetDC(IntPtr.Zero);
        }
        static void FreeDc(string dev, IntPtr hdc)
        {
            if (hdc == IntPtr.Zero) return;
            if (dev == null) N.ReleaseDC(IntPtr.Zero, hdc);
            else DeleteDC(hdc);
        }

        // El driver puede negarse o devolver basura: sin rampa creciente y no nula no hay tabla fiable.
        static bool RampLooksValid(RAMP r)
        {
            try { return r.Red[255] > 0 && r.Green[255] > 0 && r.Blue[255] > 0 && r.Red[255] > r.Red[0]; }
            catch { return false; }
        }

        static string ToB64(RAMP r)
        {
            var b = new byte[1536];
            for (int i = 0; i < 256; i++)
            {
                int o = i * 2;
                b[o] = (byte)(r.Red[i] & 0xFF); b[o + 1] = (byte)(r.Red[i] >> 8);
                b[512 + o] = (byte)(r.Green[i] & 0xFF); b[512 + o + 1] = (byte)(r.Green[i] >> 8);
                b[1024 + o] = (byte)(r.Blue[i] & 0xFF); b[1024 + o + 1] = (byte)(r.Blue[i] >> 8);
            }
            return Convert.ToBase64String(b);
        }

        static RAMP FromB64(string s)
        {
            var b = Convert.FromBase64String(s);
            if (b.Length != 1536) throw new FormatException("rampa de longitud " + b.Length);
            var r = new RAMP { Red = new ushort[256], Green = new ushort[256], Blue = new ushort[256] };
            for (int i = 0; i < 256; i++)
            {
                int o = i * 2;
                r.Red[i] = (ushort)(b[o] | (b[o + 1] << 8));
                r.Green[i] = (ushort)(b[512 + o] | (b[512 + o + 1] << 8));
                r.Blue[i] = (ushort)(b[1024 + o] | (b[1024 + o + 1] << 8));
            }
            return r;
        }

        static void ApplyGamma(float val)
        {
            lock (GammaLock)
            {
                try
                {
                    // 1) si hay restauracion pendiente, se hace primero: nunca se guarda como
                    //    original una tabla que ya fue alterada.
                    if (File.Exists(Marker))
                    {
                        RestoreGammaLocked();
                        if (File.Exists(Marker)) return;   // no se pudo restaurar (o legacy): no tocar la gamma
                    }
                    if (gammaBlocked) return;
                    // 2) tabla original por pantalla
                    var entries = new List<string>();
                    bool any = false;
                    foreach (var dev in DisplayDevices())
                    {
                        if (gammaOff.Contains(dev ?? "")) continue;
                        IntPtr hdc = DcOf(dev);
                        if (hdc == IntPtr.Zero) { gammaOff.Add(dev ?? ""); continue; }
                        try
                        {
                            RAMP orig;
                            if (!GetDeviceGammaRamp(hdc, out orig) || !RampLooksValid(orig))
                            {
                                gammaOff.Add(dev ?? "");
                                Program.Log("gamma: el controlador no devolvio una tabla original fiable para " + (dev ?? "escritorio virtual") +
                                            "; el brillo por gamma queda desactivado en esa pantalla (DDC/CI sigue disponible)");
                                continue;
                            }
                            entries.Add((dev ?? "(virtual)") + "|" + ToB64(orig));
                            any = true;
                        }
                        finally { FreeDc(dev, hdc); }
                    }
                    if (!any) return;
                    // 3) marcador con flush a disco, versionado y por pantalla
                    var txt = new System.Text.StringBuilder("V2\r\n");
                    foreach (var e in entries) txt.Append(e).Append("\r\n");
                    N.WriteMarkerFile(Marker, txt.ToString());
                    // 4) solo entonces se aplica la nueva gamma, pantalla a pantalla
                    bool ok = true;
                    foreach (var e in entries)
                    {
                        int cut = e.LastIndexOf('|');
                        string dev = e.Substring(0, cut);
                        RAMP r = FromB64(e.Substring(cut + 1));
                        double factor = 0.45 + 0.55 * Clamp(val);
                        for (int i = 0; i < 256; i++)
                        {
                            int v = (int)Math.Min(65535, Math.Max(0, Math.Round(i * 256.0 * factor)));
                            r.Red[i] = r.Green[i] = r.Blue[i] = (ushort)v;
                        }
                        IntPtr hdc = DcOf(dev == "(virtual)" ? null : dev);
                        if (hdc == IntPtr.Zero) { ok = false; continue; }
                        try { if (!SetDeviceGammaRamp(hdc, ref r)) ok = false; }
                        finally { FreeDc(dev == "(virtual)" ? null : dev, hdc); }
                    }
                    gammaActive = ok;
                    if (!ok) { try { File.Delete(Marker); } catch { } }   // la pantalla quedo intacta: no hace falta marca
                }
                catch (Exception ex) { Program.Log(ex); }
            }
        }

        // Solo toca la gamma si existe la marca: nunca se altera una tabla que no puso MacDock.
        // Se aplica exactamente la tabla guardada, pantalla a pantalla; el marcador solo se borra
        // si todas las restauraciones tuvieron exito.
        public static void RestoreGamma() { lock (GammaLock) RestoreGammaLocked(); }

        static void RestoreGammaLocked()
        {
            if (!File.Exists(Marker)) { gammaActive = false; return; }
            try
            {
                string[] lines = File.ReadAllLines(Marker);
                if (lines.Length == 0 || lines[0] != "V2")
                {
                    // Marcador antiguo (solo fecha): no hay forma de reconstruir la tabla que nunca
                    // se guardo. No se inventa una tabla lineal, no se borra ni se sobrescribe el
                    // marcador y no se hacen nuevas modificaciones de gamma en esta sesion.
                    gammaActive = false;
                    if (!gammaBlocked)
                        Program.Log("LEGACY_GAMMA_MARKER_UNRECOVERABLE: gamma.marker antiguo (solo fecha) sin tabla original; " +
                                    "no se restaura ni se vuelve a modificar la gamma por software en esta sesion (DDC/CI sigue disponible)");
                    gammaBlocked = true;
                    return;
                }
                bool ok = true;
                for (int li = 1; li < lines.Length; li++)
                {
                    string line = lines[li].Trim();
                    if (line.Length == 0) continue;
                    try
                    {
                        int cut = line.IndexOf('|');
                        if (cut <= 0) { ok = false; continue; }
                        string dev = line.Substring(0, cut);
                        RAMP r = FromB64(line.Substring(cut + 1));
                        IntPtr hdc = DcOf(dev == "(virtual)" ? null : dev);
                        if (hdc == IntPtr.Zero) { ok = false; continue; }
                        try { if (!SetDeviceGammaRamp(hdc, ref r)) ok = false; }
                        finally { FreeDc(dev == "(virtual)" ? null : dev, hdc); }
                    }
                    catch (Exception ex) { ok = false; Program.Log(ex); }
                }
                if (ok) { File.Delete(Marker); gammaActive = false; }
                else Program.Log("gamma.marker: restauracion incompleta; el marcador se conserva para reintentar");
            }
            catch (Exception ex) { Program.Log(ex); }
        }
    }

    // Telemetria instantanea (CPU, RAM, red y plan de energia). Solo se consulta bajo demanda,
    // desde el hilo de fondo del Centro de Control. Adaptado de MacDock (MIT). Ver LICENSE.
    static class SystemStats
    {
        [StructLayout(LayoutKind.Sequential)]
        struct MEMORYSTATUSEX
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
        [DllImport("powrprof.dll")] static extern uint PowerGetActiveScheme(IntPtr RootPowerKey, out IntPtr ActivePolicyGuid);
        [DllImport("powrprof.dll")] static extern uint PowerSetActiveScheme(IntPtr RootPowerKey, ref Guid SchemeGuid);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr hMem);

        static ulong prevIdle, prevKernel, prevUser;
        static int lastCpu = 8;

        public static void SampleCpuAndRam(out int cpuPct, out int ramPct, out float ramUsedGb, out float ramTotalGb)
        {
            cpuPct = lastCpu; ramPct = 0; ramUsedGb = 0f; ramTotalGb = 16f;
            try
            {
                ulong idle, kernel, user;
                if (GetSystemTimes(out idle, out kernel, out user))
                {
                    ulong dIdle = idle - prevIdle, dKernel = kernel - prevKernel, dUser = user - prevUser;
                    ulong total = dKernel + dUser;
                    if (prevKernel != 0 && total > 0)
                        lastCpu = (int)Math.Max(1, Math.Min(100, Math.Round((1.0 - (double)dIdle / total) * 100.0)));
                    prevIdle = idle; prevKernel = kernel; prevUser = user;
                    cpuPct = lastCpu;
                }
            }
            catch { }
            try
            {
                var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
                if (GlobalMemoryStatusEx(ref ms) && ms.ullTotalPhys > 0)
                {
                    ramPct = (int)ms.dwMemoryLoad;
                    ramTotalGb = (float)(ms.ullTotalPhys / (1024.0 * 1024.0 * 1024.0));
                    ramUsedGb = (float)((ms.ullTotalPhys - ms.ullAvailPhys) / (1024.0 * 1024.0 * 1024.0));
                }
            }
            catch { }
        }

        static readonly Guid GuidHighPerf = new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
        static readonly Guid GuidUltimate = new Guid("e9a42b02-d5df-448d-aa00-03f14749eb61");
        static readonly Guid GuidBalanced = new Guid("381b4222-f694-41f0-9685-ff5bb260df2e");
        static readonly Guid GuidPowerSaver = new Guid("a1841308-3541-4fab-bc81-f71556f20b4a");
        static Guid savedTurboGuid = GuidHighPerf;

        public static bool IsHighPerformancePlan()
        {
            IntPtr ptr = IntPtr.Zero;
            try
            {
                if (PowerGetActiveScheme(IntPtr.Zero, out ptr) == 0 && ptr != IntPtr.Zero)
                {
                    Guid g = (Guid)Marshal.PtrToStructure(ptr, typeof(Guid));
                    if (g != GuidBalanced && g != GuidPowerSaver) { savedTurboGuid = g; return true; }
                    return false;
                }
            }
            catch { }
            finally { if (ptr != IntPtr.Zero) LocalFree(ptr); }
            return false;
        }

        public static bool ToggleHighPerformancePlan()
        {
            try
            {
                if (IsHighPerformancePlan())
                {
                    Guid b = GuidBalanced;
                    PowerSetActiveScheme(IntPtr.Zero, ref b);
                    return false;
                }
                Guid t = savedTurboGuid;
                if (PowerSetActiveScheme(IntPtr.Zero, ref t) == 0) return true;
                Guid h = GuidHighPerf;
                PowerSetActiveScheme(IntPtr.Zero, ref h);
                return true;
            }
            catch { return false; }
        }

        // Detecta si la red activa es por cable Ethernet o Wi-Fi.
        public static void GetNetworkInfo(out bool connected, out bool isEthernet, out string label)
        {
            connected = false; isEthernet = false; label = "Desconectado";
            try
            {
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    var t = ni.NetworkInterfaceType;
                    if (t == System.Net.NetworkInformation.NetworkInterfaceType.Loopback ||
                        t == System.Net.NetworkInformation.NetworkInterfaceType.Tunnel) continue;
                    connected = true;
                    if (t == System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211)
                    {
                        isEthernet = false; label = "Wi-Fi conectado"; return;
                    }
                    if (t == System.Net.NetworkInformation.NetworkInterfaceType.Ethernet ||
                        t == System.Net.NetworkInformation.NetworkInterfaceType.GigabitEthernet ||
                        t == System.Net.NetworkInformation.NetworkInterfaceType.FastEthernetT)
                    {
                        isEthernet = true; label = "Cable LAN activo";
                    }
                }
            }
            catch { }
        }
    }

    // Controles multimedia ("Ahora suena"). Adaptado de MacDock (MIT). Ver LICENSE.
    // Una sola pasada por procesos (Process.Dispose en todos) y sin 12 GetProcessesByName seguidos.
    static class MediaControl
    {
        [DllImport("user32.dll")] static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        public static void PlayPause() { SendMediaKey(0xB3); }
        public static void NextTrack() { SendMediaKey(0xB0); }
        public static void PrevTrack() { SendMediaKey(0xB1); }

        static void SendMediaKey(byte vk)
        {
            try { keybd_event(vk, 0, 0, UIntPtr.Zero); keybd_event(vk, 0, 2, UIntPtr.Zero); } catch { }
        }

        // ---- Caratula via GSMTC (WinRT por reflexion) ----
        // Solo se carga la primera vez que se abre el panel; nunca al arrancar MacDock.
        static readonly object GsLock = new object();
        static Type gsMgrT, gsPropsT, gsStreamT, gsRefT;
        static object gsMgr;
        static MethodInfo asTaskOp, asStreamOp;
        static DateTime gsBrokenAt = DateTime.MinValue;   // un fallo no es permanente: se reintenta a los 30 s

        // La reflexion se resuelve una sola vez por tipo y nombre: MethodInfo/PropertyInfo y el
        // AsTask<T> construido quedan cacheados. Antes cada refresco repetia GetMethod, GetProperty,
        // MakeGenericMethod y la busqueda de la propiedad Result del Task.
        static readonly Dictionary<Type, MethodInfo> asTaskFor = new Dictionary<Type, MethodInfo>();
        static readonly Dictionary<Type, PropertyInfo> resultOfTask = new Dictionary<Type, PropertyInfo>();
        static readonly Dictionary<string, MethodInfo> methodCache = new Dictionary<string, MethodInfo>(StringComparer.Ordinal);
        static readonly Dictionary<string, PropertyInfo> propCache = new Dictionary<string, PropertyInfo>(StringComparer.Ordinal);

        static MethodInfo MethodOf(Type t, string name)
        {
            if (t == null) return null;
            string k = t.FullName + "|" + name;
            lock (GsLock)
            {
                MethodInfo m;
                if (!methodCache.TryGetValue(k, out m)) { m = t.GetMethod(name); methodCache[k] = m; }
                return m;
            }
        }

        static PropertyInfo PropOf(Type t, string name)
        {
            if (t == null) return null;
            string k = t.FullName + "|" + name;
            lock (GsLock)
            {
                PropertyInfo p;
                if (!propCache.TryGetValue(k, out p)) { p = t.GetProperty(name); propCache[k] = p; }
                return p;
            }
        }

        static MethodInfo AsTaskFor(Type resultType)
        {
            lock (GsLock)
            {
                MethodInfo m;
                if (!asTaskFor.TryGetValue(resultType, out m)) { m = asTaskOp.MakeGenericMethod(resultType); asTaskFor[resultType] = m; }
                return m;
            }
        }

        static object ResultOf(System.Threading.Tasks.Task t)
        {
            Type tt = t.GetType();
            PropertyInfo p;
            lock (GsLock)
            {
                if (!resultOfTask.TryGetValue(tt, out p)) { p = tt.GetProperty("Result"); resultOfTask[tt] = p; }
            }
            return p == null ? null : p.GetValue(t, null);
        }

        static bool EnsureGs()
        {
            lock (GsLock)
            {
                if (gsBrokenAt != DateTime.MinValue)
                {
                    if ((DateTime.Now - gsBrokenAt).TotalSeconds < 30) return false;
                    gsBrokenAt = DateTime.MinValue;
                }
                if (gsMgrT != null && asStreamOp != null && asTaskOp != null && gsRefT != null) return true;
                string missing = null;
                try
                {
                    var rt = Assembly.Load("System.Runtime.WindowsRuntime, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089");
                    // AsTask esta en System.WindowsRuntimeSystemExtensions; AsStreamForRead en System.IO.WindowsRuntimeStreamExtensions.
                    var ext = rt.GetType("System.WindowsRuntimeSystemExtensions");
                    if (ext == null) missing += "System.WindowsRuntimeSystemExtensions; ";
                    else foreach (var m in ext.GetMethods())
                    {
                        if (asTaskOp == null && m.Name == "AsTask" && m.IsGenericMethod && m.GetParameters().Length == 1 &&
                            m.GetParameters()[0].ParameterType.Name == "IAsyncOperation`1") asTaskOp = m;
                    }
                    if (asTaskOp == null) missing += "AsTask(IAsyncOperation<T>); ";
                    var sext = rt.GetType("System.IO.WindowsRuntimeStreamExtensions");
                    if (sext == null) missing += "System.IO.WindowsRuntimeStreamExtensions; ";
                    else foreach (var m in sext.GetMethods())
                    {
                        if (asStreamOp == null && m.Name == "AsStreamForRead" && !m.IsGenericMethod && m.GetParameters().Length == 1) asStreamOp = m;
                    }
                    if (asStreamOp == null) missing += "AsStreamForRead(IRandomAccessStream); ";
                    gsMgrT = Type.GetType("Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager, Windows, ContentType=WindowsRuntime", false);
                    if (gsMgrT == null) missing += "GlobalSystemMediaTransportControlsSessionManager; ";
                    gsPropsT = Type.GetType("Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties, Windows, ContentType=WindowsRuntime", false);
                    if (gsPropsT == null) missing += "GlobalSystemMediaTransportControlsSessionMediaProperties; ";
                    gsStreamT = Type.GetType("Windows.Storage.Streams.IRandomAccessStreamWithContentType, Windows, ContentType=WindowsRuntime", false);
                    if (gsStreamT == null) missing += "IRandomAccessStreamWithContentType; ";
                    gsRefT = Type.GetType("Windows.Storage.Streams.IRandomAccessStreamReference, Windows, ContentType=WindowsRuntime", false);
                    if (gsRefT == null) missing += "IRandomAccessStreamReference; ";
                    if (missing != null)
                    {
                        Program.Log("GSMTC no disponible, falta: " + missing.TrimEnd(' ', ';') + " (se reintenta en 30 s)");
                        gsBrokenAt = DateTime.Now; gsMgrT = null; return false;
                    }
                    return true;
                }
                catch (Exception ex) { Program.Log(ex); gsBrokenAt = DateTime.Now; gsMgrT = null; return false; }
            }
        }

        static object AwaitOp(object op, Type resultType)
        {
            if (op == null) return null;
            var t = (System.Threading.Tasks.Task)AsTaskFor(resultType).Invoke(null, new[] { op });
            if (t == null || !t.Wait(4000)) return null;
            return ResultOf(t);
        }

        static object GsManager()
        {
            // Nunca se mantiene GsLock durante RequestAsync (puede tardar): se resuelve fuera y
            // solo se guarda el resultado dentro del candado.
            lock (GsLock) { if (gsMgr != null) return gsMgr; }
            if (!EnsureGs()) return null;
            object mgr = AwaitOp(MethodOf(gsMgrT, "RequestAsync").Invoke(null, null), gsMgrT);
            if (mgr == null) return null;
            lock (GsLock)
            {
                if (gsMgr == null) gsMgr = mgr;
                return gsMgr;
            }
        }

        // Devuelve los datos de la sesion GSMTC actual y, si la clave cambio, una copia propia de la
        // caratula (72 px por defecto). El stream de WinRT se cierra siempre con IDisposable.
        public static void GetArtwork(int size, string knownKey, out string key, out string title, out string artist,
                                      out bool playing, out Bitmap art)
        {
            key = null; title = null; artist = null; playing = false; art = null;
            if (size <= 0) size = 72;
            try
            {
                object mgr = GsManager();
                if (mgr == null) return;
                object session = MethodOf(gsMgrT, "GetCurrentSession").Invoke(mgr, null);
                if (session == null) return;
                object info = null, props = null, thumb = null, rs = null;
                try
                {
                    try
                    {
                        info = MethodOf(session.GetType(), "GetPlaybackInfo").Invoke(session, null);
                        object st = info == null ? null : PropOf(info.GetType(), "PlaybackStatus").GetValue(info, null);
                        if (st != null) playing = Convert.ToInt32(st) == 4;   // Playing
                    }
                    catch { }
                    props = AwaitOp(MethodOf(session.GetType(), "TryGetMediaPropertiesAsync").Invoke(session, null), gsPropsT);
                    if (props == null) return;

                    title = (string)PropOf(gsPropsT, "Title").GetValue(props, null);
                    artist = (string)PropOf(gsPropsT, "Artist").GetValue(props, null);
                    string album = (string)PropOf(gsPropsT, "AlbumTitle").GetValue(props, null);
                    key = (title ?? "") + "|" + (artist ?? "") + "|" + (album ?? "");
                    if (key == "||") { key = null; return; }
                    if (knownKey != null && key == knownKey) return;

                    thumb = PropOf(gsPropsT, "Thumbnail").GetValue(props, null);
                    // Sin caratula se devuelve la clave igualmente: el panel programa un unico reintento
                    // a los ~1,5 s en una tarea aparte (asi no retrasa antenas ni el resto de la fase lenta).
                    if (thumb == null) return;
                    rs = AwaitOp(MethodOf(gsRefT, "OpenReadAsync").Invoke(thumb, null), gsStreamT);
                    if (rs == null) { key = null; return; }
                    var disp = rs as IDisposable;
                    try
                    {
                        using (Stream s = (Stream)asStreamOp.Invoke(null, new[] { rs }))
                        using (var ms = new MemoryStream())
                        {
                            s.CopyTo(ms); ms.Position = 0;
                            using (var src = Image.FromStream(ms))
                            {
                                var bmp = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
                                try
                                {
                                    using (var g = Graphics.FromImage(bmp))
                                    {
                                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                                        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                                        g.Clear(Color.Transparent);
                                        float sc = Math.Max((float)size / src.Width, (float)size / src.Height);
                                        int w = (int)(src.Width * sc), h = (int)(src.Height * sc);
                                        g.DrawImage(src, (size - w) / 2, (size - h) / 2, w, h);
                                    }
                                    art = bmp;
                                }
                                catch { bmp.Dispose(); throw; }
                            }
                        }
                    }
                    finally
                    {
                        if (disp != null) { try { disp.Dispose(); } catch { } }
                        else { try { var close = MethodOf(rs.GetType(), "Close"); if (close != null) close.Invoke(rs, null); } catch { } }
                    }
                }
                finally
                {
                    // RCWs creados en esta llamada: se liberan aqui y no esperan al finalizador.
                    FreeRcw(rs); FreeRcw(thumb); FreeRcw(props); FreeRcw(info); FreeRcw(session);
                }
            }
            catch (Exception ex) { Program.Log(ex); if (art != null) { art.Dispose(); art = null; } }
        }

        // Suelta un RCW propio de esta llamada (nunca compartido). Sin excepciones: si ya esta muerto, no importa.
        static void FreeRcw(object o)
        {
            if (o == null || o is string) return;
            try { Marshal.ReleaseComObject(o); } catch { }
        }

        // Suelta el gestor GSMTC cuando se cierra el panel: no queda referenciado fuera del panel
        // abierto. La siguiente apertura vuelve a pedirlo con RequestAsync.
        public static void ReleaseGs()
        {
            lock (GsLock)
            {
                if (gsMgr == null) return;
                try { Marshal.FinalReleaseComObject(gsMgr); } catch { }
                gsMgr = null;
            }
        }

        public static void GetNowPlaying(out string title, out string app, out bool playing)
        {
            title = "Reproductor multimedia";
            app = "Control de música y video";
            playing = false;
            try
            {
                var activePids = AudioSessions.ActivePids();
                playing = activePids.Count > 0;
                foreach (var pid in activePids)
                {
                    Process p = null;
                    try
                    {
                        p = Process.GetProcessById((int)pid);
                        string t = p.MainWindowTitle;
                        if (!string.IsNullOrEmpty(t))
                        {
                            FormatTrack(t, p.ProcessName, out title, out app);
                            return;
                        }
                    }
                    catch { }
                    finally { if (p != null) p.Dispose(); }
                }

                Process[] all = null;
                try
                {
                    all = Process.GetProcesses();
                    foreach (var p in all)
                    {
                        string pn, t;
                        try
                        {
                            pn = p.ProcessName;
                            // Leer MainWindowTitle recorre las ventanas de cada proceso: solo vale la
                            // pena en procesos que pueden estar reproduciendo algo.
                            if (!IsMediaProcess(pn)) continue;
                            t = p.MainWindowTitle;
                        }
                        catch { continue; }
                        if (string.IsNullOrEmpty(t)) continue;
                        FormatTrack(t, pn, out title, out app);
                        return;
                    }
                }
                finally { if (all != null) foreach (var p in all) p.Dispose(); }
            }
            catch { }
        }

        static bool IsMediaProcess(string pn)
        {
            pn = pn.ToLowerInvariant();
            return pn.Contains("spotify") || pn.Contains("applemusic") || pn.Contains("music.ui") || pn.Contains("vlc") ||
                   pn.Contains("foobar") || pn.Contains("aimp") || pn.Contains("wmplayer") || pn.Contains("chrome") ||
                   pn.Contains("msedge") || pn.Contains("brave") || pn.Contains("firefox") || pn.Contains("opera");
        }

        static void FormatTrack(string rawTitle, string procName, out string title, out string app)
        {
            title = rawTitle.Trim();
            string pn = procName.ToLowerInvariant();
            if (pn.Contains("spotify")) app = "Spotify";
            else if (pn.Contains("music")) app = "Música";
            else if (pn.Contains("vlc")) app = "VLC";
            else if (pn.Contains("chrome")) app = "Chrome";
            else if (pn.Contains("msedge")) app = "Edge";
            else if (pn.Contains("brave")) app = "Brave";
            else if (pn.Contains("firefox")) app = "Firefox";
            else app = procName;

            string[] suffixes = { " - Google Chrome", " - Microsoft\u200B Edge", " - Microsoft Edge", " - Brave", " - YouTube", " — Mozilla Firefox", " - VLC media player" };
            foreach (var sf in suffixes)
                if (title.EndsWith(sf, StringComparison.OrdinalIgnoreCase))
                    title = title.Substring(0, title.Length - sf.Length).Trim();
        }
    }

    // Esquinas activas: arriba-izquierda = ver todas las ventanas (como Mission Control),
    // abajo-izquierda = mostrar el escritorio. Hay que dejar el mouse quieto un instante en la esquina.
    class HotCorners
    {
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        DateTime enteredAt = DateTime.MaxValue; int corner = 0; bool fired = false;

        // con la barra superior activa, el logo esta arriba-izquierda: la esquina de ventanas pasa a arriba-derecha
        public void Tick(Screen scr, bool menuBarOn)
        {
            var c = Cursor.Position; var b = scr.Bounds;
            int now = 0;
            if (!menuBarOn && c.X <= b.Left + 1 && c.Y <= b.Top + 1) now = 1;
            else if (menuBarOn && c.X >= b.Right - 2 && c.Y <= b.Top + 1) now = 1;
            else if (c.X <= b.Left + 1 && c.Y >= b.Bottom - 2) now = 2;
            bool buttons = (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0;
            if (now == 0 || buttons) { corner = 0; fired = false; enteredAt = DateTime.MaxValue; return; }
            if (now != corner) { corner = now; fired = false; enteredAt = DateTime.Now; return; }
            if (fired || (DateTime.Now - enteredAt).TotalMilliseconds < 350) return;
            fired = true;
            if (corner == 1) N.PressKeys(0x5B, 0x09);      // Win+Tab: vista de todas las ventanas
            else N.PressKeys(0x5B, 0x44);                  // Win+D: escritorio
        }
    }
}
