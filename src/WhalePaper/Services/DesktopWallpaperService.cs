using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using WhalePaper.Interop;
using WhalePaper.Models;

namespace WhalePaper.Services
{
    internal sealed class DesktopWallpaperService
    {
        public bool EnsureOriginalSaved(AppSettings settings)
        {
            if (settings.HasSavedOriginal)
            {
                return false;
            }

            NativeMethods.IDesktopWallpaper wallpaper = null;
            try
            {
                AppLog.Info("Reading original wallpaper");
                wallpaper = CreateWallpaper();
                settings.OriginalWallpaperPath = wallpaper.GetWallpaper(null) ?? "";
                settings.OriginalWallpaperPosition = (int)wallpaper.GetPosition();
                settings.HasSavedOriginal = true;
                AppLog.Info("Saved original wallpaper: " + settings.OriginalWallpaperPath);
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Warn("Could not read original wallpaper: " + ex.Message);
                settings.OriginalWallpaperPath = "";
                settings.OriginalWallpaperPosition = -1;
                // Do not mark this as saved: a later run with normal permissions should retry.
                settings.HasSavedOriginal = false;
                return false;
            }
            finally
            {
                ReleaseCom(wallpaper);
            }
        }

        public bool TryReadCurrent(out string path, out int position, out string error)
        {
            path = null;
            position = -1;
            error = null;
            NativeMethods.IDesktopWallpaper wallpaper = null;
            try
            {
                wallpaper = CreateWallpaper();
                path = wallpaper.GetWallpaper(null);
                position = (int)wallpaper.GetPosition();
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                ReleaseCom(wallpaper);
            }
        }

        public bool SetWallpaper(string path, FitMode fit)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                AppLog.Warn("SetWallpaper called with missing file: " + path);
                return false;
            }

            NativeMethods.IDesktopWallpaper wallpaper = null;
            try
            {
                AppLog.Info("Setting wallpaper: " + path);
                wallpaper = CreateWallpaper();
                wallpaper.SetPosition(ToNativePosition(fit));
                wallpaper.SetWallpaper(null, path);
                AppLog.Info("Wallpaper set: " + path);
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Warn("IDesktopWallpaper failed, falling back: " + ex);
                return SetWallpaperFallback(path, fit);
            }
            finally
            {
                ReleaseCom(wallpaper);
            }
        }

        public bool RestoreOriginal(AppSettings settings)
        {
            if (settings.HasSavedOriginal && !string.IsNullOrWhiteSpace(settings.OriginalWallpaperPath)
                && File.Exists(settings.OriginalWallpaperPath))
            {
                var wallpaper = CreateWallpaper();
                try
                {
                    if (settings.OriginalWallpaperPosition >= 0 && settings.OriginalWallpaperPosition <= 5)
                    {
                        wallpaper.SetPosition((uint)settings.OriginalWallpaperPosition);
                    }
                    wallpaper.SetWallpaper(null, settings.OriginalWallpaperPath);
                    AppLog.Info("Restored original wallpaper: " + settings.OriginalWallpaperPath);
                    return true;
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Restore original failed: " + ex.Message);
                }
                finally
                {
                    ReleaseCom(wallpaper);
                }
            }

            return SetWallpaper(AppPaths.DefaultWallpaperFile, FitMode.Fill);
        }

        public bool SetDefaultWallpaper()
        {
            return SetWallpaper(AppPaths.DefaultWallpaperFile, FitMode.Fill);
        }

        private static NativeMethods.IDesktopWallpaper CreateWallpaper()
        {
            return (NativeMethods.IDesktopWallpaper)new NativeMethods.DesktopWallpaperClass();
        }

        private static void ReleaseCom(object value)
        {
            try
            {
                if (value != null && Marshal.IsComObject(value))
                {
                    Marshal.ReleaseComObject(value);
                }
            }
            catch
            {
            }
        }

        private static uint ToNativePosition(FitMode fit)
        {
            switch (fit)
            {
                case FitMode.Fit:
                    return NativeMethods.DESKTOP_WALLPAPER_POSITION_FIT;
                case FitMode.Stretch:
                    return NativeMethods.DESKTOP_WALLPAPER_POSITION_STRETCH;
                case FitMode.Center:
                    return NativeMethods.DESKTOP_WALLPAPER_POSITION_CENTER;
                case FitMode.Tile:
                    return NativeMethods.DESKTOP_WALLPAPER_POSITION_TILE;
                default:
                    return NativeMethods.DESKTOP_WALLPAPER_POSITION_FILL;
            }
        }

        private static bool SetWallpaperFallback(string path, FitMode fit)
        {
            try
            {
                string style;
                string tile;
                switch (fit)
                {
                    case FitMode.Fit:
                        style = "6";
                        tile = "0";
                        break;
                    case FitMode.Stretch:
                        style = "2";
                        tile = "0";
                        break;
                    case FitMode.Center:
                        style = "0";
                        tile = "0";
                        break;
                    case FitMode.Tile:
                        style = "0";
                        tile = "1";
                        break;
                    default:
                        style = "10";
                        tile = "0";
                        break;
                }

                try
                {
                    using (var key = Registry.CurrentUser.CreateSubKey(@"Control Panel\Desktop"))
                    {
                        if (key != null)
                        {
                            key.SetValue("WallpaperStyle", style, RegistryValueKind.String);
                            key.SetValue("TileWallpaper", tile, RegistryValueKind.String);
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Could not update wallpaper style registry: " + ex.Message);
                }

                var ok = NativeMethods.SystemParametersInfo(
                    NativeMethods.SPI_SETDESKWALLPAPER,
                    0,
                    path,
                    NativeMethods.SPIF_UPDATEINIFILE | NativeMethods.SPIF_SENDCHANGE);
                AppLog.Info("Fallback wallpaper set: " + ok);
                return ok;
            }
            catch (Exception ex)
            {
                AppLog.Error("Fallback wallpaper failed", ex);
                return false;
            }
        }
    }
}
