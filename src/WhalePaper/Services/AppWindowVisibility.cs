using System;
using System.Windows;

namespace WhalePaper.Services
{
    /// <summary>
    /// Temporarily hides the main window so the layer probe can see desktop pixels.
    /// Used both by the apply flow and by the on-demand desktop probe (which is started
    /// from a button, so the main window is in front of the desktop).
    /// </summary>
    internal static class AppWindowVisibility
    {
        private static Window _window;
        private static WindowState _state;

        public static bool Suspend()
        {
            var app = Application.Current;
            var window = app == null ? null : app.MainWindow;
            if (window == null || !window.IsVisible)
            {
                return false;
            }

            if (_window != null)
            {
                return true;
            }

            _window = window;
            _state = window.WindowState;
            try
            {
                window.Hide();
                AppLog.Info("Probe visibility: main window hidden for layer verification");
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Warn("Probe visibility: hide main window failed: " + ex.Message);
                _window = null;
                return false;
            }
        }

        public static void Resume()
        {
            var window = _window;
            var state = _state;
            _window = null;
            if (window == null)
            {
                return;
            }

            try
            {
                // Do not steal focus from whatever the user was doing.
                window.ShowActivated = false;
                window.Show();
                window.ShowActivated = true;
                if (window.WindowState != state)
                {
                    window.WindowState = state;
                }
                AppLog.Info("Probe visibility: main window restored, state=" + state);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Probe visibility: restore main window failed: " + ex.Message);
            }
        }
    }
}
