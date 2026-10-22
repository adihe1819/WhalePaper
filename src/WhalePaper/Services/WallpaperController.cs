using System;
using System.IO;
using System.Windows.Forms;
using System.Windows.Threading;
using Microsoft.Win32;
using WhalePaper.Interop;
using WhalePaper.Models;

namespace WhalePaper.Services
{
    internal sealed class WallpaperController : IDisposable
    {
        private readonly DesktopWallpaperService _desktop = new DesktopWallpaperService();
        private readonly DispatcherTimer _lifecycleTimer;
        private readonly Dispatcher _dispatcher;
        private readonly Action _saveSettings;
        private AppSettings _settings;
        private WallpaperItem _current;
        private WallpaperHostWindow _host;
        private bool _userPaused;
        private bool _fullscreenPaused;
        private bool _fullscreenRaw;
        private int _fullscreenStableTicks;
        private bool _sessionPaused;
        private bool _powerPaused;
        private bool _batteryPaused;
        private bool _remotePaused;
        private bool _isPaused;
        private bool _disposed;
        private bool _applying;
        private DateTime _lastHostRestart = DateTime.MinValue;

        public WallpaperController(Action saveSettings)
        {
            _saveSettings = saveSettings;
            _dispatcher = Dispatcher.CurrentDispatcher;
            _lifecycleTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            _lifecycleTimer.Tick += LifecycleTimer_Tick;
        }

        public event Action<string> StatusChanged;
        public event Action<bool> PausedChanged;
        public event Action<string> ErrorRaised;

        public WallpaperItem Current
        {
            get { return _current; }
        }

        public bool IsPaused
        {
            get { return _isPaused; }
        }

        public bool IsDynamicActive
        {
            get { return _host != null; }
        }

        /// <summary>
        /// False when the dynamic layer was attached without pixel verification (the
        /// desktop was fully covered). The UI then asks the user to minimize windows.
        /// </summary>
        public bool LastAttachVerified
        {
            get { return _host == null || _host.AttachVerified; }
        }

        public AppSettings Settings
        {
            get { return _settings; }
        }

        public void Initialize(AppSettings settings)
        {
            _settings = settings;
            SystemEvents.SessionSwitch += SystemEvents_SessionSwitch;
            SystemEvents.PowerModeChanged += SystemEvents_PowerModeChanged;
            SystemEvents.DisplaySettingsChanged += SystemEvents_DisplaySettingsChanged;
        }

        public bool Apply(WallpaperItem item, bool persist)
        {
            if (_disposed || item == null || _applying)
            {
                return false;
            }

            // Attaching the dynamic layer runs a short pixel probe on the UI thread; the
            // guard keeps a second Apply from re-entering that measurement.
            _applying = true;
            try
            {
                return ApplyCore(item, persist);
            }
            finally
            {
                _applying = false;
            }
        }

        private bool ApplyCore(WallpaperItem item, bool persist)
        {
            AppLog.Info("Apply start: " + item.DisplayName + " kind=" + item.Kind);
            _current = item;
            _userPaused = false;
            _isPaused = false;
            StopDynamicHost();
            EnsureOriginalSaved();

            if (item.Kind == WallpaperKind.Image)
            {
                var ok = _desktop.SetWallpaper(item.StoredPath, _settings.Fit);
                if (!ok)
                {
                    RaiseError("无法设置静态壁纸，请检查文件格式或系统权限。");
                    UpdateStatus();
                    return false;
                }

                _settings.SelectedId = item.Id;
                if (persist)
                {
                    SaveSettings();
                }
                UpdateStatus();
                AppLog.Info("Apply static complete");
                return true;
            }

            ThumbnailService.EnsureAssets(item);
            var poster = item.PosterPath;
            if (string.IsNullOrWhiteSpace(poster) || !File.Exists(poster))
            {
                poster = AppPaths.DefaultWallpaperFile;
            }

            // Set the poster first, so a decoder crash never leaves a black desktop.
            _desktop.SetWallpaper(poster, _settings.Fit);
            string failure;
            try
            {
                if (!StartDynamicHost(item, poster, out failure))
                {
                    _desktop.SetWallpaper(poster, _settings.Fit);
                    RaiseError("动态壁纸启动失败，已保留静态海报。原因：" +
                               (string.IsNullOrWhiteSpace(failure) ? "未知" : failure));
                    UpdateStatus();
                    return false;
                }
            }
            finally
            {
                // The host may have hidden the main window to get desktop pixels for the
                // probe; always restore it, even when attach threw.
                AppWindowVisibility.Resume();
            }

            _settings.SelectedId = item.Id;
            if (persist)
            {
                SaveSettings();
            }
            UpdateStatus();
            return true;
        }

        public void SetFitMode(FitMode fit)
        {
            if (_settings == null)
            {
                return;
            }

            _settings.Fit = fit;
            SaveSettings();
            if (_current == null)
            {
                return;
            }

            if (_current.Kind == WallpaperKind.Image)
            {
                _desktop.SetWallpaper(_current.StoredPath, fit);
            }
            else if (_host != null)
            {
                _host.SetFit(fit);
            }
            else
            {
                var poster = _current.PosterPath;
                if (!string.IsNullOrWhiteSpace(poster) && File.Exists(poster))
                {
                    _desktop.SetWallpaper(poster, fit);
                }
            }
        }

        public void SetMuted(bool muted)
        {
            if (_settings != null)
            {
                _settings.MuteVideos = muted;
            }
            if (_host != null)
            {
                _host.SetMuted(muted);
            }
            SaveSettings();
        }

        public void SetUserPaused(bool paused)
        {
            _userPaused = paused;
            UpdatePauseState();
        }

        public void RefreshPauseState()
        {
            if (_settings == null)
            {
                return;
            }

            _fullscreenPaused = _settings.PauseOnFullscreen && FullscreenDetector.IsFullscreenActive();
            _fullscreenRaw = _fullscreenPaused;
            _fullscreenStableTicks = 0;
            DiagnosticsProbe.LogFullscreen(_fullscreenPaused, "refresh");
            _batteryPaused = IsOnBattery();
            _remotePaused = NativeMethods.GetSystemMetrics(NativeMethods.SM_REMOTESESSION) != 0;
            UpdatePauseState();
        }

        public void ToggleUserPaused()
        {
            _userPaused = !_userPaused;
            UpdatePauseState();
        }

        public bool RestoreOriginal()
        {
            StopDynamicHost();
            _current = null;
            if (_settings != null)
            {
                _settings.SelectedId = null;
            }
            var ok = _desktop.RestoreOriginal(_settings);
            SaveSettings();
            UpdateStatus();
            return ok;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _lifecycleTimer.Stop();
            SystemEvents.SessionSwitch -= SystemEvents_SessionSwitch;
            SystemEvents.PowerModeChanged -= SystemEvents_PowerModeChanged;
            SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged;
            StopDynamicHost();

            if (_settings != null && _settings.RestoreOriginalOnExit && _settings.HasSavedOriginal)
            {
                try
                {
                    _desktop.RestoreOriginal(_settings);
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Restore on exit failed: " + ex.Message);
                }
            }
        }

        private void StartLifecycleTimer()
        {
            _fullscreenPaused = _settings.PauseOnFullscreen && FullscreenDetector.IsFullscreenActive();
            _fullscreenRaw = _fullscreenPaused;
            _fullscreenStableTicks = 0;
            DiagnosticsProbe.LogFullscreen(_fullscreenPaused, "start");
            _batteryPaused = IsOnBattery();
            _remotePaused = NativeMethods.GetSystemMetrics(NativeMethods.SM_REMOTESESSION) != 0;
            _powerPaused = false;
            _sessionPaused = false;
            UpdatePauseState();
            _lifecycleTimer.Start();
        }

        private bool StartDynamicHost(WallpaperItem item, string poster, out string failure)
        {
            failure = null;
            try
            {
                var host = new WallpaperHostWindow();
                host.PlaybackFailed += Host_PlaybackFailed;
                host.SuspendAppWindow = AppWindowVisibility.Suspend;
                host.SetPoster(poster);
                if (!host.AttachToDesktop(_settings.AttachStrategy))
                {
                    failure = host.LastFailure;
                    host.Dispose();
                    return false;
                }

                if (!host.Load(item, _settings.Fit, _settings.MuteVideos))
                {
                    failure = "媒体加载失败";
                    host.Dispose();
                    return false;
                }

                // Publish the host before the pixel check: the check pumps the dispatcher,
                // so a PlaybackFailed event can run inside it and must be able to stop
                // this host instead of leaving a dead host behind.
                _host = host;

                // While the probe window may still be hidden, check whether video frames
                // actually reach the screen. This is what tells "verified layer but the
                // video does not draw" apart from "still stuck on the poster".
                host.CheckPlaybackPixels();
                AppWindowVisibility.Resume();

                if (_disposed || !ReferenceEquals(_host, host))
                {
                    host.Dispose();
                    failure = "动态壁纸播放失败或已停止（详见日志）";
                    return false;
                }

                if (!string.IsNullOrEmpty(host.AttachStrategyId) &&
                    !string.Equals(_settings.AttachStrategy, host.AttachStrategyId, StringComparison.Ordinal))
                {
                    _settings.AttachStrategy = host.AttachStrategyId;
                    SaveSettings();
                }

                StartLifecycleTimer();
                AppLog.Info("Dynamic wallpaper started: " + item.DisplayName +
                            " layer=" + host.AttachDescription);
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Error("Start dynamic host failed", ex);
                StopDynamicHost();
                failure = ex.Message;
                return false;
            }
        }

        private void StopDynamicHost()
        {
            _lifecycleTimer.Stop();
            var host = _host;
            _host = null;
            if (host != null)
            {
                host.PlaybackFailed -= Host_PlaybackFailed;
                host.Dispose();
            }
            _isPaused = false;
        }

        private void RestartHostFromTimer()
        {
            if (_current == null || _current.Kind == WallpaperKind.Image)
            {
                return;
            }

            if ((DateTime.UtcNow - _lastHostRestart).TotalSeconds < 4)
            {
                return;
            }

            _lastHostRestart = DateTime.UtcNow;
            AppLog.Warn("Dynamic host lost its desktop layer; restarting");
            StopDynamicHost();
            var poster = _current.PosterPath;
            if (string.IsNullOrWhiteSpace(poster) || !File.Exists(poster))
            {
                poster = AppPaths.DefaultWallpaperFile;
            }

            string failure;
            if (!StartDynamicHost(_current, poster, out failure))
            {
                RaiseError("桌面宿主恢复失败，已保留静态海报。原因：" +
                           (string.IsNullOrWhiteSpace(failure) ? "未知" : failure));
            }
            UpdateStatus();
        }

        private void EnsureOriginalSaved()
        {
            if (_settings == null)
            {
                return;
            }

            if (_desktop.EnsureOriginalSaved(_settings))
            {
                SaveSettings();
            }
        }

        private void UpdatePauseState()
        {
            var shouldPause = _userPaused
                || (_settings.PauseOnFullscreen && _fullscreenPaused)
                || _sessionPaused
                || _powerPaused
                || (_settings.PauseOnBattery && _batteryPaused)
                || _remotePaused;

            DiagnosticsProbe.LogPauseState(
                "update",
                shouldPause,
                _isPaused,
                _userPaused,
                _fullscreenPaused,
                _sessionPaused,
                _powerPaused,
                _batteryPaused,
                _remotePaused,
                shouldPause ? PauseReason() : "none");

            if (_host != null)
            {
                if (shouldPause && !_isPaused)
                {
                    _host.Pause();
                }
                else if (!shouldPause && _isPaused)
                {
                    _host.Resume();
                }
            }

            if (_isPaused != shouldPause)
            {
                _isPaused = shouldPause;
                var handler = PausedChanged;
                if (handler != null)
                {
                    handler(_isPaused);
                }
            }
            UpdateStatus();
        }

        private bool IsOnBattery()
        {
            try
            {
                return SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline;
            }
            catch
            {
                return false;
            }
        }

        private void LifecycleTimer_Tick(object sender, EventArgs e)
        {
            if (_host == null)
            {
                _lifecycleTimer.Stop();
                return;
            }

            try
            {
                if (!_host.EnsureLayer())
                {
                    AppLog.Warn("Wallpaper host layer is gone (parent closed or Explorer restarted)");
                    RestartHostFromTimer();
                    return;
                }

                var fullscreen = _settings.PauseOnFullscreen && FullscreenDetector.IsFullscreenActive();
                DiagnosticsProbe.LogFullscreen(fullscreen, "timer");

                // Require two consecutive timer ticks (about four seconds) before changing
                // the pause state, so a transient shell state cannot make the wallpaper flap.
                if (fullscreen != _fullscreenRaw)
                {
                    _fullscreenRaw = fullscreen;
                    _fullscreenStableTicks = 0;
                }
                else if (_fullscreenStableTicks < 2)
                {
                    _fullscreenStableTicks++;
                }

                var confirmedFullscreen = _fullscreenStableTicks >= 2
                    ? _fullscreenRaw
                    : _fullscreenPaused;

                var battery = IsOnBattery();
                var remote = NativeMethods.GetSystemMetrics(NativeMethods.SM_REMOTESESSION) != 0;
                if (confirmedFullscreen != _fullscreenPaused || battery != _batteryPaused || remote != _remotePaused)
                {
                    _fullscreenPaused = confirmedFullscreen;
                    _batteryPaused = battery;
                    _remotePaused = remote;
                    UpdatePauseState();
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("Lifecycle timer failed: " + ex.Message);
            }
        }

        private void Host_PlaybackFailed(object sender, string message)
        {
            AppLog.Warn("Host playback failed: " + message);
            RaiseError("动态壁纸播放失败，已保留静态海报：" + message);
            StopDynamicHost();
            if (_current != null)
            {
                var poster = _current.PosterPath;
                _desktop.SetWallpaper(
                    !string.IsNullOrWhiteSpace(poster) && File.Exists(poster) ? poster : AppPaths.DefaultWallpaperFile,
                    _settings.Fit);
            }
            UpdateStatus();
        }

        private void SystemEvents_SessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            RunOnUi(() =>
            {
                if (e.Reason == SessionSwitchReason.SessionLock ||
                    e.Reason == SessionSwitchReason.SessionLogoff)
                {
                    _sessionPaused = true;
                    UpdatePauseState();
                }
                else if (e.Reason == SessionSwitchReason.SessionUnlock ||
                         e.Reason == SessionSwitchReason.SessionLogon)
                {
                    _sessionPaused = false;
                    UpdatePauseState();
                }
            });
        }

        private void SystemEvents_PowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            RunOnUi(() =>
            {
                if (e.Mode == PowerModes.Suspend)
                {
                    _powerPaused = true;
                    UpdatePauseState();
                }
                else if (e.Mode == PowerModes.Resume)
                {
                    _powerPaused = false;
                    _batteryPaused = IsOnBattery();
                    UpdatePauseState();
                }
            });
        }

        private void SystemEvents_DisplaySettingsChanged(object sender, EventArgs e)
        {
            RunOnUi(() =>
            {
                try
                {
                    if (_host != null)
                    {
                        _host.Reposition();
                    }
                    else if (_current != null && _current.Kind == WallpaperKind.Image)
                    {
                        _desktop.SetWallpaper(_current.StoredPath, _settings.Fit);
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Display change handling failed: " + ex.Message);
                }
            });
        }

        private void RunOnUi(Action action)
        {
            if (_dispatcher == null || _disposed)
            {
                return;
            }

            if (_dispatcher.CheckAccess())
            {
                action();
            }
            else
            {
                _dispatcher.BeginInvoke(DispatcherPriority.Normal, action);
            }
        }

        private void RaiseError(string message)
        {
            AppLog.Warn(message);
            var handler = ErrorRaised;
            if (handler != null)
            {
                handler(message);
            }
        }

        private void SaveSettings()
        {
            try
            {
                if (_saveSettings != null)
                {
                    _saveSettings();
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("Settings save callback failed: " + ex.Message);
            }
        }

        private void UpdateStatus()
        {
            string text;
            if (_current == null)
            {
                text = "未应用动态壁纸";
            }
            else if (_host == null)
            {
                text = "静态壁纸 · " + _current.DisplayName;
            }
            else if (_isPaused)
            {
                text = "已暂停 · " + _current.DisplayName + " · " + PauseReason();
            }
            else
            {
                text = "正在播放 · " + _current.DisplayName;
                if (!LastAttachVerified)
                {
                    text += " · 未完成自检，请先最小化其他全屏窗口后重新应用";
                }
            }

            var handler = StatusChanged;
            if (handler != null)
            {
                handler(text);
            }
        }

        private string PauseReason()
        {
            if (_userPaused) return "手动暂停";
            if (_sessionPaused) return "锁屏";
            if (_powerPaused) return "休眠";
            if (_remotePaused) return "远程桌面";
            if (_batteryPaused) return "电池供电";
            if (_fullscreenPaused) return "全屏应用";
            return "省电";
        }
    }
}
