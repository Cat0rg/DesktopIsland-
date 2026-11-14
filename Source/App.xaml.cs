using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows;
using DesktopIsland.Services;
using Forms = System.Windows.Forms;

namespace DesktopIsland;

public partial class App : System.Windows.Application
{
    private const string MutexName = "DesktopIsland_SingleInstance_Mutex_v1";
    private const string ActivateEventName = "DesktopIsland_Activate_Event_v1";

    private Mutex? _mutex;
    private EventWaitHandle? _activateEvent;
    private RegisteredWaitHandle? _activateWaitHandle;
    private Forms.NotifyIcon? _trayIcon;
    private MainWindow? _mainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        bool waitForMutex = Array.Exists(e.Args, a => string.Equals(a, "--wait-mutex", StringComparison.OrdinalIgnoreCase));
        bool createdNew = false;
        int attempts = waitForMutex ? 50 : 1;
        for (int i = 0; i < attempts; i++)
        {
            _mutex?.Dispose();
            _mutex = new Mutex(true, MutexName, out createdNew);
            if (createdNew) break;
            if (i < attempts - 1) Thread.Sleep(100);
        }

        if (!createdNew)
        {
            SignalExistingInstance();
            Shutdown();
            return;
        }

        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _activateWaitHandle = ThreadPool.RegisterWaitForSingleObject(
            _activateEvent,
            (_, _) => Dispatcher.BeginInvoke(() => _mainWindow?.BringToFront()),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);

        DispatcherUnhandledException += (_, args) =>
        {
            LoggerService.Error("UI Dispatcher unhandled exception", args.Exception);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                LoggerService.Error("AppDomain unhandled exception", ex);
        };

        try
        {
            var settings = SettingsService.Load(out bool isFirstRun);
            SettingsService.ApplyAutostart(settings.StartWithWindows);

            _mainWindow = new MainWindow(settings);
            _mainWindow.Show();
            if (isFirstRun)
                _mainWindow.OpenTab("Apps");

            // Tray icon intentionally disabled by user request
            LoggerService.Info("DesktopIsland started successfully.");
        }
        catch (Exception ex)
        {
            LoggerService.Error("Fatal startup error", ex);
            System.Windows.MessageBox.Show($"Ошибка запуска Desktop Island:\n{ex.Message}", "Desktop Island", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    private void InitTrayIcon()
    {
        Icon trayIco = LoadTrayIcon();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Развернуть / Свернуть", null, (_, _) => Dispatcher.Invoke(() => _mainWindow?.ToggleExpandPin()));
        var topItem = new Forms.ToolStripMenuItem("Поверх всех окон")
        {
            Checked = _mainWindow?.IsAlwaysOnTop ?? true,
            CheckOnClick = true
        };
        menu.Opening += (_, _) => topItem.Checked = _mainWindow?.IsAlwaysOnTop ?? true;
        topItem.Click += (_, _) => Dispatcher.Invoke(() => _mainWindow?.SetAlwaysOnTop(topItem.Checked));
        menu.Items.Add(topItem);
        menu.Items.Add("⚡ Оптимизировать ОЗУ (RAM)", null, (_, _) => Dispatcher.Invoke(() => _ = _mainWindow?.RunOptimizeMemoryAsync()));
        menu.Items.Add("Пульт и устройства", null, (_, _) => Dispatcher.Invoke(() => _mainWindow?.OpenTab("Remote")));
        menu.Items.Add("Очистка системы", null, (_, _) => Dispatcher.Invoke(() => _mainWindow?.OpenTab("Cleaner")));
        menu.Items.Add("Настройки", null, (_, _) => Dispatcher.Invoke(() => _mainWindow?.OpenSettingsWindow()));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => Dispatcher.Invoke(ShutdownApplication));

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = trayIco,
            Text = "Desktop Island",
            Visible = true,
            ContextMenuStrip = menu
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(() => _mainWindow?.ToggleExpandPin());
    }

    public void RefreshTrayIcon()
    {
        // Tray icon disabled
    }

    private static Icon LoadTrayIcon()
    {
        try
        {
            var streamInfo = GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            if (streamInfo?.Stream != null)
            {
                using var s = streamInfo.Stream;
                using var raw = new Icon(s);
                return new Icon(raw, new System.Drawing.Size(32, 32));
            }
        }
        catch { }

        try
        {
            var icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(icoPath))
            {
                using var raw = new Icon(icoPath);
                return new Icon(raw, new System.Drawing.Size(32, 32));
            }
        }
        catch { }

        try
        {
            using var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using var bg = new SolidBrush(Color.FromArgb(20, 25, 36));
                using var pen = new Pen(Color.FromArgb(56, 189, 248), 2.5f);
                g.FillEllipse(bg, 2, 6, 28, 20);
                g.DrawEllipse(pen, 2, 6, 28, 20);
                using var dot = new SolidBrush(Color.FromArgb(56, 189, 248));
                g.FillEllipse(dot, 8, 12, 7, 7);
            }
            return Icon.FromHandle(bmp.GetHicon());
        }
        catch
        {
            return SystemIcons.Application;
        }
    }

    private static void SignalExistingInstance()
    {
        try
        {
            using var ev = EventWaitHandle.OpenExisting(ActivateEventName);
            ev.Set();
            LoggerService.Info("Second instance launched -> signaled running instance to show.");
        }
        catch (Exception ex)
        {
            LoggerService.Error("Second instance could not signal existing instance.", ex);
        }
    }

    public void ShutdownApplication()
    {
        try
        {
            LoggerService.Info("DesktopIsland shutting down.");
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                _trayIcon = null;
            }
            _mainWindow?.CleanupBeforeExit();
            _mainWindow?.Close();
            _activateWaitHandle?.Unregister(null);
            _activateWaitHandle = null;
            _activateEvent?.Dispose();
            _activateEvent = null;
            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
        }
        catch { }
        Shutdown();
    }
}
