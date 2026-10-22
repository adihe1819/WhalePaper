using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WhalePaper.Interop;
using WhalePaper.Models;
using DrawingColor = System.Drawing.Color;
using DrawingPoint = System.Drawing.Point;

namespace WhalePaper.Services
{
    /// <summary>
    /// Native Win32 layered wallpaper host. On raised desktops it is a Progman child
    /// below transparent DefView and above the system wallpaper WorkerW. This keeps
    /// the icons visible while video is painted. WPF only supplies the app UI.
    ///
    /// Frames arrive as RGB32 from Media Foundation (or BGRA from the GIF decoder) and
    /// are blitted with StretchDIBits. The window itself is created on the WPF UI thread
    /// so its messages are pumped by the existing Dispatcher loop; decoding and GDI
    /// blits happen on the playback thread.
    /// </summary>
    internal sealed class WallpaperHostWindow : IDisposable
    {
        private const string HostClassName = "WhalePaperNativeHost";
        private const int ErrorClassAlreadyExists = 1410;

        private static readonly object ClassSync = new object();
        private static readonly NativeMethods.WndProcDelegate HostWndProc = HostWindowProc;
        private static bool _classRegistered;
        private static WallpaperHostWindow _currentHost;

        private readonly Dispatcher _dispatcher;
        private readonly object _paintLock = new object();

        private IntPtr _hwnd;
        private IntPtr _blackBrush;
        private DesktopLayerPlacement _placement;
        private WallpaperItem _item;
        private FitMode _fit = FitMode.Fill;
        private PinnedFrame _poster;
        private IFramePlayback _playback;
        private byte[] _packed;
        private GCHandle _packedPin;
        private List<DrawingPoint> _probePoints;
        private bool _isPaused;
        private bool _disposed;
        private bool _windowSuspendedForProbe;
        private bool _firstFrameLogged;
        private long _paintedFrameCount;
        private bool _stretchErrorLogged;
        private string _failureCode;
        private string _failureDetail;

        public WallpaperHostWindow()
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            _blackBrush = NativeMethods.CreateSolidBrush(0);
        }

        public event EventHandler<string> PlaybackFailed;

        /// <summary>
        /// Called when the desktop is fully covered and pixel verification needs more
        /// desktop area. Returns true when the window was actually hidden; the caller
        /// (WallpaperController) restores it after the attach attempt.
        /// </summary>
        public Func<bool> SuspendAppWindow { get; set; }

        public bool WindowSuspendedForProbe
        {
            get { return _windowSuspendedForProbe; }
        }

        public WallpaperItem Item
        {
            get { return _item; }
        }

        public bool IsPaused
        {
            get { return _isPaused; }
        }

        public string AttachStrategyId
        {
            get { return _placement == null ? null : _placement.Id; }
        }

        public bool AttachVerified
        {
            get { return _placement != null && _probePoints != null && _probePoints.Count > 0; }
        }

        public bool AttachLayered
        {
            get { return _placement != null && _placement.Layered; }
        }

        public string AttachDescription
        {
            get
            {
                return _placement == null
                    ? "none"
                    : _placement.Id +
                      " layered=" + (_placement.Layered ? "1" : "0") +
                      " parent=0x" + _placement.Parent.ToInt64().ToString("X");
            }
        }

        public string LastFailure { get; private set; }

        public bool FirstFramePainted
        {
            get { return _playback != null && PaintedFrameCount > 0; }
        }

        public long PaintedFrameCount
        {
            get { return Interlocked.Read(ref _paintedFrameCount); }
        }

        public bool IsAttached
        {
            get
            {
                return !_disposed
                    && _placement != null
                    && NativeMethods.IsWindow(_hwnd)
                    && NativeMethods.IsWindow(_placement.Parent)
                    && NativeMethods.GetParent(_hwnd) == _placement.Parent;
            }
        }

        /// <summary>
        /// Decodes the poster frame that is already visible as the system wallpaper. It
        /// is painted into the host before the layer probe runs, so a successful attach
        /// never flashes a black desktop.
        /// </summary>
        public void SetPoster(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return;
            }

            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                var screen = Screen.PrimaryScreen.Bounds;
                image.DecodePixelWidth = Math.Max(1, screen.Width);
                image.UriSource = new Uri(path, UriKind.Absolute);
                image.EndInit();
                image.Freeze();

                BitmapSource source = image;
                if (source.Format != PixelFormats.Bgra32)
                {
                    var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
                    converted.Freeze();
                    source = converted;
                }

                var stride = source.PixelWidth * 4;
                var pixels = new byte[stride * source.PixelHeight];
                source.CopyPixels(pixels, stride, 0);

                var poster = new PinnedFrame(pixels, source.PixelWidth, source.PixelHeight);
                var old = _poster;
                _poster = poster;
                if (old != null)
                {
                    old.Dispose();
                }

                AppLog.Info("Host poster frame loaded: " + path + " " +
                            poster.Width + "x" + poster.Height);
                Repaint();
            }
            catch (Exception ex)
            {
                AppLog.Warn("Host poster frame failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Creates the native layered child window in the first structurally valid
        /// candidate and proves it with the two-color DIB probe.
        /// </summary>
        public bool AttachToDesktop(string preferredStrategyId)
        {
            if (_disposed)
            {
                return false;
            }

            if (_hwnd != IntPtr.Zero)
            {
                return EnsureLayer();
            }

            LastFailure = null;
            _failureCode = null;
            _failureDetail = null;

            var candidates = DesktopLayer.ResolveHostCandidates();
            if (candidates.Count == 0)
            {
                _failureCode = "NATIVE_NO_CANDIDATE";
                _failureDetail = "SHELLDLL_DefView not found";
                LastFailure = "找不到桌面图标层 SHELLDLL_DefView";
                AppLog.Warn("ATTACH-CONCLUSION: " + _failureCode + " " + _failureDetail);
                return false;
            }

            ApplyPreference(candidates, preferredStrategyId);

            var progman = DesktopLayer.FindProgman();
            var samplePoints = VisibilityProbe.SelectSamplePoints(progman, 5);
            AppLog.Info("Attach start: candidates=" + candidates.Count +
                        " samplePoints=" + VisibilityProbe.DescribePoints(samplePoints));

            // When the user clicks "apply", the app window is usually in front and
            // leaves no desktop pixel to verify. Hide it briefly and sample again.
            if (samplePoints.Count < 2 && SuspendAppWindow != null)
            {
                var suspended = false;
                try
                {
                    suspended = SuspendAppWindow();
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Suspend app window for probe failed: " + ex.Message);
                }

                if (suspended)
                {
                    _windowSuspendedForProbe = true;
                    VisibilityProbe.Pump(300);
                    samplePoints = VisibilityProbe.SelectSamplePoints(progman, 5);
                    AppLog.Info("Attach probe retried with the app window hidden: samplePoints=" +
                                VisibilityProbe.DescribePoints(samplePoints));
                }
            }

            if (samplePoints.Count == 0)
            {
                AppLog.Warn("Attach probe: no desktop pixel is visible; " +
                            "continuing with structural placement only (below SysListView32)");
            }

            DiagnosticsProbe.LogDesktopTree("attach-start");

            foreach (var candidate in candidates)
            {
                string verdictDetail;
                ProbeVerdict verdict;
                if (!TryAttachCandidate(candidate, samplePoints, out verdict, out verdictDetail))
                {
                    continue;
                }

                _probePoints = verdict == ProbeVerdict.Visible
                    ? new List<DrawingPoint>(samplePoints)
                    : null;

                Repaint();

                var verified = verdict == ProbeVerdict.Visible;
                AppLog.Info("Attach success: strategy=" + _placement.Id +
                            " layered=1 parent=0x" + _placement.Parent.ToInt64().ToString("X") +
                            " verified=" + (verified ? "1" : "0") +
                            " probe=" + verdictDetail);
                AppLog.Info("ATTACH-CONCLUSION: " +
                            (verified ? "NATIVE_LAYERED_OK" : "NATIVE_UNVERIFIED") +
                            " layer=" + _placement.Id +
                            " layered=1 parent=0x" + _placement.Parent.ToInt64().ToString("X") +
                            (verified ? "" : " reason=no-desktop-pixels"));
                return true;
            }

            DestroyHostWindow();
            LastFailure = ShortFailure(_failureCode, _failureDetail);
            DiagnosticsProbe.LogDesktopTree("attach-failed");
            AppLog.Warn("ATTACH-CONCLUSION: " +
                        (string.IsNullOrEmpty(_failureCode) ? "NATIVE_ATTACH_FAILED" : _failureCode) +
                        " " + (string.IsNullOrEmpty(_failureDetail) ? LastFailure : _failureDetail));
            return false;
        }

        private bool TryAttachCandidate(
            DesktopLayerCandidate candidate,
            IList<DrawingPoint> samplePoints,
            out ProbeVerdict verdict,
            out string verdictDetail)
        {
            verdict = ProbeVerdict.Unverifiable;
            verdictDetail = "no-sample-points";

            var parent = candidate.Parent;
            if (parent == IntPtr.Zero || !NativeMethods.IsWindow(parent))
            {
                return Fail("NATIVE_NO_PARENT", candidate.Id + ": parent unavailable");
            }

            if (candidate.AboveWindow != IntPtr.Zero &&
                (!NativeMethods.IsWindow(candidate.AboveWindow) ||
                 NativeMethods.GetParent(candidate.AboveWindow) != parent))
            {
                return Fail("NATIVE_PLACE_FAILED", candidate.Id + ": anchor unavailable");
            }

            if (!CreateHostWindow(parent))
            {
                return false;
            }

            if (!DesktopLayer.Place(
                    _hwnd, parent, candidate.AboveWindow, out IntPtr below, out string placeDetail))
            {
                AppLog.Warn("Host place failed: " + placeDetail);
                DestroyHostWindow();
                return Fail("NATIVE_PLACE_FAILED", candidate.Id + ": " + placeDetail);
            }

            AppLog.Info("Host placed: strategy=" + candidate.Id +
                        " parent=0x" + parent.ToInt64().ToString("X") +
                        " above=" + (candidate.AboveWindow == IntPtr.Zero
                            ? "bottom"
                            : "0x" + candidate.AboveWindow.ToInt64().ToString("X")) +
                        " below=" + (below == IntPtr.Zero ? "0x0" : "0x" + below.ToInt64().ToString("X")) +
                        " detail=" + placeDetail);

            // Structural safety: our window must stay below the icon list. If
            // WindowFromPoint now hits our host instead of the desktop icon list, the
            // placement is wrong and the wallpaper must not be accepted.
            if (samplePoints.Count > 0 && HostIsUnderCursor(samplePoints))
            {
                AppLog.Warn("Host placement check failed: host is above the desktop icons");
                DestroyHostWindow();
                return Fail("NATIVE_PLACE_FAILED", candidate.Id + ": host hit-tests above icons");
            }

            Repaint();

            if (samplePoints.Count > 0)
            {
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    verdict = VisibilityProbe.VerifyNative(
                        samplePoints,
                        color => PaintPatches(samplePoints, color),
                        out verdictDetail);

                    AppLog.Info("Attach probe " + candidate.Id +
                                (attempt == 0 ? "" : " retry=" + attempt) +
                                ": verdict=" + verdict + " " + verdictDetail);

                    if (verdict != ProbeVerdict.Hidden)
                    {
                        break;
                    }

                    // A transient WM_PAINT can wipe the patches between the A and B
                    // frames; repaint the poster and try once more before rejecting.
                    Repaint();
                    VisibilityProbe.Pump(150);
                }

                if (verdict == ProbeVerdict.Hidden)
                {
                    DestroyHostWindow();
                    return Fail("NATIVE_HIDDEN", candidate.Id + ": " + verdictDetail);
                }
            }
            else
            {
                verdictDetail = "no-desktop-pixels";
            }

            _placement = new DesktopLayerPlacement
            {
                Strategy = candidate.Strategy,
                Id = candidate.Id,
                Parent = parent,
                AboveWindow = candidate.AboveWindow,
                Layered = true,
                Title = candidate.Title
            };
            return true;
        }

        private bool Fail(string code, string detail)
        {
            _failureCode = code;
            _failureDetail = detail;
            AppLog.Warn("Attach failed: " + code + " " + detail);
            return false;
        }

        private static string ShortFailure(string code, string detail)
        {
            switch (code)
            {
                case "NATIVE_NO_CANDIDATE":
                    return "找不到桌面图标层 SHELLDLL_DefView";
                case "NATIVE_NO_PARENT":
                    return "桌面层父窗口不可用";
                case "NATIVE_CREATE_FAILED":
                    return "无法创建原生挂载窗口" + Suffix(detail);
                case "NATIVE_LAYERED_FAILED":
                    return "原生窗口无法获得分层样式" + Suffix(detail);
                case "NATIVE_PLACE_FAILED":
                    return "无法放到桌面图标下方" + Suffix(detail);
                case "NATIVE_HIDDEN":
                    return "分层窗口像素自证不可见" + Suffix(detail);
                default:
                    return "无法挂载动态壁纸层" + Suffix(detail);
            }
        }

        private static string Suffix(string detail)
        {
            return string.IsNullOrWhiteSpace(detail) ? "" : "（" + detail + "）";
        }

        private bool CreateHostWindow(IntPtr parent)
        {
            try
            {
                EnsureWindowClass();

                int width;
                int height;
                DesktopLayer.GetLayerSize(parent, out width, out height);

                var hInstance = NativeMethods.GetModuleHandle(null);

                // 1.0.5: deliberately no WS_CLIPSIBLINGS. 1.0.4 added it and the sibling
                // SysListView32 (which covers the whole DefView client area) clipped the
                // wallpaper surface to nothing: the 1.0.4 probe showed Hidden 0/3 here,
                // while the same DIB paint was visible 3/3 at the Progman-topmost control.
                // 1.0.3's GDI probe used WS_CHILD|WS_VISIBLE only and was visible 3/3.
                var hostStyle = NativeMethods.WS_CHILD;

                var hwnd = NativeMethods.CreateWindowEx(
                    NativeMethods.WS_EX_LAYERED |
                    NativeMethods.WS_EX_NOACTIVATE |
                    NativeMethods.WS_EX_TOOLWINDOW,
                    HostClassName,
                    "WhalePaperWallpaperHost",
                    hostStyle,
                    0,
                    0,
                    width,
                    height,
                    parent,
                    IntPtr.Zero,
                    hInstance,
                    IntPtr.Zero);

                if (hwnd == IntPtr.Zero)
                {
                    var error = Marshal.GetLastWin32Error();
                    DestroyHostWindow();
                    return Fail("NATIVE_CREATE_FAILED", "CreateWindowEx error=" + error);
                }

                _hwnd = hwnd;
                _currentHost = this;

                if (!NativeMethods.SetLayeredWindowAttributes(
                        hwnd, 0, 255, NativeMethods.LWA_ALPHA))
                {
                    var error = Marshal.GetLastWin32Error();
                    var exStyle = NativeMethods.GetWindowLongPtr(
                        hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
                    AppLog.Warn("Host window layered attribute failed: error=" + error +
                                " ex=0x" + exStyle.ToString("X"));
                    DestroyHostWindow();
                    return Fail("NATIVE_LAYERED_FAILED", "SetLayeredWindowAttributes error=" + error);
                }

                var currentExStyle = NativeMethods.GetWindowLongPtr(
                    hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
                if ((currentExStyle & NativeMethods.WS_EX_LAYERED) == 0)
                {
                    AppLog.Warn("Host window lost WS_EX_LAYERED: ex=0x" +
                                currentExStyle.ToString("X"));
                    DestroyHostWindow();
                    return Fail("NATIVE_LAYERED_FAILED",
                                "WS_EX_LAYERED missing ex=0x" + currentExStyle.ToString("X"));
                }

                AppLog.Info("Host window created: hwnd=0x" + hwnd.ToInt64().ToString("X") +
                            " parent=0x" + parent.ToInt64().ToString("X") +
                            " style=0x" + hostStyle.ToString("X") +
                            " ex=0x" + currentExStyle.ToString("X") +
                            " size=" + width + "x" + height);
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Error("Create native wallpaper host failed", ex);
                DestroyHostWindow();
                return Fail("NATIVE_CREATE_FAILED", ex.Message);
            }
        }

        private static void EnsureWindowClass()
        {
            lock (ClassSync)
            {
                if (_classRegistered)
                {
                    return;
                }

                var windowClass = new NativeMethods.WNDCLASSEX
                {
                    cbSize = (uint)Marshal.SizeOf(typeof(NativeMethods.WNDCLASSEX)),
                    style = 0,
                    lpfnWndProc = HostWndProc,
                    cbClsExtra = 0,
                    cbWndExtra = 0,
                    hInstance = NativeMethods.GetModuleHandle(null),
                    hIcon = IntPtr.Zero,
                    hCursor = IntPtr.Zero,
                    hbrBackground = IntPtr.Zero,
                    lpszMenuName = null,
                    lpszClassName = HostClassName,
                    hIconSm = IntPtr.Zero
                };

                var atom = NativeMethods.RegisterClassEx(ref windowClass);
                if (atom == 0)
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error != ErrorClassAlreadyExists)
                    {
                        throw new System.ComponentModel.Win32Exception(error);
                    }
                }

                _classRegistered = true;
            }
        }

        private static IntPtr HostWindowProc(
            IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            var host = _currentHost;
            if (host != null && host._hwnd == hwnd)
            {
                if (msg == NativeMethods.WM_PAINT)
                {
                    host.OnPaint();
                    return IntPtr.Zero;
                }

                if (msg == NativeMethods.WM_ERASEBKGND)
                {
                    return new IntPtr(1);
                }
            }

            return NativeMethods.DefWindowProc(hwnd, msg, wParam, lParam);
        }

        private void OnPaint()
        {
            NativeMethods.PAINTSTRUCT paint;
            var hdc = NativeMethods.BeginPaint(_hwnd, out paint);
            try
            {
                if (hdc == IntPtr.Zero)
                {
                    return;
                }

                lock (_paintLock)
                {
                    if (_poster != null)
                    {
                        PaintFrameLocked(hdc, _poster.View);
                    }
                    else
                    {
                        FillBlack(hdc);
                    }
                }
            }
            finally
            {
                NativeMethods.EndPaint(_hwnd, ref paint);
            }
        }

        /// <summary>
        /// Paints the two probe colors through the real DIB path (StretchDIBits), which
        /// is stricter than the 1.0.3 class-brush probe. If this is visible, the video
        /// frames use the same blit path.
        /// </summary>
        private void PaintPatches(IList<DrawingPoint> points, DrawingColor color)
        {
            if (_hwnd == IntPtr.Zero || points == null)
            {
                return;
            }

            var hdc = NativeMethods.GetDC(_hwnd);
            if (hdc == IntPtr.Zero)
            {
                return;
            }

            try
            {
                var half = VisibilityProbe.PatchSizePx / 2;
                foreach (var point in points)
                {
                    var client = new NativeMethods.POINT { X = point.X, Y = point.Y };
                    NativeMethods.ScreenToClient(_hwnd, ref client);
                    var rect = new NativeMethods.RECT
                    {
                        Left = client.X - half,
                        Top = client.Y - half,
                        Right = client.X + half,
                        Bottom = client.Y + half
                    };
                    DibPaint.StretchSolid(
                        hdc, rect.Left, rect.Top, rect.Width, rect.Height, color);
                }

                NativeMethods.GdiFlush();
            }
            finally
            {
                NativeMethods.ReleaseDC(_hwnd, hdc);
            }
        }

        private void Playback_FrameReady(FrameView frame)
        {
            if (_disposed || _hwnd == IntPtr.Zero || frame.Bits == IntPtr.Zero)
            {
                return;
            }

            var hdc = NativeMethods.GetDC(_hwnd);
            if (hdc == IntPtr.Zero)
            {
                return;
            }

            try
            {
                lock (_paintLock)
                {
                    if (!PaintFrameLocked(hdc, frame)) { return; }
                }

                NativeMethods.GdiFlush();
                Interlocked.Increment(ref _paintedFrameCount);

                if (!_firstFrameLogged)
                {
                    _firstFrameLogged = true;
                    AppLog.Info("First video frame painted: " + frame.Width + "x" + frame.Height +
                                " stride=" + frame.Stride +
                                (frame.BottomUp ? " bottom-up" : " top-down"));
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("Paint wallpaper frame failed", ex);
            }
            finally
            {
                NativeMethods.ReleaseDC(_hwnd, hdc);
            }
        }

        private void Playback_Failed(string message)
        {
            AppLog.Warn("Host playback failed: " + message);
            if (_disposed)
            {
                return;
            }

            try
            {
                _dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_disposed)
                    {
                        return;
                    }

                    var handler = PlaybackFailed;
                    if (handler != null)
                    {
                        handler(this, message);
                    }
                }));
            }
            catch (Exception ex)
            {
                AppLog.Warn("Marshal playback failure to UI failed: " + ex.Message);
            }
        }

        public bool Load(WallpaperItem item, FitMode fit, bool muted)
        {
            if (_disposed || item == null)
            {
                return false;
            }

            _item = item;
            _fit = fit;
            StopPlayback();

            if (item.Kind == WallpaperKind.Gif)
            {
                return StartPlayback(new GifPlayback(item.StoredPath));
            }

            if (item.Kind == WallpaperKind.Video)
            {
                return StartPlayback(new MfVideoPlayback(item.StoredPath, 30));
            }

            return false;
        }

        private bool StartPlayback(IFramePlayback playback)
        {
            playback.FrameReady += Playback_FrameReady;
            playback.Failed += Playback_Failed;

            if (!playback.Start())
            {
                playback.FrameReady -= Playback_FrameReady;
                playback.Failed -= Playback_Failed;
                playback.Dispose();
                return false;
            }

            _playback = playback;
            AppLog.Info("Host playback started: " + playback.Description);
            return true;
        }

        private void StopPlayback()
        {
            var playback = _playback;
            _playback = null;
            if (playback == null)
            {
                return;
            }

            playback.FrameReady -= Playback_FrameReady;
            playback.Failed -= Playback_Failed;
            playback.Dispose();
            _firstFrameLogged = false;
            Interlocked.Exchange(ref _paintedFrameCount, 0);
        }

        public bool EnsureLayer()
        {
            if (_disposed || _placement == null || _hwnd == IntPtr.Zero)
            {
                return false;
            }

            if (!NativeMethods.IsWindow(_hwnd) ||
                !NativeMethods.IsWindow(_placement.Parent) ||
                NativeMethods.GetParent(_hwnd) != _placement.Parent)
            {
                return false;
            }

            return DesktopLayer.EnsurePlacement(_hwnd, _placement);
        }

        public void Reposition()
        {
            if (_hwnd == IntPtr.Zero || _placement == null)
            {
                return;
            }

            IntPtr below;
            string detail;
            var ok = DesktopLayer.Place(
                _hwnd, _placement.Parent, _placement.AboveWindow, out below, out detail);
            AppLog.Info("Host reposition: ok=" + (ok ? "1" : "0") + " " + detail);
            Repaint();
        }

        public void SetFit(FitMode fit)
        {
            _fit = fit;
            Repaint();
        }

        /// <summary>
        /// Suspends decoding only. The window stays exactly where it was verified, so
        /// the desktop Z order never changes and pause/resume cannot flap the layer.
        /// </summary>
        public void Pause()
        {
            if (_disposed)
            {
                return;
            }

            _isPaused = true;
            if (_playback != null)
            {
                _playback.Pause();
            }
        }

        public void Resume()
        {
            if (_disposed || !_isPaused)
            {
                return;
            }

            _isPaused = false;
            if (_playback != null)
            {
                _playback.Resume();
            }
        }

        public void SetMuted(bool muted)
        {
            // 1.0.4 renders picture only; audio output is a later version. The setting
            // is intentionally a no-op here instead of pretending to control audio.
        }

        public void CheckPlaybackPixels()
        {
            var playback = _playback;
            if (_disposed || _placement == null || playback == null)
            {
                return;
            }

            var points = _probePoints;
            if (points == null || points.Count == 0)
            {
                return;
            }

            try
            {
                var watch = Stopwatch.StartNew();
                while (!FirstFramePainted && watch.ElapsedMilliseconds < 4000)
                {
                    VisibilityProbe.Pump(100);
                    if (_disposed || !ReferenceEquals(_playback, playback)) { return; }
                }

                VisibilityProbe.Pump(350);
                if (_disposed || !ReferenceEquals(_playback, playback)) { return; }
                var first = CapturePointColors(points);
                VisibilityProbe.Pump(700);
                if (_disposed || !ReferenceEquals(_playback, playback)) { return; }
                var second = CapturePointColors(points);

                var changed = 0;
                var captured = 0;
                for (var index = 0; index < first.Count; index++)
                {
                    if (first[index] == DrawingColor.Empty ||
                        second[index] == DrawingColor.Empty)
                    {
                        continue;
                    }

                    captured++;
                    if (VisibilityProbe.Distance(first[index], second[index]) >= 12)
                    {
                        changed++;
                    }
                }

                AppLog.Info("PlaybackCheck changed=" + changed + "/" + captured +
                            " firstFrame=" + (FirstFramePainted ? "1" : "0") +
                            " layer=" + AttachStrategyId +
                            " at=" + VisibilityProbe.DescribePoints(points));
                AppLog.Info("PLAYBACK-CONCLUSION: " +
                            (changed > 0 ? "MOVING" : "NO-CHANGE") +
                            " changed=" + changed + "/" + captured +
                            " layer=" + AttachStrategyId);
            }
            catch (Exception ex)
            {
                AppLog.Warn("PlaybackCheck failed: " + ex.Message);
            }
        }

        private static List<DrawingColor> CapturePointColors(IList<DrawingPoint> points)
        {
            var colors = new List<DrawingColor>(points.Count);
            foreach (var point in points)
            {
                System.Drawing.Color color;
                colors.Add(VisibilityProbe.TryCaptureColor(point, out color)
                    ? color
                    : System.Drawing.Color.Empty);
            }
            return colors;
        }

        private bool HostIsUnderCursor(IList<DrawingPoint> points)
        {
            foreach (var point in points)
            {
                var hit = NativeMethods.WindowFromPoint(
                    new NativeMethods.POINT { X = point.X, Y = point.Y });
                if (hit == _hwnd)
                {
                    return true;
                }
            }
            return false;
        }

        private void Repaint()
        {
            if (_hwnd == IntPtr.Zero)
            {
                return;
            }

            var hdc = NativeMethods.GetDC(_hwnd);
            if (hdc == IntPtr.Zero)
            {
                return;
            }

            try
            {
                lock (_paintLock)
                {
                    if (_poster != null)
                    {
                        PaintFrameLocked(hdc, _poster.View);
                    }
                    else
                    {
                        FillBlack(hdc);
                    }
                }
                NativeMethods.GdiFlush();
            }
            finally
            {
                NativeMethods.ReleaseDC(_hwnd, hdc);
            }
        }

        private bool PaintFrameLocked(IntPtr hdc, FrameView view)
        {
            if (view.Width <= 0 || view.Height <= 0 || view.Bits == IntPtr.Zero)
            {
                return false;
            }

            NativeMethods.RECT client;
            if (!NativeMethods.GetClientRect(_hwnd, out client))
            {
                return false;
            }

            var clientWidth = client.Width;
            var clientHeight = client.Height;
            if (clientWidth <= 0 || clientHeight <= 0)
            {
                return false;
            }

            NativeMethods.RECT source;
            NativeMethods.RECT destination;
            ComputeRects(
                view.Width, view.Height, clientWidth, clientHeight, _fit,
                out source, out destination);

            if (_fit == FitMode.Fit || _fit == FitMode.Center)
            {
                FillBlack(hdc);
            }

            var bytesPerPixel = Math.Max(1, view.BitCount / 8);
            var packedStride = (view.Width * bytesPerPixel + 3) & ~3;
            var bits = view.Bits;
            if (view.Stride != packedStride)
            {
                bits = PackRows(view);
            }

            var bmi = new NativeMethods.BITMAPINFO();
            bmi.bmiHeader.biSize = Marshal.SizeOf(typeof(NativeMethods.BITMAPINFOHEADER));
            bmi.bmiHeader.biWidth = view.Width;
            bmi.bmiHeader.biHeight = view.BottomUp ? view.Height : -view.Height;
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = (short)view.BitCount;
            bmi.bmiHeader.biCompression = NativeMethods.BI_RGB;
            bmi.bmiHeader.biSizeImage = packedStride * view.Height;

            NativeMethods.SetStretchBltMode(hdc, NativeMethods.COLORONCOLOR);
            var result = NativeMethods.StretchDIBits(
                hdc,
                destination.Left,
                destination.Top,
                destination.Width,
                destination.Height,
                source.Left,
                source.Top,
                source.Width,
                source.Height,
                bits,
                ref bmi,
                NativeMethods.DIB_RGB_COLORS,
                NativeMethods.SRCCOPY);

            if (result == 0 || result == -1)
            {
                if (!_stretchErrorLogged)
                {
                    _stretchErrorLogged = true;
                    AppLog.Warn("StretchDIBits failed: result=" + result +
                                " err=" + Marshal.GetLastWin32Error() +
                                " frame=" + view.Width + "x" + view.Height +
                                " stride=" + view.Stride +
                                " client=" + clientWidth + "x" + clientHeight);
                }
                return false;
            }
            return true;
        }

        /// <summary>
        /// Converts a frame with row padding into a packed top-down/bottom-up buffer
        /// (same orientation). RGB32 from the Source Reader is normally already packed,
        /// so this path is a safety net.
        /// </summary>
        private IntPtr PackRows(FrameView view)
        {
            var bytesPerPixel = Math.Max(1, view.BitCount / 8);
            var rowBytes = view.Width * bytesPerPixel;
            var packedStride = (rowBytes + 3) & ~3;
            var size = packedStride * view.Height;
            if (_packed == null || _packed.Length < size)
            {
                if (_packedPin.IsAllocated)
                {
                    _packedPin.Free();
                }
                _packed = new byte[size];
                _packedPin = GCHandle.Alloc(_packed, GCHandleType.Pinned);
            }

            for (var row = 0; row < view.Height; row++)
            {
                // Surface padding is not necessarily readable (IMF2DBuffer contract).
                Marshal.Copy(new IntPtr(view.Bits.ToInt64() + (long)row * view.Stride),
                    _packed, row * packedStride, rowBytes);
            }

            return _packedPin.AddrOfPinnedObject();
        }

        private static void ComputeRects(
            int sourceWidth,
            int sourceHeight,
            int targetWidth,
            int targetHeight,
            FitMode fit,
            out NativeMethods.RECT source,
            out NativeMethods.RECT destination)
        {
            source = new NativeMethods.RECT
            {
                Left = 0,
                Top = 0,
                Right = sourceWidth,
                Bottom = sourceHeight
            };
            destination = new NativeMethods.RECT
            {
                Left = 0,
                Top = 0,
                Right = targetWidth,
                Bottom = targetHeight
            };

            if (sourceWidth <= 0 || sourceHeight <= 0 ||
                targetWidth <= 0 || targetHeight <= 0)
            {
                return;
            }

            switch (fit)
            {
                case FitMode.Fit:
                {
                    var scale = Math.Min(
                        targetWidth / (double)sourceWidth,
                        targetHeight / (double)sourceHeight);
                    var width = Math.Max(1, (int)Math.Round(sourceWidth * scale));
                    var height = Math.Max(1, (int)Math.Round(sourceHeight * scale));
                    var left = (targetWidth - width) / 2;
                    var top = (targetHeight - height) / 2;
                    destination = new NativeMethods.RECT
                    {
                        Left = left,
                        Top = top,
                        Right = left + width,
                        Bottom = top + height
                    };
                    return;
                }

                case FitMode.Center:
                {
                    var left = (targetWidth - sourceWidth) / 2;
                    var top = (targetHeight - sourceHeight) / 2;
                    destination = new NativeMethods.RECT
                    {
                        Left = left,
                        Top = top,
                        Right = left + sourceWidth,
                        Bottom = top + sourceHeight
                    };
                    return;
                }

                case FitMode.Stretch:
                    return;

                default:
                {
                    // Fill (default): cover the whole layer, crop the source evenly.
                    var scale = Math.Max(
                        targetWidth / (double)sourceWidth,
                        targetHeight / (double)sourceHeight);
                    var cropWidth = Math.Min(sourceWidth, (int)Math.Round(targetWidth / scale));
                    var cropHeight = Math.Min(sourceHeight, (int)Math.Round(targetHeight / scale));
                    var left = (sourceWidth - cropWidth) / 2;
                    var top = (sourceHeight - cropHeight) / 2;
                    source = new NativeMethods.RECT
                    {
                        Left = left,
                        Top = top,
                        Right = left + cropWidth,
                        Bottom = top + cropHeight
                    };
                    return;
                }
            }
        }

        private void FillBlack(IntPtr hdc)
        {
            NativeMethods.RECT client;
            if (!NativeMethods.GetClientRect(_hwnd, out client))
            {
                return;
            }

            NativeMethods.FillRect(hdc, ref client, _blackBrush);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            StopPlayback();
            DestroyHostWindow();

            if (_poster != null)
            {
                _poster.Dispose();
                _poster = null;
            }

            if (_packedPin.IsAllocated)
            {
                _packedPin.Free();
            }
            _packed = null;

            if (_blackBrush != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(_blackBrush);
                _blackBrush = IntPtr.Zero;
            }
        }

        private void DestroyHostWindow()
        {
            var hwnd = _hwnd;
            _hwnd = IntPtr.Zero;
            _placement = null;

            if (ReferenceEquals(_currentHost, this))
            {
                _currentHost = null;
            }

            if (hwnd != IntPtr.Zero && NativeMethods.IsWindow(hwnd))
            {
                try
                {
                    NativeMethods.DestroyWindow(hwnd);
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Destroy wallpaper host failed: " + ex.Message);
                }
            }
        }

        private static void ApplyPreference(
            IList<DesktopLayerCandidate> candidates,
            string preferredStrategyId)
        {
            if (string.IsNullOrEmpty(preferredStrategyId) || candidates.Count < 2)
            {
                return;
            }

            for (var index = 0; index < candidates.Count; index++)
            {
                if (!string.Equals(candidates[index].Id, preferredStrategyId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (index == 0)
                {
                    return;
                }

                var preferred = candidates[index];
                candidates.RemoveAt(index);
                candidates.Insert(0, preferred);
                return;
            }
        }
    }
}
