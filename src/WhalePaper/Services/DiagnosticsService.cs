using System;
using System.IO;
using System.Text;
using Microsoft.Win32;
using WhalePaper.Interop;

namespace WhalePaper.Services
{
    internal static class DiagnosticsService
    {
        public static string Run()
        {
            var report = new StringBuilder();
            report.AppendLine("鲸鱼壁纸环境自检");
            report.AppendLine("时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            report.AppendLine("------------------------------------------------------------");

            report.AppendLine("[系统]");
            report.AppendLine("  OS：" + Environment.OSVersion.VersionString + "，" +
                               (Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit"));
            report.AppendLine("  进程：" + (Environment.Is64BitProcess ? "64-bit" : "32-bit") +
                               "，CLR：" + Environment.Version);
            report.AppendLine("  程序：" + StartupService.ExecutablePath);
            report.AppendLine("  构建：1.0.8（解码预取 + 连续循环时钟 + 高精度调度）");
            report.AppendLine("  数据目录：" + AppPaths.DataRoot);
            report.AppendLine();

            report.AppendLine("[数据目录可写]");
            var dataWritable = TestDataDirectory();
            report.AppendLine("  " + (dataWritable ? "[OK] 可写" : "[FAIL] 不可写"));
            report.AppendLine();

            report.AppendLine("[用户注册表]");
            var registryWritable = TestRegistry();
            report.AppendLine("  " + (registryWritable ? "[OK] HKCU 可写" : "[FAIL] HKCU 不可写（开机启动需要）"));
            report.AppendLine();

            report.AppendLine("[桌面壁纸 COM]");
            var desktop = new DesktopWallpaperService();
            string currentPath;
            int currentPosition;
            string desktopError;
            if (desktop.TryReadCurrent(out currentPath, out currentPosition, out desktopError))
            {
                report.AppendLine("  [OK] IDesktopWallpaper 可访问");
                report.AppendLine("  当前壁纸：" + (string.IsNullOrWhiteSpace(currentPath) ? "<空>" : currentPath));
                report.AppendLine("  当前位置：" + currentPosition);
            }
            else
            {
                report.AppendLine("  [FAIL] " + desktopError);
                report.AppendLine("  这会导致静态壁纸写入失败；需要在普通用户桌面运行。");
            }
            report.AppendLine();

            report.AppendLine("[桌面 WorkerW]");
            var progman = NativeMethods.FindWindow("Progman", null);
            report.AppendLine("  Progman：" + (progman == IntPtr.Zero ? "[FAIL] 未找到" : "[OK] 0x" + progman.ToInt64().ToString("X")));
            IntPtr defViewParent;
            var defView = DesktopLayer.FindDefView(out defViewParent);
            report.AppendLine("  SHELLDLL_DefView：" + (defView == IntPtr.Zero
                ? "[FAIL] 未找到"
                : "[OK] 0x" + defView.ToInt64().ToString("X") +
                  "，父窗口=" + (defViewParent == IntPtr.Zero ? "无" : "0x" + defViewParent.ToInt64().ToString("X"))));
            var workerW = WorkerWService.FindExistingWorkerW();
            report.AppendLine("  WorkerW：" + (workerW == IntPtr.Zero
                ? "未找到（1.0.4 不再尝试 WorkerW 挂载）"
                : "已存在 0x" + workerW.ToInt64().ToString("X") + "（不用于挂载）"));
            report.AppendLine("  说明：新版桌面先启用透明图标层，原生分层壁纸窗口放在图标层与系统壁纸层之间；");
            report.AppendLine("        建窗后先用 DIB 双色像素自证，不能自证时保留静态海报，不覆盖桌面图标。");
            report.AppendLine();

            report.AppendLine("[桌面层候选]");
            foreach (var candidate in DesktopLayer.ResolveHostCandidates())
            {
                report.AppendLine("  - " + candidate.Id + "：" + candidate.Title);
            }
            report.AppendLine("  完整测量请运行：WhalePaper.exe --desktop-probe");
            report.AppendLine("  解码自证请运行：WhalePaper.exe --media-probe \"<视频或 GIF 路径>\"");
            report.AppendLine();

            report.AppendLine("[全屏判定诊断]");
            var fullscreen = FullscreenDetector.IsFullscreenActive();
            report.AppendLine("  当前判定：" + (fullscreen ? "全屏" : "非全屏"));
            report.AppendLine("  " + DiagnosticsProbe.DescribeFullscreen(fullscreen));
            report.AppendLine();

            report.AppendLine("[桌面 Z 序诊断]");
            report.AppendLine(DiagnosticsProbe.DescribeDesktopZOrder());
            report.AppendLine();

            report.AppendLine("[视频组件]");
            var mfplat = Path.Combine(Environment.SystemDirectory, "mfplat.dll");
            var mfreadwrite = Path.Combine(Environment.SystemDirectory, "mfreadwrite.dll");
            var wmp = Path.Combine(Environment.SystemDirectory, "wmp.dll");
            report.AppendLine("  mfplat.dll：" + (File.Exists(mfplat) ? "[OK]" : "[FAIL]"));
            report.AppendLine("  mfreadwrite.dll：" + (File.Exists(mfreadwrite) ? "[OK]" : "[FAIL]"));
            report.AppendLine("  wmp.dll：" + (File.Exists(wmp) ? "[OK]" : "[FAIL]"));
            var mfHr = MfInterop.MFStartup(MfInterop.MF_VERSION, MfInterop.MFSTARTUP_FULL);
            if (MfInterop.Succeeded(mfHr))
            {
                report.AppendLine("  MFStartup：[OK]");
                report.AppendLine("  " + TestMfInterop());
                report.AppendLine("  " + MfInteropSelfTest.Run());
                try
                {
                    MfInterop.MFShutdown();
                }
                catch
                {
                }
            }
            else
            {
                report.AppendLine("  MFStartup：[FAIL] " + MfInterop.HResultText(mfHr));
            }
            report.AppendLine("  H.264 由系统 Media Foundation 解码；HEVC 取决于系统是否安装对应的解码扩展。");
            report.AppendLine();

            report.AppendLine("[开机启动]");
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", false))
                {
                    var value = key == null ? null : key.GetValue("WhalePaper");
                    report.AppendLine("  " + (value == null ? "未启用" : "已启用：" + value));
                }
            }
            catch (Exception ex)
            {
                report.AppendLine("  [WARN] 无法读取：" + ex.Message);
            }
            report.AppendLine();

            report.AppendLine("[结论]");
            if (!dataWritable || !registryWritable || progman == IntPtr.Zero || defView == IntPtr.Zero)
            {
                report.AppendLine("  当前会话不满足完整桌面壁纸测试条件。");
                report.AppendLine("  请在正常登录的 Windows 桌面（不是远程低权限会话）中重新运行本检测。");
            }
            else
            {
                report.AppendLine("  基础环境正常；请继续应用一张静态图和一个视频做最终验收。");
            }

            var path = Path.Combine(AppPaths.LogDir, "self-test.txt");
            try
            {
                Directory.CreateDirectory(AppPaths.LogDir);
                File.WriteAllText(path, report.ToString(), new UTF8Encoding(true));
            }
            catch
            {
            }
            report.AppendLine();
            report.AppendLine("报告文件：" + path);
            AppLog.Info("Self-test completed");
            return report.ToString();
        }

        private static string TestMfInterop()
        {
            MfInterop.IMFMediaType mediaType = null;
            MfInterop.IMFAttributes attributes = null;
            try
            {
                var hr = MfInterop.MFCreateMediaType(out mediaType);
                if (MfInterop.Failed(hr) || mediaType == null)
                {
                    return "MFCreateMediaType：[FAIL] " + MfInterop.HResultText(hr);
                }

                var majorKey = MfInterop.MF_MT_MAJOR_TYPE;
                var videoValue = MfInterop.MFMediaType_Video;
                hr = mediaType.SetGUID(ref majorKey, ref videoValue);
                if (MfInterop.Failed(hr))
                {
                    return "SetGUID：[FAIL] " + MfInterop.HResultText(hr);
                }

                var readBack = Guid.Empty;
                hr = mediaType.GetGUID(ref majorKey, out readBack);
                if (MfInterop.Failed(hr) || readBack != videoValue)
                {
                    return "GetGUID：[FAIL] " + MfInterop.HResultText(hr);
                }

                hr = MfInterop.MFCreateAttributes(out attributes, 1);
                if (MfInterop.Failed(hr) || attributes == null)
                {
                    return "MFCreateAttributes：[FAIL] " + MfInterop.HResultText(hr);
                }

                var advancedKey = MfInterop.MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING;
                hr = attributes.SetUINT32(ref advancedKey, 1);
                var value = 0;
                var readHr = attributes.GetUINT32(ref advancedKey, out value);
                if (MfInterop.Failed(hr) || MfInterop.Failed(readHr) || value != 1)
                {
                    return "SetUINT32/GetUINT32：[FAIL] " + MfInterop.HResultText(hr);
                }

                return "COM interop（MFCreateMediaType/SetGUID/GetGUID/SetUINT32）：[OK]";
            }
            catch (Exception ex)
            {
                return "COM interop：[FAIL] " + ex.Message;
            }
            finally
            {
                MfSourceReader.ReleaseCom(mediaType);
                MfSourceReader.ReleaseCom(attributes);
            }
        }

        private static bool TestDataDirectory()        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataRoot);
                var probe = Path.Combine(AppPaths.DataRoot, ".write-test");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TestRegistry()
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(@"Software\WhalePaper\Diagnostics"))
                {
                    if (key == null)
                    {
                        return false;
                    }
                    key.SetValue("Probe", "ok", RegistryValueKind.String);
                    key.DeleteValue("Probe", false);
                }
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
