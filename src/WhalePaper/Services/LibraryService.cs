using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using WhalePaper.Models;

namespace WhalePaper.Services
{
    internal sealed class LibraryService
    {
        private static readonly HashSet<string> ImageExtensions = new HashSet<string>(
            new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tif", ".tiff" },
            StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> VideoExtensions = new HashSet<string>(
            new[] { ".mp4", ".m4v", ".wmv", ".avi", ".mov", ".webm" },
            StringComparer.OrdinalIgnoreCase);

        private readonly List<WallpaperItem> _items = new List<WallpaperItem>();

        public IReadOnlyList<WallpaperItem> Items
        {
            get { return _items; }
        }

        public void Load()
        {
            _items.Clear();
            var data = JsonStore.Load<LibraryData>(AppPaths.LibraryFile);
            if (data.Items == null)
            {
                return;
            }

            foreach (var item in data.Items)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Id))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(item.Name))
                {
                    item.Name = string.IsNullOrWhiteSpace(item.StoredPath)
                        ? "未命名壁纸"
                        : Path.GetFileNameWithoutExtension(item.StoredPath);
                }

                if (item.AddedUtc == default(DateTime))
                {
                    item.AddedUtc = DateTime.UtcNow;
                }

                if (string.IsNullOrWhiteSpace(item.StoredPath) || !File.Exists(item.StoredPath))
                {
                    AppLog.Warn("Skipping missing library file: " + item.StoredPath);
                    continue;
                }

                item.LoadThumbnail();
                _items.Add(item);
            }

            AppLog.Info("Library loaded: " + _items.Count + " items");
        }

        public void Save()
        {
            JsonStore.Save(AppPaths.LibraryFile, new LibraryData { Items = _items.ToList() });
        }

        public void EnsureAssets()
        {
            foreach (var item in _items)
            {
                ThumbnailService.EnsureAssets(item);
            }
        }

        public async Task<ImportResult> ImportAsync(
            IEnumerable<string> paths,
            IProgress<ImportProgress> progress,
            CancellationToken cancellationToken)
        {
            var result = new ImportResult();
            var distinct = paths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            for (var i = 0; i < distinct.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = distinct[i];
                progress?.Report(new ImportProgress(i, distinct.Count, Path.GetFileName(path)));
                try
                {
                    if (!File.Exists(path))
                    {
                        result.Errors.Add(Path.GetFileName(path) + "：文件不存在");
                        continue;
                    }

                    var extension = Path.GetExtension(path);
                    if (!IsSupportedExtension(extension))
                    {
                        result.Errors.Add(Path.GetFileName(path) + "：不支持的格式");
                        continue;
                    }

                    var hash = await ComputeHashAsync(path, cancellationToken).ConfigureAwait(false);
                    var duplicate = _items.FirstOrDefault(existing =>
                        string.Equals(existing.ContentHash, hash, StringComparison.OrdinalIgnoreCase));
                    if (duplicate != null)
                    {
                        result.Existing.Add(duplicate);
                        progress?.Report(new ImportProgress(i + 1, distinct.Count, "已存在：" + duplicate.DisplayName));
                        continue;
                    }

                    var id = Guid.NewGuid().ToString("N");
                    var destination = Path.Combine(AppPaths.LibraryDir, id + extension.ToLowerInvariant());
                    await CopyFileAsync(path, destination, cancellationToken).ConfigureAwait(false);

                    var item = new WallpaperItem
                    {
                        Id = id,
                        Name = Path.GetFileNameWithoutExtension(path),
                        StoredPath = destination,
                        SourcePath = path,
                        Kind = DetectKind(extension),
                        FileSize = new FileInfo(destination).Length,
                        AddedUtc = DateTime.UtcNow,
                        ContentHash = hash
                    };

                    if (item.Kind != WallpaperKind.Video)
                    {
                        int width;
                        int height;
                        ThumbnailService.GetImageDimensions(destination, out width, out height);
                        item.PixelWidth = width;
                        item.PixelHeight = height;
                    }

                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        ThumbnailService.EnsureAssets(item);
                    });

                    _items.Add(item);
                    result.Added.Add(item);
                    progress?.Report(new ImportProgress(i + 1, distinct.Count, "已导入：" + item.DisplayName));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    AppLog.Error("Import failed for " + path, ex);
                    result.Errors.Add(Path.GetFileName(path) + "：" + ex.Message);
                }
            }

            if (result.Added.Count > 0)
            {
                Save();
            }

            return result;
        }

        public bool Delete(WallpaperItem item)
        {
            if (item == null)
            {
                return false;
            }

            _items.Remove(item);
            TryDelete(item.StoredPath);
            TryDelete(item.ThumbnailPath);
            TryDelete(item.PosterPath);
            Save();
            AppLog.Info("Deleted wallpaper: " + item.DisplayName);
            return true;
        }

        public static bool IsSupportedExtension(string extension)
        {
            return ImageExtensions.Contains(extension) || VideoExtensions.Contains(extension);
        }

        public static string BuildFileDialogFilter()
        {
            return "支持的壁纸|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp;*.tif;*.tiff;*.mp4;*.m4v;*.wmv;*.avi;*.mov;*.webm|" +
                   "图片|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp;*.tif;*.tiff|" +
                   "视频|*.mp4;*.m4v;*.wmv;*.avi;*.mov;*.webm|" +
                   "所有文件|*.*";
        }

        private static WallpaperKind DetectKind(string extension)
        {
            if (string.Equals(extension, ".gif", StringComparison.OrdinalIgnoreCase))
            {
                return WallpaperKind.Gif;
            }
            if (VideoExtensions.Contains(extension))
            {
                return WallpaperKind.Video;
            }
            return WallpaperKind.Image;
        }

        private static async Task<string> ComputeHashAsync(string path, CancellationToken token)
        {
            using (var sha = SHA256.Create())
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, true))
            {
                var hash = await Task.Run(() => sha.ComputeHash(stream), token).ConfigureAwait(false);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        private static async Task CopyFileAsync(string source, string destination, CancellationToken token)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, true))
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 128, true))
            {
                await input.CopyToAsync(output, 1024 * 128, token).ConfigureAwait(false);
                output.Flush(true);
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("Could not delete " + path + ": " + ex.Message);
            }
        }
    }

    internal sealed class ImportProgress
    {
        public ImportProgress(int current, int total, string message)
        {
            Current = current;
            Total = total;
            Message = message;
        }

        public int Current { get; private set; }
        public int Total { get; private set; }
        public string Message { get; private set; }

        public double Percent
        {
            get { return Total <= 0 ? 0 : Math.Min(100, 100.0 * Current / Total); }
        }
    }

    internal sealed class ImportResult
    {
        public ImportResult()
        {
            Added = new List<WallpaperItem>();
            Existing = new List<WallpaperItem>();
            Errors = new List<string>();
        }

        public List<WallpaperItem> Added { get; private set; }
        public List<WallpaperItem> Existing { get; private set; }
        public List<string> Errors { get; private set; }
    }
}
