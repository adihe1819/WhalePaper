using System;
using System.IO;
using System.Windows;
using WhalePaper.Services;

namespace WhalePaper
{
    internal static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            try
            {
                DiagnosticsProbe.Bootstrap(
                    "Main start user=" + Environment.UserName +
                    " localAppData=" + Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) +
                    " temp=" + Path.GetTempPath() +
                    " args=" + string.Join(" ", args));
                AppPaths.Initialize(args);
                DiagnosticsProbe.Bootstrap("AppPaths data=" + AppPaths.DataRoot);
                App.StartupArgs = args;
                DiagnosticsProbe.LogBuild(args);

                if (Array.Exists(args, arg => string.Equals(arg, "--self-test", StringComparison.OrdinalIgnoreCase)))
                {
                    DiagnosticsService.Run();
                    return;
                }

                if (Array.Exists(args, arg => string.Equals(arg, "--media-probe", StringComparison.OrdinalIgnoreCase)))
                {
                    var index = Array.FindIndex(
                        args, arg => string.Equals(arg, "--media-probe", StringComparison.OrdinalIgnoreCase));
                    string mediaPath = null;
                    if (index >= 0 && index + 1 < args.Length &&
                        !args[index + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        mediaPath = args[index + 1];
                    }

                    var probeReport = MediaProbeService.Run(mediaPath);
                    Environment.ExitCode = MediaProbeService.Succeeded ? 0 : 1;
                    AppLog.Info("==== Media probe report ====" + Environment.NewLine + probeReport);
                    var quietProbe = Array.Exists(
                        args, arg => string.Equals(arg, "--quiet", StringComparison.OrdinalIgnoreCase));
                    if (!quietProbe)
                    {
                        MessageBox.Show(
                            probeReport + Environment.NewLine + Environment.NewLine +
                            "报告文件：" + MediaProbeService.ReportPath,
                            "鲸鱼壁纸 · 媒体探测",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                    }
                    return;
                }

                if (Array.Exists(args, arg => string.Equals(arg, "--desktop-probe", StringComparison.OrdinalIgnoreCase)))
                {
                    var probeReport = DesktopProbeService.Run();
                    AppLog.Info("==== Desktop probe report ====" + Environment.NewLine + probeReport);
                    var quiet = Array.Exists(args, arg => string.Equals(arg, "--quiet", StringComparison.OrdinalIgnoreCase));
                    if (!quiet && !string.IsNullOrEmpty(DesktopProbeService.Conclusion))
                    {
                        MessageBox.Show(
                            "桌面层体检完成。\n\n" + DesktopProbeService.Conclusion +
                            "\n\n报告文件：\n" + DesktopProbeService.ReportPath,
                            "鲸鱼壁纸 · 桌面层体检",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                    }
                    return;
                }

                if (Array.Exists(args, arg => string.Equals(arg, "--restore-wallpaper", StringComparison.OrdinalIgnoreCase)))
                {
                    var settings = SettingsService.Load();
                    if (settings.HasSavedOriginal)
                    {
                        new DesktopWallpaperService().RestoreOriginal(settings);
                    }
                    return;
                }

                if (Array.Exists(args, arg => string.Equals(arg, "--shutdown", StringComparison.OrdinalIgnoreCase)))
                {
                    SingleInstance.RequestExit();
                    return;
                }

                if (!SingleInstance.TryAcquire())
                {
                    return;
                }

                var app = new App();
                app.Run();
            }
            catch (Exception ex)
            {
                try
                {
                    DiagnosticsProbe.Bootstrap("Fatal startup: " + ex);
                    AppLog.Error("Fatal startup error", ex);
                    MessageBox.Show(
                        "鲸鱼壁纸启动失败：\n\n" + ex.Message,
                        "鲸鱼壁纸",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
                catch
                {
                }
            }
            finally
            {
                SingleInstance.Shutdown();
            }
        }
    }
}
