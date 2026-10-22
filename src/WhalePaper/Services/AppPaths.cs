using System;
using System.IO;

namespace WhalePaper.Services
{
    internal static class AppPaths
    {
        private static string _dataRoot;

        public static string DataRoot
        {
            get
            {
                if (_dataRoot == null)
                {
                    Initialize(null);
                }
                return _dataRoot;
            }
        }

        public static string LibraryDir
        {
            get { return Path.Combine(DataRoot, "Library"); }
        }

        public static string CacheDir
        {
            get { return Path.Combine(DataRoot, "Cache"); }
        }

        public static string LogDir
        {
            get { return Path.Combine(DataRoot, "Logs"); }
        }

        public static string SettingsFile
        {
            get { return Path.Combine(DataRoot, "settings.json"); }
        }

        public static string LibraryFile
        {
            get { return Path.Combine(DataRoot, "library.json"); }
        }

        public static string DefaultWallpaperFile
        {
            get { return Path.Combine(CacheDir, "default-wallpaper.png"); }
        }

        public static void Initialize(string[] args)
        {
            var root = ReadDataRoot(args);
            if (string.IsNullOrWhiteSpace(root))
            {
                root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WhalePaper");
            }

            try
            {
                _dataRoot = Path.GetFullPath(root);
                EnsureDirectories();
            }
            catch
            {
                // A locked-down profile should still let the user run the app.
                _dataRoot = Path.Combine(Path.GetTempPath(), "WhalePaper");
                EnsureDirectories();
            }
        }

        private static string ReadDataRoot(string[] args)
        {
            var fromEnv = Environment.GetEnvironmentVariable("WHALEPAPER_DATA");
            if (!string.IsNullOrWhiteSpace(fromEnv))
            {
                return fromEnv;
            }

            if (args != null)
            {
                for (var i = 0; i < args.Length - 1; i++)
                {
                    if (string.Equals(args[i], "--data-dir", StringComparison.OrdinalIgnoreCase))
                    {
                        return args[i + 1];
                    }
                }
            }

            return null;
        }

        private static void EnsureDirectories()
        {
            Directory.CreateDirectory(_dataRoot);
            Directory.CreateDirectory(LibraryDir);
            Directory.CreateDirectory(CacheDir);
            Directory.CreateDirectory(LogDir);
        }
    }
}
