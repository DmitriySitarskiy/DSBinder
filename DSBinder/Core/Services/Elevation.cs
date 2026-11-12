using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Security.Principal;

namespace DSBinder.Core.Services
{
    /// <summary>
    /// Права адміністратора. Потрібні, бо GTA/RAGE MP часто запущені від адміністратора,
    /// а Windows не пропускає клавіші з «адмінського» вікна до звичайної програми.
    /// </summary>
    public static class Elevation
    {
        public static bool IsElevated()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Перезапускає програму з запитом UAC. false — користувач відмовився або сталася помилка.</summary>
        public static bool TryRelaunchElevated(string[] args)
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;

            var arguments = args.Append("--elevated").Select(Quote);

            try
            {
                Process.Start(new ProcessStartInfo(exe, string.Join(" ", arguments))
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WorkingDirectory = AppContext.BaseDirectory
                });
                return true;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                Logger.Warn("Користувач відмовився надати права адміністратора");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Error("Перезапуск з правами адміністратора", ex);
                return false;
            }
        }

        private static string Quote(string a) =>
            a.Length > 0 && !a.Any(c => char.IsWhiteSpace(c) || c == '"') ? a : "\"" + a.Replace("\"", "\\\"") + "\"";
    }
}
