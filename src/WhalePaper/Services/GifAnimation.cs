using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media.Imaging;

namespace WhalePaper.Services
{
    internal sealed class GifAnimation
    {
        private GifAnimation(List<BitmapSource> frames, List<int> delays)
        {
            Frames = frames;
            Delays = delays;
        }

        public List<BitmapSource> Frames { get; private set; }
        public List<int> Delays { get; private set; }

        public static GifAnimation Load(string path)
        {
            try
            {
                using (var stream = File.OpenRead(path))
                {
                    var decoder = new GifBitmapDecoder(
                        stream,
                        BitmapCreateOptions.PreservePixelFormat,
                        BitmapCacheOption.OnLoad);

                    if (decoder.Frames.Count == 0)
                    {
                        return null;
                    }

                    var frames = new List<BitmapSource>();
                    var delays = new List<int>();
                    var totalPixels = 0L;
                    foreach (var frame in decoder.Frames)
                    {
                        totalPixels += (long)frame.PixelWidth * frame.PixelHeight;
                    }

                    var downscale = totalPixels > 160000000L; // roughly > 512 MB if all RGBA frames were kept
                    var step = decoder.Frames.Count > 180
                        ? (int)Math.Ceiling(decoder.Frames.Count / 180.0)
                        : 1;
                    for (var i = 0; i < decoder.Frames.Count; i += step)
                    {
                        var frame = decoder.Frames[i];
                        BitmapSource source = frame;
                        if (downscale)
                        {
                            source = ThumbnailService.ScaleDown(frame, 1920);
                        }
                        source.Freeze();
                        frames.Add(source);
                        delays.Add(ReadDelay(frame));
                    }

                    if (step > 1)
                    {
                        AppLog.Warn("Large GIF sampled: " + decoder.Frames.Count + " frames, step=" + step);
                    }

                    return new GifAnimation(frames, delays);
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("GIF decode failed: " + path, ex);
                return null;
            }
        }

        private static int ReadDelay(BitmapFrame frame)
        {
            try
            {
                var metadata = frame.Metadata as BitmapMetadata;
                if (metadata != null)
                {
                    var value = metadata.GetQuery("/grctlext/Delay");
                    if (value != null)
                    {
                        var delay = Convert.ToInt32(value);
                        return Math.Max(30, Math.Min(1000, delay * 10));
                    }
                }
            }
            catch
            {
            }

            return 100;
        }
    }
}
