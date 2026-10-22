using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WhalePaper.Services;

namespace WhalePaper.Models
{
    public enum WallpaperKind
    {
        Image = 0,
        Gif = 1,
        Video = 2
    }

    [DataContract]
    public sealed class WallpaperItem : INotifyPropertyChanged
    {
        private BitmapSource _thumbnail;
        private bool _isFavorite;
        private bool _isSelected;
        private bool _isCurrent;

        [DataMember]
        public string Id { get; set; }

        [DataMember]
        public string Name { get; set; }

        [DataMember]
        public string StoredPath { get; set; }

        [DataMember]
        public string SourcePath { get; set; }

        [DataMember]
        public WallpaperKind Kind { get; set; }

        [DataMember]
        public int PixelWidth { get; set; }

        [DataMember]
        public int PixelHeight { get; set; }

        [DataMember]
        public long FileSize { get; set; }

        [DataMember]
        public DateTime AddedUtc { get; set; }

        [DataMember]
        public string ContentHash { get; set; }

        [DataMember]
        public string ThumbnailFile { get; set; }

        [DataMember]
        public string PosterFile { get; set; }

        [DataMember]
        public bool IsFavorite
        {
            get { return _isFavorite; }
            set
            {
                if (_isFavorite == value)
                {
                    return;
                }
                _isFavorite = value;
                OnPropertyChanged("IsFavorite");
            }
        }

        [IgnoreDataMember]
        public bool IsSelected
        {
            get { return _isSelected; }
            set
            {
                if (_isSelected == value)
                {
                    return;
                }
                _isSelected = value;
                OnPropertyChanged("IsSelected");
            }
        }

        [IgnoreDataMember]
        public bool IsCurrent
        {
            get { return _isCurrent; }
            set
            {
                if (_isCurrent == value)
                {
                    return;
                }
                _isCurrent = value;
                OnPropertyChanged("IsCurrent");
            }
        }

        [IgnoreDataMember]
        public BitmapSource Thumbnail
        {
            get { return _thumbnail; }
            set
            {
                _thumbnail = value;
                OnPropertyChanged("Thumbnail");
            }
        }

        [IgnoreDataMember]
        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Name))
                {
                    return Name;
                }
                return string.IsNullOrWhiteSpace(StoredPath)
                    ? "未命名壁纸"
                    : Path.GetFileNameWithoutExtension(StoredPath);
            }
        }

        [IgnoreDataMember]
        public string KindLabel
        {
            get
            {
                switch (Kind)
                {
                    case WallpaperKind.Gif:
                        return "GIF 动图";
                    case WallpaperKind.Video:
                        return "动态视频";
                    default:
                        return "静态图片";
                }
            }
        }

        [IgnoreDataMember]
        public bool IsDynamic
        {
            get { return Kind == WallpaperKind.Gif || Kind == WallpaperKind.Video; }
        }

        [IgnoreDataMember]
        public string ResolutionText
        {
            get
            {
                return PixelWidth > 0 && PixelHeight > 0
                    ? PixelWidth + " × " + PixelHeight
                    : "分辨率未知";
            }
        }

        [IgnoreDataMember]
        public string FileSizeText
        {
            get { return FormatFileSize(FileSize); }
        }

        [IgnoreDataMember]
        public string SubtitleText
        {
            get { return ResolutionText + "  ·  " + FileSizeText; }
        }

        [IgnoreDataMember]
        public string AddedText
        {
            get { return AddedUtc == default(DateTime) ? "" : AddedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"); }
        }

        [IgnoreDataMember]
        public string ThumbnailPath
        {
            get
            {
                return string.IsNullOrWhiteSpace(ThumbnailFile)
                    ? null
                    : Path.Combine(AppPaths.CacheDir, ThumbnailFile);
            }
        }

        [IgnoreDataMember]
        public string PosterPath
        {
            get
            {
                return string.IsNullOrWhiteSpace(PosterFile)
                    ? null
                    : Path.Combine(AppPaths.CacheDir, PosterFile);
            }
        }

        public void LoadThumbnail()
        {
            try
            {
                var path = ThumbnailPath;
                if (path == null || !File.Exists(path))
                {
                    return;
                }

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                Thumbnail = bitmap;
            }
            catch
            {
                Thumbnail = null;
            }
        }

        public void NotifyMetadataChanged()
        {
            OnPropertyChanged("DisplayName");
            OnPropertyChanged("KindLabel");
            OnPropertyChanged("IsDynamic");
            OnPropertyChanged("ResolutionText");
            OnPropertyChanged("FileSizeText");
            OnPropertyChanged("SubtitleText");
            OnPropertyChanged("AddedText");
            OnPropertyChanged("ThumbnailPath");
            OnPropertyChanged("PosterPath");
        }

        public static string FormatFileSize(long bytes)
        {
            if (bytes <= 0)
            {
                return "—";
            }

            string[] units = { "B", "KB", "MB", "GB" };
            var value = (double)bytes;
            var unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return value >= 100 || unit == 0
                ? string.Format("{0:0} {1}", value, units[unit])
                : string.Format("{0:0.0} {1}", value, units[unit]);
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged(string name)
        {
            var handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(name));
            }
        }
    }
}
