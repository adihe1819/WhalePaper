using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using WhalePaper.Interop;
using DrawingColor = System.Drawing.Color;
using DrawingPoint = System.Drawing.Point;

namespace WhalePaper.Services
{
    internal sealed class NativeLayerMeasurement
    {
        public DesktopLayerCandidate Candidate;
        public IntPtr Parent;
        public bool Created;
        public string Detail = "";
        public ProbeVerdict Verdict = ProbeVerdict.Unverifiable;
        public string VerdictDetail = "";
        public bool ClipSiblings;
        public bool DiagnosticOnly;
    }

    internal enum NativeLayerDiagnosis
    {
        Unknown = 0,
        Ok = 1,
        Hidden = 2,
        PositionHidden = 3,
        NoSamples = 4,
        CreateFailed = 5,
        NoCandidates = 6
    }

    /// <summary>
    /// 1.0.4 layer probe. Unlike the 1.0.2/1.0.3 matrix this only measures what the
    /// shipping renderer actually uses: a native layered child window painted through
    /// StretchDIBits. The WPF question was settled in 1.0.3 and is no longer measured.
    /// </summary>
    internal static class NativeLayerProbe
    {
        private const string ClassName = "WhalePaperDibLayerProbe";
        private static readonly object ClassSync = new object();
        private static bool _classRegistered;
        private static readonly NativeMethods.WndProcDelegate ProbeWndProc = WindowProc;

        private static IntPtr _paintHwnd;
        private static int _paintWidth;
        private static int _paintHeight;
        private static DrawingColor _paintColor;

        public static List<NativeLayerMeasurement> Measure(
            IList<DesktopLayerCandidate> candidates,
            IList<DrawingPoint> points)
        {
            var results = new List<NativeLayerMeasurement>();
            if (candidates != null)
            {
                foreach (var candidate in candidates)
                {
                    // 1.0.5 default: no WS_CLIPSIBLINGS. 1.0.4 had it and the sibling
                    // SysListView32 clipped the whole wallpaper surface.
                    results.Add(MeasureCandidate(candidate, points, false, false, false));

                    // A/B diagnostic row: show what the old style does on this machine.
                    var clipped = new DesktopLayerCandidate
                    {
                        Strategy = candidate.Strategy,
                        Id = candidate.Id + "-clipsiblings",
                        Title = candidate.Title + "（诊断：带 WS_CLIPSIBLINGS）",
                        Parent = candidate.Parent,
                        AboveWindow = candidate.AboveWindow
                    };
                    results.Add(MeasureCandidate(clipped, points, false, true, true));
                }
            }

            var progman = DesktopLayer.FindProgman();
            if (progman != IntPtr.Zero && points != null && points.Count > 0)
            {
                results.Add(MeasureCandidate(
                    new DesktopLayerCandidate
                    {
                        Id = "native-dib-topmost",
                        Title = "诊断：Progman 最上层原生 DIB（不属于挂载候选）",
                        Parent = progman
                    },
                    points,
                    true,
                    false,
                    true));
            }

            return results;
        }

        private static NativeLayerMeasurement MeasureCandidate(
            DesktopLayerCandidate candidate,
            IList<DrawingPoint> points,
            bool topMost,
            bool clipSiblings,
            bool diagnosticOnly)
        {
            var measurement = new NativeLayerMeasurement
            {
                Candidate = candidate,
                Parent = candidate.Parent,
                ClipSiblings = clipSiblings,
                DiagnosticOnly = diagnosticOnly || topMost
            };

            if (candidate.Parent == IntPtr.Zero || !NativeMethods.IsWindow(candidate.Parent))
            {
                measurement.Detail = "父窗口不可用";
                return measurement;
            }

            IntPtr hwnd = IntPtr.Zero;
            try
            {
                EnsureClass();

                int width;
                int height;
                DesktopLayer.GetLayerSize(candidate.Parent, out width, out height);

                var style = NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE;
                if (clipSiblings)
                {
                    style |= NativeMethods.WS_CLIPSIBLINGS;
                }

                hwnd = NativeMethods.CreateWindowEx(
                    NativeMethods.WS_EX_LAYERED |
                    NativeMethods.WS_EX_NOACTIVATE |
                    NativeMethods.WS_EX_TOOLWINDOW,
                    ClassName,
                    "WhalePaperDibLayerProbe",
                    style,
                    0,
                    0,
                    width,
                    height,
                    candidate.Parent,
                    IntPtr.Zero,
                    NativeMethods.GetModuleHandle(null),
                    IntPtr.Zero);

                if (hwnd == IntPtr.Zero)
                {
                    measurement.Detail = "CreateWindowEx failed error=" +
                                         Marshal.GetLastWin32Error();
                    return measurement;
                }

                _paintHwnd = hwnd;
                _paintWidth = width;
                _paintHeight = height;
                _paintColor = VisibilityProbe.ColorA;

                if (!NativeMethods.SetLayeredWindowAttributes(
                        hwnd, 0, 255, NativeMethods.LWA_ALPHA))
                {
                    measurement.Detail = "SetLayeredWindowAttributes failed error=" +
                                         Marshal.GetLastWin32Error();
                    return measurement;
                }

                IntPtr below;
                var placeDetail = "";
                var placed = topMost
                    ? DesktopLayer.PlaceOnTop(hwnd, candidate.Parent)
                    : DesktopLayer.Place(
                        hwnd, candidate.Parent, candidate.AboveWindow, out below, out placeDetail);

                if (!placed)
                {
                    measurement.Detail = topMost
                        ? "place top failed"
                        : "place failed: " + placeDetail;
                    return measurement;
                }

                measurement.Created = true;
                NativeMethods.InvalidateRect(hwnd, IntPtr.Zero, false);
                NativeMethods.UpdateWindow(hwnd);
                VisibilityProbe.Pump(240);

                int captured;
                var matched = VisibilityProbe.CountMatches(
                    points, VisibilityProbe.ColorA, out captured);

                measurement.Verdict = captured == 0
                    ? ProbeVerdict.Unverifiable
                    : (matched > 0 ? ProbeVerdict.Visible : ProbeVerdict.Hidden);
                measurement.VerdictDetail = "match=" + matched + "/" + points.Count +
                                            " captured=" + captured;
                measurement.Detail = "parent=0x" + candidate.Parent.ToInt64().ToString("X") +
                                     " placed=1 layered=1 clip=" + (clipSiblings ? "1" : "0");
                return measurement;
            }
            catch (Exception ex)
            {
                measurement.Detail = "探测异常：" + ex.Message;
                return measurement;
            }
            finally
            {
                if (hwnd != IntPtr.Zero)
                {
                    NativeMethods.DestroyWindow(hwnd);
                }

                if (_paintHwnd == hwnd)
                {
                    _paintHwnd = IntPtr.Zero;
                }
            }
        }

        private static void EnsureClass()
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
                    lpfnWndProc = ProbeWndProc,
                    hInstance = NativeMethods.GetModuleHandle(null),
                    hbrBackground = IntPtr.Zero,
                    lpszClassName = ClassName
                };

                var atom = NativeMethods.RegisterClassEx(ref windowClass);
                if (atom == 0)
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error != 1410)
                    {
                        throw new System.ComponentModel.Win32Exception(error);
                    }
                }

                _classRegistered = true;
            }
        }

        private static IntPtr WindowProc(
            IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (hwnd == _paintHwnd)
            {
                if (msg == NativeMethods.WM_ERASEBKGND)
                {
                    return new IntPtr(1);
                }

                if (msg == NativeMethods.WM_PAINT)
                {
                    NativeMethods.PAINTSTRUCT paint;
                    var hdc = NativeMethods.BeginPaint(hwnd, out paint);
                    try
                    {
                        DibPaint.StretchSolid(
                            hdc, 0, 0, _paintWidth, _paintHeight, _paintColor);
                    }
                    finally
                    {
                        NativeMethods.EndPaint(hwnd, ref paint);
                    }
                    return IntPtr.Zero;
                }
            }

            return NativeMethods.DefWindowProc(hwnd, msg, wParam, lParam);
        }

        public static NativeLayerDiagnosis Diagnose(
            IList<NativeLayerMeasurement> results,
            int sampleCount,
            out string code,
            out string conclusion)
        {
            code = "UNKNOWN";
            conclusion = "诊断未完成。";

            if (results == null || results.Count == 0)
            {
                code = "NO_CANDIDATES";
                conclusion = "没有可用的挂载候选（SHELLDLL_DefView 不存在）。";
                return NativeLayerDiagnosis.NoCandidates;
            }

            var anyCandidate = false;
            var anyCreated = false;
            var anyVisible = false;
            var anyCaptured = false;
            var topMostVisible = false;
            string visibleId = null;
            string hiddenId = null;

            foreach (var item in results)
            {
                if (item.DiagnosticOnly)
                {
                    topMostVisible |= item.Verdict == ProbeVerdict.Visible;
                    continue;
                }

                anyCandidate = true;
                anyCreated |= item.Created;
                anyCaptured |= item.Verdict != ProbeVerdict.Unverifiable;
                if (item.Verdict == ProbeVerdict.Visible)
                {
                    anyVisible = true;
                    if (visibleId == null) visibleId = item.Candidate.Id;
                }
                else if (item.Verdict == ProbeVerdict.Hidden)
                {
                    if (hiddenId == null) hiddenId = item.Candidate.Id;
                }
            }

            if (!anyCandidate)
            {
                code = "NO_CANDIDATES";
                conclusion = "没有可用的挂载候选（SHELLDLL_DefView 不存在）。";
                return NativeLayerDiagnosis.NoCandidates;
            }

            if (!anyCreated)
            {
                code = "CREATE_FAILED";
                conclusion = "所有候选都无法创建原生分层窗口（CreateWindowEx / " +
                             "SetLayeredWindowAttributes 失败）。如果错误码是 5，说明当前进程权限低于" +
                             "资源管理器或桌面层不可用；请从开始菜单/资源管理器正常启动后再试。";
                return NativeLayerDiagnosis.CreateFailed;
            }

            if (sampleCount <= 0)
            {
                code = "NO_SAMPLES";
                conclusion = "挂载窗口能创建，但采集不到桌面像素（桌面被其他窗口完全遮挡，或截屏被拒绝），" +
                             "本次无法判定。请让桌面露出后重新运行桌面层体检。";
                return NativeLayerDiagnosis.NoSamples;
            }

            if (anyVisible)
            {
                code = "NATIVE_LAYERED_OK";
                conclusion = "原生分层 DIB 窗口在候选 " + visibleId + " 可见；" +
                             "1.0.4 的动态壁纸画面使用同一条 StretchDIBits 绘制路径。";
                return NativeLayerDiagnosis.Ok;
            }

            if (!anyCaptured)
            {
                code = "NO_SAMPLES";
                conclusion = "窗口创建成功，但没有一个采样点能取到像素（桌面被遮挡或截屏受限）。" +
                             "请露出桌面后重跑。";
                return NativeLayerDiagnosis.NoSamples;
            }

            if (topMostVisible)
            {
                code = "NATIVE_POSITION_HIDDEN";
                conclusion = "Progman 最上层的原生分层 DIB 可见，但候选 " + (hiddenId ?? "?") +
                             " 不可见：说明子窗口能渲染，这个位置仍被更上层表面盖住。请把报告发回。";
                return NativeLayerDiagnosis.PositionHidden;
            }

            code = "NATIVE_LAYERED_HIDDEN";
            conclusion = "原生分层 DIB 窗口在候选 " + (hiddenId ?? "?") + " 不可见，" +
                         "连 Progman 最上层也不可见。说明该桌面不合成这类子窗口，" +
                         "需要重新评估渲染表面，而不是继续换渲染器。";
            return NativeLayerDiagnosis.Hidden;
        }

        public static string FormatLines(IList<NativeLayerMeasurement> results)
        {
            var text = new System.Text.StringBuilder();
            foreach (var item in results)
            {
                text.Append("  ").Append(item.Candidate.Id)
                    .Append(" parent=").Append(Hex(item.Parent))
                    .Append(" created=").Append(item.Created ? "1" : "0")
                    .Append(" clip=").Append(item.ClipSiblings ? "1" : "0");

                if (item.Verdict != ProbeVerdict.Unverifiable)
                {
                    text.Append(" native-dib=").Append(VisibilityProbe.VerdictText(item.Verdict));
                }
                if (item.DiagnosticOnly)
                {
                    text.Append(" [diagnostic only]");
                }
                text.AppendLine();
            }
            return text.ToString().TrimEnd();
        }

        public static string FormatDetails(IList<NativeLayerMeasurement> results)
        {
            var text = new System.Text.StringBuilder();
            foreach (var item in results)
            {
                text.Append("  [").Append(item.Candidate.Id).Append("]").AppendLine();
                text.Append("    放置：").Append(item.Detail).AppendLine();
                if (item.Verdict != ProbeVerdict.Unverifiable)
                {
                    text.Append("    原生 DIB：").Append(VisibilityProbe.VerdictText(item.Verdict))
                        .Append(" ").Append(item.VerdictDetail).AppendLine();
                }
            }
            return text.ToString().TrimEnd();
        }

        private static string Hex(IntPtr value)
        {
            return value == IntPtr.Zero ? "0x0" : "0x" + value.ToInt64().ToString("X");
        }
    }
}
