using System;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace WhalePaper.Services
{
    internal static class AssetService
    {
        public static void EnsureDefaultWallpaper()
        {
            try
            {
                if (File.Exists(AppPaths.DefaultWallpaperFile))
                {
                    return;
                }

                var resource = Application.GetResourceStream(
                    new Uri("pack://application:,,,/Assets/default-wallpaper.png", UriKind.Absolute));
                if (resource == null)
                {
                    return;
                }

                Directory.CreateDirectory(AppPaths.CacheDir);
                using (var source = resource.Stream)
                using (var target = File.Create(AppPaths.DefaultWallpaperFile))
                {
                    source.CopyTo(target);
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("Could not extract default wallpaper: " + ex.Message);
            }
        }

        public static BitmapSource LoadDefaultWallpaper()
        {
            try
            {
                EnsureDefaultWallpaper();
                if (!File.Exists(AppPaths.DefaultWallpaperFile))
                {
                    return null;
                }

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bitmap.UriSource = new Uri(AppPaths.DefaultWallpaperFile, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch
            {
                return null;
            }
        }
    }
}
