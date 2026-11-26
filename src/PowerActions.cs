using System;
using System.Diagnostics;
using System.Windows.Forms;

namespace MacDock
{
    // Acciones de energia del menu del sistema. Estan separadas a proposito para que Suspender
    // no comparta ninguna ruta con Apagar ni Reiniciar: aqui no hay shutdown.exe, ni
    // ExitWindowsEx, ni PowerState.Hibernate, ni SetSuspendState(TRUE, ...).
    //
    // Suspender usa la API de Windows Forms: Application.SetSuspendState(PowerState.Suspend,
    // force=false, disableWakeEvent=false), que equivale a SetSuspendState(FALSE, FALSE, FALSE):
    // pide suspension (no hibernacion), deja que las aplicaciones reciban la solicitud y mantiene
    // habilitados los eventos de reactivacion. No pide elevacion ni toca privilegios del token.
    static class PowerActions
    {
        public static Func<bool> Diag;

        static void LogSuspend(object message)
        {
            if (Diag != null && Diag()) Program.Log(message);
        }

        // Devuelve al usuario solo si no se pudo suspender. Nunca apaga, reinicia ni hiberna
        // como alternativa y nunca repite la llamada.
        public static void Suspend()
        {
            DateTime t0 = DateTime.Now;
            bool ok = false;
            try { ok = Application.SetSuspendState(PowerState.Suspend, false, false); }
            catch (Exception ex) { LogSuspend(ex); }

            double elapsed = (DateTime.Now - t0).TotalMilliseconds;
            if (ok) { LogSuspend("Suspender: SetSuspendState(Suspend,false,false) ok (" + (int)elapsed + " ms)"); return; }
            // Si el equipo se suspendio y volvio, la llamada puede devolver false despues de un
            // tiempo largo. En ese caso no mostramos un error falso.
            if (elapsed > 1500) { LogSuspend("Suspender: retorno incierto tras " + (int)elapsed + " ms (posible reanudacion)"); return; }

            LogSuspend("Suspender: SetSuspendState(Suspend,false,false) devolvio false");
            try
            {
                MessageBox.Show(
                    "Windows no pudo entrar en suspension.\n\n" +
                    "Puede que otro programa o una directiva del equipo lo este impidiendo. " +
                    "El equipo sigue encendido: guarda tu trabajo y vuelve a intentarlo.",
                    "MacDock", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception ex) { LogSuspend(ex); }
        }

        public static void Restart()
        {
            if (!RunShutdown("/r /t 0")) Failed("reiniciar");
        }

        public static void Shutdown()
        {
            if (!RunShutdown("/s /t 0")) Failed("apagar");
        }

        public static void Logoff()
        {
            if (!RunShutdown("/l")) Failed("cerrar la sesion");
        }

        // Bloquear mantiene el comportamiento existente (Win+L simulado).
        public static void Lock()
        {
            N.PressKeys(0x5B, 0x4C);
        }

        static bool RunShutdown(string args)
        {
            try
            {
                using (var p = Process.Start(new ProcessStartInfo("shutdown.exe", args)
                { CreateNoWindow = true, UseShellExecute = false }))
                {
                    return p != null;
                }
            }
            catch (Exception ex) { Program.Log(ex); return false; }
        }

        static void Failed(string what)
        {
            try
            {
                MessageBox.Show("No se pudo " + what + ". El equipo no ha cambiado de estado.",
                    "MacDock", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch { }
        }
    }
}
