using System;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using WhalePaper.Services;
using WhalePaper.ViewModels;

namespace WhalePaper
{
    public partial class App : Application
    {
        private TrayService _tray;
        private MainViewModel _viewModel;
        private MainWindow _mainWindow;
        private bool _isExiting;

        public App()
        {
            AppLog.Info("App constructor");
            CurrentApp = this;
            DispatcherUnhandledException += App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            SessionEnding += App_SessionEnding;
        }

        public static App CurrentApp { get; private set; }

        public static string[] StartupArgs { get; set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            AppLog.Info("OnStartup");

            try
            {
                AssetService.EnsureDefaultWallpaper();
                _viewModel = new MainViewModel();
                _mainWindow = new MainWindow(_viewModel);
                MainWindow = _mainWindow;
                _tray = new TrayService(_mainWindow, _viewModel);
                SingleInstance.Activated += SingleInstance_Activated;
                SingleInstance.ExitRequested += SingleInstance_ExitRequested;

                var startMinimized = (StartupArgs ?? new string[0])
                    .Any(arg => string.Equals(arg, "--minimized", StringComparison.OrdinalIgnoreCase));

                PrepareWindow();

                if (!startMinimized)
                {
                    _mainWindow.Show();
                    if (_viewModel.Settings.WindowMaximized)
                    {
                        _mainWindow.WindowState = WindowState.Maximized;
                    }
                    _mainWindow.Activate();
                    AppLog.Info("Window shown at " + _mainWindow.Left + "," + _mainWindow.Top +
                                " size " + _mainWindow.ActualWidth + "x" + _mainWindow.ActualHeight +
                                " state=" + _mainWindow.WindowState);
                }

                _viewModel.Initialize();

                if (startMinimized)
                {
                    _mainWindow.Hide();
                }

                RunDevelopmentImportHook();
                AppLog.Info("Startup complete. minimized=" + startMinimized);
            }
            catch (Exception ex)
            {
                AppLog.Error("Startup failed", ex);
                MessageBox.Show(
                    "鲸鱼壁纸启动失败：\n\n" + ex.Message,
                    "鲸鱼壁纸",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown(1);
            }
        }

        private void RunDevelopmentImportHook()
        {
            var spec = Environment.GetEnvironmentVariable("WHALEPAPER_IMPORT");
            if (string.IsNullOrWhiteSpace(spec))
            {
                return;
            }

            var files = spec.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(path => path.Trim().Trim('"'))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToArray();
            if (files.Length == 0)
            {
                return;
            }

            AppLog.Info("Development import hook: " + files.Length + " file(s)");
            Dispatcher.InvokeAsync(async () =>
            {
                await _viewModel.ImportFilesAsync(files);
                var autoPlay = Environment.GetEnvironmentVariable("WHALEPAPER_AUTOPLAY");
                AppLog.Info("Development auto play value: " + (autoPlay ?? "<null>"));
                if (!string.Equals(autoPlay, "0", StringComparison.Ordinal))
                {
                    AppLog.Info("Development auto play selected: " +
                                (_viewModel.SelectedItem == null ? "<null>" : _viewModel.SelectedItem.DisplayName));
                    _viewModel.ApplySelectedWallpaper();
                }
            });
        }

        private void PrepareWindow()
        {
            // SystemParameters.WorkArea is not DPI-consistent across Windows builds.
            // Use the primary monitor's physical work area and convert to WPF units.
            var handle = new System.Windows.Interop.WindowInteropHelper(_mainWindow).EnsureHandle();
            var dpi = WhalePaper.Interop.NativeMethods.GetDpiForWindow(handle);
            var scale = dpi > 0 ? dpi / 96.0 : 1.0;
            var work = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
            var workWidth = work.Width / scale;
            var workHeight = work.Height / scale;
            var workLeft = work.Left / scale;
            var workTop = work.Top / scale;

            var minWidth = Math.Min(_mainWindow.MinWidth, Math.Max(800, workWidth - 16));
            var minHeight = Math.Min(_mainWindow.MinHeight, Math.Max(520, workHeight - 16));
            _mainWindow.MinWidth = minWidth;
            _mainWindow.MinHeight = minHeight;

            var width = Math.Min(Math.Max(_viewModel.Settings.WindowWidth, minWidth), Math.Max(minWidth, workWidth - 24));
            var height = Math.Min(Math.Max(_viewModel.Settings.WindowHeight, minHeight), Math.Max(minHeight, workHeight - 24));
            _mainWindow.WindowStartupLocation = WindowStartupLocation.Manual;
            _mainWindow.Width = width;
            _mainWindow.Height = height;
            _mainWindow.Left = workLeft + (workWidth - width) / 2;
            _mainWindow.Top = workTop + (workHeight - height) / 2;
            _mainWindow.WindowState = WindowState.Normal;
            AppLog.Info("DPI=" + dpi + " scale=" + scale + " physicalWork=" + work.Width + "x" + work.Height +
                        " layoutWork=" + workWidth.ToString("0") + "x" + workHeight.ToString("0") +
                        " window=" + width.ToString("0") + "x" + height.ToString("0"));
        }

        public void NotifyMinimizedToTray()
        {
            _tray?.ShowBalloonOnce();
        }

        public void ExitApplication()
        {
            if (_isExiting)
            {
                return;
            }
            _isExiting = true;
            try
            {
                _viewModel?.Shutdown();
                _tray?.Dispose();
                SingleInstance.Activated -= SingleInstance_Activated;
                SingleInstance.ExitRequested -= SingleInstance_ExitRequested;
                _mainWindow?.AllowClose();
            }
            catch (Exception ex)
            {
                AppLog.Error("Exit failed", ex);
            }
            finally
            {
                Shutdown();
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                _viewModel?.Shutdown();
                _tray?.Dispose();
                SingleInstance.Activated -= SingleInstance_Activated;
                SingleInstance.ExitRequested -= SingleInstance_ExitRequested;
            }
            catch (Exception ex)
            {
                AppLog.Error("OnExit cleanup failed", ex);
            }
            base.OnExit(e);
        }

        private void SingleInstance_ExitRequested()
        {
            Dispatcher.BeginInvoke(new Action(ExitApplication));
        }

        private void SingleInstance_Activated()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_mainWindow == null)
                {
                    return;
                }
                _mainWindow.ShowFromTray();
                _mainWindow.WindowState = WindowState.Normal;
                _mainWindow.Activate();
            }));
        }

        private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            AppLog.Error("Dispatcher unhandled exception", e.Exception);
            e.Handled = true;
            MessageBox.Show(
                "程序遇到一个错误，但壁纸不会立刻消失。\n\n" + e.Exception.Message,
                "鲸鱼壁纸",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            AppLog.Error("AppDomain unhandled exception", e.ExceptionObject as Exception);
        }

        private void App_SessionEnding(object sender, SessionEndingCancelEventArgs e)
        {
            try
            {
                _viewModel?.Shutdown();
                _tray?.Dispose();
            }
            catch
            {
            }
        }
    }
}
