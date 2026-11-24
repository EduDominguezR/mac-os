using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace MacDock
{
    // Interruptores reales del Centro de Control: Wi-Fi, Bluetooth, Compartir en proximidad y No molestar.
    // Adaptado del Centro de Control de MacDock (MIT). Ver LICENSE.
    // Wi-Fi y Bluetooth usan la API oficial de Windows (Windows.Devices.Radios), cargada por reflexion
    // para seguir compilando con el csc de .NET Framework que trae Windows, sin dependencias.
    // Las lecturas se cachean 5-8 s y se reintenta la inicializacion cada 30 s si Windows la bloquea.
    static class SysToggles
    {
        public const int WiFi = 1, Bluetooth = 3;          // Windows.Devices.Radios.RadioKind
        const int StateOn = 1, StateOff = 2;               // Windows.Devices.Radios.RadioState

        static readonly object Lock = new object();
        static bool initDone, initOk, initRunning;
        static DateTime initAt = DateTime.MinValue;
        static Type radioT, accessT, stateT;
        static MethodInfo asTaskOp;

        // cache de lecturas (5 s antenas, 8 s nombres)
        static int wifiCache = -2, btCache = -2;
        static DateTime wifiAt = DateTime.MinValue, btAt = DateTime.MinValue;
        static string wifiName; static DateTime wifiNameAt = DateTime.MinValue;
        static List<string> btNames; static DateTime btNamesAt = DateTime.MinValue;
        static bool dndOk = true;
        static DateTime dndRetryAt = DateTime.MinValue;

        static bool Init()
        {
            lock (Lock)
            {
                if (initOk) return true;
                // si fallo (modo avion, servicio lento...), reintentar cada 30 s
                if (initRunning) return false;
                if (initDone && (DateTime.Now - initAt).TotalSeconds < 30) return false;
                initDone = true; initAt = DateTime.Now; initRunning = true;
            }
            bool ok = false;
            // El Await (hasta 5 s) no puede ejecutarse con Lock: GetDnd y los demas no deben esperar.
            try
            {
                radioT = Type.GetType("Windows.Devices.Radios.Radio, Windows, ContentType=WindowsRuntime", true);
                accessT = Type.GetType("Windows.Devices.Radios.RadioAccessStatus, Windows, ContentType=WindowsRuntime", true);
                stateT = Type.GetType("Windows.Devices.Radios.RadioState, Windows, ContentType=WindowsRuntime", true);
                var rt = Assembly.Load("System.Runtime.WindowsRuntime, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089");
                asTaskOp = FindAsTask(rt.GetType("System.WindowsRuntimeSystemExtensions"));
                if (asTaskOp == null) throw new InvalidOperationException("AsTask no disponible");
                Await(radioT.GetMethod("RequestAccessAsync").Invoke(null, null), accessT);
                ok = true;
            }
            catch (Exception ex) { Program.Log(ex); }
            lock (Lock) { initOk = ok; initAt = DateTime.Now; initRunning = false; }
            return ok;
        }

        // Sin Linq: solo hay un metodo AsTask generico con un parametro IAsyncOperation<T>.
        static MethodInfo FindAsTask(Type t)
        {
            foreach (var m in t.GetMethods())
            {
                if (m.Name != "AsTask" || !m.IsGenericMethod) continue;
                var ps = m.GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType.Name == "IAsyncOperation`1") return m;
            }
            return null;
        }

        static object Await(object op, Type resultType)
        {
            var t = (Task)asTaskOp.MakeGenericMethod(resultType).Invoke(null, new[] { op });
            if (!t.Wait(5000)) return null;
            return t.GetType().GetProperty("Result").GetValue(t, null);
        }

        static object FindRadio(int kind)
        {
            var listT = typeof(IReadOnlyList<>).MakeGenericType(radioT);
            var radios = Await(radioT.GetMethod("GetRadiosAsync").Invoke(null, null), listT) as IEnumerable;
            if (radios == null) return null;
            var kindProp = radioT.GetProperty("Kind");
            foreach (var r in radios) if (Convert.ToInt32(kindProp.GetValue(r, null)) == kind) return r;
            return null;
        }

        // -1 = este equipo no lo tiene (o Windows no deja leerlo), 0 = apagado, 1 = encendido.
        // El resultado se cachea 5 s: el panel puede refrescarse sin castigar las APIs.
        public static int GetRadio(int kind)
        {
            bool wifi = kind == WiFi;
            int cached = wifi ? wifiCache : btCache;
            DateTime at = wifi ? wifiAt : btAt;
            if (cached != -2 && (DateTime.Now - at).TotalMilliseconds < 5000) return cached;
            int value;
            try
            {
                if (!Init()) return -1;
                var r = FindRadio(kind);
                if (r == null) value = -1;
                else value = Convert.ToInt32(radioT.GetProperty("State").GetValue(r, null)) == StateOn ? 1 : 0;
            }
            catch (Exception ex) { Program.Log(ex); value = -1; }
            if (wifi) { wifiCache = value; wifiAt = DateTime.Now; }
            else { btCache = value; btAt = DateTime.Now; }
            return value;
        }

        public static bool SetRadio(int kind, bool on)
        {
            try
            {
                if (!Init()) return false;
                var r = FindRadio(kind);
                if (r == null) return false;
                var op = radioT.GetMethod("SetStateAsync").Invoke(r, new[] { Enum.ToObject(stateT, on ? StateOn : StateOff) });
                var res = Await(op, accessT);
                bool ok = res != null && Convert.ToInt32(res) == 1;   // RadioAccessStatus.Allowed
                if (kind == WiFi) { wifiCache = -2; wifiAt = DateTime.MinValue; }
                else { btCache = -2; btAt = DateTime.MinValue; }
                return ok;
            }
            catch (Exception ex) { Program.Log(ex); return false; }
        }

        // ---- Nombre de la red Wi-Fi y aparatos Bluetooth conectados ----
        // Con Windows.Networking no hace falta el permiso de ubicacion que exige netsh en Windows 11 24H2.
        static Type WinRt(string name)
        {
            return Type.GetType(name + ", Windows, ContentType=WindowsRuntime", true);
        }

        public static string GetWifiName()
        {
            if (wifiName != null && (DateTime.Now - wifiNameAt).TotalMilliseconds < 8000) return wifiName;
            string found = null;
            try
            {
                var niT = WinRt("Windows.Networking.Connectivity.NetworkInformation");
                var cpT = WinRt("Windows.Networking.Connectivity.ConnectionProfile");
                var profiles = new List<object>();
                var inet = niT.GetMethod("GetInternetConnectionProfile").Invoke(null, null);
                if (inet != null) profiles.Add(inet);
                var all = niT.GetMethod("GetConnectionProfiles").Invoke(null, null) as IEnumerable;
                if (all != null) foreach (var p in all) profiles.Add(p);
                foreach (var p in profiles)
                {
                    if (!(bool)cpT.GetProperty("IsWlanConnectionProfile").GetValue(p, null)) continue;
                    if (Convert.ToInt32(cpT.GetMethod("GetNetworkConnectivityLevel").Invoke(p, null)) == 0) continue;
                    string ssid = null;
                    try
                    {
                        var det = cpT.GetProperty("WlanConnectionProfileDetails").GetValue(p, null);
                        if (det != null) ssid = (string)det.GetType().GetMethod("GetConnectedSsid").Invoke(det, null);
                    }
                    catch { }
                    if (string.IsNullOrEmpty(ssid)) ssid = (string)cpT.GetProperty("ProfileName").GetValue(p, null);
                    if (!string.IsNullOrEmpty(ssid)) { found = ssid; break; }
                }
            }
            catch (Exception ex) { Program.Log(ex); }
            wifiName = found; wifiNameAt = DateTime.Now;
            return found;
        }

        public static List<string> GetBluetoothConnected()
        {
            if (btNames != null && (DateTime.Now - btNamesAt).TotalMilliseconds < 8000) return new List<string>(btNames);
            var names = new List<string>();
            try
            {
                if (!Init()) return names;
                var diT = WinRt("Windows.Devices.Enumeration.DeviceInformation");
                var colT = WinRt("Windows.Devices.Enumeration.DeviceInformationCollection");
                var find = diT.GetMethod("FindAllAsync", new[] { typeof(string) });
                string[] kinds = { "Windows.Devices.Bluetooth.BluetoothDevice", "Windows.Devices.Bluetooth.BluetoothLEDevice" };
                foreach (var devName in kinds)
                {
                    var devT = WinRt(devName);
                    var stT = WinRt("Windows.Devices.Bluetooth.BluetoothConnectionStatus");
                    var sel = (string)devT.GetMethod("GetDeviceSelectorFromConnectionStatus").Invoke(null, new[] { Enum.ToObject(stT, 1) });
                    var res = Await(find.Invoke(null, new object[] { sel }), colT) as IEnumerable;
                    if (res == null) continue;
                    foreach (var d in res)
                    {
                        var n = (string)diT.GetProperty("Name").GetValue(d, null);
                        if (!string.IsNullOrEmpty(n) && !names.Contains(n)) names.Add(n);
                    }
                }
            }
            catch (Exception ex) { Program.Log(ex); }
            btNames = names; btNamesAt = DateTime.Now;
            return new List<string>(names);
        }

        // ---- Compartir en proximidad (Configuracion > Sistema > Compartir en proximidad) ----
        const string CdpKey = @"Software\Microsoft\Windows\CurrentVersion\CDP";
        const string NearShareValue = "NearShareChannelUserAuthzPolicy";

        // Solo hay control si Windows ya publica NearShareChannelUserAuthzPolicy; no se inventa la clave.
        public static bool NearShareAvailable
        {
            get
            {
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(CdpKey))
                        return k != null && k.GetValue(NearShareValue) != null;
                }
                catch { return false; }
            }
        }

        public static bool GetNearShare()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(CdpKey))
                    return k != null && Convert.ToInt32(k.GetValue(NearShareValue, 0)) > 0;
            }
            catch { return false; }
        }

        // Encendido = "Solo mis dispositivos"; se escribe unicamente el valor oficial de Windows.
        public static bool SetNearShare(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(CdpKey, true))
                {
                    if (k == null || k.GetValue(NearShareValue) == null) return false;
                    k.SetValue(NearShareValue, on ? 1 : 0, RegistryValueKind.DWord);
                }
                return GetNearShare() == on;
            }
            catch (Exception ex) { Program.Log(ex); return false; }
        }

        // ---- No molestar / Enfoque ----
        // Estado que Windows publica para "No molestar" (WNF_SHEL_QUIETHOURS_ACTIVE_PROFILE_CHANGED).
        // 0 = apagado, 1 = solo prioridad (lo que activa el boton de No molestar).
        [DllImport("ntdll.dll")] static extern int NtQueryWnfStateData(ref ulong name, IntPtr typeId, IntPtr scope, out uint changeStamp, byte[] buf, ref uint size);
        [DllImport("ntdll.dll")] static extern int NtUpdateWnfStateData(ref ulong name, byte[] buf, int len, IntPtr typeId, IntPtr scope, uint matchingStamp, int checkStamp);
        const ulong QuietHours = 0x0D83063EA3BF1C75UL;

        public static bool DndAvailable { get { return dndOk; } }

        public static bool GetDnd()
        {
            if (!dndOk && DateTime.Now < dndRetryAt) return false;
            try
            {
                ulong n = QuietHours; uint stamp; var buf = new byte[4]; uint size = 4;
                int rc = NtQueryWnfStateData(ref n, IntPtr.Zero, IntPtr.Zero, out stamp, buf, ref size);
                if (rc != 0 || size < 1) { dndOk = false; dndRetryAt = DateTime.Now.AddSeconds(30); return false; }
                dndOk = true;
                return buf[0] != 0;
            }
            catch { dndOk = false; dndRetryAt = DateTime.Now.AddSeconds(30); return false; }
        }

        // Se lee de vuelta: si Windows no cambia el estado, el boton se oculta en el panel.
        public static bool SetDnd(bool on)
        {
            try
            {
                ulong n = QuietHours; var buf = new byte[] { (byte)(on ? 1 : 0), 0, 0, 0 };
                if (NtUpdateWnfStateData(ref n, buf, 4, IntPtr.Zero, IntPtr.Zero, 0, 0) != 0) return false;
                bool now = GetDnd();
                if (now != on) { dndOk = false; dndRetryAt = DateTime.Now.AddSeconds(30); return false; }
                return true;
            }
            catch (Exception ex) { Program.Log(ex); return false; }
        }
    }
}
