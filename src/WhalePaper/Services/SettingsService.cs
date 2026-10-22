using System.IO;
using WhalePaper.Models;

namespace WhalePaper.Services
{
    internal static class SettingsService
    {
        public static AppSettings Load()
        {
            var isNew = !File.Exists(AppPaths.SettingsFile);
            var settings = isNew
                ? AppSettings.CreateDefault()
                : JsonStore.Load<AppSettings>(AppPaths.SettingsFile);

            if (settings == null)
            {
                settings = AppSettings.CreateDefault();
            }

            // DataContractJsonSerializer may leave defaults in a partially written file.
            if (settings.WindowWidth <= 0 || settings.WindowHeight <= 0)
            {
                var defaults = AppSettings.CreateDefault();
                settings.WindowWidth = defaults.WindowWidth;
                settings.WindowHeight = defaults.WindowHeight;
                settings.PauseOnFullscreen = defaults.PauseOnFullscreen;
                settings.PauseOnBattery = defaults.PauseOnBattery;
                settings.MuteVideos = defaults.MuteVideos;
                settings.FirstRun = true;
            }

            if (settings.SchemaVersion < 1)
            {
                var defaults = AppSettings.CreateDefault();
                settings.PauseOnFullscreen = defaults.PauseOnFullscreen;
                settings.PauseOnBattery = defaults.PauseOnBattery;
                settings.MuteVideos = defaults.MuteVideos;
                settings.SchemaVersion = 1;
            }

            settings.Normalize();
            return settings;
        }

        public static void Save(AppSettings settings)
        {
            if (settings != null)
            {
                JsonStore.Save(AppPaths.SettingsFile, settings);
            }
        }
    }
}
