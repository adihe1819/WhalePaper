using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using WhalePaper.Interop;
using DrawingPoint = System.Drawing.Point;

namespace WhalePaper.Services
{
    /// <summary>
    /// One measured candidate. On Windows 11 24H2+ a plain (non-layered) child window is
    /// not composited inside Progman, so both a "layered" and a legacy "plain" variant are
    /// measured: GDI, WPF hardware, and (when it can change the verdict) WPF software.
    /// </summary>
    internal sealed class DesktopLayerMeasurement
    {
        public DesktopLayerCandidate Candidate;
        public IntPtr Parent;
        public bool Created;
        public bool SpawnedWorker;
        public string CreateDetail = "";

        public ProbeVerdict Gdi = ProbeVerdict.Unverifiable;
        public string GdiDetail = "";
        public ProbeVerdict Wpf = ProbeVerdict.Unverifiable;
        public string WpfDetail = "";

        public ProbeVerdict GdiLayered = ProbeVerdict.Unverifiable;
        public string GdiLayeredDetail = "";
        public ProbeVerdict WpfLayered = ProbeVerdict.Unverifiable;
        public string WpfLayeredDetail = "";
        public ProbeVerdict WpfLayeredSoftware = ProbeVerdict.Unverifiable;
        public string WpfLayeredSoftwareDetail = "";

        public bool DiagnosticOnly;
    }

    internal enum LayerDiagnosis
    {
        Unknown = 0,
        Verified = 1,
        LayeredOk = 2,
        WpfLayeredBlocked = 3,
        WpfLayeredSoftwareOnly = 4,
        LayeredStillWrong = 5,
        WpfBlockedLegacy = 6,
        CreateFailed = 7,
        NoSamples = 8
    }

    /// <summary>
    /// 1.0.2/1.0.3 WPF probe matrix. Retired in 1.0.4: the WPF question was settled in
    /// 1.0.3 (HwndSource cannot hold WS_EX_LAYERED, error 87). Kept for historical
    /// reference only; the shipping probe is <see cref="NativeLayerProbe"/>.
    /// </summary>
    internal static class DesktopLayerMatrix
    {
        public static List<DesktopLayerMeasurement> Measure(
            IList<DesktopLayerCandidate> candidates,
            IList<DrawingPoint> points,
            bool full)
        {
            var results = new List<DesktopLayerMeasurement>();
            if (candidates == null)
            {
                return results;
            }

            var candidateIndex = 0;
            foreach (var candidate in candidates)
            {
                var measurement = new DesktopLayerMeasurement { Candidate = candidate };
                results.Add(measurement);
                var measureLegacy = full && candidateIndex == 0;
                candidateIndex++;

                try
                {
                    var parent = candidate.Parent;
                    if (candidate.RequiresWorkerSpawn)
                    {
                        measurement.SpawnedWorker = true;
                        parent = WorkerWService.TrySpawnWorkerW();
                    }
                    measurement.Parent = parent;

                    if (parent == IntPtr.Zero || !NativeMethods.IsWindow(parent))
                    {
                        measurement.CreateDetail = "父窗口不可用";
                        continue;
                    }

                    // 1) Layered variants: the required style on Windows 11 24H2+.
                    string gdiDetail;
                    measurement.GdiLayered = GdiProbeWindow.Verify(
                        parent, candidate.AboveWindow, points, true, out gdiDetail);
                    measurement.GdiLayeredDetail = gdiDetail;

                    using (var surface = new LayerProbeSurface())
                    {
                        string detail;
                        var created = surface.Create(
                            parent, candidate.AboveWindow, false, true, out detail);
                        measurement.CreateDetail = detail;
                        measurement.Created |= created;

                        if (created && points != null && points.Count > 0)
                        {
                            string wpfDetail;
                            measurement.WpfLayered = VisibilityProbe.Verify(
                                surface.Handle, surface.Layer, points, out wpfDetail);
                            measurement.WpfLayeredDetail = wpfDetail;
                        }
                    }

                    // 2) Software rendering only matters when the layer works (GDI visible)
                    //    but the hardware WPF surface is hidden.
                    if (measurement.GdiLayered == ProbeVerdict.Visible &&
                        measurement.WpfLayered == ProbeVerdict.Hidden &&
                        points != null && points.Count > 0)
                    {
                        using (var software = new LayerProbeSurface())
                        {
                            string detail;
                            if (software.Create(parent, candidate.AboveWindow, true, true, out detail))
                            {
                                string swDetail;
                                measurement.WpfLayeredSoftware = VisibilityProbe.Verify(
                                    software.Handle, software.Layer, points, out swDetail);
                                measurement.WpfLayeredSoftwareDetail = swDetail;
                            }
                            else
                            {
                                measurement.WpfLayeredSoftwareDetail = detail;
                            }
                        }
                    }

                    // 3) Legacy plain windows, for comparison with 1.0.2 (first candidate
                    //    only - they cannot work on 24H2+ anyway).
                    if (measureLegacy)
                    {
                        string legacyGdiDetail;
                        measurement.Gdi = GdiProbeWindow.Verify(
                            parent, candidate.AboveWindow, points, false, out legacyGdiDetail);
                        measurement.GdiDetail = legacyGdiDetail;

                        using (var surface = new LayerProbeSurface())
                        {
                            string detail;
                            var created = surface.Create(
                                parent, candidate.AboveWindow, false, false, out detail);
                            measurement.Created |= created;
                            if (created && points != null && points.Count > 0)
                            {
                                string wpfDetail;
                                measurement.Wpf = VisibilityProbe.Verify(
                                    surface.Handle, surface.Layer, points, out wpfDetail);
                                measurement.WpfDetail = wpfDetail;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    measurement.CreateDetail = "测量异常：" + ex.Message;
                }
            }

            // Diagnostic rows: GDI windows placed above the desktop icons. They answer
            // "does Progman render children at all", independent of the wallpaper layer.
            if (points != null && points.Count > 0)
            {
                var progman = DesktopLayer.FindProgman();
                if (progman != IntPtr.Zero)
                {
                    results.Add(MeasureTopMost(progman, points, true));
                    if (full)
                    {
                        results.Add(MeasureTopMost(progman, points, false));
                    }
                }
            }

            return results;
        }

        private static DesktopLayerMeasurement MeasureTopMost(
            IntPtr progman,
            IList<DrawingPoint> points,
            bool layered)
        {
            var extra = new DesktopLayerMeasurement
            {
                Candidate = new DesktopLayerCandidate
                {
                    Id = layered ? "gdi-topmost-layered" : "gdi-topmost",
                    Title = "诊断：Progman 最上层 GDI（不属于挂载候选）",
                    Parent = progman
                },
                Parent = progman,
                Created = true,
                DiagnosticOnly = true
            };

            try
            {
                string detail;
                if (layered)
                {
                    extra.GdiLayered = GdiProbeWindow.VerifyTopMost(progman, points, true, out detail);
                    extra.GdiLayeredDetail = detail;
                }
                else
                {
                    extra.Gdi = GdiProbeWindow.VerifyTopMost(progman, points, false, out detail);
                    extra.GdiDetail = detail;
                }
                extra.CreateDetail = "topmost diagnostic";
            }
            catch (Exception ex)
            {
                extra.CreateDetail = "诊断异常：" + ex.Message;
            }
            return extra;
        }

        public static LayerDiagnosis Diagnose(
            IList<DesktopLayerMeasurement> results,
            int sampleCount,
            out string code,
            out string conclusion)
        {
            code = "UNKNOWN";
            conclusion = "诊断未完成。";

            if (results == null || results.Count == 0)
            {
                code = "NO_CANDIDATES";
                conclusion = "没有可用的候选层（Progman / SHELLDLL_DefView 均不存在）。";
                return LayerDiagnosis.CreateFailed;
            }

            var anyCreated = false;
            var topMostLayeredVisible = false;
            var topMostPlainVisible = false;
            for (var index = 0; index < results.Count; index++)
            {
                var item = results[index];
                if (item.DiagnosticOnly)
                {
                    topMostLayeredVisible |= item.GdiLayered == ProbeVerdict.Visible;
                    topMostPlainVisible |= item.Gdi == ProbeVerdict.Visible;
                    continue;
                }
                anyCreated |= item.Created;
            }

            if (!anyCreated)
            {
                code = "CREATE_FAILED";
                conclusion = "所有候选都无法创建挂载窗口（进程权限低于资源管理器，或桌面层不可用）。" +
                             "请从开始菜单/资源管理器正常启动后再试。";
                return LayerDiagnosis.CreateFailed;
            }

            if (sampleCount <= 0)
            {
                code = "NO_SAMPLES";
                conclusion = "挂载窗口能创建，但采集不到桌面像素（桌面被其他窗口完全遮挡，或截屏被拒绝），" +
                             "本次无法判定。请让桌面露出后重新运行桌面层体检。";
                return LayerDiagnosis.NoSamples;
            }

            var anyWpfLayered = false;
            var anyWpf = false;
            var anyGdiLayered = false;
            var anyGdi = false;
            var anyLayeredSoftware = false;
            string wpfLayeredId = null;
            string wpfId = null;
            string gdiLayeredId = null;
            string gdiId = null;
            string softwareId = null;

            foreach (var item in results)
            {
                if (item.DiagnosticOnly)
                {
                    continue;
                }
                if (item.WpfLayered == ProbeVerdict.Visible)
                {
                    anyWpfLayered = true;
                    if (wpfLayeredId == null) wpfLayeredId = item.Candidate.Id;
                }
                if (item.Wpf == ProbeVerdict.Visible)
                {
                    anyWpf = true;
                    if (wpfId == null) wpfId = item.Candidate.Id;
                }
                if (item.GdiLayered == ProbeVerdict.Visible)
                {
                    anyGdiLayered = true;
                    if (gdiLayeredId == null) gdiLayeredId = item.Candidate.Id;
                }
                if (item.Gdi == ProbeVerdict.Visible)
                {
                    anyGdi = true;
                    if (gdiId == null) gdiId = item.Candidate.Id;
                }
                if (item.WpfLayeredSoftware == ProbeVerdict.Visible)
                {
                    anyLayeredSoftware = true;
                    if (softwareId == null) softwareId = item.Candidate.Id;
                }
            }

            if (anyWpfLayered)
            {
                code = "LAYERED_OK";
                conclusion = "分层窗口 + 候选 " + wpfLayeredId + " 的 WPF 表面像素可见，壁纸层挂载成功。";
                return LayerDiagnosis.LayeredOk;
            }

            if (anyWpf)
            {
                code = "VERIFIED";
                conclusion = "候选 " + wpfId + " 的非分层 WPF 表面像素可见，壁纸层挂载成功（旧系统路径）。";
                return LayerDiagnosis.Verified;
            }

            if (anyGdiLayered)
            {
                if (anyLayeredSoftware)
                {
                    code = "WPF_LAYERED_SOFTWARE_OK";
                    conclusion = "分层窗口修复了层级问题：GDI 分层窗口在候选 " + gdiLayeredId + " 可见，" +
                                 "WPF 硬件渲染不可见但软件渲染在候选 " + softwareId + " 可见。" +
                                 "下一版改用软件渲染的分层宿主。";
                    return LayerDiagnosis.WpfLayeredSoftwareOnly;
                }

                code = "WPF_LAYERED_BLOCKED";
                conclusion = "分层窗口修复了层级问题：GDI 分层窗口在候选 " + gdiLayeredId + " 可见，" +
                             "但 WPF 绘制表面（硬件和软件渲染）仍未被合成。" +
                             "下一版改用原生 WinForms/GDI 分层宿主承载壁纸，不再用 WPF 当壁纸窗口。";
                return LayerDiagnosis.WpfLayeredBlocked;
            }

            if (anyGdi)
            {
                code = "WPF_BLOCKED";
                conclusion = "非分层 GDI 子窗口在候选 " + gdiId + " 可见，但 WPF 表面不可见；" +
                             "分层窗口不可见。请把本报告发给开发者。";
                return LayerDiagnosis.WpfBlockedLegacy;
            }

            code = "LAYERED_STILL_WRONG";
            if (topMostLayeredVisible)
            {
                conclusion = "分层窗口仍然不行：Progman 最上层的分层 GDI 窗口可见（子窗口能显示），" +
                             "但所有候选位置都被壁纸/图标层盖住，说明不存在“壁纸之上、图标之下”的位置。" +
                             "需要换挂载思路。";
            }
            else if (topMostPlainVisible)
            {
                conclusion = "分层窗口仍然不行：只有非分层的 Progman 最上层 GDI 可见，" +
                             "说明分层子窗口在这台机器上的合成行为与预期不同。请把本报告发给开发者。";
            }
            else
            {
                conclusion = "分层窗口仍然不行：连 Progman 最上层的分层 GDI 窗口都不可见，" +
                             "说明这个桌面不承载第三方子窗口（可能完全由 DirectComposition 绘制）。" +
                             "需要换挂载思路，而不是继续换渲染器。";
            }
            return LayerDiagnosis.LayeredStillWrong;
        }

        public static string FormatLines(IList<DesktopLayerMeasurement> results)
        {
            var text = new StringBuilder();
            foreach (var item in results)
            {
                text.Append("  ").Append(item.Candidate.Id)
                    .Append(" parent=").Append(Hex(item.Parent))
                    .Append(" created=").Append(item.Created ? "1" : "0");

                if (item.GdiLayered != ProbeVerdict.Unverifiable)
                {
                    text.Append(" GDI分层=").Append(VisibilityProbe.VerdictText(item.GdiLayered));
                }
                if (item.WpfLayered != ProbeVerdict.Unverifiable)
                {
                    text.Append(" WPF分层=").Append(VisibilityProbe.VerdictText(item.WpfLayered));
                }
                if (item.WpfLayeredSoftware != ProbeVerdict.Unverifiable)
                {
                    text.Append(" WPF分层软件=").Append(VisibilityProbe.VerdictText(item.WpfLayeredSoftware));
                }
                if (item.Gdi != ProbeVerdict.Unverifiable)
                {
                    text.Append(" GDI普通=").Append(VisibilityProbe.VerdictText(item.Gdi));
                }
                if (item.Wpf != ProbeVerdict.Unverifiable)
                {
                    text.Append(" WPF普通=").Append(VisibilityProbe.VerdictText(item.Wpf));
                }
                if (item.DiagnosticOnly)
                {
                    text.Append(" [diagnostic only]");
                }
                if (!item.Created)
                {
                    text.Append(" detail=").Append(item.CreateDetail);
                }
                text.AppendLine();
            }
            return text.ToString().TrimEnd();
        }

        public static string FormatDetails(IList<DesktopLayerMeasurement> results)
        {
            var text = new StringBuilder();
            foreach (var item in results)
            {
                text.Append("  [").Append(item.Candidate.Id).Append("]").AppendLine();
                text.Append("    create：").Append(item.CreateDetail).AppendLine();
                if (item.GdiLayered != ProbeVerdict.Unverifiable)
                {
                    text.Append("    GDI 分层：").Append(VisibilityProbe.VerdictText(item.GdiLayered))
                        .Append(" ").Append(item.GdiLayeredDetail).AppendLine();
                }
                if (item.WpfLayered != ProbeVerdict.Unverifiable)
                {
                    text.Append("    WPF 分层：").Append(VisibilityProbe.VerdictText(item.WpfLayered))
                        .Append(" ").Append(item.WpfLayeredDetail).AppendLine();
                }
                if (item.WpfLayeredSoftware != ProbeVerdict.Unverifiable)
                {
                    text.Append("    WPF 分层(软件)：").Append(VisibilityProbe.VerdictText(item.WpfLayeredSoftware))
                        .Append(" ").Append(item.WpfLayeredSoftwareDetail).AppendLine();
                }
                if (item.Gdi != ProbeVerdict.Unverifiable)
                {
                    text.Append("    GDI 普通：").Append(VisibilityProbe.VerdictText(item.Gdi))
                        .Append(" ").Append(item.GdiDetail).AppendLine();
                }
                if (item.Wpf != ProbeVerdict.Unverifiable)
                {
                    text.Append("    WPF 普通：").Append(VisibilityProbe.VerdictText(item.Wpf))
                        .Append(" ").Append(item.WpfDetail).AppendLine();
                }
            }
            return text.ToString().TrimEnd();
        }

        private static string Hex(IntPtr value)
        {
            return value == IntPtr.Zero ? "0x0" : "0x" + value.ToInt64().ToString("X");
        }
    }

    /// <summary>
    /// Throw-away WPF child window used for measurement only. Optionally layered (the
    /// Windows 11 24H2+ requirement) and/or forced into WPF software rendering.
    /// </summary>
    internal sealed class LayerProbeSurface : IDisposable
    {
        private HwndSource _source;
        private RenderMode _previousRenderMode;
        private bool _renderModeChanged;

        public IntPtr Handle
        {
            get { return _source == null ? IntPtr.Zero : _source.Handle; }
        }

        public Canvas Layer { get; private set; }

        public bool Create(
            IntPtr parent,
            IntPtr above,
            bool software,
            bool layered,
            out string detail)
        {
            detail = "";
            try
            {
                if (software)
                {
                    _previousRenderMode = RenderOptions.ProcessRenderMode;
                    RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
                    _renderModeChanged = true;
                }

                int width;
                int height;
                DesktopLayer.GetLayerSize(parent, out width, out height);

                Layer = new Canvas
                {
                    Visibility = Visibility.Collapsed,
                    IsHitTestVisible = false
                };
                var root = new Grid { Background = Brushes.Black };
                root.Children.Add(Layer);

                var exStyle = NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
                if (layered)
                {
                    exStyle |= NativeMethods.WS_EX_LAYERED;
                }

                var parameters = new HwndSourceParameters("WhalePaperLayerProbe")
                {
                    ParentWindow = parent,
                    WindowStyle = NativeMethods.WS_CHILD |
                                  NativeMethods.WS_VISIBLE |
                                  NativeMethods.WS_CLIPSIBLINGS,
                    ExtendedWindowStyle = exStyle,
                    PositionX = 0,
                    PositionY = 0,
                    Width = width,
                    Height = height
                };

                _source = new HwndSource(parameters);
                _source.RootVisual = root;

                if (layered && !GdiProbeWindow.ApplyLayered(_source.Handle))
                {
                    detail = "SetLayeredWindowAttributes failed error=" +
                             System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                    return false;
                }

                IntPtr below;
                string placeDetail;
                var placed = DesktopLayer.Place(
                    _source.Handle, parent, above, out below, out placeDetail);
                var prev = NativeMethods.GetWindow(_source.Handle, NativeMethods.GW_HWNDPREV);
                detail = "layered=" + (layered ? "1" : "0") +
                         " software=" + (software ? "1" : "0") +
                         " ok=" + (placed ? "1" : "0") +
                         " hwnd=0x" + _source.Handle.ToInt64().ToString("X") +
                         " parent=0x" + NativeMethods.GetParent(_source.Handle).ToInt64().ToString("X") +
                         " prev=" + (prev == IntPtr.Zero ? "0x0" : "0x" + prev.ToInt64().ToString("X")) +
                         " " + placeDetail;
                return placed;
            }
            catch (Exception ex)
            {
                detail = "创建失败：" + ex.Message;
                if (ex is System.ComponentModel.Win32Exception)
                {
                    detail += "（当前进程权限低于资源管理器，无法挂载）";
                }
                Dispose();
                return false;
            }
        }

        public void Dispose()
        {
            try
            {
                if (_source != null)
                {
                    try
                    {
                        _source.RootVisual = null;
                    }
                    catch
                    {
                    }
                    _source.Dispose();
                }
            }
            catch
            {
            }
            finally
            {
                _source = null;
                RestoreRenderMode();
            }
        }

        private void RestoreRenderMode()
        {
            if (!_renderModeChanged)
            {
                return;
            }

            _renderModeChanged = false;
            try
            {
                RenderOptions.ProcessRenderMode = _previousRenderMode;
            }
            catch
            {
            }
        }
    }
}
