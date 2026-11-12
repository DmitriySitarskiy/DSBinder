using System;
using System.IO;

namespace DSBinder.Core.Services
{
    /// <summary>Простий файловий лог: %AppData%\DSBinder\log.txt</summary>
    public static class Logger
    {
        private static readonly object Sync = new();

        public static string LogPath => Path.Combine(ConfigService.DataDirectory, "log.txt");

        public static void Info(string message) => Write("INFO", message);

        public static void Warn(string message) => Write("WARN", message);

        public static void Error(string context, Exception? ex) => Write("ERROR", $"{context}: {ex}");

        private static void Write(string level, string message)
        {
            try
            {
                lock (Sync)
                {
                    Directory.CreateDirectory(ConfigService.DataDirectory);
                    var info = new FileInfo(LogPath);
                    if (info.Exists && info.Length > 1_000_000) info.Delete();
                    File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
                }
            }
            catch
            {
                // логування ніколи не повинно ламати програму
            }
        }
    }
}
