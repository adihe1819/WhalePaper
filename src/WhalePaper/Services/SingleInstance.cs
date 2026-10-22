using System;
using System.Threading;

namespace WhalePaper.Services
{
    internal static class SingleInstance
    {
        private const string MutexName = @"Local\WhalePaper.SingleInstance";
        private const string EventName = @"Local\WhalePaper.Activate";
        private const string ExitEventName = @"Local\WhalePaper.Exit";

        private static Mutex _mutex;
        private static EventWaitHandle _activateEvent;
        private static EventWaitHandle _exitEvent;
        private static Thread _listener;
        private static Thread _exitListener;
        private static volatile bool _stopping;

        public static event Action Activated;
        public static event Action ExitRequested;

        public static bool TryAcquire()
        {
            bool created;
            _mutex = new Mutex(true, MutexName, out created);
            if (!created)
            {
                SignalExisting();
                return false;
            }

            _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
            _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
            _listener = new Thread(Listen)
            {
                IsBackground = true,
                Name = "WhalePaper.ActivationListener"
            };
            _listener.Start();
            _exitListener = new Thread(ListenForExit)
            {
                IsBackground = true,
                Name = "WhalePaper.ExitListener"
            };
            _exitListener.Start();
            return true;
        }

        public static bool RequestExit()
        {
            try
            {
                using (var handle = EventWaitHandle.OpenExisting(ExitEventName))
                {
                    handle.Set();
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        public static void Shutdown()
        {
            _stopping = true;
            try
            {
                if (_activateEvent != null)
                {
                    _activateEvent.Set();
                }
                if (_exitEvent != null)
                {
                    _exitEvent.Set();
                }
            }
            catch
            {
            }

            try
            {
                if (_mutex != null)
                {
                    _mutex.ReleaseMutex();
                    _mutex.Dispose();
                }
            }
            catch
            {
            }
        }

        private static void SignalExisting()
        {
            for (var i = 0; i < 8; i++)
            {
                try
                {
                    using (var handle = EventWaitHandle.OpenExisting(EventName))
                    {
                        handle.Set();
                        return;
                    }
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    Thread.Sleep(120);
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Could not signal existing instance: " + ex.Message);
                    return;
                }
            }
        }

        private static void Listen()
        {
            while (!_stopping)
            {
                try
                {
                    if (_activateEvent.WaitOne(500))
                    {
                        if (_stopping)
                        {
                            return;
                        }

                        InvokeHandlers(Activated, "Activation callback failed: ");
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Activation listener failed: " + ex.Message);
                    return;
                }
            }
        }

        private static void ListenForExit()
        {
            while (!_stopping)
            {
                try
                {
                    if (_exitEvent.WaitOne(500))
                    {
                        if (_stopping)
                        {
                            return;
                        }

                        InvokeHandlers(ExitRequested, "Exit callback failed: ");
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Exit listener failed: " + ex.Message);
                    return;
                }
            }
        }

        private static void InvokeHandlers(Action handler, string errorPrefix)
        {
            if (handler == null)
            {
                return;
            }

            try
            {
                handler();
            }
            catch (Exception ex)
            {
                AppLog.Warn(errorPrefix + ex.Message);
            }
        }
    }
}
