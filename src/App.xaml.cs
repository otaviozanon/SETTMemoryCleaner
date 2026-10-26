using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;
using System.Threading;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Threading;

namespace SETTMemoryCleaner
{
    /// <summary>
    /// SETT Memory Cleaner
    /// </summary>
    public partial class App : IDisposable
    {
        #region Fields

        private static bool _isRunning;
        private static Mutex _mutex;
        private static NotifyIcon _notifyIcon;
        private static readonly List<string> _notifications = new List<string>();
        private static readonly object _showHidelock = new object();

        #endregion

        #region Constructors

        /// <summary>
        /// Initializes a new instance of the <see cref="App" /> class.
        /// </summary>
        public App()
        {
            // Log
            Logger.Level = IsInDebugMode ? Enums.Log.Levels.Debug : Enums.Log.Levels.Information;

            // Events
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            Dispatcher.UnhandledException += OnDispatcherUnhandledException;
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets a value indicating whether this instance is in debug mode.
        /// </summary>
        /// <value>
        /// <c>true</c> if this instance is in debug mode; otherwise, <c>false</c>.
        /// </value>
        public static bool IsInDebugMode
        {
            get
            {
#if DEBUG
                return true;
#else
                return false;
#endif
            }
        }

        /// <summary>
        /// Gets a value indicating whether this instance is in design mode.
        /// </summary>
        /// <value>
        ///   <c>true</c> if this instance is in design mode; otherwise, <c>false</c>.
        /// </value>
        public static bool IsInDesignMode
        {
            get
            {
                return DesignerProperties.GetIsInDesignMode(new DependencyObject());
            }
        }

        /// <summary>
        /// App path
        /// </summary>
        public static string Path { get; private set; }

        /// <summary>
        /// App version
        /// </summary>
        public static Version Version { get; private set; }

        #endregion

        #region IDisposable

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="disposing"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                Logger.Dispose();

                try
                {
                    SystemEvents.PowerModeChanged -= OnPowerModeChanged;
                }
                catch
                {
                    // ignored
                }

                if (_mutex != null)
                {
                    try
                    {
                        _mutex.ReleaseMutex();
                    }
                    catch
                    {
                        // ignored
                    }

                    try
                    {
                        _mutex.Dispose();
                    }
                    catch
                    {
                        // ignored
                    }

                    _mutex = null;
                }

                try
                {
                    if (_notifyIcon != null)
                        _notifyIcon.Dispose();
                }
                catch
                {
                    // ignored
                }
            }
        }

        #endregion

        #region Methods

        private void Initialize()
        {
            // DI/IOC
            DependencyInjection.Container.Register<IComputerService, ComputerService>();
            DependencyInjection.Container.Register<IHotkeyService, HotkeyService>();
            DependencyInjection.Container.Register<INotificationService, NotificationService>();

            // App properties
            Path = Helper.GetExecutablePath();
            Version = Helper.GetVersion();

            // Check if app is already running
            bool createdNew;

            _mutex = new Mutex(true, Constants.App.Id, out createdNew);
            _isRunning = !createdNew;

            // App priority
            SetPriority(Settings.RunOnPriority);
        }

        /// <summary>
        /// Navigates the specified URI.
        /// </summary>
        /// <param name="uri">The URI.</param>
        public static void Navigate(Uri uri)
        {
            if (uri == null)
                return;

            using (Process.Start(new ProcessStartInfo
            {
                FileName = uri.AbsoluteUri,
                UseShellExecute = true
            })) { }
        }

        /// <summary>
        /// Called when [dispatcher unhandled exception].
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="e">The <see cref="DispatcherUnhandledExceptionEventArgs" /> instance containing the event data.</param>
        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            e.Handled = true;

            Logger.Error(e.Exception);
        }

        /// <summary>
        /// Handles power mode changes (suspend/resume from hibernation).
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="e">The <see cref="PowerModeChangedEventArgs" /> instance containing the event data.</param>
        private static void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            try
            {
                switch (e.Mode)
                {
                    case PowerModes.Resume:
                        ThreadPool.QueueUserWorkItem(_ =>
                        {
                            try
                            {
                                // XP/2003 need more stabilization time before reinitialization
                                Thread.Sleep(Environment.OSVersion.Version.Major < 6 ? 10000 : 5000);

                                // Retry logic for handling transient failures during system resume
                                const int maxRetries = 3;
                                var retryCount = 0;
                                Exception lastException = null;

                                while (retryCount < maxRetries)
                                {
                                    try
                                    {
                                        var mainViewModel = DependencyInjection.Container.Resolve<MainViewModel>();

                                        if (mainViewModel == null)
                                            throw new InvalidOperationException("MainViewModel could not be resolved from the DI container");

                                        mainViewModel.ReinitializeAfterHibernation();
                                        return;
                                    }
                                    catch (Exception ex)
                                    {
                                        lastException = ex;
                                        retryCount++;

                                        if (retryCount >= maxRetries)
                                        {
                                            var failureException = new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Failed to reinitialize after hibernation after {0} attempts", maxRetries), lastException);
                                            Logger.Error(failureException.Message + ": " + failureException.GetMessage());
                                            throw;
                                        }

                                        // Wait before retrying
                                        Thread.Sleep(5000);
                                    }
                                }
                            }
                            catch (Exception threadEx)
                            {
                                Logger.Error("Critical error in power mode resume handling: " + threadEx.GetMessage());
                            }
                        });
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Error handling power mode change: " + ex.GetMessage());
            }
        }

        /// <summary>
        /// Called when [process exit].
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="e">The <see cref="EventArgs" /> instance containing the event data.</param>
        private void OnProcessExit(object sender, EventArgs e)
        {
            Dispose();
        }

        /// <summary>
        /// Called when [notify icon click].
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="e">The <see cref="EventArgs" /> instance containing the event data.</param>
        private void OnNotifyIconClick(object sender, System.Windows.Forms.MouseEventArgs e)
        {
            lock (_showHidelock)
            {
                switch (e.Button)
                {
                    // Show/Hide
                    case MouseButtons.Left:
                        if (MainWindow == null)
                            return;

                        if (MainWindow.OwnedWindows.Cast<View>().Any(window => window != null && window.IsDialog))
                        {
                            MainWindow.Activate();
                            MainWindow.Topmost = true;
                            MainWindow.Topmost = Settings.AlwaysOnTop;

                            return;
                        }

                        switch (MainWindow.Visibility)
                        {
                            case Visibility.Collapsed:
                            case Visibility.Hidden:
                                MainWindow.Show();

                                MainWindow.WindowState = WindowState.Normal;

                                MainWindow.Activate();
                                MainWindow.Focus();

                                MainWindow.Topmost = true;
                                MainWindow.Topmost = Settings.AlwaysOnTop;
                                MainWindow.ShowInTaskbar = true;

                                // Focus the Optimize button when restoring from notification area
                                MainWindow.Dispatcher.BeginInvoke((Action)(() =>
                                {
                                    var mainWindow = MainWindow as MainWindow;

                                    if (mainWindow != null)
                                    {
                                        var optimizeButton = mainWindow.FindName("Optimize") as UIElement;

                                        if (optimizeButton != null)
                                        {
                                            Keyboard.Focus(optimizeButton);
                                            FocusManager.SetFocusedElement(mainWindow, optimizeButton);
                                        }
                                    }
                                }), DispatcherPriority.ApplicationIdle);
                                break;

                            case Visibility.Visible:
                                MainWindow.Hide();

                                MainWindow.ShowInTaskbar = false;
                                break;
                        }

                        ReleaseMemory();
                        return;

                    // Optimize
                    case MouseButtons.Middle:
                        if (!Settings.TrayIconOptimizeOnMiddleMouseClick)
                            return;

                        var mainViewModel = DependencyInjection.Container.Resolve<MainViewModel>();

                        if (mainViewModel.OptimizeCommand.CanExecute(null))
                            mainViewModel.OptimizeCommand.Execute(null);
                        break;
                }
            }
        }

        /// <summary>
        /// Raises the <see cref="E:Startup" /> event.
        /// </summary>
        /// <param name="startupEvent">The <see cref="StartupEventArgs" /> instance containing the event data.</param>
        protected override void OnStartup(StartupEventArgs startupEvent)
        {
            try
            {
                Initialize();

                // Check if app is already running
                if (_isRunning)
                {
                    try
                    {
                        var appHandle = NativeMethods.FindWindow(null, Constants.App.Title);

                        if (appHandle != IntPtr.Zero && NativeMethods.IsWindowVisible(appHandle))
                        {
                            int appId;

                            if (NativeMethods.GetWindowThreadProcessId(appHandle, out appId) != Constants.WinAPI.SystemErrorCode.ErrorSuccess)
                                NativeMethods.AllowSetForegroundWindow(appId);

                            NativeMethods.ShowWindowAsync(appHandle, Constants.WinAPI.ShowWindow.Restore);
                            NativeMethods.SetForegroundWindow(appHandle);
                        }
                    }
                    finally
                    {
                        Shutdown(true);
                    }
                }

                NativeMethods.AllowSetForegroundWindow(Process.GetCurrentProcess().Id);

                // App startup shortcut
                Helper.StartMenuShortcut(Settings.CreateStartMenuShortcut);

                // Theme
                ThemeManager.Theme = Enums.Theme.Dark;

                // Notification Areas
                _notifyIcon = new NotifyIcon();
                _notifyIcon.MouseUp += OnNotifyIconClick;

                // DI/IOC
                DependencyInjection.Container.Register(_notifyIcon);

                var mainWindow = new MainWindow();

                if (!Settings.StartMinimized)
                {
                    mainWindow.Show();
                    mainWindow.Activate();
                    mainWindow.Focus();
                    mainWindow.Topmost = true;
                    mainWindow.Topmost = Settings.AlwaysOnTop;
                }

                // Subscribe to power events
                SystemEvents.PowerModeChanged += OnPowerModeChanged;

                // Process notifications
                foreach (var notification in _notifications)
                {
                    Logger.Information(notification);
                    DependencyInjection.Container.Resolve<INotificationService>().Notify(notification);
                }

                RunOnStartup(Settings.RunOnStartup);
                ReleaseMemory();
            }
            catch (Exception e)
            {
                Logger.Error(e);
                ShowDialog(e);
                Shutdown(true);
            }
        }

        /// <summary>
        /// Called when [unhandled exception].
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="e">The <see cref="UnhandledExceptionEventArgs" /> instance containing the event data.</param>
        private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Logger.Error((Exception)e.ExceptionObject);
        }

        /// <summary>
        /// Releases the app memory
        /// </summary>
        public static void ReleaseMemory()
        {
            // Garbage Collector
            try
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced);
                GC.WaitForPendingFinalizers();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced);
            }
            catch
            {
                // ignored
            }

            // Optimize App Working Set
            try
            {
                NativeMethods.EmptyWorkingSet(Process.GetCurrentProcess().Handle);
            }
            catch (Exception)
            {
                // ignored
            }
        }

        /// <summary>
        /// Runs the app on startup
        /// </summary>
        /// <param name="enable">if set to <c>true</c> [enable].</param>
        public static void RunOnStartup(bool enable)
        {
            try
            {
                if (enable)
                {
                    var taskXml = string.Format
                        (
                            CultureInfo.InvariantCulture,
                            @"<?xml version=""1.0"" encoding=""UTF-16""?>
                            <Task version=""1.2""
                                xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
                                <RegistrationInfo>
                                    <Author>{3}</Author>
                                    <Description>Runs {0} at logon.</Description>
                                    <Date>{4}</Date>
                                </RegistrationInfo>
                                <Triggers>
                                    <LogonTrigger>
                                        <Enabled>true</Enabled>
                                    </LogonTrigger>
                                </Triggers>
                                <Principals>
                                    <Principal id=""Author"">
                                        <UserId>{2}</UserId>
                                        <LogonType>InteractiveToken</LogonType>
                                        <RunLevel>HighestAvailable</RunLevel>
                                    </Principal>
                                </Principals>
                                <Settings>
                                    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                                    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                                    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                                    <AllowHardTerminate>true</AllowHardTerminate>
                                    <StartWhenAvailable>true</StartWhenAvailable>
                                    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                                    <IdleSettings>
                                        <WaitTimeout>PT10M</WaitTimeout>
                                        <StopOnIdleEnd>false</StopOnIdleEnd>
                                        <RestartOnIdle>false</RestartOnIdle>
                                    </IdleSettings>
                                    <AllowStartOnDemand>true</AllowStartOnDemand>
                                    <Enabled>true</Enabled>
                                    <Hidden>false</Hidden>
                                    <RunOnlyIfIdle>false</RunOnlyIfIdle>
                                    <WakeToRun>false</WakeToRun>
                                    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                                    <Priority>7</Priority>
                                </Settings>
                                <Actions Context=""Author"">
                                    <Exec>
                                        <Command>""{1}""</Command>
                                    </Exec>
                                </Actions>
                            </Task>",
                            Constants.App.Title,
                            Path,
                            WindowsIdentity.GetCurrent().User.Value,
                            string.Format(CultureInfo.InvariantCulture, "SETT {0} ({1})", string.Format(Localizer.Culture, Constants.App.VersionFormat, Version.Major, Version.Minor, Version.Build), Environment.UserName),
                            DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)
                        );

                    RegisterScheduledTask(Constants.App.Title, taskXml);
                }
                else
                {
                    DeleteScheduledTask(Constants.App.Title);
                }
            }
            catch (Exception e)
            {
                Logger.Error(string.Format(Localizer.Culture, "An error occurred while managing the scheduled task for app startup. Error: {0}", e.GetMessage()));
            }
        }

        /// <summary>
        /// Registers (creates or updates) a Logon scheduled task using the native Task Scheduler COM API.
        /// Avoids spawning external processes (schtasks.exe) or writing temporary XML files to disk,
        /// which are patterns commonly flagged by antivirus heuristic/ML engines.
        /// </summary>
        /// <param name="taskName">Name of the scheduled task.</param>
        /// <param name="taskXml">Task definition in Task Scheduler XML format.</param>
        private static void RegisterScheduledTask(string taskName, string taskXml)
        {
            object service = null;
            object rootFolder = null;

            try
            {
                var serviceType = Type.GetTypeFromProgID("Schedule.Service");
                service = Activator.CreateInstance(serviceType);

                serviceType.InvokeMember("Connect", BindingFlags.InvokeMethod, null, service, null);

                rootFolder = serviceType.InvokeMember("GetFolder", BindingFlags.InvokeMethod, null, service, new object[] { @"\" });

                // TASK_CREATE_OR_UPDATE = 6, TASK_LOGON_INTERACTIVE_TOKEN = 3
                rootFolder.GetType().InvokeMember
                (
                    "RegisterTask",
                    BindingFlags.InvokeMethod,
                    null,
                    rootFolder,
                    new object[] { taskName, taskXml, 6, null, null, 3, string.Empty }
                );
            }
            finally
            {
                if (rootFolder != null)
                    Marshal.FinalReleaseComObject(rootFolder);

                if (service != null)
                    Marshal.FinalReleaseComObject(service);
            }
        }

        /// <summary>
        /// Deletes a scheduled task using the native Task Scheduler COM API.
        /// </summary>
        /// <param name="taskName">Name of the scheduled task.</param>
        private static void DeleteScheduledTask(string taskName)
        {
            object service = null;
            object rootFolder = null;

            try
            {
                var serviceType = Type.GetTypeFromProgID("Schedule.Service");
                service = Activator.CreateInstance(serviceType);

                serviceType.InvokeMember("Connect", BindingFlags.InvokeMethod, null, service, null);

                rootFolder = serviceType.InvokeMember("GetFolder", BindingFlags.InvokeMethod, null, service, new object[] { @"\" });

                rootFolder.GetType().InvokeMember("DeleteTask", BindingFlags.InvokeMethod, null, rootFolder, new object[] { taskName, 0 });
            }
            catch (COMException)
            {
                // Task does not exist - nothing to delete
            }
            catch (TargetInvocationException ex)
            {
                if (!(ex.InnerException is COMException))
                    throw;

                // Task does not exist - nothing to delete
            }
            finally
            {
                if (rootFolder != null)
                    Marshal.FinalReleaseComObject(rootFolder);

                if (service != null)
                    Marshal.FinalReleaseComObject(service);
            }
        }

        /// <summary>
        /// Sets the app priority for the Windows
        /// </summary>
        public static void SetPriority(Enums.Priority priority)
        {
            bool priorityBoostEnabled;
            ProcessPriorityClass processPriorityClass;
            ThreadPriority threadPriority;
            ThreadPriorityLevel threadPriorityLevel;

            switch (priority)
            {
                case Enums.Priority.Low:
                    priorityBoostEnabled = false;
                    processPriorityClass = ProcessPriorityClass.Idle;
                    threadPriority = ThreadPriority.Lowest;
                    threadPriorityLevel = ThreadPriorityLevel.Idle;
                    break;

                case Enums.Priority.Normal:
                    priorityBoostEnabled = true;
                    processPriorityClass = ProcessPriorityClass.Normal;
                    threadPriority = ThreadPriority.Normal;
                    threadPriorityLevel = ThreadPriorityLevel.Normal;
                    break;

                case Enums.Priority.High:
                    priorityBoostEnabled = true;
                    processPriorityClass = ProcessPriorityClass.High;
                    threadPriority = ThreadPriority.Highest;
                    threadPriorityLevel = ThreadPriorityLevel.Highest;
                    break;

                default:
                    throw new NotImplementedException();
            }

            try
            {
                Thread.CurrentThread.Priority = threadPriority;
            }
            catch
            {
                // ignored
            }

            try
            {
                var process = Process.GetCurrentProcess();

                try
                {
                    process.PriorityBoostEnabled = priorityBoostEnabled;
                }
                catch
                {
                    // ignored
                }

                try
                {
                    process.PriorityClass = processPriorityClass;
                }
                catch
                {
                    // ignored
                }

                foreach (ProcessThread thread in process.Threads)
                {
                    try
                    {
                        thread.PriorityBoostEnabled = priorityBoostEnabled;
                    }
                    catch
                    {
                        // ignored
                    }

                    try
                    {
                        thread.PriorityLevel = threadPriorityLevel;
                    }
                    catch
                    {
                        // ignored
                    }
                }
            }
            catch
            {
                // ignored
            }
        }

        /// <summary>
        /// Shows a dialog
        /// </summary>
        /// <param name="exception">Exception</param>
        /// <param name="message">The message.</param>
        private void ShowDialog(Exception exception, string message = null)
        {
            ShowDialog(message ?? exception.GetMessage(), MessageBoxButton.OK, MessageBoxImage.Error);
        }

        /// <summary>
        /// Shows the dialog.
        /// </summary>
        /// <param name="message">The message.</param>
        /// <param name="button">The button.</param>
        /// <param name="icon">The icon.</param>
        /// <param name="defaultResult">The default result.</param>
        /// <param name="options">The options.</param>
        private void ShowDialog(string message, MessageBoxButton button = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None, MessageBoxResult defaultResult = MessageBoxResult.OK, System.Windows.MessageBoxOptions options = System.Windows.MessageBoxOptions.None)
        {
            try
            {
                System.Windows.MessageBox.Show(message, Constants.App.Title, button, icon, defaultResult, options);
            }
            catch
            {
                // ignored
            }
        }

        /// <summary>
        /// Shuts down the app
        /// </summary>
        /// <param name="force">if set to <c>true</c> [force].</param>
        public static void Shutdown(bool force = false)
        {
            try
            {
                if (force)
                    Environment.Exit(Constants.WinAPI.SystemErrorCode.ErrorSuccess);
                else
                    Current.Shutdown();
            }
            catch
            {
                Environment.Exit(Constants.WinAPI.SystemErrorCode.ErrorSuccess);
            }
        }

        #endregion
    }
}
