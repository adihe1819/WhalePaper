using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using WhalePaper.Interop;

namespace WhalePaper.Services
{
    // Exercises real decoding, owned buffers, pacing and the same GDI blit into an
    // off-screen DIB. This mode never creates/attaches a desktop wallpaper window.
    internal static class PlaybackProbeService
    {
        public static int Run(string[] args)
        {
            var index = Array.IndexOf(args, "--playback-probe");
            var path = index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
            var seconds = ReadNumber(args, "--probe-seconds", 65, 5, 120);
            var maxFps = ReadNumber(args, "--probe-max-fps", 240, 1, 240);
            var noPaint = Array.IndexOf(args, "--probe-no-paint") >= 0;
            var report = Path.Combine(AppPaths.LogDir, "playback-probe.txt");
            Bitmap bitmap = null; Graphics graphics = null;
            string failure = null;
            long count = 0;
            var pausePassed = false; var resumePassed = false;
            var player = new MfVideoPlayback(path, maxFps);
            try
            {
                player.Failed += message => { failure = message; };
                player.FrameReady += frame =>
                {
                    if (!noPaint)
                    {
                        if (bitmap == null)
                        { bitmap = new Bitmap(2560, 1440, PixelFormat.Format32bppRgb); graphics = Graphics.FromImage(bitmap); }
                        var hdc = graphics.GetHdc();
                        try
                        {
                            var bmi = new NativeMethods.BITMAPINFO();
                            bmi.bmiHeader.biSize = Marshal.SizeOf(typeof(NativeMethods.BITMAPINFOHEADER));
                            bmi.bmiHeader.biWidth = frame.Width;
                            bmi.bmiHeader.biHeight = frame.BottomUp ? frame.Height : -frame.Height;
                            bmi.bmiHeader.biPlanes = 1; bmi.bmiHeader.biBitCount = (short)frame.BitCount;
                            bmi.bmiHeader.biCompression = NativeMethods.BI_RGB;
                            NativeMethods.SetStretchBltMode(hdc, NativeMethods.COLORONCOLOR);
                            var result = NativeMethods.StretchDIBits(hdc, 0, 0, 2560, 1440,
                                0, 0, frame.Width, frame.Height, frame.Bits, ref bmi, NativeMethods.DIB_RGB_COLORS, NativeMethods.SRCCOPY);
                            if (result == 0 || result == -1) { throw new InvalidOperationException("Offscreen GDI blit failed"); }
                            NativeMethods.GdiFlush();
                        }
                        finally { graphics.ReleaseHdc(hdc); }
                    }
                    else { Marshal.ReadByte(frame.Bits); }
                    Interlocked.Increment(ref count);
                };
                if (!player.Start()) { throw new InvalidDataException("Cannot start playback probe"); }
                var startup = Stopwatch.StartNew();
                while (Interlocked.Read(ref count) == 0 && failure == null && startup.ElapsedMilliseconds < 15000) { Thread.Sleep(20); }
                if (Interlocked.Read(ref count) == 0) { throw new InvalidDataException(failure ?? "First frame timeout"); }
                var watch = Stopwatch.StartNew();
                var pauseDone = false;
                while (watch.Elapsed.TotalSeconds < seconds && failure == null)
                {
                    if (!pauseDone && watch.Elapsed.TotalSeconds >= 2)
                    {
                        player.Pause(); Thread.Sleep(100);
                        var before = Interlocked.Read(ref count); Thread.Sleep(350);
                        pausePassed = before == Interlocked.Read(ref count);
                        player.Resume(); Thread.Sleep(200);
                        resumePassed = Interlocked.Read(ref count) > before;
                        pauseDone = true;
                    }
                    Thread.Sleep(20);
                }
                player.Dispose();
                var text = "path=" + path + Environment.NewLine + "offscreenGdi=" + !noPaint + Environment.NewLine +
                    "seconds=" + seconds + Environment.NewLine + "pausePassed=" + pausePassed + Environment.NewLine +
                    "resumePassed=" + resumePassed + Environment.NewLine + player.GetDiagnostics() + Environment.NewLine +
                    "failure=" + (failure ?? "none");
                File.WriteAllText(report, text);
                AppLog.Info("Playback probe: " + text);
                return failure == null && pausePassed && resumePassed ? 0 : 1;
            }
            catch (Exception ex)
            { File.WriteAllText(report, ex.ToString()); AppLog.Error("Playback probe failed", ex); return 1; }
            finally
            { player.Dispose(); if (graphics != null) { graphics.Dispose(); } if (bitmap != null) { bitmap.Dispose(); } }
        }

        private static int ReadNumber(string[] args, string name, int fallback, int min, int max)
        {
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int value) ? Math.Max(min, Math.Min(max, value)) : fallback;
        }
    }
}
