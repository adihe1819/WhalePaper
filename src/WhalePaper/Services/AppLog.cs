using System;
using System.IO;
using System.Text;

namespace WhalePaper.Services
{
    internal static class AppLog
    {
        private static readonly object Sync = new object();
        private static readonly long MaxBytes = 2 * 1024 * 1024;

        public static string LogPath
        {
            get { return Path.Combine(AppPaths.LogDir, "app.log"); }
        }

        public static void Info(string message)
        {
            Write("INFO", message);
        }

        public static void Warn(string message)
        {
            Write("WARN", message);
        }

        public static void Error(string message, Exception ex = null)
        {
            Write("ERROR", ex == null ? message : message + Environment.NewLine + ex);
        }

        private static void Write(string level, string message)
        {
            var line = string.Format(
                "{0:yyyy-MM-dd HH:mm:ss.fff} [{1}] [T{2}] {3}{4}",
                DateTime.Now,
                level,
                Environment.CurrentManagedThreadId,
                message,
                Environment.NewLine);

            lock (Sync)
            {
                try
                {
                    Directory.CreateDirectory(AppPaths.LogDir);
                    var path = LogPath;
                    if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                    {
                        var old = path + ".old";
                        if (File.Exists(old))
                        {
                            File.Delete(old);
                        }
                        File.Move(path, old);
                    }

                    File.AppendAllText(path, line, Encoding.UTF8);
                }
                catch
                {
                    // Logging must never take down the application.
                }

                try
                {
                    MirrorDiagnosticLog(line);
                }
                catch
                {
                    // The diagnostic mirror is best-effort only.
                }
            }
        }

        private static void MirrorDiagnosticLog(string line)
        {
            var mirror = Environment.GetEnvironmentVariable("WHALEPAPER_DIAG_LOG");
            if (string.IsNullOrWhiteSpace(mirror))
            {
                return;
            }

            var dir = Path.GetDirectoryName(mirror);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.AppendAllText(mirror, line, Encoding.UTF8);
        }
    }
}
