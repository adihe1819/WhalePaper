using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace WhalePaper.Services
{
    /// <summary>
    /// Plays a video by pulling RGB32 frames from a Media Foundation Source Reader
    /// and pushing them to the native wallpaper host. Runs on its own MTA thread so
    /// decoding and GDI blits never block the WPF UI thread.
    /// </summary>
    internal sealed class MfVideoPlayback : IFramePlayback
    {
        private readonly string _path;
        private readonly int _maxFps;
        private Thread _thread;
        private volatile bool _stop;
        private volatile bool _paused;
        private volatile bool _firstFrame;
        private volatile string _description;
        private bool _disposed;

        public MfVideoPlayback(string path, int maxFps)
        {
            _path = path;
            _maxFps = Math.Max(1, Math.Min(60, maxFps));
            _description = "视频";
        }

        public event Action<FrameView> FrameReady;
        public event Action<string> Failed;

        public bool FirstFramePainted
        {
            get { return _firstFrame; }
        }

        public string Description
        {
            get { return _description ?? "视频"; }
        }

        public bool Start()
        {
            if (_disposed || string.IsNullOrWhiteSpace(_path) || !File.Exists(_path))
            {
                return false;
            }

            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "WhalePaperMF"
            };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
            return true;
        }

        private void Run()
        {
            MfSourceReader reader = null;
            try
            {
                reader = MfSourceReader.Open(_path, out int errorHr, out string error);
                if (reader == null)
                {
                    Fail("视频解码器打开失败：" + error);
                    return;
                }

                _description = "视频 " + reader.Description;
                AppLog.Info("MF playback start: " + reader.Description);

                var fps = reader.Fps > 0 ? reader.Fps : 30;
                fps = Math.Min(_maxFps, fps);
                var interval = 10000000.0 / Math.Max(1, fps);
                var watch = new Stopwatch();
                long? firstTimestamp = null;
                double nextFrameTimestamp = double.MinValue;

                while (!_stop)
                {
                    if (_paused)
                    {
                        watch.Stop();
                        Thread.Sleep(30);
                        continue;
                    }
                    watch.Start();

                    var status = reader.ReadSample(out FrameSample sample);

                    if (status == MfReadStatus.EndOfStream)
                    {
                        if (!reader.SeekToStart())
                        {
                            AppLog.Warn("MF loop seek failed; reopening the source reader");
                            reader.Dispose();
                            reader = MfSourceReader.Open(_path, out errorHr, out error);
                            if (reader == null)
                            {
                                Fail("视频循环时重新打开失败：" + error);
                                break;
                            }
                            _description = "视频 " + reader.Description;
                        }
                        firstTimestamp = null;
                        nextFrameTimestamp = double.MinValue;
                        watch.Reset();
                        continue;
                    }

                    if (status == MfReadStatus.StreamTick)
                    {
                        Thread.Sleep(5);
                        continue;
                    }

                    if (status == MfReadStatus.Error || sample == null)
                    {
                        Fail("MF_DECODE_FAILED 视频解码失败（" + reader.LastErrorStage +
                             "）" + MfInteropText(reader.LastError));
                        break;
                    }

                    try
                    {
                        if (!firstTimestamp.HasValue)
                        {
                            firstTimestamp = sample.Timestamp;
                            watch.Restart();
                        }

                        // Limit painting to maxFps by dropping intermediate source frames.
                        // Sleeping once per decoded frame would play a 60fps video at
                        // half speed when the wallpaper paint limit is 30fps.
                        if (sample.Timestamp < nextFrameTimestamp) { continue; }
                        var dueMs = (sample.Timestamp - firstTimestamp.Value) / 10000.0;
                        WaitUntil(watch, dueMs);
                        if (_stop) { break; }

                        var handler = FrameReady;
                        if (handler != null)
                        {
                            handler(new FrameView(
                                sample.Bits,
                                sample.Width,
                                sample.Height,
                                sample.Stride,
                                sample.BottomUp,
                                sample.BitCount));
                        }
                        _firstFrame = true;
                        nextFrameTimestamp = sample.Timestamp + interval - 1;
                    }
                    catch (Exception ex)
                    {
                        AppLog.Error("Paint video frame failed", ex);
                    }
                    finally
                    {
                        sample.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("MF playback failed", ex);
                Fail(ex.Message);
            }
            finally
            {
                if (reader != null)
                {
                    reader.Dispose();
                }
                AppLog.Info("MF playback stopped");
            }
        }

        private static string MfInteropText(int hr)
        {
            return hr == 0 ? "(unknown)" : "0x" + ((uint)hr).ToString("X8");
        }

        private void WaitUntil(Stopwatch watch, double dueMilliseconds)
        {
            while (!_stop)
            {
                if (_paused)
                {
                    watch.Stop();
                    Thread.Sleep(20);
                    continue;
                }
                watch.Start();
                var remaining = dueMilliseconds - watch.Elapsed.TotalMilliseconds;
                if (remaining <= 0) { return; }
                Thread.Sleep(Math.Max(1, (int)Math.Min(20, remaining)));
            }
        }

        private void Fail(string message)
        {
            AppLog.Warn("Video playback failed: " + message);
            var handler = Failed;
            if (handler != null)
            {
                handler(message);
            }
        }

        public void Pause()
        {
            _paused = true;
        }

        public void Resume()
        {
            _paused = false;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _stop = true;

            var thread = _thread;
            _thread = null;
            if (thread != null && thread.IsAlive)
            {
                if (!thread.Join(3000))
                {
                    AppLog.Warn("MF playback thread did not stop in time");
                }
            }
        }
    }
}
