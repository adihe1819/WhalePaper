using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using WhalePaper.Interop;
using DrawingColor = System.Drawing.Color;
using DrawingPoint = System.Drawing.Point;

namespace WhalePaper.Services
{
    /// <summary>
    /// Plain GDI child window painted with one solid color. It is never used as a
    /// wallpaper; it only answers "can any child window be seen in this desktop layer?".
    /// If GDI shows up but WPF does not, the layer is fine and the WPF surface is the
    /// problem, which points 1.0.2 at a native video surface.
    /// </summary>
    internal static class GdiProbeWindow
    {
        private static readonly NativeMethods.WndProcDelegate WindowProc =
            new NativeMethods.WndProcDelegate(NativeMethods.DefWindowProc);

        public static ProbeVerdict Verify(
            IntPtr parent,
            IntPtr above,
            IList<DrawingPoint> points,
            bool layered,
            out string detail)
        {
            return VerifyCore(parent, above, false, layered, points, out detail);
        }

        /// <summary>
        /// Diagnostic variant: places the GDI window at the very top of the parent's
        /// child Z order. It answers "do children of Progman render at all" without
        /// depending on the wallpaper layer.
        /// </summary>
        public static ProbeVerdict VerifyTopMost(
            IntPtr parent,
            IList<DrawingPoint> points,
            bool layered,
            out string detail)
        {
            return VerifyCore(parent, IntPtr.Zero, true, layered, points, out detail);
        }

        private static ProbeVerdict VerifyCore(
            IntPtr parent,
            IntPtr above,
            bool topMost,
            bool layered,
            IList<DrawingPoint> points,
            out string detail)
        {
            detail = "no-sample-points";
            if (parent == IntPtr.Zero || !NativeMethods.IsWindow(parent) || points == null || points.Count == 0)
            {
                return ProbeVerdict.Unverifiable;
            }

            var className = "WhalePaperLayerProbe_" + Guid.NewGuid().ToString("N");
            var hInstance = NativeMethods.GetModuleHandle(null);
            var hwnd = IntPtr.Zero;
            var brush = IntPtr.Zero;

            try
            {
                brush = NativeMethods.CreateSolidBrush(ColorRef(VisibilityProbe.ColorA));
                var windowClass = new NativeMethods.WNDCLASSEX
                {
                    cbSize = (uint)Marshal.SizeOf(typeof(NativeMethods.WNDCLASSEX)),
                    lpfnWndProc = WindowProc,
                    hInstance = hInstance,
                    hbrBackground = brush,
                    lpszClassName = className
                };

                if (NativeMethods.RegisterClassEx(ref windowClass) == 0)
                {
                    detail = "RegisterClassEx failed error=" + Marshal.GetLastWin32Error();
                    return ProbeVerdict.Unverifiable;
                }

                var exStyle = NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
                if (layered)
                {
                    exStyle |= NativeMethods.WS_EX_LAYERED;
                }

                int width;
                int height;
                DesktopLayer.GetLayerSize(parent, out width, out height);
                hwnd = NativeMethods.CreateWindowEx(
                    exStyle,
                    className,
                    "WhalePaperLayerProbe",
                    NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE,
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
                    detail = "CreateWindowEx failed error=" + Marshal.GetLastWin32Error();
                    return ProbeVerdict.Unverifiable;
                }

                if (layered && !ApplyLayered(hwnd))
                {
                    detail = "SetLayeredWindowAttributes failed error=" + Marshal.GetLastWin32Error();
                    return ProbeVerdict.Unverifiable;
                }

                IntPtr below;
                var placeDetail = string.Empty;
                var placed = topMost
                    ? DesktopLayer.PlaceOnTop(hwnd, parent)
                    : DesktopLayer.Place(hwnd, parent, above, out below, out placeDetail);
                if (!placed)
                {
                    detail = "place failed: " + (topMost ? "top" : placeDetail);
                    return ProbeVerdict.Hidden;
                }

                VisibilityProbe.Pump(220);
                int captured;
                var matched = VisibilityProbe.CountMatches(points, VisibilityProbe.ColorA, out captured);
                detail = "match=" + matched + "/" + points.Count +
                         " captured=" + captured +
                         " at=" + VisibilityProbe.DescribePoints(points);
                if (captured == 0)
                {
                    return ProbeVerdict.Unverifiable;
                }
                return matched > 0 ? ProbeVerdict.Visible : ProbeVerdict.Hidden;
            }
            catch (Exception ex)
            {
                detail = "gdi probe failed: " + ex.Message;
                return ProbeVerdict.Unverifiable;
            }
            finally
            {
                if (hwnd != IntPtr.Zero)
                {
                    NativeMethods.DestroyWindow(hwnd);
                }
                if (brush != IntPtr.Zero)
                {
                    NativeMethods.DeleteObject(brush);
                }
                NativeMethods.UnregisterClass(className, hInstance);
            }
        }

        private static uint ColorRef(DrawingColor color)
        {
            return (uint)(color.R | (color.G << 8) | (color.B << 16));
        }

        internal static bool ApplyLayered(IntPtr hwnd)
        {
            // Constant alpha 255 (fully opaque). A layered child window gets its own
            // composition surface, which is what Windows 11 24H2+ needs for wallpaper
            // windows inside Progman.
            return NativeMethods.SetLayeredWindowAttributes(
                hwnd, 0, 255, NativeMethods.LWA_ALPHA);
        }
    }
}
