using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using WhalePaper.Interop;

namespace WhalePaper.Services
{
    /// <summary>
    /// Diagnostics for the desktop layer. Everything here only observes; it must not
    /// change pause, attach, wallpaper or media behavior.
    /// </summary>
    internal static class DiagnosticsProbe
    {
        public const string BuildTag = "1.0.7";

        private static readonly object Sync = new object();
        private static string _lastFullscreenSignature;
        private static DateTime _lastFullscreenLogUtc = DateTime.MinValue;
        private static string _lastPauseSignature;
        private static bool _fullscreenErrorLogged;

        public static void LogBuild(string[] args)
        {
            AppLog.Info("==== BUILD " + BuildTag + " (native layered host + RGB32/RGB24, no-clip) ====");
            AppLog.Info("Build env: OS=" + Environment.OSVersion.VersionString +
                        " 64bitOS=" + Environment.Is64BitOperatingSystem +
                        " 64bitProc=" + Environment.Is64BitProcess +
                        " CLR=" + Environment.Version);
            AppLog.Info("Build paths: exe=" + StartupService.ExecutablePath +
                        " data=" + AppPaths.DataRoot);
            var bounds = Screen.PrimaryScreen.Bounds;
            var work = Screen.PrimaryScreen.WorkingArea;
            AppLog.Info("Build screen: bounds=" + RectText(bounds) +
                        " work=" + RectText(work) +
                        " device=" + Screen.PrimaryScreen.DeviceName);
            AppLog.Info("Build args: " + string.Join(" ", args ?? new string[0]));
        }

        public static void Bootstrap(string message)
        {
            try
            {
                var mirror = Environment.GetEnvironmentVariable("WHALEPAPER_DIAG_LOG");
                if (string.IsNullOrWhiteSpace(mirror))
                {
                    return;
                }

                var dir = Path.GetDirectoryName(mirror);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var line = string.Format(
                    "{0:yyyy-MM-dd HH:mm:ss.fff} [BOOT] [T{1}] {2}{3}",
                    DateTime.Now,
                    Environment.CurrentManagedThreadId,
                    message,
                    Environment.NewLine);
                File.AppendAllText(mirror, line, Encoding.UTF8);
            }
            catch
            {
                // Bootstrap logging must never take down the application.
            }
        }

        public static void LogFullscreen(bool result, string trigger)
        {
            try
            {
                var text = BuildFullscreenText(result, trigger, true);
                var signature = BuildFullscreenSignature(result);
                var now = DateTime.UtcNow;
                lock (Sync)
                {
                    if (signature == _lastFullscreenSignature &&
                        (now - _lastFullscreenLogUtc).TotalSeconds < 30)
                    {
                        return;
                    }

                    _lastFullscreenSignature = signature;
                    _lastFullscreenLogUtc = now;
                }

                AppLog.Info(text);
            }
            catch (Exception ex)
            {
                if (!_fullscreenErrorLogged)
                {
                    _fullscreenErrorLogged = true;
                    AppLog.Warn("FullscreenProbe failed: " + ex.Message);
                }
            }
        }

        public static string DescribeFullscreen(bool result)
        {
            try
            {
                return BuildFullscreenText(result, "self-test", false);
            }
            catch (Exception ex)
            {
                return "FullscreenProbe failed: " + ex.Message;
            }
        }

        public static void LogPauseState(
            string trigger,
            bool shouldPause,
            bool oldPaused,
            bool userPaused,
            bool fullscreenPaused,
            bool sessionPaused,
            bool powerPaused,
            bool batteryPaused,
            bool remotePaused,
            string reason)
        {
            try
            {
                var signature = string.Join("|", new object[]
                {
                    shouldPause, oldPaused, userPaused, fullscreenPaused,
                    sessionPaused, powerPaused, batteryPaused, remotePaused, reason
                });
                lock (Sync)
                {
                    if (signature == _lastPauseSignature)
                    {
                        return;
                    }

                    _lastPauseSignature = signature;
                }

                AppLog.Info("PauseState trigger=" + trigger +
                            " shouldPause=" + Bool(shouldPause) +
                            " oldPaused=" + Bool(oldPaused) +
                            " user=" + Bool(userPaused) +
                            " fullscreen=" + Bool(fullscreenPaused) +
                            " session=" + Bool(sessionPaused) +
                            " power=" + Bool(powerPaused) +
                            " battery=" + Bool(batteryPaused) +
                            " remote=" + Bool(remotePaused) +
                            " reason=" + reason);
            }
            catch (Exception ex)
            {
                AppLog.Warn("PauseProbe failed: " + ex.Message);
            }
        }

        public static void LogDesktopTree(string stage)
        {
            try
            {
                AppLog.Info("DesktopTree stage=" + stage + Environment.NewLine +
                            DesktopLayer.DescribeDesktopTree());
            }
            catch (Exception ex)
            {
                AppLog.Warn("DesktopTree probe failed: " + ex.Message);
            }
        }

        public static string DescribeDesktopZOrder()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine(BuildWindowTreeText("self-test"));
                sb.AppendLine(BuildHitTestText("self-test", IntPtr.Zero));
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return "Desktop Z-order probe failed: " + ex.Message;
            }
        }

        public static void LogHitTest(string stage, IntPtr hostHwnd)
        {
            try
            {
                AppLog.Info(BuildHitTestText(stage, hostHwnd));
            }
            catch (Exception ex)
            {
                AppLog.Warn("HitTest probe failed: " + ex.Message);
            }
        }

        private static string BuildFullscreenText(bool result, string trigger, bool includeTrigger)
        {
            var sb = new StringBuilder(640);
            sb.Append("FullscreenProbe");
            if (includeTrigger)
            {
                sb.Append(" trigger=").Append(trigger);
            }

            sb.Append(" result=").Append(Bool(result));

            NativeMethods.QUERY_USER_NOTIFICATION_STATE quns;
            var qhr = NativeMethods.SHQueryUserNotificationState(out quns);
            sb.Append(" qunsHr=0x").Append(qhr.ToString("X8"));
            sb.Append(" quns=").Append(quns);

            var foreground = NativeMethods.GetForegroundWindow();
            sb.Append(" fg=").Append(Hex(foreground));
            if (foreground != IntPtr.Zero)
            {
                uint pid;
                NativeMethods.GetWindowThreadProcessId(foreground, out pid);
                var selfPid = Process.GetCurrentProcess().Id;
                sb.Append(" pid=").Append(pid);
                sb.Append(" self=").Append(Bool(pid == (uint)selfPid));
                sb.Append(" cls=").Append(ClassName(foreground));
                sb.Append(" vis=").Append(Bool(NativeMethods.IsWindowVisible(foreground)));
                sb.Append(" icon=").Append(Bool(NativeMethods.IsIconic(foreground)));
                sb.Append(" zoom=").Append(Bool(NativeMethods.IsZoomed(foreground)));

                int cloaked;
                var chr = NativeMethods.DwmGetWindowAttribute(
                    foreground,
                    NativeMethods.DWMWA_CLOAKED,
                    out cloaked,
                    sizeof(int));
                sb.Append(" cloakHr=0x").Append(chr.ToString("X8"));
                sb.Append(" cloak=").Append(cloaked);

                var style = NativeMethods.GetWindowLongPtr(foreground, NativeMethods.GWL_STYLE).ToInt64();
                var exStyle = NativeMethods.GetWindowLongPtr(foreground, NativeMethods.GWL_EXSTYLE).ToInt64();
                sb.Append(" style=0x").Append(style.ToString("X8"));
                sb.Append(" ex=0x").Append(exStyle.ToString("X8"));
                sb.Append(" wsMax=").Append(Bool((style & NativeMethods.WS_MAXIMIZE) != 0));
                sb.Append(" topmost=").Append(Bool((exStyle & NativeMethods.WS_EX_TOPMOST) != 0));

                NativeMethods.RECT rect;
                if (NativeMethods.GetWindowRect(foreground, out rect))
                {
                    sb.Append(" rect=").Append(RectText(rect));
                }

                NativeMethods.RECT dwmRect;
                var dhr = NativeMethods.DwmGetWindowAttributeRect(
                    foreground,
                    NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
                    out dwmRect,
                    Marshal.SizeOf(typeof(NativeMethods.RECT)));
                sb.Append(" dwmHr=0x").Append(dhr.ToString("X8"));
                if (dhr == 0)
                {
                    sb.Append(" dwmRect=").Append(RectText(dwmRect));
                }

                var monitor = NativeMethods.MonitorFromWindow(
                    foreground,
                    NativeMethods.MONITOR_DEFAULTTONEAREST);
                var monitorInfo = new NativeMethods.MONITORINFO
                {
                    cbSize = Marshal.SizeOf(typeof(NativeMethods.MONITORINFO))
                };
                if (monitor != IntPtr.Zero && NativeMethods.GetMonitorInfo(monitor, ref monitorInfo))
                {
                    sb.Append(" monitor=").Append(RectText(monitorInfo.rcMonitor));
                    sb.Append(" work=").Append(RectText(monitorInfo.rcWork));
                }

                sb.Append(" dpi=").Append(NativeMethods.GetDpiForWindow(foreground));
            }

            sb.Append(" screen=").Append(RectText(Screen.PrimaryScreen.Bounds));
            sb.Append(" screenWork=").Append(RectText(Screen.PrimaryScreen.WorkingArea));
            return sb.ToString();
        }

        private static string BuildFullscreenSignature(bool result)
        {
            var sb = new StringBuilder(256);
            NativeMethods.QUERY_USER_NOTIFICATION_STATE quns;
            NativeMethods.SHQueryUserNotificationState(out quns);
            sb.Append(Bool(result)).Append('|').Append((int)quns);

            var foreground = NativeMethods.GetForegroundWindow();
            sb.Append('|').Append(foreground.ToInt64());
            if (foreground != IntPtr.Zero)
            {
                sb.Append('|').Append(ClassName(foreground));
                NativeMethods.RECT rect;
                if (NativeMethods.GetWindowRect(foreground, out rect))
                {
                    sb.Append('|').Append(rect.Left).Append(',')
                      .Append(rect.Top).Append(',')
                      .Append(rect.Right).Append(',')
                      .Append(rect.Bottom);
                }

                sb.Append('|').Append(Bool(NativeMethods.IsZoomed(foreground)));
                var exStyle = NativeMethods.GetWindowLongPtr(foreground, NativeMethods.GWL_EXSTYLE).ToInt64();
                sb.Append('|').Append(Bool((exStyle & NativeMethods.WS_EX_TOPMOST) != 0));
            }

            return sb.ToString();
        }

        private static string BuildWindowTreeText(string stage)
        {
            var sb = new StringBuilder(768);
            sb.Append("WindowTree stage=").Append(stage);

            var progman = NativeMethods.FindWindow("Progman", null);
            sb.Append(" progman=").Append(WindowBrief(progman));
            if (progman != IntPtr.Zero)
            {
                sb.Append(" defView=").Append(
                    WindowBrief(NativeMethods.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null)));
                sb.Append(" progmanWorkerChild=").Append(
                    WindowBrief(NativeMethods.FindWindowEx(progman, IntPtr.Zero, "WorkerW", null)));
            }

            IntPtr shellViewParent = IntPtr.Zero;
            NativeMethods.EnumWindows((top, lParam) =>
            {
                if (NativeMethods.FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                {
                    shellViewParent = top;
                    return false;
                }

                return true;
            }, IntPtr.Zero);
            sb.Append(" shellViewParent=").Append(WindowBrief(shellViewParent));

            var after = IntPtr.Zero;
            for (var i = 0; i < 12; i++)
            {
                var worker = NativeMethods.FindWindowEx(IntPtr.Zero, after, "WorkerW", null);
                if (worker == IntPtr.Zero)
                {
                    break;
                }

                sb.Append(" worker[").Append(i).Append("]=").Append(WindowSummary(worker));
                after = worker;
            }

            return sb.ToString();
        }

        private static string BuildHitTestText(string stage, IntPtr hostHwnd)
        {
            var screen = Screen.PrimaryScreen.Bounds;
            var points = new[]
            {
                new NativeMethods.POINT { X = 100, Y = 100 },
                new NativeMethods.POINT { X = screen.Width / 2, Y = screen.Height / 2 },
                new NativeMethods.POINT { X = screen.Width / 2, Y = screen.Height - 20 },
                new NativeMethods.POINT { X = screen.Width - 100, Y = 100 }
            };

            var sb = new StringBuilder(512);
            sb.Append("HitTest stage=").Append(stage);
            foreach (var point in points)
            {
                var hit = NativeMethods.WindowFromPoint(point);
                var root = hit == IntPtr.Zero
                    ? IntPtr.Zero
                    : NativeMethods.GetAncestor(hit, NativeMethods.GA_ROOT);
                if (root == IntPtr.Zero)
                {
                    root = hit;
                }

                uint pid;
                NativeMethods.GetWindowThreadProcessId(hit, out pid);
                sb.Append(" (").Append(point.X).Append(',').Append(point.Y).Append(')')
                  .Append(" hit=").Append(Hex(hit))
                  .Append(" cls=").Append(ClassName(hit))
                  .Append(" root=").Append(Hex(root))
                  .Append(" rootCls=").Append(ClassName(root))
                  .Append(" pid=").Append(pid)
                  .Append(" host=").Append(Bool(hostHwnd != IntPtr.Zero &&
                                                  (hit == hostHwnd || root == hostHwnd)));
            }

            return sb.ToString();
        }

        private static string WindowBrief(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
            {
                return "0x0";
            }

            return Hex(hwnd) + "(" + ClassName(hwnd) + ")";
        }

        private static string WindowSummary(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
            {
                return "0x0";
            }

            var parent = NativeMethods.GetParent(hwnd);
            NativeMethods.RECT rect;
            var hasRect = NativeMethods.GetWindowRect(hwnd, out rect);
            uint pid;
            NativeMethods.GetWindowThreadProcessId(hwnd, out pid);
            var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE).ToInt64();
            var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            return Hex(hwnd) +
                   " cls=" + ClassName(hwnd) +
                   " pid=" + pid +
                   " parent=" + Hex(parent) +
                   " vis=" + Bool(NativeMethods.IsWindowVisible(hwnd)) +
                   " rect=" + (hasRect ? RectText(rect) : "?") +
                   " style=0x" + style.ToString("X8") +
                   " ex=0x" + exStyle.ToString("X8") +
                   " zPrev=" + WindowBrief(NativeMethods.GetWindow(hwnd, NativeMethods.GW_HWNDPREV)) +
                   " zNext=" + WindowBrief(NativeMethods.GetWindow(hwnd, NativeMethods.GW_HWNDNEXT));
        }

        private static string ClassName(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
            {
                return "?";
            }

            var text = new StringBuilder(128);
            NativeMethods.GetClassName(hwnd, text, text.Capacity);
            return text.Length == 0 ? "?" : text.ToString();
        }

        private static string RectText(Rectangle rect)
        {
            return rect.Left + "," + rect.Top + "," + rect.Right + "," + rect.Bottom;
        }

        private static string RectText(NativeMethods.RECT rect)
        {
            return rect.Left + "," + rect.Top + "," + rect.Right + "," + rect.Bottom;
        }

        private static string Hex(IntPtr value)
        {
            return "0x" + value.ToInt64().ToString("X");
        }

        private static string Bool(bool value)
        {
            return value ? "1" : "0";
        }
    }
}
