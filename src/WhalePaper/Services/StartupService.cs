using System;
using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace WhalePaper.Services
{
    internal static class StartupService
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "WhalePaper";

        public static string ExecutablePath
        {
            get
            {
                var path = Assembly.GetEntryAssembly() != null
                    ? Assembly.GetEntryAssembly().Location
                    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WhalePaper.exe");
                return Path.GetFullPath(path);
            }
        }

        public static bool IsEnabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    return key != null && key.GetValue(ValueName) != null;
                }
            }
            catch
            {
                return false;
            }
        }

        public static void SetEnabled(bool enabled)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (key == null)
                    {
                        return;
                    }

                    if (enabled)
                    {
                        key.SetValue(ValueName, Quote(ExecutablePath) + " --minimized", RegistryValueKind.String);
                        AppLog.Info("Autostart enabled: " + ExecutablePath);
                    }
                    else
                    {
                        key.DeleteValue(ValueName, false);
                        AppLog.Info("Autostart disabled");
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("Could not update autostart", ex);
            }
        }

        private static string Quote(string path)
        {
            return "\"" + path + "\"";
        }
    }
}
