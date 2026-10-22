using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WhalePaper.Services
{
    /// <summary>
    /// One frame to blit. The pointer must stay valid for the duration of the
    /// FrameReady callback only.
    /// </summary>
    internal struct FrameView
    {
        public IntPtr Bits;
        public int Width;
        public int Height;
        public int Stride;
        public bool BottomUp;
        public int BitCount;

        public FrameView(IntPtr bits, int width, int height, int stride, bool bottomUp, int bitCount)
        {
            Bits = bits;
            Width = width;
            Height = height;
            Stride = stride;
            BottomUp = bottomUp;
            BitCount = bitCount;
        }

        public int BytesPerPixel
        {
            get { return BitCount / 8; }
        }
    }

    internal interface IFramePlayback : IDisposable
    {
        event Action<FrameView> FrameReady;
        event Action<string> Failed;

        bool FirstFramePainted { get; }
        string Description { get; }

        bool Start();
        void Pause();
        void Resume();
    }

    /// <summary>
    /// A byte buffer pinned for GDI. Used for the poster and for GIF frames.
    /// </summary>
    internal sealed class PinnedFrame : IDisposable
    {
        private GCHandle _pin;

        public PinnedFrame(byte[] pixels, int width, int height)
        {
            Pixels = pixels;
            Width = width;
            Height = height;
            Stride = width * 4;
            _pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        }

        public byte[] Pixels { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public int Stride { get; private set; }

        public IntPtr Bits
        {
            get { return _pin.IsAllocated ? _pin.AddrOfPinnedObject() : IntPtr.Zero; }
        }

        public FrameView View
        {
            get { return new FrameView(Bits, Width, Height, Stride, false, 32); }
        }

        public void Dispose()
        {
            if (_pin.IsAllocated)
            {
                _pin.Free();
            }
            Pixels = null;
        }
    }

    /// <summary>
    /// GIF playback through the existing WPF decoder (decode only, no WPF rendering).
    /// Frames are converted to BGRA and blitted by the native host like video frames.
    /// </summary>
    internal sealed class GifPlayback : IFramePlayback
    {
        private readonly string _path;
        private GifAnimation _animation;
        private Thread _thread;
        private volatile bool _stop;
        private volatile bool _paused;
        private volatile bool _firstFrame;
        private volatile string _description;
        private byte[] _buffer;
        private GCHandle _bufferPin;
        private bool _disposed;

        public GifPlayback(string path)
        {
            _path = path;
        }

        public event Action<FrameView> FrameReady;
        public event Action<string> Failed;

        public bool FirstFramePainted
        {
            get { return _firstFrame; }
        }

        public string Description
        {
            get { return _description ?? "GIF"; }
        }

        public bool Start()
        {
            if (_disposed || string.IsNullOrWhiteSpace(_path) || !File.Exists(_path))
            {
                return false;
            }

            _animation = GifAnimation.Load(_path);
            if (_animation == null || _animation.Frames == null || _animation.Frames.Count == 0)
            {
                return false;
            }

            var first = _animation.Frames[0];
            _description = "GIF " + first.PixelWidth + "x" + first.PixelHeight +
                           " frames=" + _animation.Frames.Count;

            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "WhalePaperGif"
            };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
            return true;
        }

        private void Run()
        {
            try
            {
                var frames = _animation.Frames;
                var delays = _animation.Delays;
                var index = 0;

                while (!_stop)
                {
                    if (_paused)
                    {
                        Thread.Sleep(30);
                        continue;
                    }

                    var frame = frames[index];
                    var delay = index < delays.Count ? delays[index] : 100;

                    var width = frame.PixelWidth;
                    var height = frame.PixelHeight;
                    if (width <= 0 || height <= 0)
                    {
                        Fail("GIF 帧尺寸无效");
                        return;
                    }

                    var stride = width * 4;
                    EnsureBuffer(stride * height);

                    BitmapSource source = frame;
                    if (source.Format != PixelFormats.Bgra32)
                    {
                        source = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
                    }

                    source.CopyPixels(_buffer, stride, 0);

                    var handler = FrameReady;
                    if (handler != null)
                    {
                        handler(new FrameView(
                            _bufferPin.AddrOfPinnedObject(), width, height, stride, false, 32));
                    }
                    _firstFrame = true;

                    SleepInterruptible(delay);
                    index = (index + 1) % frames.Count;
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("GIF playback failed", ex);
                Fail(ex.Message);
            }
        }

        private void EnsureBuffer(int size)
        {
            if (_buffer != null && _buffer.Length >= size)
            {
                return;
            }

            if (_bufferPin.IsAllocated)
            {
                _bufferPin.Free();
            }

            _buffer = new byte[size];
            _bufferPin = GCHandle.Alloc(_buffer, GCHandleType.Pinned);
        }

        private void SleepInterruptible(int milliseconds)
        {
            var end = Environment.TickCount + Math.Max(1, milliseconds);
            while (!_stop && !_paused && Environment.TickCount < end)
            {
                var remaining = end - Environment.TickCount;
                Thread.Sleep(Math.Max(1, Math.Min(20, remaining)));
            }
        }

        private void Fail(string message)
        {
            AppLog.Warn("GIF playback failed: " + message);
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
                if (!thread.Join(2000))
                {
                    AppLog.Warn("GIF playback thread did not stop in time");
                }
            }

            if (_bufferPin.IsAllocated)
            {
                _bufferPin.Free();
            }
            _buffer = null;
            _bufferPin = default(GCHandle);
        }
    }
}
