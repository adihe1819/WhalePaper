using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace WhalePaper.Services
{
    // Decode and presentation are independent. Owned RGB frames cushion synchronous
    // MF seeks, while one presentation clock spans every loop of the source file.
    internal sealed class MfVideoPlayback : IFramePlayback
    {
        private const long BufferBudget = 512L * 1024 * 1024;
        private const int MaximumBuffers = 64;
        private readonly string _path;
        private readonly int _maxFps;
        private readonly CancellationTokenSource _cancel = new CancellationTokenSource();
        private readonly ManualResetEventSlim _resume = new ManualResetEventSlim(true);
        private readonly ManualResetEventSlim _ready = new ManualResetEventSlim(false);
        private readonly object _poolLock = new object(), _statisticsLock = new object();
        private readonly Stack<OwnedFrame> _pool = new Stack<OwnedFrame>();
        private readonly Queue<double> _gaps = new Queue<double>();
        private BlockingCollection<OwnedFrame> _frames;
        private Thread _decoder, _presenter;
        private IntPtr _waitTimer;
        private volatile bool _stop, _paused, _firstFrame;
        private volatile string _description = "视频", _decodeFailure;
        private int _disposed, _workers, _allocated, _bufferLimit, _frameBytes, _queueCapacity;
        private int _width, _height, _stride, _bitCount;
        private bool _bottomUp;
        private double _targetIntervalMs;
        private long _presented, _dropped, _loopTransitions, _underruns;
        private double _firstPresentedMs = -1, _lastPresentedMs = -1;
        private double _maximumGap, _maximumLoopGap, _maximumPaintMs;

        public MfVideoPlayback(string path, int maxFps)
        { _path = path; _maxFps = Math.Max(1, Math.Min(240, maxFps)); }
        public event Action<FrameView> FrameReady;
        public event Action<string> Failed;
        public bool FirstFramePainted { get { return _firstFrame; } }
        public string Description { get { return _description; } }

        public bool Start()
        {
            if (Volatile.Read(ref _disposed) != 0 || _decoder != null ||
                string.IsNullOrWhiteSpace(_path) || !File.Exists(_path)) { return false; }
            _workers = 2;
            _decoder = new Thread(Decode) { IsBackground = true, Name = "WhalePaperDecodeAhead" };
            _presenter = new Thread(Present) { IsBackground = true, Name = "WhalePaperPresent" };
            _decoder.SetApartmentState(ApartmentState.MTA);
            _presenter.SetApartmentState(ApartmentState.MTA);
            _decoder.Start(); _presenter.Start();
            return true;
        }

        private void Configure(FrameSample sample, double sourceFps)
        {
            _width = sample.Width; _height = sample.Height; _bitCount = sample.BitCount;
            _bottomUp = sample.BottomUp;
            var rowBytes = checked(_width * (_bitCount / 8));
            _stride = checked((rowBytes + 3) & ~3);
            var size = checked((long)_stride * _height);
            if (size <= 0 || size > BufferBudget / 4 || size > int.MaxValue)
            { throw new InvalidDataException("视频单帧超过安全缓冲限制。"); }
            _frameBytes = (int)size;
            _bufferLimit = (int)Math.Min(MaximumBuffers, BufferBudget / _frameBytes);
            var fps = Math.Max(1, Math.Min(_maxFps, sourceFps));
            _targetIntervalMs = 1000.0 / fps;
            // The budget also includes one frame in the decoder and one in the presenter.
            _queueCapacity = Math.Max(2, Math.Min(_bufferLimit - 2, (int)Math.Ceiling(fps * .4)));
            _frames = new BlockingCollection<OwnedFrame>(_queueCapacity);
            AppLog.Info("MF prefetch: targetFps=" + fps.ToString("0.###") +
                " queue=" + _queueCapacity + " frameBytes=" + _frameBytes +
                " maxBuffers=" + _bufferLimit + " budgetMiB=" + (BufferBudget / 1048576));
        }

        private OwnedFrame Rent()
        {
            lock (_poolLock)
            {
                if (_pool.Count > 0) { return _pool.Pop(); }
                if (_allocated >= _bufferLimit) { throw new InvalidOperationException("视频预取缓冲达到内存上限。"); }
                var frame = new OwnedFrame(_frameBytes); _allocated++; return frame;
            }
        }

        private void Return(OwnedFrame frame)
        { if (frame != null) { lock (_poolLock) { _pool.Push(frame); } } }

        private void Decode()
        {
            MfSourceReader reader = null;
            OwnedFrame owned = null;
            try
            {
                reader = MfSourceReader.Open(_path, out int hr, out string error);
                if (reader == null) { throw new InvalidDataException("视频解码器打开失败：" + error); }
                _description = "视频 " + reader.Description;
                var sourceFps = reader.Fps > 0 ? reader.Fps : 30;
                var sourceInterval = 10000000.0 / sourceFps;
                var outputInterval = 10000000.0 / Math.Max(1, Math.Min(_maxFps, sourceFps));
                long? firstTimestamp = null;
                double lastRelative = 0, loopOffset = 0, nextSelected = double.MinValue;
                long loop = 0;
                var decodedThisLoop = 0;
                while (!_stop)
                {
                    _resume.Wait(_cancel.Token);
                    var status = reader.ReadSample(out FrameSample sample);
                    if (status == MfReadStatus.EndOfStream)
                    {
                        if (decodedThisLoop == 0) { throw new InvalidDataException("视频循环未解码到任何帧。"); }
                        // Include the duration of the last frame; do not reset the display clock.
                        loopOffset += lastRelative + sourceInterval;
                        loop++; firstTimestamp = null; nextSelected = double.MinValue; decodedThisLoop = 0;
                        var seek = Stopwatch.StartNew();
                        if (!reader.SeekToStart())
                        {
                            reader.Dispose(); reader = MfSourceReader.Open(_path, out hr, out error);
                            if (reader == null) { throw new InvalidDataException("视频循环重新打开失败：" + error); }
                        }
                        AppLog.Info("MF seek ahead: loop=" + loop + " seekMs=" +
                            seek.Elapsed.TotalMilliseconds.ToString("0.###") + " queued=" + _frames.Count);
                        continue;
                    }
                    if (status == MfReadStatus.StreamTick)
                    { if (_cancel.Token.WaitHandle.WaitOne(1)) { break; } continue; }
                    if (status != MfReadStatus.Frame || sample == null)
                    { throw new InvalidDataException("MF_DECODE_FAILED " + reader.LastErrorStage + " " + reader.LastError); }
                    try
                    {
                        if (!firstTimestamp.HasValue) { firstTimestamp = sample.Timestamp; }
                        lastRelative = Math.Max(0, sample.Timestamp - firstTimestamp.Value); decodedThisLoop++;
                        if (sample.Timestamp < nextSelected) { continue; }
                        if (_frames == null) { Configure(sample, sourceFps); }
                        if (sample.Width != _width || sample.Height != _height || sample.BitCount != _bitCount || sample.BottomUp != _bottomUp)
                        { throw new InvalidDataException("播放中视频尺寸或像素格式发生改变。"); }
                        owned = Rent(); owned.CopyFrom(sample, _stride, _frameBytes);
                        owned.DueMilliseconds = (loopOffset + lastRelative) / 10000.0; owned.Loop = loop;
                        nextSelected = sample.Timestamp + outputInterval - 1;
                    }
                    finally { sample.Dispose(); }
                    // Release the locked MF buffer before the bounded queue can block.
                    _frames.Add(owned, _cancel.Token); owned = null;
                    if (_frames.Count >= _queueCapacity) { _ready.Set(); }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _decodeFailure = ex.Message; AppLog.Error("MF decode-ahead failed", ex); }
            finally
            {
                Return(owned);
                if (reader != null) { reader.Dispose(); }
                if (_frames != null) { _frames.CompleteAdding(); }
                _ready.Set(); WorkerFinished();
            }
        }

        private void Present()
        {
            var timerResolution = timeBeginPeriod(1) == 0;
            _waitTimer = CreatePresentationTimer();
            var clock = new Stopwatch(); OwnedFrame current = null;
            long previousLoop = -1; double nextStatistics = 5000;
            try
            {
                _ready.Wait(_cancel.Token);
                if (_frames == null)
                { if (!_stop && _decodeFailure != null) { Fail(_decodeFailure); } return; }
                clock.Start();
                while (!_stop)
                {
                    WaitWhilePaused(clock);
                    if (!_frames.TryTake(out current, 5, _cancel.Token))
                    {
                        if (_frames.IsCompleted) { break; }
                        lock (_statisticsLock) { _underruns++; } continue;
                    }
                    WaitUntil(clock, current.DueMilliseconds);
                    if (_stop) { break; }
                    // Recover from a slow display callback rather than slowing the entire movie.
                    if (_frames.Count > 0 && clock.Elapsed.TotalMilliseconds - current.DueMilliseconds > Math.Max(10, _targetIntervalMs * 1.5))
                    { lock (_statisticsLock) { _dropped++; } Return(current); current = null; continue; }
                    var before = clock.Elapsed.TotalMilliseconds;
                    var handler = FrameReady;
                    if (handler != null) { handler(new FrameView(current.Bits, _width, _height, _stride, _bottomUp, _bitCount)); }
                    var paintMs = clock.Elapsed.TotalMilliseconds - before; _firstFrame = true;
                    var newLoop = previousLoop >= 0 && current.Loop != previousLoop;
                    double gap;
                    lock (_statisticsLock)
                    {
                        gap = _lastPresentedMs >= 0 ? before - _lastPresentedMs : 0;
                        if (_firstPresentedMs < 0) { _firstPresentedMs = before; }
                        if (_lastPresentedMs >= 0)
                        {
                            _maximumGap = Math.Max(_maximumGap, gap);
                            if (_gaps.Count == 32768) { _gaps.Dequeue(); } _gaps.Enqueue(gap);
                        }
                        if (newLoop) { _loopTransitions++; _maximumLoopGap = Math.Max(_maximumLoopGap, gap); }
                        _lastPresentedMs = before; _presented++; _maximumPaintMs = Math.Max(_maximumPaintMs, paintMs);
                    }
                    if (newLoop) { AppLog.Info("MF loop presented: loop=" + current.Loop + " gapMs=" + gap.ToString("0.###") + " queued=" + _frames.Count); }
                    previousLoop = current.Loop; Return(current); current = null;
                    if (clock.Elapsed.TotalMilliseconds >= nextStatistics)
                    { AppLog.Info("MF performance: " + GetDiagnostics()); nextStatistics += 5000; }
                }
                if (!_stop && _decodeFailure != null) { Fail(_decodeFailure); }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { AppLog.Error("MF presentation failed", ex); if (!_stop) { Fail(ex.Message); } }
            finally
            {
                Return(current); _stop = true; _cancel.Cancel(); _resume.Set();
                ClosePresentationTimer();
                if (timerResolution) { timeEndPeriod(1); }
                AppLog.Info("MF playback stopped: " + GetDiagnostics()); WorkerFinished();
            }
        }

        private void WaitWhilePaused(Stopwatch clock)
        {
            if (!_paused) { return; }
            clock.Stop(); _resume.Wait(_cancel.Token); if (!_stop) { clock.Start(); }
        }

        private void WaitUntil(Stopwatch clock, double dueMilliseconds)
        {
            while (!_stop)
            {
                WaitWhilePaused(clock);
                var remaining = dueMilliseconds - clock.Elapsed.TotalMilliseconds;
                if (remaining <= 0) { return; }
                // Relative, one-shot high-resolution timers also work while this
                // process is occluded. timeBeginPeriod can be ignored in that state
                // on Windows 11, causing Sleep(1) to round up to a 15.6-ms tick.
                if (_waitTimer != IntPtr.Zero && remaining > .2)
                {
                    long due = -Math.Max(1L, (long)(Math.Min(10, remaining - .2) * 10000));
                    if (SetWaitableTimer(_waitTimer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
                    {
                        var result = WaitForSingleObject(_waitTimer, 20);
                        if (result == 0 || result == 0x102) { continue; }
                    }
                    AppLog.Warn("High-resolution presentation wait failed: " + Marshal.GetLastWin32Error() + "; falling back to Sleep");
                    ClosePresentationTimer();
                }
                if (remaining > 1.5) { Thread.Sleep(Math.Max(1, (int)Math.Min(10, remaining - .75))); }
                else if (remaining > .2) { Thread.Yield(); }
                else { Thread.SpinWait(64); }
            }
        }

        private static IntPtr CreatePresentationTimer()
        {
            try
            {
                // Auto-reset, high-resolution; only modify-state and synchronize access.
                var timer = CreateWaitableTimerExW(IntPtr.Zero, null, 0x2, 0x00100002);
                if (timer == IntPtr.Zero) { AppLog.Warn("High-resolution timer unavailable: " + Marshal.GetLastWin32Error()); }
                else { AppLog.Info("High-resolution waitable presentation timer enabled"); }
                return timer;
            }
            catch (EntryPointNotFoundException) { return IntPtr.Zero; }
        }

        private void ClosePresentationTimer()
        {
            var timer = Interlocked.Exchange(ref _waitTimer, IntPtr.Zero);
            if (timer != IntPtr.Zero) { CloseHandle(timer); }
        }

        public string GetDiagnostics()
        {
            lock (_statisticsLock)
            {
                var gaps = _gaps.ToArray(); Array.Sort(gaps);
                var p95 = gaps.Length == 0 ? 0 : gaps[(int)Math.Min(gaps.Length - 1, Math.Ceiling(gaps.Length * .95) - 1)];
                var fps = _lastPresentedMs > _firstPresentedMs ? (_presented - 1) * 1000 / (_lastPresentedMs - _firstPresentedMs) : 0;
                return "fps=" + fps.ToString("0.###") + " painted=" + _presented + " dropped=" + _dropped +
                    " underrunWaits=" + _underruns + " loops=" + _loopTransitions + " gapP95Ms=" + p95.ToString("0.###") +
                    " gapMaxMs=" + _maximumGap.ToString("0.###") + " loopGapMaxMs=" + _maximumLoopGap.ToString("0.###") +
                    " paintMaxMs=" + _maximumPaintMs.ToString("0.###") + " buffers=" + _allocated +
                    " bufferMiB=" + ((_allocated * (long)_frameBytes) / 1048576.0).ToString("0.###");
            }
        }

        private void WorkerFinished()
        {
            if (Interlocked.Decrement(ref _workers) != 0) { return; }
            if (_frames != null)
            { while (_frames.TryTake(out OwnedFrame frame)) { Return(frame); } _frames.Dispose(); }
            lock (_poolLock) { while (_pool.Count > 0) { _pool.Pop().Dispose(); } }
            _ready.Dispose(); _resume.Dispose(); _cancel.Dispose();
        }

        private void Fail(string message)
        { AppLog.Warn("Video playback failed: " + message); var handler = Failed; if (handler != null) { handler(message); } }
        public void Pause() { if (_stop) { return; } _paused = true; _resume.Reset(); }
        public void Resume() { if (_stop) { return; } _paused = false; _resume.Set(); }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }
            _stop = true;
            if (Volatile.Read(ref _workers) == 0) { return; }
            try { _cancel.Cancel(); _resume.Set(); _ready.Set(); } catch (ObjectDisposedException) { }
            var current = Thread.CurrentThread;
            if (_presenter != null && _presenter != current && !_presenter.Join(3000)) { AppLog.Warn("MF presenter still stopping; buffers remain owned by worker threads"); }
            if (_decoder != null && _decoder != current && !_decoder.Join(3000)) { AppLog.Warn("MF decoder still stopping; buffers remain owned by worker threads"); }
            if (_presenter == null || !_presenter.IsAlive) { ClosePresentationTimer(); }
        }

        private sealed class OwnedFrame : IDisposable
        {
            public IntPtr Bits { get; private set; }
            public double DueMilliseconds;
            public long Loop;
            public OwnedFrame(int bytes) { Bits = Marshal.AllocHGlobal(bytes); }
            public void CopyFrom(FrameSample source, int stride, int bytes)
            {
                var rowBytes = source.Width * source.BytesPerPixel;
                if (source.Stride == stride) { CopyMemory(Bits, source.Bits, new UIntPtr((uint)(bytes - (stride - rowBytes)))); }
                else
                { for (var row = 0; row < source.Height; row++) { CopyMemory(IntPtr.Add(Bits, row * stride), IntPtr.Add(source.Bits, row * source.Stride), new UIntPtr((uint)rowBytes)); } }
            }
            public void Dispose() { if (Bits != IntPtr.Zero) { Marshal.FreeHGlobal(Bits); Bits = IntPtr.Zero; } }
        }
        [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")] private static extern void CopyMemory(IntPtr destination, IntPtr source, UIntPtr length);
        [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint period);
        [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint period);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string name, uint flags, uint access);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr completion, IntPtr argument, [MarshalAs(UnmanagedType.Bool)] bool resume);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    }
}
