using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using WhalePaper.Interop;

namespace WhalePaper.Services
{
    internal static class FullscreenDetector
    {
        private static readonly string[] ShellClasses =
        {
            "Progman",
            "WorkerW",
            "Shell_TrayWnd",
            "Shell_SecondaryTrayWnd",
            "Windows.UI.Core.CoreWindow",
            "ApplicationFrameWindow"
        };

        public static bool IsFullscreenActive()
        {
            try
            {
                var foreground = NativeMethods.GetForegroundWindow();
                if (foreground == IntPtr.Zero)
                {
                    return false;
                }

                // Our own main window or wallpaper host must never count as a fullscreen app.
                // This check must happen before QUNS_BUSY, because the app's own fullscreen-sized
                // host can make SHQueryUserNotificationState report QUNS_BUSY.
                uint processId;
                NativeMethods.GetWindowThreadProcessId(foreground, out processId);
                if (processId == (uint)Process.GetCurrentProcess().Id)
                {
                    return false;
                }

                if (!NativeMethods.IsWindowVisible(foreground) || NativeMethods.IsIconic(foreground))
                {
                    return false;
                }

                int cloaked;
                if (NativeMethods.DwmGetWindowAttribute(
                        foreground,
                        NativeMethods.DWMWA_CLOAKED,
                        out cloaked,
                        sizeof(int)) == 0 && cloaked != 0)
                {
                    return false;
                }

                var className = new StringBuilder(128);
                NativeMethods.GetClassName(foreground, className, className.Capacity);
                var cls = className.ToString();
                foreach (var shellClass in ShellClasses)
                {
                    if (string.Equals(cls, shellClass, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }

                var exStyle = NativeMethods.GetWindowLongPtr(foreground, NativeMethods.GWL_EXSTYLE).ToInt64();
                if ((exStyle & (NativeMethods.WS_EX_TOOLWINDOW |
                                NativeMethods.WS_EX_NOACTIVATE |
                                NativeMethods.WS_EX_TRANSPARENT)) != 0)
                {
                    return false;
                }

                var monitor = NativeMethods.MonitorFromWindow(
                    foreground,
                    NativeMethods.MONITOR_DEFAULTTONEAREST);
                if (monitor == IntPtr.Zero)
                {
                    return false;
                }

                var monitorInfo = new NativeMethods.MONITORINFO
                {
                    cbSize = Marshal.SizeOf(typeof(NativeMethods.MONITORINFO))
                };
                if (!NativeMethods.GetMonitorInfo(monitor, ref monitorInfo))
                {
                    return false;
                }

                var monitorRect = monitorInfo.rcMonitor;
                var workRect = monitorInfo.rcWork;

                // A maximized window only covers the work area when the taskbar is visible.
                // Do not treat it as a fullscreen app.
                if (NativeMethods.IsZoomed(foreground) &&
                    (workRect.Left != monitorRect.Left ||
                     workRect.Top != monitorRect.Top ||
                     workRect.Right != monitorRect.Right ||
                     workRect.Bottom != monitorRect.Bottom))
                {
                    return false;
                }

                NativeMethods.RECT frame;
                var haveDwmFrame = NativeMethods.DwmGetWindowAttributeRect(
                    foreground,
                    NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
                    out frame,
                    Marshal.SizeOf(typeof(NativeMethods.RECT))) == 0;
                if (!haveDwmFrame && !NativeMethods.GetWindowRect(foreground, out frame))
                {
                    return false;
                }

                const int tolerance = 3;
                var coversMonitor = frame.Left <= monitorRect.Left + tolerance
                    && frame.Top <= monitorRect.Top + tolerance
                    && frame.Right >= monitorRect.Right - tolerance
                    && frame.Bottom >= monitorRect.Bottom - tolerance;
                if (!coversMonitor)
                {
                    return false;
                }

                // QUNS is only a corroborating signal. A true fullscreen foreground window
                // already passed the monitor check above; QUNS_BUSY alone is not enough.
                NativeMethods.QUERY_USER_NOTIFICATION_STATE state;
                if (NativeMethods.SHQueryUserNotificationState(out state) == 0)
                {
                    if (state == NativeMethods.QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN ||
                        state == NativeMethods.QUERY_USER_NOTIFICATION_STATE.QUNS_PRESENTATION_MODE)
                    {
                        return true;
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                AppLog.Warn("Fullscreen detection failed: " + ex.Message);
                return false;
            }
        }
    }
}
