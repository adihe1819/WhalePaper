using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace WhalePaper.Services
{
    /// <summary>
    /// Desktop layer health check used by --desktop-probe and by the "桌面层体检" button.
    /// It builds the same candidate list the wallpaper host uses and measures every
    /// candidate with a native GDI child window and a WPF child window (hardware, and
    /// software when that can change the verdict), then prints one explicit diagnosis.
    /// </summary>
    internal static class DesktopProbeService
    {
        public static string ReportPath { get; private set; }

        public static string Conclusion { get; private set; }

        public static string Run()
        {
            var report = new StringBuilder();
            report.AppendLine("鲸鱼壁纸 · 桌面层体检（" + DiagnosticsProbe.BuildTag + "）");
            report.AppendLine("时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            report.AppendLine("------------------------------------------------------------");

            var progman = DesktopLayer.FindProgman();
            IntPtr defViewParent;
            var defView = DesktopLayer.FindDefView(out defViewParent);
            var screen = System.Windows.Forms.Screen.PrimaryScreen;

            report.AppendLine("[环境]");
            report.AppendLine("  OS：" + Environment.OSVersion.VersionString +
                               "，64-bit=" + Environment.Is64BitOperatingSystem);
            report.AppendLine("  进程：" + (Environment.Is64BitProcess ? "64-bit" : "32-bit") +
                               "，CLR：" + Environment.Version);
            report.AppendLine("  程序：" + StartupService.ExecutablePath);
            report.AppendLine("  屏幕：" + screen.Bounds.Width + "x" + screen.Bounds.Height +
                               "，工作区：" + screen.WorkingArea.Width + "x" + screen.WorkingArea.Height);
            report.AppendLine("  会话：" + System.Diagnostics.Process.GetCurrentProcess().SessionId +
                               "，交互桌面：" + Environment.UserInteractive);
            System.Drawing.Color screenSample;
            var captureWorks = VisibilityProbe.TryCaptureColor(
                new System.Drawing.Point(screen.Bounds.Left + 100, screen.Bounds.Top + 100),
                out screenSample);
            report.AppendLine("  屏幕取色(100,100)：" + (captureWorks ? VisibilityProbe.Hex(screenSample) : "失败"));
            report.AppendLine("  Progman：" + (progman == IntPtr.Zero ? "未找到" : "0x" + progman.ToInt64().ToString("X")));
            report.AppendLine("  SHELLDLL_DefView：" + (defView == IntPtr.Zero ? "未找到" : "0x" + defView.ToInt64().ToString("X")));
            report.AppendLine("  DefView 父窗口：" + (defViewParent == IntPtr.Zero ? "无" : "0x" + defViewParent.ToInt64().ToString("X")));
            report.AppendLine();
            report.AppendLine("提示：体检会短暂显示测试色块；主窗口会被临时隐藏以便采样。");
            report.AppendLine("      1.0.6 只测量原生分层 DIB 宿主（与动态壁纸实际绘制路径一致）。");
            report.AppendLine();

            report.AppendLine("[桌面窗口树]");
            report.AppendLine(DesktopLayer.DescribeDesktopTree());
            report.AppendLine();

            var suspended = false;
            var points = VisibilityProbe.SelectSamplePoints(progman, 5);
            if (points.Count < 2)
            {
                suspended = AppWindowVisibility.Suspend();
                if (suspended)
                {
                    VisibilityProbe.Pump(300);
                    points = VisibilityProbe.SelectSamplePoints(progman, 5);
                }
            }

            try
            {
                report.AppendLine("[采样点]");
                report.AppendLine("  可用点：" + points.Count + "（" + VisibilityProbe.DescribePoints(points) + "）");
                report.AppendLine("  主窗口已临时隐藏：" + (suspended ? "是" : "否"));
                report.AppendLine("  说明：采样点只落在桌面图标层上；被普通窗口遮挡的位置会被跳过。");
                report.AppendLine("  候选点明细：");
                report.AppendLine(VisibilityProbe.DescribeSampleCandidates(progman));
                report.AppendLine();

                var candidates = DesktopLayer.ResolveHostCandidates();
                report.AppendLine("[候选挂载点]");
                foreach (var candidate in candidates)
                {
                    report.AppendLine("  " + candidate.Id + "：" + candidate.Title +
                                      "（" + candidate.Describe() + "）");
                }
                report.AppendLine();

                report.AppendLine("[逐项测量]");
                var results = NativeLayerProbe.Measure(candidates, points);
                report.AppendLine(NativeLayerProbe.FormatLines(results));
                report.AppendLine("[明细]");
                report.AppendLine(NativeLayerProbe.FormatDetails(results));
                report.AppendLine();

                string code;
                string conclusion;
                NativeLayerProbe.Diagnose(results, points.Count, out code, out conclusion);
                Conclusion = conclusion;

                report.AppendLine("[结论]");
                report.AppendLine("  结论代码：" + code);
                report.AppendLine("  " + conclusion);
                report.AppendLine();

                AppLog.Info("Desktop probe diagnosis: code=" + code + " " + conclusion);
            }
            finally
            {
                if (suspended)
                {
                    AppWindowVisibility.Resume();
                }
            }

            var path = Path.Combine(AppPaths.LogDir, "desktop-probe.txt");
            ReportPath = path;
            try
            {
                Directory.CreateDirectory(AppPaths.LogDir);
                File.WriteAllText(path, report.ToString(), new UTF8Encoding(true));
            }
            catch (Exception ex)
            {
                AppLog.Warn("Write desktop-probe report failed: " + ex.Message);
            }

            AppLog.Info("Desktop probe report written: " + path);
            report.AppendLine("报告文件：" + path);
            return report.ToString();
        }
    }
}
