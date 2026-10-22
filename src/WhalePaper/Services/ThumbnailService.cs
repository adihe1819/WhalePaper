using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WhalePaper.Models;

namespace WhalePaper.Services
{
    internal static class ThumbnailService
    {
        private const int ThumbnailWidth = 520;
        private const int PosterWidth = 1920;
        private const int PosterHeight = 1080;

        public static BitmapSource LoadImage(string path, int decodeWidth)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                if (decodeWidth > 0)
                {
                    bitmap.DecodePixelWidth = decodeWidth;
                }
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch (Exception ex)
            {
                AppLog.Warn("Image decode failed for " + path + ": " + ex.Message);
                return null;
            }
        }

        public static BitmapSource LoadFirstFrame(string path, int maxWidth)
        {
            try
            {
                using (var stream = File.OpenRead(path))
                {
                    var decoder = BitmapDecoder.Create(
                        stream,
                        BitmapCreateOptions.PreservePixelFormat,
                        BitmapCacheOption.OnLoad);
                    if (decoder.Frames.Count == 0)
                    {
                        return null;
                    }

                    var frame = decoder.Frames[0];
                    var source = ScaleDown(frame, maxWidth);
                    source.Freeze();
                    return source;
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("First frame decode failed for " + path + ": " + ex.Message);
                return null;
            }
        }

        public static void GetImageDimensions(string path, out int width, out int height)
        {
            width = 0;
            height = 0;
            try
            {
                using (var stream = File.OpenRead(path))
                {
                    var decoder = BitmapDecoder.Create(
                        stream,
                        BitmapCreateOptions.DelayCreation,
                        BitmapCacheOption.None);
                    if (decoder.Frames.Count > 0)
                    {
                        width = decoder.Frames[0].PixelWidth;
                        height = decoder.Frames[0].PixelHeight;
                    }
                }
            }
            catch
            {
            }
        }

        public static void EnsureAssets(WallpaperItem item)
        {
            if (item == null)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(AppPaths.CacheDir);
                var sourcePath = item.StoredPath;
                if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                {
                    return;
                }

                if (string.IsNullOrWhiteSpace(item.ThumbnailFile) || !File.Exists(item.ThumbnailPath))
                {
                    var thumb = BuildThumbnail(item, sourcePath);
                    if (thumb != null)
                    {
                        item.ThumbnailFile = item.Id + "-thumb.png";
                        SavePng(thumb, item.ThumbnailPath);
                    }
                }

                if (item.IsDynamic &&
                    (string.IsNullOrWhiteSpace(item.PosterFile) || !File.Exists(item.PosterPath)))
                {
                    var poster = BuildPoster(item, sourcePath);
                    if (poster != null)
                    {
                        item.PosterFile = item.Id + "-poster.jpg";
                        SaveJpeg(poster, item.PosterPath, 88);
                    }
                }

                item.LoadThumbnail();
            }
            catch (Exception ex)
            {
                AppLog.Error("EnsureAssets failed for " + item.DisplayName, ex);
            }
        }

        private static BitmapSource BuildThumbnail(WallpaperItem item, string path)
        {
            if (item.Kind == WallpaperKind.Video)
            {
                var shell = ShellThumbnailService.GetThumbnail(path, 640, 360);
                if (shell != null)
                {
                    return ScaleDown(shell, ThumbnailWidth);
                }
                return AssetService.LoadDefaultWallpaper();
            }

            if (item.Kind == WallpaperKind.Gif)
            {
                var frame = LoadFirstFrame(path, ThumbnailWidth);
                if (frame != null)
                {
                    return frame;
                }
            }

            var image = LoadImage(path, ThumbnailWidth);
            if (image != null)
            {
                return ScaleDown(image, ThumbnailWidth);
            }

            return ShellThumbnailService.GetThumbnail(path, 640, 360)
                ?? AssetService.LoadDefaultWallpaper();
        }

        private static BitmapSource BuildPoster(WallpaperItem item, string path)
        {
            if (item.Kind == WallpaperKind.Video)
            {
                var shell = ShellThumbnailService.GetThumbnail(path, PosterWidth, PosterHeight);
                if (shell != null)
                {
                    return ScaleDown(shell, PosterWidth);
                }
            }
            else
            {
                var frame = LoadFirstFrame(path, PosterWidth);
                if (frame != null)
                {
                    return frame;
                }
            }

            return AssetService.LoadDefaultWallpaper();
        }

        public static BitmapSource ScaleDown(BitmapSource source, int maxWidth)
        {
            if (source == null || maxWidth <= 0 || source.PixelWidth <= maxWidth)
            {
                return source;
            }

            var scale = (double)maxWidth / source.PixelWidth;
            var transformed = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            transformed.Freeze();
            return transformed;
        }

        private static void SavePng(BitmapSource source, string path)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using (var stream = File.Create(path))
            {
                encoder.Save(stream);
            }
        }

        private static void SaveJpeg(BitmapSource source, string path, int quality)
        {
            var encoder = new JpegBitmapEncoder { QualityLevel = quality };
            encoder.Frames.Add(BitmapFrame.Create(source));
            using (var stream = File.Create(path))
            {
                encoder.Save(stream);
            }
        }
    }
}
