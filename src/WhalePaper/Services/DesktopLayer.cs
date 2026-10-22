using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Forms;
using WhalePaper.Interop;

namespace WhalePaper.Services
{
    internal enum DesktopLayerStrategy
    {
        None = 0,
        ProgmanBelowDefView = 1,
        DefViewBelowIcons = 2,
        ExistingWorkerW = 3,
        ProgmanBottom = 4,
        SpawnedWorkerW = 5
    }

    /// <summary>
    /// One candidate place to hang the wallpaper host window. The host is always a
    /// WS_CHILD window; <see cref="AboveWindow"/> is the sibling that must end up
    /// directly above it (Zero means "bottom-most child of Parent").
    /// </summary>
    internal sealed class DesktopLayerCandidate
    {
        public DesktopLayerStrategy Strategy;
        public string Id;
        public string Title;
        public IntPtr Parent;
        public IntPtr AboveWindow;
        public bool RequiresWorkerSpawn;

        /// <summary>
        /// Preferred when no desktop pixel can be sampled (pixel verification is
        /// impossible). Points at the layer other C# wallpaper tools use on 26002+.
        /// </summary>
        public bool UnverifiedDefault;

        public string Describe()
        {
            return Id + " parent=0x" + Parent.ToInt64().ToString("X") +
                   " above=" + (AboveWindow == IntPtr.Zero
                       ? "bottom"
                       : "0x" + AboveWindow.ToInt64().ToString("X"));
        }
    }

    /// <summary>
    /// A candidate that was actually used. Used later to verify that Explorer did not
    /// silently move the wallpaper layer.
    /// </summary>
    internal sealed class DesktopLayerPlacement
    {
        public DesktopLayerStrategy Strategy;
        public string Id;
        public IntPtr Parent;
        public IntPtr AboveWindow;
        public bool Layered;
        public string Title;
    }

    /// <summary>
    /// Resolves the desktop icon layer and places the wallpaper host right below it.
    /// Explorer's window tree differs between Windows builds, so nothing here trusts a
    /// fixed class hierarchy: candidates are generated from what actually exists and the
    /// caller verifies the result by pixels.
    /// </summary>
    internal static class DesktopLayer
    {
        private const string DefViewClass = "SHELLDLL_DefView";
        private const string IconViewClass = "SysListView32";
        private const string ProgmanClass = "Progman";
        private const string WorkerWClass = "WorkerW";

        public static IntPtr FindProgman()
        {
            return NativeMethods.FindWindow(ProgmanClass, null);
        }

        public static IntPtr FindDefView(out IntPtr parent)
        {
            parent = IntPtr.Zero;
            var progman = FindProgman();
            if (progman != IntPtr.Zero)
            {
                var direct = NativeMethods.FindWindowEx(progman, IntPtr.Zero, DefViewClass, null);
                if (direct != IntPtr.Zero)
                {
                    parent = progman;
                    return direct;
                }

                var nested = FindDescendant(progman, DefViewClass);
                if (nested != IntPtr.Zero)
                {
                    parent = NativeMethods.GetParent(nested);
                    return nested;
                }
            }

            // Older Explorer builds keep SHELLDLL_DefView under a top-level WorkerW.
            var topFound = IntPtr.Zero;
            NativeMethods.EnumWindows((top, lParam) =>
            {
                var inner = FindDescendant(top, DefViewClass);
                if (inner != IntPtr.Zero)
                {
                    topFound = inner;
                    return false;
                }
                return true;
            }, IntPtr.Zero);

            if (topFound != IntPtr.Zero)
            {
                parent = NativeMethods.GetParent(topFound);
            }
            return topFound;
        }

        public static IntPtr FindIcons(IntPtr defView)
        {
            if (defView == IntPtr.Zero || !NativeMethods.IsWindow(defView))
            {
                return IntPtr.Zero;
            }

            var icons = NativeMethods.FindWindowEx(defView, IntPtr.Zero, IconViewClass, null);
            if (icons == IntPtr.Zero)
            {
                icons = NativeMethods.GetWindow(defView, NativeMethods.GW_CHILD);
            }
            return icons;
        }

        /// <summary>
        /// Candidate order (1.0.2, based on how Lively and other C# wallpaper tools
        /// handle Windows 11 build 26002+):
        /// 1. inside SHELLDLL_DefView, below SysListView32 - works when DefView paints
        ///    the wallpaper itself, which is what the 26002+ layout looks like;
        /// 2. the WorkerW child of Progman that Lively's #2050 fix looks for;
        /// 3. a WorkerW created by one 0x052C message;
        /// 4. a Progman child right below SHELLDLL_DefView (1.0.1 default, unproven);
        /// 5. the bottom-most Progman child.
        /// </summary>
        public static List<DesktopLayerCandidate> ResolveCandidates()
        {
            var list = new List<DesktopLayerCandidate>();
            var progman = FindProgman();
            var worker = WorkerWService.FindExistingWorkerW();

            IntPtr defViewParent;
            var defView = FindDefView(out defViewParent);
            if (defView == IntPtr.Zero)
            {
                defViewParent = progman;
            }

            var icons = FindIcons(defView);
            if (icons != IntPtr.Zero)
            {
                list.Add(new DesktopLayerCandidate
                {
                    Strategy = DesktopLayerStrategy.DefViewBelowIcons,
                    Id = "defview-below-icons",
                    Title = "SHELLDLL_DefView 子窗口，紧贴 SysListView32 下方",
                    Parent = defView,
                    AboveWindow = icons
                });
            }

            if (worker != IntPtr.Zero)
            {
                list.Add(new DesktopLayerCandidate
                {
                    Strategy = DesktopLayerStrategy.ExistingWorkerW,
                    Id = "existing-workerw",
                    Title = "已存在的全屏 WorkerW 子层（Lively 在 26002+ 使用的层）",
                    Parent = worker,
                    AboveWindow = IntPtr.Zero,
                    UnverifiedDefault = true
                });
            }

            list.Add(new DesktopLayerCandidate
            {
                Strategy = DesktopLayerStrategy.SpawnedWorkerW,
                Id = "spawned-workerw",
                Title = "0x052C 新建 WorkerW（Lively 在 26002+ 使用的层）",
                Parent = IntPtr.Zero,
                AboveWindow = IntPtr.Zero,
                RequiresWorkerSpawn = true,
                UnverifiedDefault = worker == IntPtr.Zero
            });

            if (defView != IntPtr.Zero && defViewParent != IntPtr.Zero)
            {
                list.Add(new DesktopLayerCandidate
                {
                    Strategy = DesktopLayerStrategy.ProgmanBelowDefView,
                    Id = "progman-below-defview",
                    Title = "Progman 子窗口，紧贴 SHELLDLL_DefView 下方",
                    Parent = defViewParent,
                    AboveWindow = defView
                });
            }

            if (defViewParent != IntPtr.Zero)
            {
                list.Add(new DesktopLayerCandidate
                {
                    Strategy = DesktopLayerStrategy.ProgmanBottom,
                    Id = "progman-bottom",
                    Title = "Progman 子窗口最底层",
                    Parent = defViewParent,
                    AboveWindow = IntPtr.Zero
                });
            }

            return list;
        }

        /// <summary>
        /// On a raised Windows desktop, prepare transparent DefView and place a layered
        /// sibling directly under it. The former DefView-child route passed color
        /// probes but hid icons during real video playback. Older desktops retain the
        /// legacy route until their separate compatibility matrix is validated.
        /// </summary>
        public static List<DesktopLayerCandidate> ResolveHostCandidates()
        {
            var list = new List<DesktopLayerCandidate>();
            var progman = FindProgman();
            var raised = progman != IntPtr.Zero &&
                (NativeMethods.GetWindowLongPtr(progman, NativeMethods.GWL_EXSTYLE).ToInt64() &
                 NativeMethods.WS_EX_NOREDIRECTIONBITMAP) != 0;

            if (raised)
            {
                // The old DefView-child route draws over icons even though hit testing
                // still returns the icon list. Use a sibling under transparent DefView.
                if (!WorkerWService.PrepareRaisedDesktop(progman)) { return list; }
                var shellView = NativeMethods.FindWindowEx(progman, IntPtr.Zero, DefViewClass, null);
                if (shellView != IntPtr.Zero)
                {
                    list.Add(new DesktopLayerCandidate
                    {
                        Strategy = DesktopLayerStrategy.ProgmanBelowDefView,
                        Id = "raised-desktop-below-icons",
                        Title = "透明桌面图标层下方，系统壁纸层上方",
                        Parent = progman,
                        AboveWindow = shellView
                    });
                }
                return list;
            }

            IntPtr defViewParent;
            var defView = FindDefView(out defViewParent);
            if (defView == IntPtr.Zero || !NativeMethods.IsWindow(defView))
            {
                return list;
            }

            var icons = NativeMethods.FindWindowEx(defView, IntPtr.Zero, IconViewClass, null);
            if (icons != IntPtr.Zero)
            {
                list.Add(new DesktopLayerCandidate
                {
                    Strategy = DesktopLayerStrategy.DefViewBelowIcons,
                    Id = "defview-below-icons",
                    Title = "SHELLDLL_DefView 子窗口，紧贴 SysListView32 下方",
                    Parent = defView,
                    AboveWindow = icons
                });
            }
            else
            {
                list.Add(new DesktopLayerCandidate
                {
                    Strategy = DesktopLayerStrategy.DefViewBelowIcons,
                    Id = "defview-bottom",
                    Title = "SHELLDLL_DefView 最底层（未找到 SysListView32）",
                    Parent = defView,
                    AboveWindow = IntPtr.Zero
                });
            }

            return list;
        }

        /// <summary>
        /// When no desktop pixel can be sampled, prefer the layer that a widely used
        /// C# wallpaper application (Lively) has proven on this Windows build.
        /// Kept for the 1.0.3 diagnostic probe; the 1.0.4 host uses a single
        /// structurally verified candidate instead.
        /// </summary>
        public static void EnsureUnverifiedDefaultFirst(IList<DesktopLayerCandidate> candidates)
        {
            if (candidates == null)
            {
                return;
            }

            for (var index = 0; index < candidates.Count; index++)
            {
                if (!candidates[index].UnverifiedDefault)
                {
                    continue;
                }

                if (index > 0)
                {
                    var candidate = candidates[index];
                    candidates.RemoveAt(index);
                    candidates.Insert(0, candidate);
                }
                return;
            }
        }

        public static bool IsDirectlyBelow(IntPtr hwnd, IntPtr above)
        {
            return hwnd != IntPtr.Zero
                && above != IntPtr.Zero
                && NativeMethods.GetWindow(hwnd, NativeMethods.GW_HWNDPREV) == above;
        }

        public static bool IsBottomMost(IntPtr hwnd)
        {
            return hwnd != IntPtr.Zero
                && NativeMethods.GetWindow(hwnd, NativeMethods.GW_HWNDNEXT) == IntPtr.Zero;
        }

        /// <summary>
        /// Diagnostic only: puts a child window at the very top of its parent's child
        /// Z order (covering the desktop icons). Used to test whether children of Progman
        /// render at all, independently of the wallpaper layer.
        /// </summary>
        public static bool PlaceOnTop(IntPtr hwnd, IntPtr parent)
        {
            if (hwnd == IntPtr.Zero || parent == IntPtr.Zero || !NativeMethods.IsWindow(parent))
            {
                return false;
            }

            int width;
            int height;
            GetLayerSize(parent, out width, out height);
            NativeMethods.SetWindowPos(
                hwnd, NativeMethods.HWND_TOP, 0, 0, width, height,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW |
                NativeMethods.SWP_FRAMECHANGED);

            return NativeMethods.GetParent(hwnd) == parent
                && NativeMethods.GetWindow(hwnd, NativeMethods.GW_HWNDPREV) == IntPtr.Zero;
        }

        /// <summary>
        /// Places the child window at (parent, above). The documented meaning of
        /// hWndInsertAfter has been read both ways over the years, so both orders are
        /// attempted and the result is verified against the real Z order.
        /// </summary>
        public static bool Place(
            IntPtr hwnd,
            IntPtr parent,
            IntPtr above,
            out IntPtr below,
            out string detail)
        {
            below = IntPtr.Zero;
            if (hwnd == IntPtr.Zero || parent == IntPtr.Zero || !NativeMethods.IsWindow(parent))
            {
                detail = "invalid parent";
                return false;
            }

            int width;
            int height;
            GetLayerSize(parent, out width, out height);

            const uint flags = NativeMethods.SWP_NOACTIVATE |
                               NativeMethods.SWP_SHOWWINDOW |
                               NativeMethods.SWP_FRAMECHANGED;

            if (above == IntPtr.Zero)
            {
                NativeMethods.SetWindowPos(
                    hwnd, NativeMethods.HWND_BOTTOM, 0, 0, width, height, flags);
                var bottomOk = NativeMethods.GetParent(hwnd) == parent && IsBottomMost(hwnd);
                detail = "bottom ok=" + BoolText(bottomOk);
                return bottomOk;
            }

            if (!NativeMethods.IsWindow(above) || NativeMethods.GetParent(above) != parent)
            {
                detail = "anchor is not a sibling";
                return false;
            }

            below = NativeMethods.GetWindow(above, NativeMethods.GW_HWNDNEXT);
            if (below == hwnd)
            {
                below = NativeMethods.GetWindow(hwnd, NativeMethods.GW_HWNDNEXT);
            }

            NativeMethods.SetWindowPos(hwnd, above, 0, 0, width, height, flags);
            if (NativeMethods.GetParent(hwnd) == parent && IsDirectlyBelow(hwnd, above))
            {
                detail = "anchored-below-anchor";
                return true;
            }

            if (below != IntPtr.Zero && NativeMethods.IsWindow(below))
            {
                NativeMethods.SetWindowPos(hwnd, below, 0, 0, width, height, flags);
                if (NativeMethods.GetParent(hwnd) == parent && IsDirectlyBelow(hwnd, above))
                {
                    detail = "anchored-above-below";
                    return true;
                }
            }

            // Last resort: bottom of the layer. The caller still verifies by pixels, so a
            // wrong layer can never end up covering the desktop icons.
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_BOTTOM, 0, 0, width, height, flags);
            var fallbackOk = NativeMethods.GetParent(hwnd) == parent && IsDirectlyBelow(hwnd, above);
            detail = "anchor-fallback ok=" + BoolText(fallbackOk) +
                     " prev=0x" + NativeMethods.GetWindow(hwnd, NativeMethods.GW_HWNDPREV).ToInt64().ToString("X") +
                     " expect=0x" + above.ToInt64().ToString("X");
            return fallbackOk;
        }

        public static bool CheckPlacement(IntPtr hwnd, DesktopLayerPlacement placement)
        {
            if (placement == null || hwnd == IntPtr.Zero)
            {
                return false;
            }
            if (!NativeMethods.IsWindow(hwnd) || !NativeMethods.IsWindow(placement.Parent))
            {
                return false;
            }
            if (NativeMethods.GetParent(hwnd) != placement.Parent)
            {
                return false;
            }
            if (placement.AboveWindow != IntPtr.Zero)
            {
                return NativeMethods.IsWindow(placement.AboveWindow) &&
                       IsDirectlyBelow(hwnd, placement.AboveWindow);
            }
            return IsBottomMost(hwnd);
        }

        /// <summary>
        /// Cheap periodic check. Re-asserts the Z order when Explorer reorders our child
        /// window, and reports false only when the layer itself is gone.
        /// </summary>
        public static bool EnsurePlacement(IntPtr hwnd, DesktopLayerPlacement placement)
        {
            if (placement == null || hwnd == IntPtr.Zero)
            {
                return false;
            }
            if (!NativeMethods.IsWindow(hwnd) ||
                !NativeMethods.IsWindow(placement.Parent) ||
                NativeMethods.GetParent(hwnd) != placement.Parent)
            {
                return false;
            }

            if (CheckPlacement(hwnd, placement))
            {
                return true;
            }

            AppLog.Warn("Wallpaper host Z order drifted; re-asserting " + placement.Id);

            // Reuse the full placement routine: it tries both documented readings of
            // hWndInsertAfter and verifies the resulting sibling order.
            IntPtr below;
            string detail;
            if (placement.AboveWindow != IntPtr.Zero && NativeMethods.IsWindow(placement.AboveWindow))
            {
                Place(hwnd, placement.Parent, placement.AboveWindow, out below, out detail);
            }
            else
            {
                NativeMethods.SetWindowPos(
                    hwnd,
                    NativeMethods.HWND_BOTTOM,
                    0, 0, 0, 0,
                    NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOMOVE |
                    NativeMethods.SWP_NOACTIVATE);
            }

            return CheckPlacement(hwnd, placement);
        }

        public static void GetLayerSize(IntPtr parent, out int width, out int height)
        {
            NativeMethods.RECT client;
            if (parent != IntPtr.Zero && NativeMethods.GetClientRect(parent, out client))
            {
                width = Math.Max(1, client.Width);
                height = Math.Max(1, client.Height);
                return;
            }

            var screen = Screen.PrimaryScreen.Bounds;
            width = Math.Max(1, screen.Width);
            height = Math.Max(1, screen.Height);
        }

        public static bool CoversPrimary(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
            {
                return false;
            }

            NativeMethods.RECT rect;
            if (!NativeMethods.GetWindowRect(hwnd, out rect))
            {
                return false;
            }

            var screen = Screen.PrimaryScreen.Bounds;
            return rect.Width >= screen.Width * 0.9 && rect.Height >= screen.Height * 0.9;
        }

        public static string DescribeDesktopTree()
        {
            var sb = new StringBuilder(1024);
            var progman = FindProgman();
            sb.Append("DesktopTree progman=").Append(DescribeWindow(progman));

            if (progman != IntPtr.Zero)
            {
                sb.AppendLine();
                var child = NativeMethods.GetWindow(progman, NativeMethods.GW_CHILD);
                var index = 0;
                while (child != IntPtr.Zero && index < 20)
                {
                    sb.Append("  child[").Append(index).Append("] top->bottom ")
                      .Append(DescribeWindow(child)).AppendLine();
                    child = NativeMethods.GetWindow(child, NativeMethods.GW_HWNDNEXT);
                    index++;
                }
            }

            var workerIndex = 0;
            var after = IntPtr.Zero;
            while (workerIndex < 12)
            {
                var worker = NativeMethods.FindWindowEx(IntPtr.Zero, after, WorkerWClass, null);
                if (worker == IntPtr.Zero)
                {
                    break;
                }
                sb.Append("  topWorker[").Append(workerIndex).Append("] ")
                  .Append(DescribeWindow(worker)).AppendLine();
                after = worker;
                workerIndex++;
            }

            return sb.ToString().TrimEnd();
        }

        public static string DescribeWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
            {
                return "0x0";
            }

            var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE).ToInt64();
            var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            NativeMethods.RECT rect;
            var hasRect = NativeMethods.GetWindowRect(hwnd, out rect);
            uint pid;
            NativeMethods.GetWindowThreadProcessId(hwnd, out pid);

            var sb = new StringBuilder(320);
            sb.Append("0x").Append(hwnd.ToInt64().ToString("X"))
              .Append("(").Append(ClassName(hwnd)).Append(")")
              .Append(" pid=").Append(pid)
              .Append(" parent=0x").Append(NativeMethods.GetParent(hwnd).ToInt64().ToString("X"))
              .Append(" vis=").Append(BoolText(NativeMethods.IsWindowVisible(hwnd)))
              .Append(" rect=").Append(hasRect
                  ? rect.Left + "," + rect.Top + "," + rect.Right + "," + rect.Bottom
                  : "?")
              .Append(" style=0x").Append(style.ToString("X8"))
              .Append(" ex=0x").Append(exStyle.ToString("X8"));

            var flags = new StringBuilder();
            if ((style & NativeMethods.WS_CHILD) != 0) flags.Append("child,");
            if ((style & NativeMethods.WS_VISIBLE) != 0) flags.Append("visible,");
            if ((exStyle & NativeMethods.WS_EX_NOREDIRECTIONBITMAP) != 0) flags.Append("noRedir,");
            if ((exStyle & NativeMethods.WS_EX_TRANSPARENT) != 0) flags.Append("transparent,");
            if ((exStyle & NativeMethods.WS_EX_NOACTIVATE) != 0) flags.Append("noActivate,");
            if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0) flags.Append("tool,");
            if (flags.Length > 0)
            {
                flags.Length--;
                sb.Append(" flags=").Append(flags);
            }

            return sb.ToString();
        }

        public static string ClassName(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
            {
                return "?";
            }

            var text = new StringBuilder(128);
            NativeMethods.GetClassName(hwnd, text, text.Capacity);
            return text.Length == 0 ? "?" : text.ToString();
        }

        private static IntPtr FindDescendant(IntPtr root, string className)
        {
            if (root == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            var found = IntPtr.Zero;
            NativeMethods.EnumChildWindows(root, (child, lParam) =>
            {
                if (string.Equals(ClassName(child), className, StringComparison.Ordinal))
                {
                    found = child;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        private static string BoolText(bool value)
        {
            return value ? "1" : "0";
        }
    }
}
