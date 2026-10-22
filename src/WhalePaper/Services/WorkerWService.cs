using System;
using System.Text;
using WhalePaper.Interop;

namespace WhalePaper.Services
{
    /// <summary>
    /// WorkerW discovery. Explorer creates a fresh WorkerW every time Progman receives
    /// 0x052C on recent Windows 11 builds, so this class never sends that message unless
    /// a caller explicitly asks for the legacy layer, and it caches the layer it found.
    /// </summary>
    internal static class WorkerWService
    {
        private static IntPtr _cached;
        private static bool _spawnAttempted;

        /// <summary>
        /// On a raised desktop (Progman has WS_EX_NOREDIRECTIONBITMAP), DefView must
        /// be made transparent before a sibling wallpaper can show through. D/1 is
        /// the Explorer request for that state; D/0 is not a substitute for it.
        /// </summary>
        public static bool PrepareRaisedDesktop(IntPtr progman)
        {
            if (progman == IntPtr.Zero) { return false; }
            SendWorkerMessage(progman, new IntPtr(0xD), new IntPtr(1));
            var worker = NativeMethods.FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
            AppLog.Info("Raised desktop prepared: progman=0x" + progman.ToInt64().ToString("X") +
                        " wallpaperWorker=0x" + worker.ToInt64().ToString("X"));
            return worker != IntPtr.Zero && NativeMethods.IsWindow(worker);
        }

        public static IntPtr FindExistingWorkerW()
        {
            if (IsUsable(_cached))
            {
                return _cached;
            }
            _cached = IntPtr.Zero;

            var progman = DesktopLayer.FindProgman();
            if (progman != IntPtr.Zero)
            {
                var best = IntPtr.Zero;
                long bestArea = 0;
                var child = NativeMethods.GetWindow(progman, NativeMethods.GW_CHILD);
                var guard = 0;
                while (child != IntPtr.Zero && guard < 50)
                {
                    if (IsWorkerWClass(child) && IsUsable(child))
                    {
                        var area = Area(child);
                        if (area > bestArea)
                        {
                            bestArea = area;
                            best = child;
                        }
                    }

                    child = NativeMethods.GetWindow(child, NativeMethods.GW_HWNDNEXT);
                    guard++;
                }

                if (best != IntPtr.Zero)
                {
                    _cached = best;
                    return best;
                }
            }

            var topBest = IntPtr.Zero;
            long topArea = 0;
            NativeMethods.EnumWindows((top, lParam) =>
            {
                if (IsWorkerWClass(top) && IsUsable(top))
                {
                    var area = Area(top);
                    if (area > topArea)
                    {
                        topArea = area;
                        topBest = top;
                    }
                }
                return true;
            }, IntPtr.Zero);

            _cached = topBest;
            return topBest;
        }

        /// <summary>
        /// Legacy layer creation. Called at most once per process and only after every
        /// existing layer has failed pixel verification.
        /// </summary>
        public static IntPtr TrySpawnWorkerW()
        {
            if (_spawnAttempted)
            {
                return IsUsable(_cached) ? _cached : IntPtr.Zero;
            }
            _spawnAttempted = true;

            var progman = DesktopLayer.FindProgman();
            if (progman == IntPtr.Zero)
            {
                AppLog.Warn("WorkerW spawn skipped: Progman not found");
                return IntPtr.Zero;
            }

            AppLog.Info("WorkerW spawn: sending 0x052C once (legacy layer)");
            SendWorkerMessage(progman, new IntPtr(0xD), IntPtr.Zero);
            _cached = IntPtr.Zero;
            var worker = FindExistingWorkerW();
            if (worker == IntPtr.Zero)
            {
                SendWorkerMessage(progman, new IntPtr(0xD), new IntPtr(1));
                _cached = IntPtr.Zero;
                worker = FindExistingWorkerW();
            }

            AppLog.Info(worker == IntPtr.Zero
                ? "WorkerW spawn: no layer produced"
                : "WorkerW spawn: using 0x" + worker.ToInt64().ToString("X"));
            return worker;
        }

        public static bool IsUsable(IntPtr hwnd)
        {
            return hwnd != IntPtr.Zero
                && NativeMethods.IsWindow(hwnd)
                && NativeMethods.IsWindowVisible(hwnd)
                && DesktopLayer.CoversPrimary(hwnd);
        }

        public static string DescribeCached()
        {
            return _cached == IntPtr.Zero
                ? "none"
                : DesktopLayer.DescribeWindow(_cached);
        }

        private static bool IsWorkerWClass(IntPtr hwnd)
        {
            var text = new StringBuilder(64);
            NativeMethods.GetClassName(hwnd, text, text.Capacity);
            return string.Equals(text.ToString(), "WorkerW", StringComparison.Ordinal);
        }

        private static long Area(IntPtr hwnd)
        {
            NativeMethods.RECT rect;
            if (!NativeMethods.GetWindowRect(hwnd, out rect))
            {
                return 0;
            }
            return Math.Max(0, rect.Width) * (long)Math.Max(0, rect.Height);
        }

        private static void SendWorkerMessage(IntPtr progman, IntPtr wParam, IntPtr lParam)
        {
            IntPtr result;
            NativeMethods.SendMessageTimeout(
                progman,
                NativeMethods.WM_SPAWN_WORKER,
                wParam,
                lParam,
                NativeMethods.SMTO_ABORTIFHUNG,
                1200,
                out result);
        }
    }
}
