using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using WhalePaper.Models;
using WhalePaper.Services;

namespace WhalePaper.ViewModels
{
    public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly LibraryService _library;
        private readonly WallpaperController _controller;
        private readonly ObservableCollection<WallpaperItem> _items;
        private readonly AppSettings _settings;
        private ICollectionView _filteredItems;
        private WallpaperItem _selectedItem;
        private string _searchText;
        private string _statusText;
        private bool _isBusy;
        private string _busyText;
        private double _busyProgress;
        private bool _isPaused;
        private bool _isDynamicActive;
        private FitOption _selectedFitOption;
        private bool _disposed;

        public MainViewModel()
        {
            _settings = SettingsService.Load();
            _library = new LibraryService();
            _library.Load();

            _items = new ObservableCollection<WallpaperItem>(_library.Items);
            _filteredItems = CollectionViewSource.GetDefaultView(_items);
            _filteredItems.Filter = FilterItem;

            _controller = new WallpaperController(SaveSettings);
            _controller.Initialize(_settings);
            _controller.StatusChanged += Controller_StatusChanged;
            _controller.PausedChanged += Controller_PausedChanged;
            _controller.ErrorRaised += Controller_ErrorRaised;

            FitOptions = new ObservableCollection<FitOption>
            {
                new FitOption(FitMode.Fill, "填充（铺满，可能裁切）"),
                new FitOption(FitMode.Fit, "适应（完整显示）"),
                new FitOption(FitMode.Stretch, "拉伸（铺满，可能变形）"),
                new FitOption(FitMode.Center, "居中"),
                new FitOption(FitMode.Tile, "平铺")
            };
            _selectedFitOption = FitOptions.FirstOrDefault(option => option.Mode == _settings.Fit) ?? FitOptions[0];

            ApplySelectedCommand = new RelayCommand(ApplySelected, () => SelectedItem != null && !IsBusy);
            DeleteSelectedCommand = new RelayCommand(DeleteSelected, () => SelectedItem != null && !IsBusy);
            TogglePauseCommand = new RelayCommand(TogglePause, () => IsDynamicActive);
            RestoreOriginalCommand = new RelayCommand(RestoreOriginal, () => _settings.HasSavedOriginal);
            OpenLibraryFolderCommand = new RelayCommand(OpenLibraryFolder);
            OpenLogFolderCommand = new RelayCommand(OpenLogFolder);
            ClearSearchCommand = new RelayCommand(() => SearchText = string.Empty);

            _statusText = "准备就绪";
            _busyText = "正在导入...";

            if (_items.Count > 0)
            {
                var selected = !string.IsNullOrWhiteSpace(_settings.SelectedId)
                    ? _items.FirstOrDefault(item => item.Id == _settings.SelectedId)
                    : null;
                SelectedItem = selected ?? _items[0];
            }

            if (_settings.AutoStart)
            {
                // Refresh the stored path if the app was moved.
                StartupService.SetEnabled(true);
            }
        }

        public ObservableCollection<WallpaperItem> Items
        {
            get { return _items; }
        }

        public ICollectionView FilteredItems
        {
            get { return _filteredItems; }
        }

        public IReadOnlyList<FitOption> FitOptions { get; private set; }

        public WallpaperItem SelectedItem
        {
            get { return _selectedItem; }
            set
            {
                if (_selectedItem == value)
                {
                    return;
                }

                if (_selectedItem != null)
                {
                    _selectedItem.IsSelected = false;
                }
                _selectedItem = value;
                if (_selectedItem != null)
                {
                    _selectedItem.IsSelected = true;
                }
                OnPropertyChanged("SelectedItem");
                OnPropertyChanged("HasSelection");
                RaiseCommandStates();
            }
        }

        public bool HasSelection
        {
            get { return SelectedItem != null; }
        }

        public string SearchText
        {
            get { return _searchText; }
            set
            {
                if (_searchText == value)
                {
                    return;
                }
                _searchText = value;
                OnPropertyChanged("SearchText");
                _filteredItems.Refresh();
            }
        }

        public string StatusText
        {
            get { return _statusText; }
            private set
            {
                if (_statusText == value)
                {
                    return;
                }
                _statusText = value;
                OnPropertyChanged("StatusText");
            }
        }

        public bool IsBusy
        {
            get { return _isBusy; }
            private set
            {
                if (_isBusy == value)
                {
                    return;
                }
                _isBusy = value;
                OnPropertyChanged("IsBusy");
                RaiseCommandStates();
            }
        }

        public string BusyText
        {
            get { return _busyText; }
            private set
            {
                if (_busyText == value)
                {
                    return;
                }
                _busyText = value;
                OnPropertyChanged("BusyText");
            }
        }

        public double BusyProgress
        {
            get { return _busyProgress; }
            private set
            {
                if (Math.Abs(_busyProgress - value) < 0.01)
                {
                    return;
                }
                _busyProgress = value;
                OnPropertyChanged("BusyProgress");
            }
        }

        public bool IsPaused
        {
            get { return _isPaused; }
            private set
            {
                if (_isPaused == value)
                {
                    return;
                }
                _isPaused = value;
                OnPropertyChanged("IsPaused");
                OnPropertyChanged("PauseButtonText");
            }
        }

        public bool IsDynamicActive
        {
            get { return _isDynamicActive; }
            private set
            {
                if (_isDynamicActive == value)
                {
                    return;
                }
                _isDynamicActive = value;
                OnPropertyChanged("IsDynamicActive");
                OnPropertyChanged("PauseButtonText");
                RaiseCommandStates();
            }
        }

        public string PauseButtonText
        {
            get
            {
                if (!IsDynamicActive)
                {
                    return "暂停动态壁纸";
                }
                return IsPaused ? "继续动态壁纸" : "暂停动态壁纸";
            }
        }

        public string ActiveWallpaperText
        {
            get
            {
                var current = _controller.Current;
                return current == null ? "未应用壁纸" : current.DisplayName;
            }
        }

        public string LibraryCountText
        {
            get { return _items.Count + " 张壁纸"; }
        }

        public string AppVersion
        {
            get
            {
                var version = typeof(MainViewModel).Assembly.GetName().Version;
                return version == null ? "1.0.0" : version.ToString(3);
            }
        }

        public string DataFolder
        {
            get { return AppPaths.DataRoot; }
        }

        public string LogFolder
        {
            get { return AppPaths.LogDir; }
        }

        public AppSettings Settings
        {
            get { return _settings; }
        }

        public FitOption SelectedFitOption
        {
            get { return _selectedFitOption; }
            set
            {
                if (value == null || _selectedFitOption == value)
                {
                    return;
                }
                _selectedFitOption = value;
                OnPropertyChanged("SelectedFitOption");
                _controller.SetFitMode(value.Mode);
            }
        }

        public bool AutoStart
        {
            get { return _settings.AutoStart; }
            set
            {
                if (_settings.AutoStart == value)
                {
                    return;
                }
                _settings.AutoStart = value;
                StartupService.SetEnabled(value);
                SaveSettings();
                OnPropertyChanged("AutoStart");
            }
        }

        public bool PauseOnFullscreen
        {
            get { return _settings.PauseOnFullscreen; }
            set
            {
                if (_settings.PauseOnFullscreen == value)
                {
                    return;
                }
                _settings.PauseOnFullscreen = value;
                SaveSettings();
                _controller.RefreshPauseState();
                OnPropertyChanged("PauseOnFullscreen");
            }
        }

        public bool PauseOnBattery
        {
            get { return _settings.PauseOnBattery; }
            set
            {
                if (_settings.PauseOnBattery == value)
                {
                    return;
                }
                _settings.PauseOnBattery = value;
                SaveSettings();
                _controller.RefreshPauseState();
                OnPropertyChanged("PauseOnBattery");
            }
        }

        public bool MuteVideos
        {
            get { return _settings.MuteVideos; }
            set
            {
                if (_settings.MuteVideos == value)
                {
                    return;
                }
                _settings.MuteVideos = value;
                _controller.SetMuted(value);
                SaveSettings();
                OnPropertyChanged("MuteVideos");
            }
        }

        public bool RestoreOriginalOnExit
        {
            get { return _settings.RestoreOriginalOnExit; }
            set
            {
                if (_settings.RestoreOriginalOnExit == value)
                {
                    return;
                }
                _settings.RestoreOriginalOnExit = value;
                SaveSettings();
                OnPropertyChanged("RestoreOriginalOnExit");
            }
        }

        public ICommand ApplySelectedCommand { get; private set; }
        public ICommand DeleteSelectedCommand { get; private set; }
        public ICommand TogglePauseCommand { get; private set; }
        public ICommand RestoreOriginalCommand { get; private set; }
        public ICommand OpenLibraryFolderCommand { get; private set; }
        public ICommand OpenLogFolderCommand { get; private set; }
        public ICommand ClearSearchCommand { get; private set; }

        public void Initialize()
        {
            AssetService.EnsureDefaultWallpaper();
            if (_settings.FirstRun)
            {
                _settings.FirstRun = false;
                SaveSettings();
            }

            var selected = !string.IsNullOrWhiteSpace(_settings.SelectedId)
                ? _items.FirstOrDefault(item => item.Id == _settings.SelectedId)
                : null;
            if (selected != null)
            {
                SelectedItem = selected;
                ApplyItem(selected, false);
            }
            else
            {
                UpdateCurrentFlags(null);
                StatusText = "就绪 · 所有文件保存在本机";
            }

            // Generate missing thumbnails after the main window has rendered.
            foreach (var item in _items.ToList())
            {
                var captured = item;
                Application.Current.Dispatcher.BeginInvoke(
                    new Action(() => ThumbnailService.EnsureAssets(captured)),
                    DispatcherPriority.Background);
            }
        }

        public async System.Threading.Tasks.Task ImportFilesAsync(IEnumerable<string> paths)
        {
            var fileList = paths == null ? new List<string>() : paths.ToList();
            if (fileList.Count == 0 || IsBusy)
            {
                return;
            }

            IsBusy = true;
            BusyProgress = 0;
            BusyText = "正在导入...";
            try
            {
                var progress = new Progress<ImportProgress>(p =>
                {
                    BusyText = p.Message;
                    BusyProgress = p.Percent;
                });

                var result = await _library.ImportAsync(fileList, progress, System.Threading.CancellationToken.None);
                foreach (var item in result.Added)
                {
                    if (!_items.Contains(item))
                    {
                        _items.Add(item);
                    }
                }

                OnPropertyChanged("LibraryCountText");
                if (result.Added.Count > 0)
                {
                    SelectedItem = result.Added[result.Added.Count - 1];
                    StatusText = "已导入 " + result.Added.Count + " 张壁纸";
                }
                else if (result.Existing.Count > 0)
                {
                    SelectedItem = result.Existing[result.Existing.Count - 1];
                    StatusText = "这些壁纸已经在库中";
                }

                if (result.Errors.Count > 0)
                {
                    MessageBox.Show(
                        "以下文件未能导入：\n\n" + string.Join("\n", result.Errors.Take(8)) +
                        (result.Errors.Count > 8 ? "\n…" : ""),
                        "导入完成",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("Import failed", ex);
                StatusText = "导入失败，请查看日志";
                MessageBox.Show("导入失败：" + ex.Message, "鲸鱼壁纸", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
                BusyProgress = 0;
                _filteredItems.Refresh();
            }
        }

        public async System.Threading.Tasks.Task RefreshAssetsAsync()
        {
            foreach (var item in _items.ToList())
            {
                await Application.Current.Dispatcher.InvokeAsync(() => ThumbnailService.EnsureAssets(item));
            }
        }

        public void Shutdown()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            if (Application.Current.MainWindow != null)
            {
                _settings.WindowWidth = Application.Current.MainWindow.Width;
                _settings.WindowHeight = Application.Current.MainWindow.Height;
                _settings.WindowMaximized = Application.Current.MainWindow.WindowState == WindowState.Maximized;
            }
            SaveSettings();
            _controller.Dispose();
        }

        public void TogglePauseFromTray()
        {
            TogglePause();
        }

        public void ApplySelectedWallpaper()
        {
            try
            {
                ApplySelected();
            }
            catch (Exception ex)
            {
                AppLog.Error("ApplySelectedWallpaper failed", ex);
                StatusText = "应用壁纸失败：" + ex.Message;
            }
        }

        public bool CanTogglePause
        {
            get { return IsDynamicActive; }
        }

        private void ApplySelected()
        {
            if (SelectedItem == null)
            {
                return;
            }
            ApplyItem(SelectedItem, true);
        }

        private void ApplyItem(WallpaperItem item, bool persist)
        {
            var ok = _controller.Apply(item, persist);
            if (ok)
            {
                UpdateCurrentFlags(item);
                IsDynamicActive = _controller.IsDynamicActive;
                IsPaused = _controller.IsPaused;
                if (_controller.IsDynamicActive)
                {
                    StatusText = "正在播放 · " + item.DisplayName +
                                 (_controller.LastAttachVerified
                                     ? ""
                                     : " · 未完成自检，请先最小化其他全屏窗口后重新应用");
                }
                else
                {
                    StatusText = "静态壁纸 · " + item.DisplayName;
                }
                OnPropertyChanged("ActiveWallpaperText");
            }
            else
            {
                IsDynamicActive = false;
                StatusText = "应用失败，已保留静态海报";
            }
            RaiseCommandStates();
        }

        private void DeleteSelected()
        {
            var item = SelectedItem;
            if (item == null)
            {
                return;
            }

            if (MessageBox.Show(
                    "确定要从壁纸库删除《" + item.DisplayName + "》吗？\n原始文件不会被删除。",
                    "删除壁纸",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }

            if (item.IsCurrent)
            {
                _controller.RestoreOriginal();
                UpdateCurrentFlags(null);
                IsDynamicActive = false;
                IsPaused = false;
            }

            _library.Delete(item);
            _items.Remove(item);
            OnPropertyChanged("LibraryCountText");
            SelectedItem = _items.FirstOrDefault();
            _filteredItems.Refresh();
            StatusText = "已删除壁纸";
            RaiseCommandStates();
        }

        private void TogglePause()
        {
            if (!_controller.IsDynamicActive)
            {
                StatusText = "当前是静态壁纸，没有动态内容需要暂停";
                return;
            }

            _controller.ToggleUserPaused();
            IsPaused = _controller.IsPaused;
        }

        private void RestoreOriginal()
        {
            if (_controller.RestoreOriginal())
            {
                UpdateCurrentFlags(null);
                IsDynamicActive = false;
                IsPaused = false;
                StatusText = "已恢复原壁纸";
            }
            else
            {
                StatusText = "恢复原壁纸失败";
            }
            RaiseCommandStates();
        }

        private void OpenLibraryFolder()
        {
            try
            {
                Process.Start("explorer.exe", "\"" + AppPaths.LibraryDir + "\"");
            }
            catch (Exception ex)
            {
                StatusText = "无法打开文件夹：" + ex.Message;
            }
        }

        private void OpenLogFolder()
        {
            try
            {
                Process.Start("explorer.exe", "\"" + AppPaths.LogDir + "\"");
            }
            catch (Exception ex)
            {
                StatusText = "无法打开文件夹：" + ex.Message;
            }
        }

        private bool FilterItem(object value)
        {
            var item = value as WallpaperItem;
            if (item == null)
            {
                return false;
            }
            if (string.IsNullOrWhiteSpace(SearchText))
            {
                return true;
            }
            var term = SearchText.Trim();
            return (item.DisplayName ?? "").IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0
                || (item.KindLabel ?? "").IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0
                || (item.ResolutionText ?? "").IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void UpdateCurrentFlags(WallpaperItem current)
        {
            foreach (var item in _items)
            {
                item.IsCurrent = current != null && item.Id == current.Id;
            }
            OnPropertyChanged("ActiveWallpaperText");
        }

        private void SaveSettings()
        {
            SettingsService.Save(_settings);
        }

        private void Controller_StatusChanged(string text)
        {
            StatusText = text;
            IsDynamicActive = _controller.IsDynamicActive;
            IsPaused = _controller.IsPaused;
            OnPropertyChanged("ActiveWallpaperText");
        }

        private void Controller_PausedChanged(bool paused)
        {
            IsPaused = paused;
        }

        private void Controller_ErrorRaised(string message)
        {
            StatusText = message;
            IsDynamicActive = _controller.IsDynamicActive;
        }

        private void RaiseCommandStates()
        {
            (ApplySelectedCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (DeleteSelectedCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (TogglePauseCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (RestoreOriginalCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _controller.Dispose();
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged(string name)
        {
            var handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(name));
            }
        }
    }

    public sealed class FitOption
    {
        public FitOption(FitMode mode, string label)
        {
            Mode = mode;
            Label = label;
        }

        public FitMode Mode { get; private set; }
        public string Label { get; private set; }
    }
}
