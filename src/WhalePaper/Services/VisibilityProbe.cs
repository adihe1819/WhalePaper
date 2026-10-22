using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using WhalePaper.Interop;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingColor = System.Drawing.Color;
using DrawingCopyPixelOperation = System.Drawing.CopyPixelOperation;
using DrawingGraphics = System.Drawing.Graphics;
using DrawingPixelFormat = System.Drawing.Imaging.PixelFormat;
using DrawingPoint = System.Drawing.Point;
using DrawingSize = System.Drawing.Size;
using Screen = System.Windows.Forms.Screen;
using WpfColor = System.Windows.Media.Color;

namespace WhalePaper.Services
{
    internal enum ProbeVerdict
    {
        Unverifiable = 0,
        Visible = 1,
        Hidden = 2
    }

    /// <summary>
    /// Decides whether the wallpaper host window is really composited on the visible
    /// desktop. The host paints small solid patches in two different colors; the result
    /// is read back from the screen. Window styles and parent handles cannot answer this
    /// question, only pixels can.
    /// </summary>
    internal static class VisibilityProbe
    {
        public const int PatchSizePx = 110;

        private const int ColorTolerance = 3;
        private const int CaptureTimeoutMs = 420;
        private const int CaptureRetryDelayMs = 60;

        // Deliberately unusual colors: the chance that a wallpaper contains both exact
        // values at the sampled points is negligible, and two colors rule out a lucky hit.
        public static readonly DrawingColor ColorA = DrawingColor.FromArgb(255, 0x12, 0xE7, 0x57);
        public static readonly DrawingColor ColorB = DrawingColor.FromArgb(255, 0xE7, 0x12, 0x87);

        public static List<DrawingPoint> SelectSamplePoints(IntPtr desktopRoot, int maxPoints)
        {
            var points = new List<DrawingPoint>();
            if (maxPoints <= 0)
            {
                return points;
            }

            foreach (var candidate in GridPoints())
            {
                if (!IsUsablePoint(candidate, desktopRoot))
                {
                    continue;
                }

                if (IsTooClose(points, candidate.X, candidate.Y, 180))
                {
                    continue;
                }

                points.Add(candidate);
                if (points.Count >= maxPoints)
                {
                    break;
                }
            }

            return points;
        }

        /// <summary>
        /// Diagnostic dump of every grid point and why it was accepted or skipped.
        /// </summary>
        public static string DescribeSampleCandidates(IntPtr desktopRoot)
        {
            var text = new System.Text.StringBuilder();
            foreach (var candidate in GridPoints())
            {
                var point = new NativeMethods.POINT { X = candidate.X, Y = candidate.Y };
                var hit = NativeMethods.WindowFromPoint(point);
                var root = hit == IntPtr.Zero
                    ? IntPtr.Zero
                    : NativeMethods.GetAncestor(hit, NativeMethods.GA_ROOT);
                if (root == IntPtr.Zero)
                {
                    root = hit;
                }

                text.Append("  ").Append(candidate.X).Append(',').Append(candidate.Y)
                    .Append(" hit=").Append(hit == IntPtr.Zero ? "0x0" : "0x" + hit.ToInt64().ToString("X"))
                    .Append(" cls=").Append(DesktopLayer.ClassName(hit))
                    .Append(" root=").Append(root == IntPtr.Zero ? "0x0" : "0x" + root.ToInt64().ToString("X"))
                    .Append(" rootCls=").Append(DesktopLayer.ClassName(root))
                    .Append(" visible=").Append(NativeMethods.IsWindowVisible(hit) ? "1" : "0")
                    .Append(" accept=").Append(IsDesktopSurface(hit, desktopRoot) && IsInsideWorkArea(candidate) ? "1" : "0")
                    .AppendLine();
            }
            return text.ToString().TrimEnd();
        }

        private static List<DrawingPoint> GridPoints()
        {
            var screen = Screen.PrimaryScreen.Bounds;
            var fractions = new[]
            {
                new[] { 0.22, 0.24 },
                new[] { 0.78, 0.68 },
                new[] { 0.50, 0.18 },
                new[] { 0.24, 0.74 },
                new[] { 0.76, 0.26 },
                new[] { 0.50, 0.52 },
                new[] { 0.08, 0.12 },
                new[] { 0.92, 0.88 },
                new[] { 0.08, 0.88 },
                new[] { 0.92, 0.12 }
            };

            var points = new List<DrawingPoint>();
            foreach (var fraction in fractions)
            {
                points.Add(new DrawingPoint(
                    screen.Left + (int)(screen.Width * fraction[0]),
                    screen.Top + (int)(screen.Height * fraction[1])));
            }
            return points;
        }

        private static bool IsUsablePoint(DrawingPoint candidate, IntPtr desktopRoot)
        {
            if (!IsInsideWorkArea(candidate))
            {
                return false;
            }

            var point = new NativeMethods.POINT { X = candidate.X, Y = candidate.Y };
            var hit = NativeMethods.WindowFromPoint(point);
            return hit != IntPtr.Zero && IsDesktopSurface(hit, desktopRoot);
        }

        private static bool IsInsideWorkArea(DrawingPoint point)
        {
            var work = Screen.PrimaryScreen.WorkingArea;
            return point.X >= work.Left && point.Y >= work.Top
                && point.X < work.Right && point.Y < work.Bottom;
        }

        public static void DrawPatches(Canvas layer, IntPtr hwnd, IList<DrawingPoint> points, DrawingColor color)
        {
            if (layer == null || points == null)
            {
                return;
            }

            layer.Children.Clear();
            layer.Visibility = Visibility.Visible;

            var scale = GetScale(hwnd);
            var brush = new SolidColorBrush(WpfColor.FromArgb(color.A, color.R, color.G, color.B));
            brush.Freeze();
            var size = PatchSizePx / scale;

            foreach (var point in points)
            {
                var client = new NativeMethods.POINT { X = point.X, Y = point.Y };
                NativeMethods.ScreenToClient(hwnd, ref client);

                var patch = new Rectangle
                {
                    Width = size,
                    Height = size,
                    Fill = brush,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(patch, (client.X - PatchSizePx / 2.0) / scale);
                Canvas.SetTop(patch, (client.Y - PatchSizePx / 2.0) / scale);
                layer.Children.Add(patch);
            }
        }

        public static void ClearPatches(Canvas layer)
        {
            if (layer == null)
            {
                return;
            }

            layer.Children.Clear();
            layer.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// Paints the two test colors and reads the pixels back. A point counts as ours
        /// only when the second frame really differs from the first and each frame is
        /// closer to its own color than to the other one, which keeps the verdict stable
        /// under display colour management or HDR tone mapping.
        /// </summary>
        public static ProbeVerdict Verify(
            IntPtr hwnd,
            Canvas layer,
            IList<DrawingPoint> points,
            out string detail)
        {
            try
            {
                return VerifyCore(hwnd, layer, points, out detail);
            }
            catch (Exception ex)
            {
                detail = "probe failed: " + ex.Message;
                return ProbeVerdict.Unverifiable;
            }
        }

        private static ProbeVerdict VerifyCore(
            IntPtr hwnd,
            Canvas layer,
            IList<DrawingPoint> points,
            out string detail)
        {
            detail = "no-sample-points";
            if (points == null || points.Count == 0)
            {
                return ProbeVerdict.Unverifiable;
            }

            DrawPatches(layer, hwnd, points, ColorA);
            Pump(240);
            int capturedA;
            var observedA = CaptureAll(points, ColorA, out capturedA);

            DrawPatches(layer, hwnd, points, ColorB);
            Pump(120);
            int capturedB;
            var observedB = CaptureAll(points, ColorB, out capturedB);

            ClearPatches(layer);

            var need = points.Count >= 2 ? 2 : 1;
            var matched = 0;
            var captured = 0;
            for (var index = 0; index < points.Count; index++)
            {
                if (observedA[index] == DrawingColor.Empty || observedB[index] == DrawingColor.Empty)
                {
                    continue;
                }

                captured++;
                if (IsOurPair(observedA[index], observedB[index]))
                {
                    matched++;
                }
            }

            detail = "points=" + points.Count +
                     " match=" + matched + "/" + need +
                     " captured=" + capturedA + "/" + capturedB +
                     " colors=" + Hex(ColorA) + "/" + Hex(ColorB) +
                     " at=" + DescribePoints(points);

            if (captured == 0)
            {
                return ProbeVerdict.Unverifiable;
            }

            return matched >= need ? ProbeVerdict.Visible : ProbeVerdict.Hidden;
        }

        /// <summary>
        /// Native-host variant: the caller paints a solid color through GDI (the same
        /// path that paints wallpaper frames). The paired-color logic is unchanged, so
        /// a static desktop can never be mistaken for a composited host.
        /// </summary>
        public static ProbeVerdict VerifyNative(
            IList<DrawingPoint> points,
            Action<DrawingColor> paintSolid,
            out string detail)
        {
            try
            {
                return VerifyNativeCore(points, paintSolid, out detail);
            }
            catch (Exception ex)
            {
                detail = "native probe failed: " + ex.Message;
                return ProbeVerdict.Unverifiable;
            }
        }

        private static ProbeVerdict VerifyNativeCore(
            IList<DrawingPoint> points,
            Action<DrawingColor> paintSolid,
            out string detail)
        {
            detail = "no-sample-points";
            if (points == null || points.Count == 0 || paintSolid == null)
            {
                return ProbeVerdict.Unverifiable;
            }

            paintSolid(ColorA);
            NativeMethods.GdiFlush();
            Pump(240);
            int capturedA;
            var observedA = CaptureAll(points, ColorA, out capturedA);

            paintSolid(ColorB);
            NativeMethods.GdiFlush();
            Pump(120);
            int capturedB;
            var observedB = CaptureAll(points, ColorB, out capturedB);

            var need = points.Count >= 2 ? 2 : 1;
            var matched = 0;
            var captured = 0;
            for (var index = 0; index < points.Count; index++)
            {
                if (observedA[index] == DrawingColor.Empty || observedB[index] == DrawingColor.Empty)
                {
                    continue;
                }

                captured++;
                if (IsOurPair(observedA[index], observedB[index]))
                {
                    matched++;
                }
            }

            detail = "points=" + points.Count +
                     " match=" + matched + "/" + need +
                     " captured=" + capturedA + "/" + capturedB +
                     " colors=" + Hex(ColorA) + "/" + Hex(ColorB) +
                     " at=" + DescribePoints(points);

            if (captured == 0)
            {
                return ProbeVerdict.Unverifiable;
            }

            return matched >= need ? ProbeVerdict.Visible : ProbeVerdict.Hidden;
        }

        public static bool TryCaptureColor(DrawingPoint point, out DrawingColor color)
        {
            color = DrawingColor.Empty;
            try
            {
                var screen = Screen.PrimaryScreen.Bounds;
                if (point.X < screen.Left || point.Y < screen.Top ||
                    point.X >= screen.Right || point.Y >= screen.Bottom)
                {
                    return false;
                }

                using (var bitmap = new DrawingBitmap(1, 1, DrawingPixelFormat.Format32bppArgb))
                using (var graphics = DrawingGraphics.FromImage(bitmap))
                {
                    graphics.CopyFromScreen(
                        point.X, point.Y, 0, 0, new DrawingSize(1, 1),
                        DrawingCopyPixelOperation.SourceCopy);
                    var captured = bitmap.GetPixel(0, 0);
                    color = DrawingColor.FromArgb(255, captured.R, captured.G, captured.B);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        public static bool Matches(DrawingColor left, DrawingColor right)
        {
            return Math.Abs(left.R - right.R) <= ColorTolerance
                && Math.Abs(left.G - right.G) <= ColorTolerance
                && Math.Abs(left.B - right.B) <= ColorTolerance;
        }

        public static string DescribePoints(IList<DrawingPoint> points)
        {
            if (points == null || points.Count == 0)
            {
                return "none";
            }

            var text = new System.Text.StringBuilder();
            foreach (var point in points)
            {
                if (text.Length > 0)
                {
                    text.Append(';');
                }
                text.Append(point.X).Append(',').Append(point.Y);
            }
            return text.ToString();
        }

        public static string Hex(DrawingColor color)
        {
            return "#" + color.R.ToString("X2") + color.G.ToString("X2") + color.B.ToString("X2");
        }

        public static string VerdictText(ProbeVerdict verdict)
        {
            switch (verdict)
            {
                case ProbeVerdict.Visible:
                    return "可见";
                case ProbeVerdict.Hidden:
                    return "不可见";
                default:
                    return "无法确认";
            }
        }

        public static double GetScale(IntPtr hwnd)
        {
            var dpi = NativeMethods.GetDpiForWindow(hwnd);
            return dpi > 0 ? dpi / 96.0 : 1.0;
        }

        /// <summary>
        /// Pumps the WPF dispatcher. Rendering and DWM presentation both need messages,
        /// so a plain Thread.Sleep would always report "not visible".
        /// </summary>
        public static void Pump(int milliseconds)
        {
            if (milliseconds <= 0)
            {
                return;
            }

            var dispatcher = Dispatcher.CurrentDispatcher;
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(milliseconds)
            };

            EventHandler handler = null;
            handler = (sender, args) =>
            {
                timer.Stop();
                timer.Tick -= handler;
                frame.Continue = false;
            };
            timer.Tick += handler;
            timer.Start();
            Dispatcher.PushFrame(frame);
        }

        /// <summary>
        /// Captures every point, retrying until at least one point already shows the
        /// expected colour (DWM may present the first frame late). Returns the last
        /// observation per point so both passes can be compared as pairs.
        /// </summary>
        private static List<DrawingColor> CaptureAll(
            IList<DrawingPoint> points,
            DrawingColor expected,
            out int captured)
        {
            var result = new List<DrawingColor>(points.Count);
            foreach (var unused in points)
            {
                result.Add(DrawingColor.Empty);
            }

            var watch = Stopwatch.StartNew();
            captured = 0;

            while (true)
            {
                var close = 0;
                captured = 0;
                for (var index = 0; index < points.Count; index++)
                {
                    DrawingColor color;
                    if (!TryCaptureColor(points[index], out color))
                    {
                        result[index] = DrawingColor.Empty;
                        continue;
                    }

                    result[index] = color;
                    captured++;
                    if (Matches(color, expected))
                    {
                        close++;
                    }
                }

                if (close > 0 || watch.ElapsedMilliseconds >= CaptureTimeoutMs)
                {
                    return result;
                }

                Pump(CaptureRetryDelayMs);
            }
        }

        /// <summary>
        /// True when the two frames differ enough and each is closer to its own test
        /// colour than to the other one. A static desktop background (our window not
        /// composited) produces two identical frames and therefore never matches.
        /// </summary>
        private static bool IsOurPair(DrawingColor first, DrawingColor second)
        {
            if (first == DrawingColor.Empty || second == DrawingColor.Empty)
            {
                return false;
            }

            if (Distance(first, second) < 60)
            {
                return false;
            }

            return Distance(first, ColorA) < Distance(first, ColorB)
                && Distance(second, ColorB) < Distance(second, ColorA);
        }

        public static int Distance(DrawingColor left, DrawingColor right)
        {
            return Math.Abs(left.R - right.R)
                 + Math.Abs(left.G - right.G)
                 + Math.Abs(left.B - right.B);
        }

        public static int CountMatches(
            IList<DrawingPoint> points,
            DrawingColor expected,
            out int captured)
        {
            captured = 0;
            var matched = 0;
            foreach (var point in points)
            {
                DrawingColor actual;
                if (!TryCaptureColor(point, out actual))
                {
                    continue;
                }

                captured++;
                if (Matches(actual, expected))
                {
                    matched++;
                }
            }
            return matched;
        }

        private static bool IsDesktopSurface(IntPtr hit, IntPtr desktopRoot)
        {
            var root = NativeMethods.GetAncestor(hit, NativeMethods.GA_ROOT);
            if (root == IntPtr.Zero)
            {
                root = hit;
            }

            if (desktopRoot != IntPtr.Zero && root == desktopRoot)
            {
                return true;
            }

            if (string.Equals(DesktopLayer.ClassName(hit), "SysListView32", StringComparison.Ordinal))
            {
                return true;
            }

            // Classic layout: the icon layer lives inside a top-level WorkerW.
            return NativeMethods.FindWindowEx(root, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero;
        }

        private static bool IsTooClose(IList<DrawingPoint> points, int x, int y, int minimumDistance)
        {
            foreach (var point in points)
            {
                var dx = point.X - x;
                var dy = point.Y - y;
                if (dx * dx + dy * dy < minimumDistance * minimumDistance)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
