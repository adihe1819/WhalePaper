using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using WhalePaper.Interop;
using WhalePaper.Models;
using WhalePaper.Services;
using WhalePaper.ViewModels;

namespace WhalePaper
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;
        private bool _allowClose;

        public MainWindow(MainViewModel viewModel)
        {
            _viewModel = viewModel;
            InitializeComponent();
            Title = "鲸鱼壁纸 · 诊断版 " + DiagnosticsProbe.BuildTag;
            DataContext = viewModel;
            StateChanged += MainWindow_StateChanged;
            Loaded += MainWindow_Loaded;
        }

        public MainViewModel ViewModel
        {
            get { return _viewModel; }
        }

        public void ShowFromTray()
        {
            if (!IsVisible)
            {
                Show();
            }
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }
            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
        }

        public void HideToTray()
        {
            Hide();
        }

        public void AllowClose()
        {
            _allowClose = true;
            Close();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var handle = new WindowInteropHelper(this).Handle;
            var source = HwndSource.FromHwnd(handle);
            if (source != null)
            {
                source.AddHook(WndProc);
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == NativeMethods.WM_GETMINMAXINFO)
            {
                var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
                var info = new NativeMethods.MONITORINFO
                {
                    cbSize = Marshal.SizeOf(typeof(NativeMethods.MONITORINFO))
                };
                if (monitor != IntPtr.Zero && NativeMethods.GetMonitorInfo(monitor, ref info))
                {
                    var mmi = (NativeMethods.MINMAXINFO)Marshal.PtrToStructure(
                        lParam,
                        typeof(NativeMethods.MINMAXINFO));
                    mmi.ptMaxPosition.X = info.rcWork.Left - info.rcMonitor.Left;
                    mmi.ptMaxPosition.Y = info.rcWork.Top - info.rcMonitor.Top;
                    mmi.ptMaxSize.X = info.rcWork.Right - info.rcWork.Left;
                    mmi.ptMaxSize.Y = info.rcWork.Bottom - info.rcWork.Top;
                    mmi.ptMaxTrackSize.X = Math.Max(mmi.ptMaxTrackSize.X, mmi.ptMaxSize.X);
                    mmi.ptMaxTrackSize.Y = Math.Max(mmi.ptMaxTrackSize.Y, mmi.ptMaxSize.Y);
                    Marshal.StructureToPtr(mmi, lParam, true);
                    handled = true;
                }
            }
            return IntPtr.Zero;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Ensure the first page selection matches the sidebar after bindings resolve.
            if (Pages != null && NavList != null)
            {
                NavList.SelectedIndex = Pages.SelectedIndex;
            }
        }

        private void TopBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (FindAncestor<Button>(e.OriginalSource as DependencyObject) != null)
            {
                return;
            }

            if (e.ClickCount == 2)
            {
                ToggleMaximize();
                return;
            }

            try
            {
                DragMove();
            }
            catch
            {
            }
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleMaximize();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Hide();
            App.CurrentApp?.NotifyMinimizedToTray();
        }

        private void ToggleMaximize()
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }

        private void MainWindow_StateChanged(object sender, EventArgs e)
        {
            if (MaximizeButton != null)
            {
                MaximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
            }
            if (WindowBorder != null)
            {
                WindowBorder.BorderThickness = WindowState == WindowState.Maximized
                    ? new Thickness(0)
                    : new Thickness(1);
            }
        }

        private async void ImportButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择壁纸文件",
                Filter = LibraryService.BuildFileDialogFilter(),
                Multiselect = true,
                CheckFileExists = true
            };

            if (dialog.ShowDialog(this) == true)
            {
                await _viewModel.ImportFilesAsync(dialog.FileNames);
            }
        }

        private void OpenLogButton_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel.OpenLogFolderCommand.CanExecute(null))
            {
                _viewModel.OpenLogFolderCommand.Execute(null);
            }
        }

        private void RunDiagnosticsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var report = DiagnosticsService.Run();
                MessageBox.Show(
                    report,
                    "鲸鱼壁纸环境检测",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AppLog.Error("Diagnostics failed", ex);
                MessageBox.Show(
                    "环境检测失败：" + ex.Message,
                    "鲸鱼壁纸",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private void RunDesktopProbeButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                DesktopProbeService.Run();
                Mouse.OverrideCursor = null;
                MessageBox.Show(
                    (DesktopProbeService.Conclusion ?? "桌面层体检完成。") +
                    "\n\n报告文件：\n" + DesktopProbeService.ReportPath,
                    "鲸鱼壁纸 · 桌面层体检",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AppLog.Error("Desktop probe failed", ex);
                MessageBox.Show(
                    "桌面层体检失败：" + ex.Message,
                    "鲸鱼壁纸",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = GetSupportedDroppedFiles(e).Count > 0
                ? DragDropEffects.Copy
                : DragDropEffects.None;
            e.Handled = true;
        }

        private async void Window_Drop(object sender, DragEventArgs e)
        {
            var files = GetSupportedDroppedFiles(e);
            if (files.Count > 0)
            {
                await _viewModel.ImportFilesAsync(files);
            }
        }

        private static List<string> GetSupportedDroppedFiles(DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                return new List<string>();
            }

            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null)
            {
                return new List<string>();
            }

            return files
                .Where(File.Exists)
                .Where(path => LibraryService.IsSupportedExtension(Path.GetExtension(path)))
                .ToList();
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_allowClose)
            {
                e.Cancel = true;
                Hide();
                App.CurrentApp?.NotifyMinimizedToTray();
            }
        }

        private static T FindAncestor<T>(DependencyObject current) where T : DependencyObject
        {
            while (current != null)
            {
                var match = current as T;
                if (match != null)
                {
                    return match;
                }
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }
    }
}
