using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using WhalePaper.ViewModels;

namespace WhalePaper.Services
{
    internal sealed class TrayService : IDisposable
    {
        private readonly MainWindow _window;
        private readonly MainViewModel _viewModel;
        private readonly NotifyIcon _notifyIcon;
        private readonly ToolStripMenuItem _pauseItem;
        private bool _balloonShown;
        private bool _disposed;

        public TrayService(MainWindow window, MainViewModel viewModel)
        {
            _window = window;
            _viewModel = viewModel;

            Icon icon;
            try
            {
                icon = Icon.ExtractAssociatedIcon(StartupService.ExecutablePath);
            }
            catch
            {
                icon = SystemIcons.Application;
            }

            var menu = new ContextMenuStrip();
            var openItem = new ToolStripMenuItem("打开鲸鱼壁纸");
            openItem.Click += (s, e) => ShowWindow();
            _pauseItem = new ToolStripMenuItem("暂停动态壁纸");
            _pauseItem.Click += (s, e) =>
            {
                _viewModel.TogglePauseFromTray();
                UpdateMenu();
            };
            var exitItem = new ToolStripMenuItem("退出");
            exitItem.Click += (s, e) => App.CurrentApp.ExitApplication();
            menu.Items.Add(openItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_pauseItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);

            _notifyIcon = new NotifyIcon
            {
                Icon = icon,
                Text = "鲸鱼壁纸",
                Visible = true,
                ContextMenuStrip = menu
            };
            _notifyIcon.DoubleClick += (s, e) => ShowWindow();
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
            UpdateMenu();
        }

        public void ShowBalloonOnce()
        {
            if (_balloonShown || _notifyIcon == null)
            {
                return;
            }
            _balloonShown = true;
            try
            {
                _notifyIcon.ShowBalloonTip(
                    2200,
                    "鲸鱼壁纸仍在运行",
                    "壁纸会继续留在桌面。双击托盘图标可以重新打开窗口。",
                    ToolTipIcon.Info);
            }
            catch
            {
            }
        }

        public void ShowWindow()
        {
            _window.ShowFromTray();
        }

        public void UpdateMenu()
        {
            if (_pauseItem == null)
            {
                return;
            }
            _pauseItem.Enabled = _viewModel.IsDynamicActive;
            _pauseItem.Text = _viewModel.IsPaused ? "继续动态壁纸" : "暂停动态壁纸";
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
            }
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "IsPaused" || e.PropertyName == "IsDynamicActive")
            {
                UpdateMenu();
            }
        }
    }
}
