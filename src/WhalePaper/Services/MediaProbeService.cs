using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WhalePaper.Services
{
    /// <summary>
    /// --media-probe: decodes a video or GIF without creating any window, prints the
    /// negotiated Media Foundation output and writes PNG frames for inspection. This is
    /// how the 1.0.4 renderer is verified on a machine where the desktop cannot be
    /// sampled (or before the wallpaper host is involved at all).
    /// </summary>
    internal static class MediaProbeService
    {
        public static string ReportPath { get; private set; }
        public static bool Succeeded { get; private set; }

        public static string OutputDirectory
        {
            get { return Path.Combine(AppPaths.LogDir, "media-probe"); }
        }

        public static string Run(string path)
        {
            Succeeded = false;
            var report = new StringBuilder();
            report.AppendLine("鲸鱼壁纸 · 媒体探测（" + DiagnosticsProbe.BuildTag + "）");
            report.AppendLine("时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            report.AppendLine("系统：" + Environment.OSVersion.VersionString +
                               "，Build=" + Environment.OSVersion.Version.Build);
            report.AppendLine("文件：" + (string.IsNullOrWhiteSpace(path) ? "<未指定>" : path));
            report.AppendLine("------------------------------------------------------------");

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                report.AppendLine("结果：文件不存在");
                return Finish(report);
            }

            try
            {
                var extension = Path.GetExtension(path);
                if (string.Equals(extension, ".gif", StringComparison.OrdinalIgnoreCase))
                {
                    Succeeded = RunGif(path, report);
                }
                else
                {
                    Succeeded = RunVideo(path, report);
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("Media probe failed", ex);
                report.AppendLine("结果：异常 " + ex.Message);
            }

            return Finish(report);
        }

        private static bool RunVideo(string path, StringBuilder report)
        {
            var reader = MfSourceReader.Open(path, out _, out string error);
            if (reader == null)
            {
                report.AppendLine("结果：打开失败 " + error);
                return false;
            }

            try
            {
                var outputDirectory = OutputDirectory;
                Directory.CreateDirectory(outputDirectory);

                report.AppendLine("协商格式：" + reader.Description);
                report.AppendLine("源帧率：" + reader.SourceFps.ToString("0.###"));
                report.AppendLine("输出打包：" + (reader.RowsArePacked ? "是" : "否（有行填充，已按行复制）"));

                var watch = Stopwatch.StartNew();
                var frames = 0;
                var saved = 0;
                var looped = false;
                var seekRequested = false;
                var failed = false;
                var firstFrameMs = -1L;

                for (var index = 0; index < 120; index++)
                {
                    var status = reader.ReadSample(out FrameSample sample);
                    if (status == MfReadStatus.EndOfStream)
                    {
                        report.AppendLine("到达流末尾（第 " + index + " 次读取），测试回 0");
                        if (!reader.SeekToStart())
                        {
                            report.AppendLine("回 0 失败：" + MfInteropText(reader.LastError));
                            failed = true;
                            break;
                        }
                        seekRequested = true;
                        continue;
                    }

                    if (status == MfReadStatus.StreamTick)
                    {
                        Thread.Sleep(5);
                        continue;
                    }

                    if (status == MfReadStatus.Error || sample == null)
                    {
                        report.AppendLine("解码错误：" + MfInteropText(reader.LastError) +
                                          "（第 " + index + " 次读取，步骤=" + reader.LastErrorStage + "）");
                        failed = true;
                        break;
                    }

                    try
                    {
                        frames++;
                        if (seekRequested) { looped = true; }
                        if (frames == 1)
                        {
                            firstFrameMs = watch.ElapsedMilliseconds;
                            SaveFramePng(sample, Path.Combine(outputDirectory, "frame-0001.png"));
                            saved++;
                        }
                        else if (frames == 30)
                        {
                            SaveFramePng(sample, Path.Combine(outputDirectory, "frame-0030.png"));
                            saved++;
                        }
                    }

                    finally
                    {
                        sample.Dispose();
                    }

                    // Exercise seeking even for long videos. A successful seek alone
                    // is insufficient: require a decoded frame after it as well.
                    if (frames >= 60 && !seekRequested)
                    {
                        if (!reader.SeekToStart())
                        {
                            report.AppendLine("回 0 失败：" + MfInteropText(reader.LastError));
                            failed = true;
                            break;
                        }
                        seekRequested = true;
                    }

                    if (frames >= 60 && looped)
                    {
                        break;
                    }
                }

                report.AppendLine("解码帧数：" + frames +
                                  "，总耗时：" + watch.ElapsedMilliseconds + " ms" +
                                  "，首帧：" + (firstFrameMs < 0 ? "无" : firstFrameMs + " ms"));
                report.AppendLine("实际帧布局：" + reader.Description);
                report.AppendLine("循环（Seek 回 0）：" + (looped ? "已测试" : "未触发"));
                report.AppendLine("已保存 PNG：" + saved + " 张，目录：" + outputDirectory);
                var passed = !failed && frames > 0 && saved > 0 && looped;
                report.AppendLine("结果：" + (passed ? "OK" : "FAIL（解码或循环验证未通过）"));
                return passed;
            }
            finally
            {
                reader.Dispose();
            }
        }

        private static bool RunGif(string path, StringBuilder report)
        {
            var animation = GifAnimation.Load(path);
            if (animation == null || animation.Frames == null || animation.Frames.Count == 0)
            {
                report.AppendLine("结果：GIF 解码失败");
                return false;
            }

            var outputDirectory = OutputDirectory;
            Directory.CreateDirectory(outputDirectory);

            var first = animation.Frames[0];
            report.AppendLine("输出格式：GIF BGRA32 " + first.PixelWidth + "x" + first.PixelHeight +
                              "，帧数=" + animation.Frames.Count);

            var count = Math.Min(3, animation.Frames.Count);
            for (var index = 0; index < count; index++)
            {
                var pathOut = Path.Combine(outputDirectory, "gif-frame-" + (index + 1).ToString("0000") + ".png");
                SaveBitmapSourcePng(animation.Frames[index], pathOut);
            }

            report.AppendLine("已保存 PNG：" + count + " 张，目录：" + outputDirectory);
            report.AppendLine("结果：OK");
            return true;
        }

        private static void SaveFramePng(FrameSample sample, string path)
        {
            var width = sample.Width;
            var height = sample.Height;
            var sourceStride = width * sample.BytesPerPixel;
            var packedStride = width * 4;
            var pixels = new byte[packedStride * height];
            var rowBytes = new byte[sourceStride];

            for (var row = 0; row < height; row++)
            {
                var sourceRow = sample.BottomUp ? height - 1 - row : row;
                var source = new IntPtr(
                    sample.Bits.ToInt64() + (long)sourceRow * sample.Stride);
                Marshal.Copy(source, rowBytes, 0, rowBytes.Length);

                var destination = row * packedStride;
                for (var x = 0; x < width; x++)
                {
                    var input = x * sample.BytesPerPixel;
                    pixels[destination] = rowBytes[input];
                    pixels[destination + 1] = rowBytes[input + 1];
                    pixels[destination + 2] = rowBytes[input + 2];
                    pixels[destination + 3] = 255;
                    destination += 4;
                }
            }

            var bitmap = BitmapSource.Create(
                width, height, 96, 96, PixelFormats.Bgra32, null, pixels, packedStride);
            SaveBitmapSourcePng(bitmap, path);
        }

        private static void SaveBitmapSourcePng(BitmapSource source, string path)
        {
            BitmapSource bitmap = source;
            if (bitmap.Format != PixelFormats.Bgra32)
            {
                var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
                converted.Freeze();
                bitmap = converted;
            }

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path))
            {
                encoder.Save(stream);
            }
        }

        private static string MfInteropText(int hr)
        {
            return hr == 0 ? "(unknown)" : "0x" + ((uint)hr).ToString("X8");
        }

        private static string Finish(StringBuilder report)
        {
            var text = report.ToString();
            try
            {
                Directory.CreateDirectory(AppPaths.LogDir);
                var path = Path.Combine(AppPaths.LogDir, "media-probe.txt");
                File.WriteAllText(path, text, new UTF8Encoding(true));
                ReportPath = path;
            }
            catch (Exception ex)
            {
                AppLog.Warn("Write media probe report failed: " + ex.Message);
            }

            return text;
        }
    }
}
