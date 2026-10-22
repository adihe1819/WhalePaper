using System.Runtime.Serialization;

namespace WhalePaper.Models
{
    public enum FitMode
    {
        Fill = 0,
        Fit = 1,
        Stretch = 2,
        Center = 3,
        Tile = 4
    }

    [DataContract]
    public sealed class AppSettings
    {
        [DataMember]
        public int SchemaVersion { get; set; }

        [DataMember]
        public string SelectedId { get; set; }

        /// <summary>
        /// Desktop layer that was last proven visible by the pixel probe. Used only as a
        /// preference; every attach still verifies the layer before playing media.
        /// </summary>
        [DataMember]
        public string AttachStrategy { get; set; }

        [DataMember]
        public FitMode Fit { get; set; }

        [DataMember]
        public bool AutoStart { get; set; }

        [DataMember]
        public bool PauseOnFullscreen { get; set; }

        [DataMember]
        public bool PauseOnBattery { get; set; }

        [DataMember]
        public bool MuteVideos { get; set; }

        [DataMember]
        public bool RestoreOriginalOnExit { get; set; }

        [DataMember]
        public bool FirstRun { get; set; }

        [DataMember]
        public bool HasSavedOriginal { get; set; }

        [DataMember]
        public string OriginalWallpaperPath { get; set; }

        [DataMember]
        public int OriginalWallpaperPosition { get; set; }

        [DataMember]
        public double WindowWidth { get; set; }

        [DataMember]
        public double WindowHeight { get; set; }

        [DataMember]
        public bool WindowMaximized { get; set; }

        public static AppSettings CreateDefault()
        {
            return new AppSettings
            {
                SchemaVersion = 1,
                Fit = FitMode.Fill,
                AutoStart = false,
                PauseOnFullscreen = true,
                PauseOnBattery = true,
                MuteVideos = true,
                RestoreOriginalOnExit = false,
                FirstRun = true,
                HasSavedOriginal = false,
                OriginalWallpaperPosition = -1,
                WindowWidth = 1140,
                WindowHeight = 800,
                WindowMaximized = false
            };
        }

        public void Normalize()
        {
            if (WindowWidth < 940 || WindowWidth > 4000)
            {
                WindowWidth = 1140;
            }
            if (WindowHeight < 620 || WindowHeight > 3000)
            {
                WindowHeight = 800;
            }
            if (OriginalWallpaperPosition < -1 || OriginalWallpaperPosition > 5)
            {
                OriginalWallpaperPosition = -1;
            }
            switch (Fit)
            {
                case FitMode.Fill:
                case FitMode.Fit:
                case FitMode.Stretch:
                case FitMode.Center:
                case FitMode.Tile:
                    break;
                default:
                    Fit = FitMode.Fill;
                    break;
            }
        }
    }
}
