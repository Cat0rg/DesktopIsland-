using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using DesktopIsland.Models;
using DesktopIsland.Services;
using DesktopIsland.Views;
using Forms = System.Windows.Forms;
using Win32 = Microsoft.Win32;
using MediaColor = System.Windows.Media.Color;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaPoint = System.Windows.Point;
using IOPath = System.IO.Path;

namespace DesktopIsland;

public partial class MainWindow : Window
{
    private const double CompactHeight = 36.0;
    private const double ExpandedWidth = 442.0;

    private const int WmDeviceChange = 0x0219;
    private const int DbtDeviceArrival = 0x8000;
    private const int DbtDevtypVolume = 0x00000002;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint lpPoint);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExAppWindow = 0x00040000L;
    private const int VkMenu = 0x12; // Alt key
    private const int WmHotkey = 0x0312;
    private const int HotkeyToggleTopmostId = 0x4449;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint VkKeyD = 0x44; // Ctrl + Alt + D

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEV_BROADCAST_HDR
    {
        public int dbch_size;
        public int dbch_devicetype;
        public int dbch_reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEV_BROADCAST_VOLUME
    {
        public int dbcv_size;
        public int dbcv_devicetype;
        public int dbcv_reserved;
        public int dbcv_unitmask;
        public short dbcv_flags;
    }

    private AppSettings _settings;
    private readonly SystemMonitorService _sysMonitor = new();
    private readonly ObservableCollection<CleanupCategory> _cleanupCategories = new();
    private readonly ObservableCollection<DriveMetric> _drives = new();
    private readonly ObservableCollection<ProcessMetricItem> _topProcesses = new();
    private readonly ObservableCollection<RecentDownloadItem> _recentDownloads = new();
    private readonly ObservableCollection<BluetoothDeviceItem> _btDevices = new();
    private readonly ObservableCollection<ScreenTimeItem> _screenTimeItems = new();
    private readonly Dictionary<string, ImageSource?> _iconCache = new(StringComparer.OrdinalIgnoreCase);

    private sealed class ScreenTimeAccumulator
    {
        public double Seconds;
        public bool IsGame;
        public double FpsSum;
        public int FpsSamples;
    }

    private readonly Dictionary<string, ScreenTimeAccumulator> _screenTimeStats = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _islandSessionStartUtc = DateTime.UtcNow;
    private DateTime _lastScreenTimeTickUtc = DateTime.UtcNow;
    private string _lastTrackedGameName = string.Empty;
    private double _lastTrackedGameFps;
    private double? _lastTrackedGameGpu;
    private double? _lastTrackedGameTemp;
    private int _lastTrackedGamePing = -1;

    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _monitorTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _collapseDelayTimer = new() { Interval = TimeSpan.FromMilliseconds(340) };
    private readonly DispatcherTimer _eqTimer = new() { Interval = TimeSpan.FromMilliseconds(95) };
    private readonly DispatcherTimer _ramLabelResetTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3.2) };
    private readonly DispatcherTimer _weatherTimer = new() { Interval = TimeSpan.FromMinutes(30) };
    private readonly DispatcherTimer _gameInputTimer = new() { Interval = TimeSpan.FromMilliseconds(110) };
    private readonly DispatcherTimer _borderAlertResetTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    private HwndSource? _hwndSource;
    private uint _taskbarCreatedMsg;
    private IntPtr _ownHwnd;
    private IntPtr _lastExternalHwnd;

    private bool _isExpanded;
    private bool _isPinned;
    private bool _isOptimizingRam;
    private bool _isShowingRamResult;
    private bool _isAppsEditMode;
    private bool _isBtExpanded;
    private bool _isUpdatingVolumeSlider;
    private bool _isInFullscreenGame;
    private bool _isInFullscreenVideo;
    private bool _isMediaPlaying;
    private bool _isCurrentlyClickThrough;
    private bool _isAltHeldInGame;
    private bool _isBorderAlertActive;
    private DateTime _lastGameAlertAt = DateTime.MinValue;
    private DateTime _lastGameBoostAt = DateTime.MinValue;
    private DateTime? _gameSessionStartUtc;
    private DateTime _lastGameExitUtc = DateTime.MinValue;
    private uint _gameSessionPid;

    private List<BluetoothDeviceItem> _allBtDevices = new();

    private DateTime _lastAutoRamAt = DateTime.MinValue;
    private DateTime _lastBtPollAt = DateTime.MinValue;
    private HashSet<string> _connectedBtNames = new(StringComparer.OrdinalIgnoreCase);
    private WeatherInfo? _currentWeather;
    private Action? _toastAction;

    private DateTime? _countdownEndUtc;

    // Horizontal X drag
    private bool _isWindowDragging;
    private bool _didHorizontalDrag;
    private NativePoint _dragStartCursor;
    private double _dragStartOffset;
    private double _dragStartWindowLeft;

    private Border? _reorderingTile;
    private PinnedApp? _reorderingApp;
    private MediaPoint _reorderStartPoint;
    private bool _isReorderingApp;
    private bool _suppressReorderLostCapture;
    private bool _isRamHovered;
    private bool _isMicHovered;

    public bool IsAlwaysOnTop => _settings.AlwaysOnTop;

    public MainWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        DrivesItemsControl.ItemsSource = _drives;
        TopProcessesList.ItemsSource = _topProcesses;
        RecentDownloadsList.ItemsSource = _recentDownloads;
        BtDevicesList.ItemsSource = _btDevices;
        ScreenTimeListControl.ItemsSource = _screenTimeItems;
        CleanupItemsControl.ItemsSource = _cleanupCategories;

        _clockTimer.Tick += ClockTimer_Tick;
        _monitorTimer.Tick += MonitorTimer_Tick;
        _collapseDelayTimer.Tick += CollapseDelayTimer_Tick;
        _eqTimer.Tick += EqTimer_Tick;
        _ramLabelResetTimer.Tick += RamLabelResetTimer_Tick;
        _toastTimer.Tick += ToastTimer_Tick;
        _weatherTimer.Tick += async (_, _) => await RefreshWeatherAsync();
        _gameInputTimer.Tick += GameInputTimer_Tick;
        _borderAlertResetTimer.Tick += (_, _) =>
        {
            _borderAlertResetTimer.Stop();
            _isBorderAlertActive = false;
            ApplyCurrentIslandBorder();
        };

        ApplySettingsVisuals();
        RebuildAppTiles();
        UpdateRamPillState(animate: false);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var helper = new WindowInteropHelper(this);
        _ownHwnd = helper.Handle;
        _taskbarCreatedMsg = RegisterWindowMessage("TaskbarCreated");

        // Hide DesktopIsland completely from Alt+Tab and Task View
        try
        {
            long ex = GetWindowLongPtr(_ownHwnd, GwlExStyle).ToInt64();
            ex = (ex | WsExToolWindow) & ~WsExAppWindow;
            SetWindowLongPtr(_ownHwnd, GwlExStyle, new IntPtr(ex));
        }
        catch { }

        _hwndSource = HwndSource.FromHwnd(_ownHwnd);
        _hwndSource?.AddHook(WindowWndProc);
        RegisterHotKey(_ownHwnd, HotkeyToggleTopmostId, ModControl | ModAlt, VkKeyD);

        PositionTopCenter();
        UpdateClockDisplay();
        _clockTimer.Start();

        _monitorTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(_settings.PollingIntervalMs, 1000, 10000));
        _monitorTimer.Start();
        _eqTimer.Start();
        _weatherTimer.Start();

        _ = RefreshSystemAndMediaAsync();
        RefreshDrives();
        _ = RefreshWeatherAsync();
        _ = RefreshCleanupScanAsync();

        UpdateCompactWidth(animate: false);

        if (!SettingsService.IsRunningAsAdministrator())
        {
            AdminWarningBanner.Visibility = Visibility.Visible;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                ShowToast("🛡", "Лучше запустить от администратора", "От админа", RestartAsAdmin, durationMs: 8500));
        }
    }

    private IntPtr WindowWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyToggleTopmostId)
        {
            handled = true;
            Dispatcher.BeginInvoke(ToggleTopmostFromHotkey);
            return IntPtr.Zero;
        }
        if (_taskbarCreatedMsg != 0 && msg == (int)_taskbarCreatedMsg)
        {
            Dispatcher.BeginInvoke(() => (System.Windows.Application.Current as App)?.RefreshTrayIcon());
        }
        else if (msg == WmDeviceChange && wParam.ToInt32() == DbtDeviceArrival && lParam != IntPtr.Zero)
        {
            try
            {
                var hdr = Marshal.PtrToStructure<DEV_BROADCAST_HDR>(lParam);
                if (hdr.dbch_devicetype == DbtDevtypVolume)
                {
                    var vol = Marshal.PtrToStructure<DEV_BROADCAST_VOLUME>(lParam);
                    var driveLetter = FirstDriveFromMask(vol.dbcv_unitmask);
                    if (!string.IsNullOrEmpty(driveLetter))
                    {
                        var root = $"{driveLetter}:\\";
                        Dispatcher.BeginInvoke(() =>
                        {
                            RefreshDrives();
                            ShowToast("💾", $"Подключен диск ({driveLetter}:)", "Открыть", () => OpenShellTarget(root), 4200);
                        });
                    }
                }
            }
            catch { }
        }
        return IntPtr.Zero;
    }

    private static string FirstDriveFromMask(int unitmask)
    {
        for (int i = 0; i < 26; i++)
        {
            if ((unitmask & (1 << i)) != 0)
                return ((char)('A' + i)).ToString();
        }
        return string.Empty;
    }

    #region Theme, Positioning & Collapse/Expand Animation

    private void ApplyThemeBrushes()
    {
        var (primary, bright) = ThemeHelper.GetAccentColors(_settings.AccentTheme);
        Resources["AccentBrush"] = new SolidColorBrush(primary);
        Resources["AccentBrightBrush"] = new SolidColorBrush(bright);
        Resources["AccentSoftBrush"] = new SolidColorBrush(MediaColor.FromArgb(46, primary.R, primary.G, primary.B));
    }

    private void ApplySettingsVisuals()
    {
        ApplyThemeBrushes();
        Topmost = _settings.AlwaysOnTop;
        UpdateEyeButtonVisual();
        UpdateWindowOpacity();

        _monitorTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(_settings.PollingIntervalMs, 1000, 10000));
        _collapseDelayTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(_settings.HoverOutDelayMs, 150, 2000));

        ApplyGameVisualChrome();
        CompactNetText.Visibility = (_settings.CompactShowNetwork && _settings.EnableNetworkMonitor)
            ? Visibility.Visible : Visibility.Collapsed;

        EditAppsBtn.Visibility = (TabApps.IsChecked == true && _isExpanded)
            ? Visibility.Visible : Visibility.Collapsed;

        UpdateCenterButtonVisibility();
        PositionTopCenter();
    }

    private bool IsDockedToTop => _settings.DockToTopEdge || (_isInFullscreenGame && _settings.GameModeAutoProfile);
    private bool IsGameNotchCollapsed => _isInFullscreenGame && _settings.GameModeAutoProfile && !_isExpanded;

    private void ApplyGameVisualChrome()
    {
        bool gameNotch = IsGameNotchCollapsed;
        bool dockTop = IsDockedToTop;
        bool gameMode = _isInFullscreenGame && _settings.GameModeAutoProfile;

        CompactContentGrid.Height = gameNotch ? 30.0 : CompactHeight;
        CompactWeatherText.Visibility = (!gameNotch && _settings.CompactShowWeather && _currentWeather != null)
            ? Visibility.Visible : Visibility.Collapsed;

        GameMicPill.Visibility = (gameMode && _settings.GameShowMicMute)
            ? Visibility.Visible : Visibility.Collapsed;

        RamQuickPill.Margin = dockTop ? new Thickness(6, 0, 0, 0) : new Thickness(6, 2, 0, 0);
        UpdateRamPillState(animate: false);
        UpdateMicPillState(animate: false);
        SyncMicMuteUi();

        IslandBorder.BorderThickness = dockTop ? new Thickness(1, 0, 1, 1) : new Thickness(1);
        if (!_isExpanded)
            UpdateCompactWidth(animate: false);

        if (_ownHwnd != IntPtr.Zero)
        {
            bool clickThrough = gameMode && _settings.GameClickThrough && !_isExpanded && !_isAltHeldInGame;
            SetWindowClickThrough(clickThrough);
        }
        ApplyCurrentIslandBorder();
    }

    private void SetWindowClickThrough(bool enable)
    {
        if (_ownHwnd == IntPtr.Zero || _isCurrentlyClickThrough == enable) return;
        try
        {
            long ex = GetWindowLongPtr(_ownHwnd, GwlExStyle).ToInt64();
            ex = enable ? (ex | WsExTransparent) : (ex & ~WsExTransparent);
            SetWindowLongPtr(_ownHwnd, GwlExStyle, new IntPtr(ex));
            _isCurrentlyClickThrough = enable;
            if (enable && (_isRamHovered || _isMicHovered))
            {
                _isRamHovered = false;
                _isMicHovered = false;
                RamQuickPill.BorderBrush = new SolidColorBrush(MediaColor.FromRgb(46, 50, 69));
                RamLightningPath.Stroke = MediaBrushes.Transparent;
                SyncMicMuteUi();
                UpdateRamPillState(animate: true);
                UpdateMicPillState(animate: true);
            }
        }
        catch { }
    }

    private void GameInputTimer_Tick(object? sender, EventArgs e)
    {
        if (!_isInFullscreenGame)
        {
            _gameInputTimer.Stop();
            return;
        }

        bool altHeld = (GetAsyncKeyState(VkMenu) & 0x8000) != 0;
        if (altHeld != _isAltHeldInGame)
        {
            _isAltHeldInGame = altHeld;
            ApplyCurrentIslandBorder();
        }

        bool clickThrough = _settings.GameModeAutoProfile && _settings.GameClickThrough && !_isExpanded && !altHeld;
        SetWindowClickThrough(clickThrough);
    }

    private void ApplyCurrentIslandBorder()
    {
        if (_isBorderAlertActive) return;
        if (_isInFullscreenGame && _settings.GameModeAutoProfile && _isAltHeldInGame)
            IslandBorder.BorderBrush = new SolidColorBrush(MediaColor.FromRgb(56, 189, 248));
        else
            IslandBorder.BorderBrush = new SolidColorBrush(MediaColor.FromRgb(46, 50, 69));
    }

    private void FlashIslandBorder(MediaColor color)
    {
        _isBorderAlertActive = true;
        IslandBorder.BorderBrush = new SolidColorBrush(color);
        _borderAlertResetTimer.Stop();
        _borderAlertResetTimer.Start();
    }

    private void GameMicPill_Click(object sender, MouseButtonEventArgs e)
    {
        bool nextMuted = CoreAudioService.ToggleMicMute();
        SyncMicMuteUi();
        ShowToast(nextMuted ? "🔇" : "🎙", nextMuted ? "Микрофон выключен" : "Микрофон включён", durationMs: 2000);
        e.Handled = true;
    }

    private void GameMicPill_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _isMicHovered = true;
        if (!CoreAudioService.IsMicMuted())
            GameMicPill.SetResourceReference(Border.BorderBrushProperty, "AccentBrightBrush");
        UpdateMicPillState(animate: true);
    }

    private void GameMicPill_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _isMicHovered = false;
        SyncMicMuteUi();
        UpdateMicPillState(animate: true);
    }

    private void UpdateMicPillState(bool animate)
    {
        bool dockTop = IsDockedToTop;
        bool fullHeight = _isMicHovered || _isExpanded;
        double targetHeight = fullHeight ? 30.0 : 15.0;
        double radius = fullHeight ? 15.0 : 9.0;

        GameMicPill.Margin = dockTop ? new Thickness(5, 0, 0, 0) : new Thickness(5, 2, 0, 0);
        GameMicPill.CornerRadius = dockTop
            ? new CornerRadius(0, 0, radius, radius)
            : new CornerRadius(fullHeight ? 15.0 : 7.5);
        GameMicPill.BorderThickness = dockTop ? new Thickness(1, 0, 1, 1) : new Thickness(1);
        GameMicViewbox.Width = fullHeight ? 13.0 : 9.5;
        GameMicViewbox.Height = fullHeight ? 13.0 : 9.5;

        var dur = _settings.EnableAnimations && animate
            ? TimeSpan.FromMilliseconds(180)
            : TimeSpan.Zero;

        if (dur == TimeSpan.Zero)
        {
            GameMicPill.BeginAnimation(HeightProperty, null);
            GameMicPill.Height = targetHeight;
        }
        else
        {
            var anim = new DoubleAnimation(targetHeight, dur)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            GameMicPill.BeginAnimation(HeightProperty, anim);
        }
    }

    private void SyncMicMuteUi()
    {
        bool muted = CoreAudioService.IsMicMuted();
        if (muted)
        {
            MicCapsulePath.Fill = new SolidColorBrush(MediaColor.FromRgb(248, 113, 113));
            MicStandPath.Stroke = new SolidColorBrush(MediaColor.FromRgb(248, 113, 113));
            MicSlashLine.Visibility = Visibility.Visible;
            GameMicPill.BorderBrush = new SolidColorBrush(MediaColor.FromRgb(239, 68, 68));
            GameMicPill.ToolTip = "Микрофон ВЫКЛ (Alt + клик — включить)";
        }
        else
        {
            MicCapsulePath.Fill = new SolidColorBrush(MediaColor.FromRgb(226, 232, 240));
            MicStandPath.Stroke = new SolidColorBrush(MediaColor.FromRgb(226, 232, 240));
            MicSlashLine.Visibility = Visibility.Collapsed;
            GameMicPill.BorderBrush = new SolidColorBrush(MediaColor.FromRgb(46, 50, 69));
            GameMicPill.ToolTip = "Микрофон ВКЛ (Alt + клик — выключить)";
        }
    }

    private void UpdateCenterButtonVisibility()
    {
        CenterPosBtn.Visibility = Math.Abs(_settings.HorizontalOffsetPx) > 8
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateWindowOpacity()
    {
        // Expanded island is ALWAYS crisp & opaque so content is 100% readable
        if (_isExpanded)
        {
            RootCanvasContainer.Opacity = 1.0;
            return;
        }
        if (_isInFullscreenVideo && _settings.DimOnFullscreenVideo)
        {
            RootCanvasContainer.Opacity = 0.32;
            return;
        }
        RootCanvasContainer.Opacity = Math.Clamp(_settings.CapsuleOpacity, 0.72, 1.0);
    }

    private Forms.Screen GetTargetScreen()
    {
        var screens = Forms.Screen.AllScreens;
        if (_settings.MonitorIndex >= 0 && _settings.MonitorIndex < screens.Length)
            return screens[_settings.MonitorIndex];
        return Forms.Screen.PrimaryScreen ?? screens[0];
    }

    private void PositionTopCenter()
    {
        try
        {
            var screen = GetTargetScreen();
            var dpiScale = VisualTreeHelper.GetDpi(this);
            double scaleX = dpiScale.DpiScaleX <= 0 ? 1.0 : dpiScale.DpiScaleX;
            double scaleY = dpiScale.DpiScaleY <= 0 ? 1.0 : dpiScale.DpiScaleY;

            double screenLeft = screen.Bounds.Left / scaleX;
            double screenTop = screen.Bounds.Top / scaleY;
            double screenWidth = screen.Bounds.Width / scaleX;

            double pillW = double.IsNaN(RamQuickPill.Width) ? 74 : RamQuickPill.Width;
            double activeContentWidth = (_isExpanded ? ExpandedWidth : IslandBorder.Width) + pillW + 14;
            if (activeContentWidth < 260) activeContentWidth = 420;

            double maxOffset = Math.Max(40, (screenWidth - activeContentWidth) / 2.0 - 16);
            double clampedOffset = Math.Clamp(_settings.HorizontalOffsetPx, -maxOffset, maxOffset);
            if (_isWindowDragging)
                _settings.HorizontalOffsetPx = clampedOffset;

            bool dockTop = IsDockedToTop;
            RootCanvasContainer.Margin = dockTop ? new Thickness(2, 0, 2, 2) : new Thickness(2);
            IslandHoverZone.Padding = dockTop ? new Thickness(2, 0, 2, 2) : new Thickness(2);

            Left = screenLeft + (screenWidth - ActualWidth) / 2.0 + clampedOffset;
            Top = dockTop ? screenTop : screenTop + Math.Clamp(_settings.TopOffsetPx, 0, 120);
        }
        catch { }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        PositionTopCenter();
    }

    private double MeasureCompactWidth()
    {
        CompactMetricsStrip.Measure(new System.Windows.Size(double.PositiveInfinity, CompactHeight));
        double contentW = CompactMetricsStrip.DesiredSize.Width;
        if (CompactToastStrip.Visibility == Visibility.Visible)
        {
            CompactToastStrip.Measure(new System.Windows.Size(double.PositiveInfinity, CompactHeight));
            contentW = Math.Max(contentW, CompactToastStrip.DesiredSize.Width + 24);
        }
        return Math.Clamp(Math.Ceiling(contentW + 4), 190, 520);
    }

    private void UpdateCompactWidth(bool animate)
    {
        if (_isExpanded) return;
        double targetW = MeasureCompactWidth();
        bool dockTop = IsDockedToTop;
        bool gameNotch = IsGameNotchCollapsed;
        double h = gameNotch ? 30.0 : CompactHeight;
        double radius = gameNotch ? 15.0 : 18.0;
        var corner = dockTop ? new CornerRadius(0, 0, radius, radius) : new CornerRadius(18.0);
        AnimateIslandSize(targetW, h, corner, animate);
    }

    private void SetExpanded(bool expand)
    {
        if (_isExpanded == expand) return;
        _isExpanded = expand;

        UpdateWindowOpacity();
        UpdateRamPillState(animate: true);
        UpdateMicPillState(animate: true);
        if (_isInFullscreenGame && _settings.GameModeAutoProfile)
            SetWindowClickThrough(!expand && _settings.GameClickThrough && !_isAltHeldInGame);

        IslandBorder.BorderThickness = IsDockedToTop ? new Thickness(1, 0, 1, 1) : new Thickness(1);

        if (expand)
        {
            _collapseDelayTimer.Stop();
            CompactContentGrid.Visibility = Visibility.Collapsed;
            ExpandedContentGrid.Visibility = Visibility.Visible;
            EditAppsBtn.Visibility = TabApps.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

            if (TabSystem.IsChecked == true) RefreshDrives();
            if (TabScreenTime.IsChecked == true) RefreshScreenTimeUi();
            if (TabRemote.IsChecked == true) _ = RefreshMediaAndDevicesAsync(forceBluetooth: true);
            if (TabCleaner.IsChecked == true) _ = RefreshCleanupScanAsync();

            ScheduleExpandedResize(animate: true);
        }
        else
        {
            _isAppsEditMode = false;
            _isBtExpanded = false;
            EditAppsBtn.IsChecked = false;
            EditAppsBtn.Visibility = Visibility.Collapsed;
            RebuildAppTiles();

            ExpandedContentGrid.Visibility = Visibility.Collapsed;
            CompactContentGrid.Visibility = Visibility.Visible;
            UpdateCompactWidth(animate: true);
        }
    }

    private void ScheduleExpandedResize(bool animate)
    {
        if (!_isExpanded) return;
        UpdateExpandedBounds(animate);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => UpdateExpandedBounds(animate));
    }

    private void UpdateExpandedBounds(bool animate)
    {
        if (!_isExpanded) return;
        ExpandedContentGrid.Width = ExpandedWidth - 28;
        ExpandedContentGrid.Height = double.NaN;
        ExpandedContentGrid.InvalidateMeasure();
        ExpandedContentGrid.Measure(new System.Windows.Size(ExpandedWidth, double.PositiveInfinity));

        double measuredH = ExpandedContentGrid.DesiredSize.Height;
        double targetHeight = Math.Clamp(Math.Ceiling(measuredH + 2), 120, 760);
        var expandedCorner = IsDockedToTop ? new CornerRadius(0, 0, 22, 22) : new CornerRadius(22.0);
        AnimateIslandSize(ExpandedWidth, targetHeight, expandedCorner, animate);
    }

    private void AnimateIslandSize(double targetW, double targetH, CornerRadius targetCorner, bool animate)
    {
        var dur = _settings.EnableAnimations && animate
            ? TimeSpan.FromMilliseconds(Math.Clamp(_settings.AnimationDurationMs, 100, 450))
            : TimeSpan.Zero;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        IslandHoverZone.CornerRadius = new CornerRadius(
            targetCorner.TopLeft > 0 ? targetCorner.TopLeft + 2 : 0,
            targetCorner.TopRight > 0 ? targetCorner.TopRight + 2 : 0,
            targetCorner.BottomRight + 2,
            targetCorner.BottomLeft + 2);

        if (dur == TimeSpan.Zero)
        {
            IslandBorder.BeginAnimation(WidthProperty, null);
            IslandBorder.BeginAnimation(HeightProperty, null);
            IslandBorder.Width = targetW;
            IslandBorder.Height = targetH;
            IslandBorder.CornerRadius = targetCorner;
            PositionTopCenter();
            return;
        }

        var wAnim = new DoubleAnimation(targetW, dur) { EasingFunction = ease };
        var hAnim = new DoubleAnimation(targetH, dur) { EasingFunction = ease };
        wAnim.Completed += (_, _) => PositionTopCenter();
        IslandBorder.BeginAnimation(WidthProperty, wAnim);
        IslandBorder.BeginAnimation(HeightProperty, hAnim);
        IslandBorder.CornerRadius = targetCorner;
    }

    private void UpdateRamPillState(bool animate)
    {
        bool dockTop = IsDockedToTop;
        bool gameNotch = IsGameNotchCollapsed;
        bool showText = _isExpanded || _isShowingRamResult;
        bool fullHeight = _isRamHovered || _isExpanded || _isShowingRamResult;

        RamQuickText.Visibility = showText ? Visibility.Visible : Visibility.Collapsed;
        if (!_isShowingRamResult)
            RamQuickText.Text = "ОЗУ";

        double normalHeight = (gameNotch && !_isShowingRamResult) ? 30.0 : 36.0;
        double targetHeight = fullHeight ? normalHeight : normalHeight / 2.0;
        double targetWidth = showText ? (_isShowingRamResult ? 78.0 : 72.0) : normalHeight;

        RamQuickPill.Margin = dockTop ? new Thickness(6, 0, 0, 0) : new Thickness(6, 2, 0, 0);
        if (dockTop)
        {
            double r = fullHeight ? targetHeight / 2.0 : (gameNotch ? 9.0 : 10.0);
            RamQuickPill.CornerRadius = new CornerRadius(0, 0, r, r);
            RamQuickPill.BorderThickness = new Thickness(1, 0, 1, 1);
        }
        else
        {
            RamQuickPill.CornerRadius = new CornerRadius(targetHeight / 2.0);
            RamQuickPill.BorderThickness = new Thickness(1);
        }

        RamLightningPath.Width = fullHeight ? (gameNotch ? 12.0 : 13.0) : (gameNotch ? 8.5 : 9.0);
        RamLightningPath.Height = fullHeight ? (gameNotch ? 14.0 : 15.0) : (gameNotch ? 10.0 : 10.5);

        var dur = _settings.EnableAnimations && animate
            ? TimeSpan.FromMilliseconds(180)
            : TimeSpan.Zero;

        if (dur == TimeSpan.Zero)
        {
            RamQuickPill.BeginAnimation(WidthProperty, null);
            RamQuickPill.BeginAnimation(HeightProperty, null);
            RamQuickPill.Width = targetWidth;
            RamQuickPill.Height = targetHeight;
        }
        else
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            RamQuickPill.BeginAnimation(WidthProperty, new DoubleAnimation(targetWidth, dur) { EasingFunction = ease });
            RamQuickPill.BeginAnimation(HeightProperty, new DoubleAnimation(targetHeight, dur) { EasingFunction = ease });
        }
    }

    #endregion

    #region Hover, Wheel Volume, Drag X & Anti-Misclick

    private void IslandHoverZone_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _collapseDelayTimer.Stop();
        if (_isInFullscreenGame && _settings.GameModeAutoProfile && !_isPinned)
            return;
        if (_settings.ExpandOnHover && !_isExpanded)
            SetExpanded(true);
    }

    private void IslandHoverZone_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_isReorderingApp || _isWindowDragging) return;
        if (_isExpanded && !_isPinned)
            _collapseDelayTimer.Start();
    }

    private void RamQuickPill_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _isRamHovered = true;
        // Blue outline around circle + yellow outline on lightning bolt; NEVER expands island!
        RamQuickPill.SetResourceReference(Border.BorderBrushProperty, "AccentBrightBrush");
        RamLightningPath.Stroke = new SolidColorBrush(MediaColor.FromRgb(250, 204, 21));
        UpdateRamPillState(animate: true);
    }

    private void RamQuickPill_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _isRamHovered = false;
        RamQuickPill.BorderBrush = new SolidColorBrush(MediaColor.FromRgb(46, 50, 69));
        RamLightningPath.Stroke = MediaBrushes.Transparent;
        UpdateRamPillState(animate: true);
    }

    private void CollapseDelayTimer_Tick(object? sender, EventArgs e)
    {
        _collapseDelayTimer.Stop();
        if (_isReorderingApp || _isWindowDragging) return;
        if (!_isPinned && !IslandHoverZone.IsMouseOver)
            SetExpanded(false);
    }

    private void Window_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        int delta = e.Delta > 0 ? 3 : -3;
        int vol = CoreAudioService.ChangeMasterVolume(delta);
        SyncVolumeUi(vol);
        string icon = vol == 0 ? "🔇" : (vol < 40 ? "🔈" : (vol < 75 ? "🔉" : "🔊"));
        ShowToast(icon, $"Громкость: {vol}%", durationMs: 2200);
        e.Handled = true;
    }

    private void Island_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isReorderingApp) return;
        if (IsInteractiveAncestor(e.OriginalSource as DependencyObject))
            return;

        if (e.ClickCount == 2)
        {
            ResetIslandCenter();
            e.Handled = true;
            return;
        }

        GetCursorPos(out _dragStartCursor);
        _dragStartOffset = _settings.HorizontalOffsetPx;
        _dragStartWindowLeft = Left;
        _isWindowDragging = true;
        _didHorizontalDrag = false;
    }

    private void Island_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isWindowDragging) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            FinishHorizontalDrag();
            return;
        }

        GetCursorPos(out var now);
        var dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double dx = (now.X - _dragStartCursor.X) / (dpiScale <= 0 ? 1.0 : dpiScale);

        if (!_didHorizontalDrag && Math.Abs(dx) >= 5)
        {
            _didHorizontalDrag = true;
            IslandBorder.CaptureMouse();
        }

        if (_didHorizontalDrag)
        {
            _collapseDelayTimer.Stop();
            _settings.HorizontalOffsetPx = _dragStartOffset + dx;
            PositionTopCenter();
            UpdateCenterButtonVisibility();
            e.Handled = true;
        }
    }

    private void Island_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isWindowDragging) return;
        bool wasDrag = _didHorizontalDrag;
        FinishHorizontalDrag();

        if (wasDrag)
        {
            SettingsService.Save(_settings);
            e.Handled = true;
        }
        else if (!_isExpanded)
        {
            SetExpanded(true);
            e.Handled = true;
        }
    }

    private void FinishHorizontalDrag()
    {
        _isWindowDragging = false;
        _didHorizontalDrag = false;
        if (IslandBorder.IsMouseCaptured)
            IslandBorder.ReleaseMouseCapture();
    }

    private void CenterPosBtn_Click(object sender, RoutedEventArgs e) => ResetIslandCenter();

    private void ResetIslandCenter()
    {
        _settings.HorizontalOffsetPx = 0;
        SettingsService.Save(_settings);
        UpdateCenterButtonVisibility();
        PositionTopCenter();
        ShowToast("⌖", "Островок по центру", durationMs: 2000);
    }

    private static DependencyObject? SafeGetParent(DependencyObject? d) => d switch
    {
        null => null,
        Visual or System.Windows.Media.Media3D.Visual3D => VisualTreeHelper.GetParent(d),
        _ => LogicalTreeHelper.GetParent(d)
    };

    private static bool IsInteractiveAncestor(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is System.Windows.Controls.Primitives.ButtonBase ||
                d is System.Windows.Controls.Slider ||
                d is System.Windows.Controls.TextBox ||
                (d is Border b && b.Tag is PinnedApp))
                return true;
            d = SafeGetParent(d);
        }
        return false;
    }

    private static bool HasButtonAncestor(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is System.Windows.Controls.Primitives.ButtonBase)
                return true;
            d = SafeGetParent(d);
        }
        return false;
    }

    #endregion

    #region Dynamic Toast Banner

    private void ShowToast(string icon, string message, string? actionLabel = null, Action? onAction = null, int durationMs = 3000)
    {
        CompactToastIcon.Text = icon;
        CompactToastText.Text = message;
        _toastAction = onAction;

        if (!string.IsNullOrEmpty(actionLabel) && onAction != null)
        {
            CompactToastActionBtn.Content = actionLabel;
            CompactToastActionBtn.Visibility = Visibility.Visible;
        }
        else
        {
            CompactToastActionBtn.Visibility = Visibility.Collapsed;
        }

        CompactMetricsStrip.Visibility = Visibility.Collapsed;
        CompactToastStrip.Visibility = Visibility.Visible;

        ExpandedToastText.Text = $"{icon} {message}";
        ExpandedToastPill.Visibility = Visibility.Visible;

        if (!_isExpanded) UpdateCompactWidth(animate: true);

        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromMilliseconds(durationMs);
        _toastTimer.Start();
    }

    private void ToastTimer_Tick(object? sender, EventArgs e)
    {
        _toastTimer.Stop();
        CompactToastStrip.Visibility = Visibility.Collapsed;
        CompactMetricsStrip.Visibility = Visibility.Visible;
        ExpandedToastPill.Visibility = Visibility.Collapsed;
        _toastAction = null;
        if (!_isExpanded) UpdateCompactWidth(animate: true);
    }

    private void CompactToastAction_Click(object sender, RoutedEventArgs e)
    {
        var act = _toastAction;
        ToastTimer_Tick(null, EventArgs.Empty);
        act?.Invoke();
    }

    #endregion

    #region Timers, Monitoring, Privacy, Fullscreen Context & Auto-RAM

    private void ClockTimer_Tick(object? sender, EventArgs e)
    {
        UpdateClockDisplay();

        var fg = GetForegroundWindow();
        if (fg != IntPtr.Zero && fg != _ownHwnd)
            _lastExternalHwnd = fg;

        if (_countdownEndUtc.HasValue)
        {
            var left = _countdownEndUtc.Value - DateTime.UtcNow;
            if (left <= TimeSpan.Zero)
            {
                _countdownEndUtc = null;
                CompactTimerPill.Visibility = Visibility.Collapsed;
                TimerStatusText.Text = "Выкл.";
                System.Media.SystemSounds.Asterisk.Play();
                ShowToast("⏰", "Таймер завершён!", durationMs: 4500);
            }
            else
            {
                var fmt = left.TotalHours >= 1
                    ? left.ToString(@"h\:mm\:ss")
                    : left.ToString(@"mm\:ss");
                CompactTimerText.Text = $"⏱ {fmt}";
                CompactTimerPill.Visibility = Visibility.Visible;
                TimerStatusText.Text = fmt;
            }
        }
    }

    private void UpdateClockDisplay()
    {
        var now = DateTime.Now;
        CompactTimeText.Text = now.ToString("HH:mm");
        ExpandedTimeText.Text = now.ToString("HH:mm:ss");
        ExpandedDateText.Text = now.ToString("dd MMM, ddd");
    }

    private async void MonitorTimer_Tick(object? sender, EventArgs e)
    {
        await RefreshSystemAndMediaAsync();
    }

    private async Task RefreshSystemAndMediaAsync()
    {
        try
        {
            var fgContext = PrivacyAndGameService.DetectFullscreenContext(_isMediaPlaying, Environment.ProcessId);
            bool gameChanged = _isInFullscreenGame != fgContext.IsFullscreenGame;
            bool videoChanged = _isInFullscreenVideo != fgContext.IsFullscreenVideo;
            _isInFullscreenGame = fgContext.IsFullscreenGame;
            _isInFullscreenVideo = fgContext.IsFullscreenVideo;
            bool includeGpu = _settings.GameShowGpuTemp &&
                              (_isInFullscreenGame || (_isExpanded && TabSystem.IsChecked == true));
            var metrics = await Task.Run(() => _sysMonitor.SampleSystemAndNetwork(includeGpu));

            if (gameChanged || videoChanged)
            {
                UpdateWindowOpacity();
                ApplyGameVisualChrome();
                PositionTopCenter();
            }

            if (gameChanged)
            {
                if (_isInFullscreenGame && _settings.GameModeAutoProfile)
                {
                    _gameInputTimer.Start();
                    if (_gameSessionStartUtc == null ||
                        (metrics.ActivePid != 0 && metrics.ActivePid != _gameSessionPid) ||
                        (DateTime.UtcNow - _lastGameExitUtc).TotalMinutes > 2)
                    {
                        _gameSessionStartUtc = DateTime.UtcNow;
                    }
                    if (metrics.ActivePid != 0) _gameSessionPid = metrics.ActivePid;

                    if (_settings.GameAutoBoost && (DateTime.UtcNow - _lastGameBoostAt).TotalSeconds >= 120)
                    {
                        _lastGameBoostAt = DateTime.UtcNow;
                        _ = RunGameAutoBoostAsync(metrics.ActivePid);
                    }
                }
                else
                {
                    _lastGameExitUtc = DateTime.UtcNow;
                    _gameInputTimer.Stop();
                    _isAltHeldInGame = false;
                    SetWindowClickThrough(false);
                    ApplyCurrentIslandBorder();
                    _ = Task.Run(QuickToolsService.RestorePreviousPowerPlan);
                }
            }

            if (_isInFullscreenGame && _settings.GameModeAutoProfile && _gameSessionStartUtc.HasValue)
            {
                var elapsed = DateTime.UtcNow - _gameSessionStartUtc.Value;
                if (elapsed.TotalMinutes >= 1)
                {
                    CompactSessionText.Text = elapsed.TotalHours >= 1
                        ? $"⏱ {(int)elapsed.TotalHours}ч{elapsed.Minutes:D2}м"
                        : $"⏱ {(int)elapsed.TotalMinutes}м";
                    CompactSessionText.Visibility = Visibility.Visible;
                }
                else CompactSessionText.Visibility = Visibility.Collapsed;
            }
            else
            {
                CompactSessionText.Visibility = Visibility.Collapsed;
            }

            if (_settings.CompactShowPing || _isExpanded || _isInFullscreenGame)
                _ = _sysMonitor.SamplePingAsync(metrics.ActivePid, _isInFullscreenGame);

            var privacy = await Task.Run(PrivacyAndGameService.GetActiveMicAndCamera);
            UpdatePrivacyVisuals(privacy);

            if (_settings.EnableSystemMonitor)
            {
                // FPS left of CPU:
                // In fullscreen games: gamepad icon + real FPS colored RED (<60) or GREEN (>=60)
                if (_isInFullscreenGame)
                {
                    if (_sysMonitor.IsEtwFpsActive || metrics.ActiveFps >= 1)
                    {
                        int fpsInt = (int)Math.Round(metrics.ActiveFps);
                        CompactFpsText.Text = $"🎮 {fpsInt} FPS";
                        var fpsColor = fpsInt < 60
                            ? new SolidColorBrush(MediaColor.FromRgb(248, 113, 113))  // Red (<60)
                            : new SolidColorBrush(MediaColor.FromRgb(74, 222, 128));   // Green (>=60)
                        CompactFpsText.Foreground = fpsColor;
                        FpsDetailText.Foreground = fpsColor;
                        CompactFpsText.ToolTip = "Игровой режим (ховер отключён, клик открывает). ETW FPS игры";
                        FpsDetailText.Text = string.IsNullOrEmpty(metrics.ActiveWindowTitle)
                            ? $"{fpsInt} FPS"
                            : $"{fpsInt} FPS · {metrics.ActiveWindowTitle}";
                    }
                    else
                    {
                        CompactFpsText.Text = "🎮 — FPS";
                        var warnCol = new SolidColorBrush(MediaColor.FromRgb(248, 113, 113));
                        CompactFpsText.Foreground = warnCol;
                        FpsDetailText.Foreground = warnCol;
                        CompactFpsText.ToolTip = "Для показа точного FPS игр запустите Enable_Game_FPS_Access.bat с правами Админа";
                        FpsDetailText.Text = string.IsNullOrEmpty(metrics.ActiveWindowTitle)
                            ? "Нет прав ETW"
                            : $"Нет прав ETW · {metrics.ActiveWindowTitle}";
                    }
                }
                else
                {
                    CompactFpsText.Text = $"{metrics.ActiveFps:F0} FPS";
                    CompactFpsText.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrightBrush");
                    FpsDetailText.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrightBrush");
                    CompactFpsText.ToolTip = _sysMonitor.IsEtwFpsActive ? "Реальный ETW FPS активного окна" : "DWM FPS активного окна";
                    FpsDetailText.Text = string.IsNullOrEmpty(metrics.ActiveWindowTitle)
                        ? $"{metrics.ActiveFps:F0} FPS"
                        : $"{metrics.ActiveFps:F0} FPS · {metrics.ActiveWindowTitle}";
                }
                CompactFpsText.Visibility = (_settings.CompactShowFps && _isInFullscreenGame)
                    ? Visibility.Visible
                    : Visibility.Collapsed;

                bool highCpu = metrics.CpuPercent >= 75;
                bool highRam = metrics.RamPercent >= 80;
                bool highTemp = metrics.GpuTempC.HasValue && metrics.GpuTempC.Value >= 84;
                var highBlue = new SolidColorBrush(MediaColor.FromRgb(56, 189, 248));
                var hotCol = new SolidColorBrush(MediaColor.FromRgb(251, 146, 60));
                var normalCompactCol = new SolidColorBrush(MediaColor.FromRgb(203, 213, 225));
                var normalCardCol = new SolidColorBrush(MediaColor.FromRgb(248, 250, 252));

                if (_settings.CompactShowCpuRam)
                {
                    string sysLabel = $"CPU {metrics.CpuPercent:F0}%";
                    if (_isInFullscreenGame && _settings.GameShowGpuTemp && metrics.GpuPercent.HasValue)
                    {
                        string tempPart = metrics.GpuTempC.HasValue ? $" {metrics.GpuTempC.Value:F0}°" : "";
                        sysLabel += $" · GPU {metrics.GpuPercent.Value:F0}%{tempPart}";
                    }
                    sysLabel += $" · RAM {metrics.RamPercent:F0}%";

                    CompactSysText.Text = sysLabel;
                    bool heavyLoad = highCpu || highRam;
                    CompactSysText.Foreground = highTemp ? hotCol : (heavyLoad ? highBlue : normalCompactCol);
                    CompactSysText.FontWeight = (heavyLoad || highTemp) ? FontWeights.SemiBold : FontWeights.Normal;
                    CompactSysText.Visibility = Visibility.Visible;
                }
                else
                {
                    CompactSysText.Visibility = Visibility.Collapsed;
                }

                if ((_settings.CompactShowPing || _isInFullscreenGame) && metrics.PingMs >= 0)
                {
                    CompactPingText.Text = $"{metrics.PingMs}ms";
                    CompactPingText.ToolTip = _sysMonitor.PingTargetLabel;
                    if (_isInFullscreenGame)
                    {
                        var pingColor = metrics.PingMs < 60
                            ? MediaColor.FromRgb(74, 222, 128)
                            : metrics.PingMs < 110
                                ? MediaColor.FromRgb(250, 204, 21)
                                : MediaColor.FromRgb(248, 113, 113);
                        CompactPingText.Foreground = new SolidColorBrush(pingColor);
                    }
                    else
                    {
                        CompactPingText.Foreground = new SolidColorBrush(MediaColor.FromRgb(148, 163, 184));
                    }
                    CompactPingText.Visibility = Visibility.Visible;
                }
                else
                {
                    CompactPingText.Visibility = Visibility.Collapsed;
                }

                bool anyMetric = CompactFpsText.Visibility == Visibility.Visible ||
                                 CompactSysText.Visibility == Visibility.Visible ||
                                 CompactPingText.Visibility == Visibility.Visible;
                CompactSep1.Visibility = anyMetric ? Visibility.Visible : Visibility.Collapsed;

                CpuValText.Text = $"{metrics.CpuPercent:F0}%";
                CpuValText.Foreground = highCpu ? highBlue : normalCardCol;
                RamValText.Text = $"{metrics.RamPercent:F0}%";
                RamValText.Foreground = highRam ? highBlue : normalCardCol;
                RamSubText.Text = $"{metrics.RamUsedGb:F1} / {metrics.RamTotalGb:F1} ГБ";

                if (_settings.GameShowGpuTemp && metrics.GpuPercent.HasValue)
                {
                    string gTemp = metrics.GpuTempC.HasValue ? $" · {metrics.GpuTempC.Value:F0}°C" : "";
                    GpuDetailText.Text = $"GPU {metrics.GpuPercent.Value:F0}%{gTemp}";
                    GpuDetailText.Foreground = highTemp ? hotCol : new SolidColorBrush(MediaColor.FromRgb(148, 163, 184));
                    GpuDetailText.Visibility = Visibility.Visible;
                }
                else
                {
                    GpuDetailText.Visibility = Visibility.Collapsed;
                }

                // Soft perimeter flash alert on FPS drop or Ping spike during gameplay
                if (_isInFullscreenGame && _settings.GameModeAutoProfile &&
                    (DateTime.UtcNow - _lastGameAlertAt).TotalSeconds >= 10)
                {
                    if (_sysMonitor.IsEtwFpsActive && metrics.ActiveFps > 0 && metrics.ActiveFps < 45)
                    {
                        _lastGameAlertAt = DateTime.UtcNow;
                        FlashIslandBorder(MediaColor.FromRgb(248, 113, 113));
                    }
                    else if (metrics.PingMs > 120)
                    {
                        _lastGameAlertAt = DateTime.UtcNow;
                        FlashIslandBorder(MediaColor.FromRgb(250, 204, 21));
                    }
                }
            }

            UpdateScreenTimeTracking(metrics);

            if (_settings.EnableNetworkMonitor)
            {
                var down = SystemMonitorService.FormatSpeed(metrics.NetDownBytesPerSec);
                var up = SystemMonitorService.FormatSpeed(metrics.NetUpBytesPerSec);
                NetDownText.Text = $"↓ {down}";
                NetUpText.Text = $"↑ {up}";
                PingHeaderLabel.Text = metrics.PingMs >= 0
                    ? (_sysMonitor.IsGameServerPing ? $"Сеть · Игр. ping {metrics.PingMs}мс" : $"Сеть · Ping {metrics.PingMs}мс")
                    : "Сеть";

                if (_settings.CompactShowNetwork)
                {
                    CompactNetText.Text = $"↓{ShortSpeed(metrics.NetDownBytesPerSec)}";
                    CompactNetText.Visibility = Visibility.Visible;
                }
                else
                {
                    CompactNetText.Visibility = Visibility.Collapsed;
                }
            }

            if (_settings.AutoOptimizeRamOnThreshold &&
                !_isOptimizingRam &&
                metrics.RamPercent >= _settings.RamThresholdPercent &&
                (DateTime.UtcNow - _lastAutoRamAt).TotalSeconds >= 60)
            {
                _lastAutoRamAt = DateTime.UtcNow;
                _ = RunOptimizeMemoryAsync(isAuto: true);
            }

            if (_isExpanded && TabSystem.IsChecked == true)
            {
                var procs = await Task.Run(() => _sysMonitor.GetTopProcesses(3));
                _topProcesses.Clear();
                foreach (var p in procs) _topProcesses.Add(p);

                var dl = await Task.Run(() => _sysMonitor.GetRecentDownloads(4));
                _recentDownloads.Clear();
                foreach (var f in dl) _recentDownloads.Add(f);

                ScheduleExpandedResize(animate: true);
            }
            else if (_isExpanded && TabScreenTime.IsChecked == true)
            {
                RefreshScreenTimeUi();
                ScheduleExpandedResize(animate: true);
            }

            await RefreshMediaAndDevicesAsync(forceBluetooth: (_isExpanded && TabRemote.IsChecked == true));

            if (!_isExpanded && CompactToastStrip.Visibility != Visibility.Visible)
                UpdateCompactWidth(animate: false);
        }
        catch (Exception ex)
        {
            LoggerService.Error("Monitor tick error", ex);
        }
    }

    private void ResetScreenTime_Click(object sender, RoutedEventArgs e)
    {
        _screenTimeStats.Clear();
        _screenTimeItems.Clear();
        _islandSessionStartUtc = DateTime.UtcNow;
        _lastScreenTimeTickUtc = DateTime.UtcNow;
        _lastTrackedGameName = string.Empty;
        _lastTrackedGameFps = 0;
        _lastTrackedGameGpu = null;
        _lastTrackedGameTemp = null;
        _lastTrackedGamePing = -1;
        RefreshScreenTimeUi();
        if (_isExpanded && TabScreenTime.IsChecked == true)
            ScheduleExpandedResize(animate: true);
        ShowToast("⏱", "Статистика сессии сброшена", durationMs: 2000);
    }

    private void UpdateScreenTimeTracking(SystemMetrics metrics)
    {
        var now = DateTime.UtcNow;
        double dt = Math.Clamp((now - _lastScreenTimeTickUtc).TotalSeconds, 0, 10);
        _lastScreenTimeTickUtc = now;

        string procName = metrics.ActiveWindowTitle?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(procName) ||
            procName.Equals("DesktopIsland", StringComparison.OrdinalIgnoreCase) ||
            procName.Equals("Idle", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!_screenTimeStats.TryGetValue(procName, out var acc))
        {
            acc = new ScreenTimeAccumulator();
            _screenTimeStats[procName] = acc;
        }
        acc.Seconds += dt;

        if (_isInFullscreenGame)
        {
            acc.IsGame = true;
            if (metrics.ActiveFps >= 1)
            {
                acc.FpsSum += metrics.ActiveFps;
                acc.FpsSamples++;
                _lastTrackedGameFps = metrics.ActiveFps;
            }
            _lastTrackedGameName = procName;
            if (metrics.GpuPercent.HasValue) _lastTrackedGameGpu = metrics.GpuPercent;
            if (metrics.GpuTempC.HasValue) _lastTrackedGameTemp = metrics.GpuTempC;
            if (metrics.PingMs >= 0) _lastTrackedGamePing = metrics.PingMs;
        }
    }

    private static string FormatDurationShort(TimeSpan dur) => dur.TotalHours >= 1
        ? $"{(int)dur.TotalHours}ч {dur.Minutes:D2}м"
        : (dur.TotalMinutes >= 1 ? $"{(int)dur.TotalMinutes}м" : $"{(int)dur.TotalSeconds}с");

    private void RefreshScreenTimeUi()
    {
        var total = DateTime.UtcNow - _islandSessionStartUtc;
        ScreenTimeTotalText.Text = FormatDurationShort(total);
        ScreenTimeAppsCountText.Text = $"Программ: {_screenTimeStats.Count}";

        var allGames = _screenTimeStats.Where(kv => kv.Value.IsGame).ToList();
        double gamesSec = allGames.Sum(kv => kv.Value.Seconds);
        ScreenTimeGamesTotalText.Text = FormatDurationShort(TimeSpan.FromSeconds(gamesSec));

        var topGame = allGames
            .OrderByDescending(kv => kv.Value.Seconds)
            .FirstOrDefault();

        if (!string.IsNullOrEmpty(topGame.Key))
        {
            string gFmt = FormatDurationShort(TimeSpan.FromSeconds(topGame.Value.Seconds));
            ScreenTimeLastGameText.Text = topGame.Key;

            var parts = new List<string> { $"🎮 {topGame.Key}: {gFmt}" };
            if (topGame.Value.FpsSamples > 0)
            {
                double avgFps = topGame.Value.FpsSum / topGame.Value.FpsSamples;
                string cur = _lastTrackedGameFps >= 1 ? $"{_lastTrackedGameFps:F0}" : $"{avgFps:F0}";
                parts.Add($"FPS {cur} (ср. {avgFps:F0})");
            }
            if (_lastTrackedGameGpu.HasValue)
            {
                string t = _lastTrackedGameTemp.HasValue ? $"/{_lastTrackedGameTemp.Value:F0}°C" : "";
                parts.Add($"GPU {_lastTrackedGameGpu.Value:F0}%{t}");
            }
            if (_lastTrackedGamePing >= 0)
                parts.Add($"Ping {_lastTrackedGamePing}ms");

            GameStatsDetailText.Text = string.Join(" · ", parts);
            GameStatsDetailText.Foreground = new SolidColorBrush(MediaColor.FromRgb(226, 232, 240));
        }
        else
        {
            ScreenTimeLastGameText.Text = "Игр не было";
            GameStatsDetailText.Text = "Полноэкранная игра в этой сессии пока не запускалась";
            GameStatsDetailText.Foreground = new SolidColorBrush(MediaColor.FromRgb(148, 163, 184));
        }

        var topApps = _screenTimeStats
            .OrderByDescending(kv => kv.Value.Seconds)
            .Take(8)
            .ToList();

        double maxSec = topApps.Count > 0 ? Math.Max(1.0, topApps[0].Value.Seconds) : 1.0;

        _screenTimeItems.Clear();
        foreach (var kv in topApps)
        {
            double? avg = kv.Value.IsGame && kv.Value.FpsSamples > 0
                ? kv.Value.FpsSum / kv.Value.FpsSamples
                : null;
            _screenTimeItems.Add(new ScreenTimeItem
            {
                ProcessName = kv.Key,
                IsGame = kv.Value.IsGame,
                Duration = TimeSpan.FromSeconds(kv.Value.Seconds),
                SharePercent = Math.Clamp((kv.Value.Seconds / maxSec) * 100.0, 4, 100),
                AvgFps = avg
            });
        }
        ScreenTimeEmptyText.Visibility = _screenTimeItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdatePrivacyVisuals(PrivacyStatusInfo privacy)
    {
        if (privacy.IsCameraActive)
        {
            var col = new SolidColorBrush(MediaColor.FromRgb(239, 68, 68));
            StatusDot.Fill = col;
            ExpandedStatusDot.Fill = col;
            StatusDot.ToolTip = $"Камера активна: {privacy.CameraApp}";
            PrivacyStatusText.Text = $"📷 Камера: {privacy.CameraApp}";
            PrivacyStatusText.Foreground = col;
        }
        else if (privacy.IsMicActive)
        {
            var col = new SolidColorBrush(MediaColor.FromRgb(245, 158, 11));
            StatusDot.Fill = col;
            ExpandedStatusDot.Fill = col;
            StatusDot.ToolTip = $"Микрофон активен: {privacy.MicApp}";
            PrivacyStatusText.Text = $"🎙 Микрофон: {privacy.MicApp}";
            PrivacyStatusText.Foreground = col;
        }
        else
        {
            StatusDot.SetResourceReference(Shape.FillProperty, "AccentBrightBrush");
            ExpandedStatusDot.SetResourceReference(Shape.FillProperty, "AccentBrightBrush");
            StatusDot.ToolTip = "Desktop Island активен";
            PrivacyStatusText.Text = "🔒 Камера и мик свободны";
            PrivacyStatusText.Foreground = new SolidColorBrush(MediaColor.FromRgb(100, 116, 139));
        }
    }

    private static string ShortSpeed(double bps)
    {
        if (bps >= 1024 * 1024) return $"{bps / (1024 * 1024):F1}M";
        if (bps >= 1024) return $"{bps / 1024:F0}K";
        return $"{bps:F0}B";
    }

    private async Task RefreshWeatherAsync()
    {
        if (!_settings.CompactShowWeather) return;
        var w = await QuickToolsService.FetchWeatherAsync(_settings.WeatherCity);
        if (w != null)
        {
            _currentWeather = w;
            CompactWeatherText.Text = w.ShortText;
            CompactWeatherText.ToolTip = $"{w.City}: {w.TempC:F1}°C";
            CompactWeatherText.Visibility = Visibility.Visible;
            if (!_isExpanded) UpdateCompactWidth(animate: false);
        }
    }

    #endregion

    #region Remote Tab ("Пульт": Media, Audio, Mic & Bluetooth)

    private async Task RefreshMediaAndDevicesAsync(bool forceBluetooth)
    {
        var track = await MediaControlService.GetCurrentTrackAsync();
        _isMediaPlaying = track.HasSession && track.IsPlaying;

        CompactEqPanel.Visibility = (_settings.CompactShowMedia && _isMediaPlaying)
            ? Visibility.Visible : Visibility.Collapsed;

        MediaTitleText.Text = track.HasSession ? track.Title : "Ничего не воспроизводится";
        MediaArtistText.Text = track.HasSession
            ? (string.IsNullOrEmpty(track.Artist) ? "Медиаплеер активен" : track.Artist)
            : "Включите музыку или видео в любом плеере";
        MediaPlayPauseBtn.Content = _isMediaPlaying ? "⏸" : "▶";

        if (_isExpanded && TabRemote.IsChecked == true)
        {
            int vol = CoreAudioService.GetMasterVolumePercent();
            SyncVolumeUi(vol);

            bool micMuted = CoreAudioService.IsMicMuted();
            MicMuteBtn.Content = micMuted ? "🔇 Микрофон: Выкл" : "🎙 Микрофон: Вкл";
            MicMuteBtn.Foreground = micMuted
                ? new SolidColorBrush(MediaColor.FromRgb(252, 165, 165))
                : new SolidColorBrush(MediaColor.FromRgb(226, 232, 240));

            var outputs = CoreAudioService.GetPlaybackDevices();
            var def = outputs.FirstOrDefault(o => o.IsDefault);
            AudioSwitchBtn.Content = def != null ? $"{def.IconGlyph} Вывод" : "🔊 Вывод";
            AudioSwitchBtn.ToolTip = outputs.Count > 1
                ? $"Текущее: {def?.Name}. Нажмите, чтобы переключить ({outputs.Count} устр.)"
                : $"Вывод звука: {def?.Name ?? "По умолчанию"}";
        }

        if (forceBluetooth || (DateTime.UtcNow - _lastBtPollAt).TotalSeconds >= 12)
        {
            _lastBtPollAt = DateTime.UtcNow;
            var state = await BluetoothService.GetAdapterStateAsync();
            var devices = state.Supported ? await BluetoothService.GetPairedDevicesAsync() : new List<BluetoothDeviceItem>();

            var nowConnected = new HashSet<string>(devices.Where(d => d.IsConnected).Select(d => d.Name), StringComparer.OrdinalIgnoreCase);
            if (_connectedBtNames.Count > 0)
            {
                foreach (var newly in nowConnected.Where(n => !_connectedBtNames.Contains(n)))
                    ShowToast("🎧", $"Bluetooth подключен: {newly}", durationMs: 3200);
            }
            _connectedBtNames = nowConnected;

            if (_isExpanded && TabRemote.IsChecked == true)
            {
                BtStateText.Text = !state.Supported ? "· адаптер не найден" : (state.IsOn ? "· Включён" : "· Выключен");
                BtToggleBtn.IsEnabled = state.Supported;
                BtToggleBtn.Content = state.IsOn ? "Выключить" : "Включить";

                _allBtDevices = devices;
                ApplyBtListFilter();
            }
        }
    }

    private void ApplyBtListFilter()
    {
        _btDevices.Clear();
        var shown = _isBtExpanded
            ? _allBtDevices
            : _allBtDevices.Where(d => d.IsConnected).ToList();

        foreach (var d in shown) _btDevices.Add(d);

        BtExpandBtn.Content = _isBtExpanded ? "Скрыть ▴" : $"Все ({_allBtDevices.Count}) ▾";
        BtExpandBtn.Visibility = _allBtDevices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (_btDevices.Count == 0)
        {
            BtEmptyText.Text = _allBtDevices.Count == 0
                ? "Сопряжённые устройства не найдены"
                : "Подключённых устройств нет";
            BtEmptyText.Visibility = Visibility.Visible;
        }
        else
        {
            BtEmptyText.Visibility = Visibility.Collapsed;
        }

        if (_isExpanded && TabRemote.IsChecked == true)
            ScheduleExpandedResize(animate: true);
    }

    private void BtExpandBtn_Click(object sender, RoutedEventArgs e)
    {
        _isBtExpanded = !_isBtExpanded;
        ApplyBtListFilter();
    }

    private void EqTimer_Tick(object? sender, EventArgs e)
    {
        if (CompactEqPanel.Visibility != Visibility.Visible || _isExpanded) return;

        float peak = CoreAudioService.GetRenderPeakLevel();
        if (peak < 0.02f) peak = _isMediaPlaying ? 0.25f : 0.0f;

        double t = Environment.TickCount64 / 120.0;
        EqBar1.Height = 3 + Math.Clamp(peak * 11 * (0.65 + 0.35 * Math.Sin(t)), 1, 11);
        EqBar2.Height = 3 + Math.Clamp(peak * 11 * (0.65 + 0.35 * Math.Sin(t + 1.5)), 1, 11);
        EqBar3.Height = 3 + Math.Clamp(peak * 11 * (0.65 + 0.35 * Math.Sin(t + 3.0)), 1, 11);
        EqBar4.Height = 3 + Math.Clamp(peak * 11 * (0.65 + 0.35 * Math.Sin(t + 4.5)), 1, 11);
    }

    private void SyncVolumeUi(int percent)
    {
        _isUpdatingVolumeSlider = true;
        MasterVolumeSlider.Value = percent;
        VolumePercentText.Text = $"🔊 Громкость: {percent}%";
        _isUpdatingVolumeSlider = false;
    }

    private void MasterVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingVolumeSlider) return;
        int val = (int)Math.Round(e.NewValue);
        CoreAudioService.SetMasterVolumePercent(val);
        VolumePercentText.Text = $"🔊 Громкость: {val}%";
    }

    private async void MediaPrev_Click(object sender, RoutedEventArgs e)
    {
        await MediaControlService.PreviousAsync();
        await Task.Delay(250);
        await RefreshMediaAndDevicesAsync(false);
    }

    private async void MediaPlayPause_Click(object sender, RoutedEventArgs e)
    {
        await MediaControlService.PlayPauseAsync();
        await Task.Delay(250);
        await RefreshMediaAndDevicesAsync(false);
    }

    private async void MediaNext_Click(object sender, RoutedEventArgs e)
    {
        await MediaControlService.NextAsync();
        await Task.Delay(250);
        await RefreshMediaAndDevicesAsync(false);
    }

    private void SwitchAudioOutput_Click(object sender, RoutedEventArgs e)
    {
        var devices = CoreAudioService.GetPlaybackDevices();
        if (devices.Count < 2)
        {
            ShowToast("🔊", devices.Count == 1 ? $"Только 1 устройство: {devices[0].Name}" : "Устройства вывода не найдены");
            return;
        }

        int curIdx = devices.FindIndex(d => d.IsDefault);
        int nextIdx = (curIdx + 1) % devices.Count;
        var next = devices[nextIdx];
        if (CoreAudioService.SetDefaultPlaybackDevice(next.Id))
        {
            AudioSwitchBtn.Content = $"{next.IconGlyph} Вывод";
            ShowToast(next.IconGlyph, $"Вывод: {next.Name}");
            _ = RefreshMediaAndDevicesAsync(false);
        }
    }

    private void ToggleMicMute_Click(object sender, RoutedEventArgs e)
    {
        bool nowMuted = CoreAudioService.ToggleMicMute();
        MicMuteBtn.Content = nowMuted ? "🔇 Микрофон: Выкл" : "🎙 Микрофон: Вкл";
        ShowToast(nowMuted ? "🔇" : "🎙", nowMuted ? "Микрофон выключен" : "Микрофон включён");
    }

    private async void ToggleBluetooth_Click(object sender, RoutedEventArgs e)
    {
        var state = await BluetoothService.GetAdapterStateAsync();
        if (!state.Supported) return;
        BtToggleBtn.IsEnabled = false;
        await BluetoothService.SetAdapterStateAsync(!state.IsOn);
        await Task.Delay(500);
        await RefreshMediaAndDevicesAsync(forceBluetooth: true);
    }

    private void OpenBtSettings_Click(object sender, RoutedEventArgs e) => BluetoothService.OpenBluetoothSettings();

    private async void BtDeviceAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is BluetoothDeviceItem item)
        {
            var msg = await BluetoothService.ToggleDeviceConnectionAsync(item);
            ShowToast("🎧", msg, durationMs: 2800);
            await Task.Delay(800);
            await RefreshMediaAndDevicesAsync(forceBluetooth: true);
        }
    }

    #endregion

    #region Launch Apps (Max 4 per row + Edit Mode + Drag & Drop Reorder)

    private void EditAppsBtn_Changed(object sender, RoutedEventArgs e)
    {
        _isAppsEditMode = EditAppsBtn.IsChecked == true;
        AppsEditHintText.Visibility = _isAppsEditMode ? Visibility.Visible : Visibility.Collapsed;
        RebuildAppTiles();
        if (_isExpanded) ScheduleExpandedResize(animate: true);
    }

    private void RebuildAppTiles()
    {
        AppsWrapPanel.Children.Clear();

        foreach (var app in _settings.PinnedApps)
            AppsWrapPanel.Children.Add(CreateAppTile(app));

        if (_isAppsEditMode)
            AppsWrapPanel.Children.Add(CreateAddAppTile());
    }

    private Border CreateAppTile(PinnedApp app)
    {
        var tile = new Border
        {
            Width = 96,
            Height = 74,
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(MediaColor.FromRgb(22, 23, 34)),
            BorderBrush = new SolidColorBrush(MediaColor.FromRgb(37, 40, 56)),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 7, 7),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = _isAppsEditMode ? $"Перетащите «{app.Name}» для смены места" : $"Запустить {app.Name}",
            Tag = app,
            AllowDrop = _isAppsEditMode
        };

        var root = new Grid();

        var center = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(4)
        };

        var iconImg = new System.Windows.Controls.Image
        {
            Width = 28,
            Height = 28,
            Margin = new Thickness(0, 0, 0, 5),
            Source = ResolveAppIcon(app.ExecutablePath)
        };

        var label = new TextBlock
        {
            Text = app.Name,
            FontSize = 10.5,
            Foreground = new SolidColorBrush(MediaColor.FromRgb(226, 232, 240)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            MaxWidth = 84,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        center.Children.Add(iconImg);
        center.Children.Add(label);
        root.Children.Add(center);

        if (_isAppsEditMode)
        {
            var minusBadge = new System.Windows.Controls.Button
            {
                Width = 17,
                Height = 17,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 4, 4, 0),
                Padding = new Thickness(0),
                BorderThickness = new Thickness(0),
                Background = MediaBrushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = $"Убрать «{app.Name}»"
            };

            var circle = new Border
            {
                Width = 17,
                Height = 17,
                CornerRadius = new CornerRadius(8.5),
                Background = new SolidColorBrush(MediaColor.FromRgb(239, 68, 68)),
                Child = new System.Windows.Shapes.Path
                {
                    Data = Geometry.Parse("M 4.5,8.5 L 12.5,8.5"),
                    Stroke = MediaBrushes.White,
                    StrokeThickness = 1.8,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    Width = 17,
                    Height = 17
                }
            };
            minusBadge.Template = CreateRawContentButtonTemplate();
            minusBadge.Content = circle;
            minusBadge.Click += (_, ev) =>
            {
                ev.Handled = true;
                _settings.PinnedApps.Remove(app);
                SettingsService.Save(_settings);
                RebuildAppTiles();
                ScheduleExpandedResize(animate: true);
            };
            root.Children.Add(minusBadge);
        }

        tile.Child = root;

        tile.MouseEnter += (_, _) =>
        {
            tile.Background = new SolidColorBrush(MediaColor.FromRgb(30, 32, 48));
            tile.SetResourceReference(Border.BorderBrushProperty, "AccentBrightBrush");
        };
        tile.MouseLeave += (_, _) =>
        {
            tile.Background = new SolidColorBrush(MediaColor.FromRgb(22, 23, 34));
            tile.BorderBrush = new SolidColorBrush(MediaColor.FromRgb(37, 40, 56));
        };

        if (_isAppsEditMode)
        {
            tile.MouseLeftButtonDown += (_, ev) =>
            {
                if (HasButtonAncestor(ev.OriginalSource as DependencyObject))
                    return;

                _reorderingTile = tile;
                _reorderingApp = app;
                _reorderStartPoint = ev.GetPosition(AppsWrapPanel);
                _isReorderingApp = false;
                tile.CaptureMouse();
                ev.Handled = true;
            };
            tile.MouseMove += (_, ev) =>
            {
                if (_reorderingTile != tile || _reorderingApp != app) return;
                if (ev.LeftButton != MouseButtonState.Pressed)
                {
                    FinishAppTileReorder(save: _isReorderingApp);
                    return;
                }

                var pos = ev.GetPosition(AppsWrapPanel);
                if (!_isReorderingApp)
                {
                    if (Math.Abs(pos.X - _reorderStartPoint.X) < 5 &&
                        Math.Abs(pos.Y - _reorderStartPoint.Y) < 5)
                        return;

                    _isReorderingApp = true;
                    tile.Opacity = 0.65;
                    tile.SetResourceReference(Border.BorderBrushProperty, "AccentBrightBrush");
                    tile.BorderThickness = new Thickness(1.6);
                }

                foreach (UIElement child in AppsWrapPanel.Children)
                {
                    if (child is Border targetBorder && targetBorder != tile && targetBorder.Tag is PinnedApp targetApp)
                    {
                        var topLeft = targetBorder.TranslatePoint(new MediaPoint(0, 0), AppsWrapPanel);
                        var rect = new Rect(topLeft.X, topLeft.Y, targetBorder.ActualWidth, targetBorder.ActualHeight);
                        if (rect.Contains(pos))
                        {
                            int fromIdx = _settings.PinnedApps.IndexOf(app);
                            int toIdx = _settings.PinnedApps.IndexOf(targetApp);
                            if (fromIdx >= 0 && toIdx >= 0 && fromIdx != toIdx)
                            {
                                _settings.PinnedApps.RemoveAt(fromIdx);
                                _settings.PinnedApps.Insert(toIdx, app);

                                _suppressReorderLostCapture = true;
                                AppsWrapPanel.Children.Remove(tile);
                                AppsWrapPanel.Children.Insert(toIdx, tile);
                                AppsWrapPanel.UpdateLayout();
                                if (!tile.IsMouseCaptured) tile.CaptureMouse();
                                _suppressReorderLostCapture = false;
                            }
                            break;
                        }
                    }
                }
                ev.Handled = true;
            };
            tile.MouseLeftButtonUp += (_, ev) =>
            {
                if (_reorderingTile == tile)
                {
                    FinishAppTileReorder(save: _isReorderingApp);
                    ev.Handled = true;
                }
            };
            tile.LostMouseCapture += (_, _) =>
            {
                if (!_suppressReorderLostCapture && _reorderingTile == tile)
                    FinishAppTileReorder(save: _isReorderingApp);
            };
        }
        else
        {
            tile.MouseLeftButtonUp += (_, _) => LaunchPinnedApp(app);
        }

        return tile;
    }

    private void FinishAppTileReorder(bool save)
    {
        var tile = _reorderingTile;
        bool wasReordering = _isReorderingApp;
        _reorderingTile = null;
        _reorderingApp = null;
        _isReorderingApp = false;

        if (tile != null)
        {
            if (tile.IsMouseCaptured)
                tile.ReleaseMouseCapture();
            tile.Opacity = 1.0;
            tile.BorderBrush = new SolidColorBrush(MediaColor.FromRgb(37, 40, 56));
            tile.BorderThickness = new Thickness(1);
        }

        if (save && wasReordering)
            SettingsService.Save(_settings);
    }

    private Border CreateAddAppTile()
    {
        var tile = new Border
        {
            Width = 96,
            Height = 74,
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(MediaColor.FromRgb(18, 20, 30)),
            BorderBrush = new SolidColorBrush(MediaColor.FromRgb(51, 65, 85)),
            BorderThickness = new Thickness(1.2),
            Margin = new Thickness(0, 0, 7, 7),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "Добавить программу или ярлык"
        };

        var stack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        };
        var plusCircle = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(14),
            Margin = new Thickness(0, 0, 0, 5),
            Child = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 14,8 L 14,20 M 8,14 L 20,14"),
                StrokeThickness = 2.1,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Width = 28,
                Height = 28,
                Stroke = Resources["AccentBrightBrush"] as System.Windows.Media.Brush ?? MediaBrushes.DeepSkyBlue
            }
        };
        plusCircle.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush");

        stack.Children.Add(plusCircle);
        stack.Children.Add(new TextBlock
        {
            Text = "Добавить",
            FontSize = 10.5,
            Foreground = new SolidColorBrush(MediaColor.FromRgb(148, 163, 184)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        });
        tile.Child = stack;

        tile.MouseEnter += (_, _) => tile.Background = new SolidColorBrush(MediaColor.FromRgb(28, 30, 44));
        tile.MouseLeave += (_, _) => tile.Background = new SolidColorBrush(MediaColor.FromRgb(18, 20, 30));
        tile.MouseLeftButtonUp += (_, _) =>
        {
            var dlg = new Win32.OpenFileDialog
            {
                Filter = "Программы и ярлыки (*.exe;*.lnk)|*.exe;*.lnk|Все файлы (*.*)|*.*",
                Title = "Выберите приложение для Desktop Island"
            };
            if (dlg.ShowDialog() == true)
            {
                _settings.PinnedApps.Add(new PinnedApp
                {
                    Name = IOPath.GetFileNameWithoutExtension(dlg.FileName),
                    ExecutablePath = dlg.FileName
                });
                SettingsService.Save(_settings);
                RebuildAppTiles();
                ScheduleExpandedResize(animate: true);
            }
        };

        return tile;
    }

    private static ControlTemplate CreateRawContentButtonTemplate()
    {
        var template = new ControlTemplate(typeof(System.Windows.Controls.Button));
        var fef = new FrameworkElementFactory(typeof(ContentPresenter));
        template.VisualTree = fef;
        return template;
    }

    private ImageSource? ResolveAppIcon(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (_iconCache.TryGetValue(path, out var cached)) return cached;

        try
        {
            if (File.Exists(path))
            {
                using var ico = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (ico != null)
                {
                    var src = Imaging.CreateBitmapSourceFromHIcon(
                        ico.Handle,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();
                    _iconCache[path] = src;
                    return src;
                }
            }
        }
        catch { }
        _iconCache[path] = null;
        return null;
    }

    private void LaunchPinnedApp(PinnedApp app)
    {
        try
        {
            var psi = new ProcessStartInfo(app.ExecutablePath, app.Arguments) { UseShellExecute = true };
            Process.Start(psi);
            if (!_isPinned) SetExpanded(false);
        }
        catch (Exception ex)
        {
            LoggerService.Error($"Failed launching {app.Name}", ex);
            ShowToast("⚠", $"Не удалось запустить {app.Name}");
        }
    }

    #endregion

    #region RAM Optimizer & Lightning Animation

    private void RamQuickPill_Click(object sender, MouseButtonEventArgs e)
    {
        _ = RunOptimizeMemoryAsync(isAuto: false);
    }

    private async Task RunGameAutoBoostAsync(uint gamePid)
    {
        try
        {
            bool switchedPlan = await Task.Run(QuickToolsService.EnterGameHighPerformancePlan);
            var ramRes = await MemoryOptimizerService.OptimizeAsync(excludePid: gamePid > 0 ? gamePid : null);
            string msg = switchedPlan
                ? $"Буст игры: High Perf · +{ramRes.FreedGb:F1} ГБ ОЗУ"
                : $"Буст игры: +{ramRes.FreedGb:F1} ГБ ОЗУ";
            LoggerService.Info(msg);
            ShowToast("🚀", msg, durationMs: 2800);
        }
        catch { }
    }

    public async Task RunOptimizeMemoryAsync(bool isAuto = false)
    {
        if (_isOptimizingRam) return;
        _isOptimizingRam = true;

        try
        {
            OptimizeRamBtn.IsEnabled = false;
            AnimateLightningFill(1.0, TimeSpan.FromMilliseconds(550));
            var res = await MemoryOptimizerService.OptimizeAsync();

            _isShowingRamResult = true;
            RamQuickText.Text = res.FreedGb >= 0.05 ? $"+{res.FreedGb:F1} ГБ" : $"{res.AfterPercent}%";
            RamQuickText.Foreground = new SolidColorBrush(MediaColor.FromRgb(250, 204, 21));
            UpdateRamPillState(animate: true);

            _ramLabelResetTimer.Stop();
            _ramLabelResetTimer.Start();

            var toastMsg = isAuto
                ? $"Авто-ОЗУ: освобождено {res.FreedGb:F1} ГБ ({res.AfterPercent}%)"
                : $"ОЗУ: освобождено {res.FreedGb:F1} ГБ ({res.AfterPercent}%)";
            ShowToast("⚡", toastMsg, durationMs: 3200);
            CleanupStatusText.Text = toastMsg;
        }
        catch (Exception ex)
        {
            LoggerService.Error("RAM optimization error", ex);
        }
        finally
        {
            OptimizeRamBtn.IsEnabled = true;
            _isOptimizingRam = false;
        }
    }

    private void RamLabelResetTimer_Tick(object? sender, EventArgs e)
    {
        _ramLabelResetTimer.Stop();
        _isShowingRamResult = false;
        RamQuickText.Text = "ОЗУ";
        RamQuickText.Foreground = new SolidColorBrush(MediaColor.FromRgb(226, 232, 240));
        UpdateRamPillState(animate: true);
        AnimateLightningFill(0.0, TimeSpan.FromMilliseconds(400));
    }

    private void AnimateLightningFill(double targetOffset, TimeSpan dur)
    {
        if (!_settings.EnableAnimations)
        {
            LightningFillStop1.Offset = targetOffset;
            LightningFillStop2.Offset = targetOffset;
            return;
        }
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        LightningFillStop1.BeginAnimation(GradientStop.OffsetProperty, new DoubleAnimation(targetOffset, dur) { EasingFunction = ease });
        LightningFillStop2.BeginAnimation(GradientStop.OffsetProperty, new DoubleAnimation(targetOffset, dur) { EasingFunction = ease });
    }

    private void OptimizeRam_Click(object sender, RoutedEventArgs e) => _ = RunOptimizeMemoryAsync(false);

    #endregion

    #region System Tab Actions (Processes, Drives, Recent Downloads)

    private void RefreshDrives()
    {
        if (!_settings.EnableDiskMonitor) return;
        _drives.Clear();
        foreach (var d in _sysMonitor.SampleDrives())
            _drives.Add(d);

        var procs = _sysMonitor.GetTopProcesses(3);
        _topProcesses.Clear();
        foreach (var p in procs) _topProcesses.Add(p);

        var dl = _sysMonitor.GetRecentDownloads(4);
        _recentDownloads.Clear();
        foreach (var f in dl) _recentDownloads.Add(f);

        if (_isExpanded && TabSystem.IsChecked == true)
            ScheduleExpandedResize(animate: true);
    }

    private void OpenDrive_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string root)
            OpenShellTarget(root);
    }

    private void KillProcess_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is ProcessMetricItem proc)
        {
            if (_sysMonitor.KillProcess(proc.Pid))
            {
                _topProcesses.Remove(proc);
                ShowToast("🧹", $"Завершён процесс {proc.Name}");
                ScheduleExpandedResize(animate: true);
            }
            else
            {
                ShowToast("⚠", $"Нет прав для завершения {proc.Name}");
            }
        }
    }

    private void OpenRecentFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string path && File.Exists(path))
            OpenShellTarget(path);
    }

    private static void OpenShellTarget(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
    }

    #endregion

    #region Quick Actions in Remote ("Пульт")

    private void StartTimerPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && int.TryParse(fe.Tag?.ToString(), out int mins))
        {
            _countdownEndUtc = DateTime.UtcNow.AddMinutes(mins);
            CompactTimerText.Text = $"⏱ {mins:00}:00";
            CompactTimerPill.Visibility = Visibility.Visible;
            TimerStatusText.Text = $"{mins:00}:00";
            ShowToast("⏱", $"Таймер на {mins} мин.");
        }
    }

    private void StopTimer_Click(object sender, RoutedEventArgs e)
    {
        _countdownEndUtc = null;
        CompactTimerPill.Visibility = Visibility.Collapsed;
        TimerStatusText.Text = "Выкл.";
    }

    private void PinExternalWindow_Click(object sender, RoutedEventArgs e)
    {
        var (ok, pinned, appName) = QuickToolsService.TogglePinExternalWindow(_lastExternalHwnd);
        ShowToast("📌", ok ? $"{appName}: {(pinned ? "поверх окон" : "откреплено")}" : "Сначала выберите окно");
    }

    private void ToggleDesktopIcons_Click(object sender, RoutedEventArgs e)
    {
        bool ok = QuickToolsService.ToggleDesktopIcons();
        ShowToast("🙈", ok ? "Иконки стола переключены" : "Не удалось переключить");
    }

    private void TakeScreenshot_Click(object sender, RoutedEventArgs e)
    {
        if (!_isPinned) SetExpanded(false);
        QuickToolsService.TakeScreenshot();
    }

    private void LockPc_Click(object sender, RoutedEventArgs e) => QuickToolsService.LockPc();
    private void SleepPc_Click(object sender, RoutedEventArgs e) => QuickToolsService.SleepPc();
    private void TurnOffScreen_Click(object sender, RoutedEventArgs e) => QuickToolsService.TurnOffMonitors();

    #endregion

    #region Cleaner Tab

    private async Task RefreshCleanupScanAsync()
    {
        CleanupStatusText.Text = "Сканирование...";
        var cats = await CleanupService.AnalyzeAsync();
        _cleanupCategories.Clear();
        foreach (var c in cats) _cleanupCategories.Add(c);
        long total = cats.Sum(c => c.SizeBytes);
        CleanupStatusText.Text = $"Найдено: {CleanupCategory.FormatBytes(total)}";
        if (_isExpanded && TabCleaner.IsChecked == true)
            ScheduleExpandedResize(animate: true);
    }

    private void RefreshCleanup_Click(object sender, RoutedEventArgs e) => _ = RefreshCleanupScanAsync();

    private async void CleanNow_Click(object sender, RoutedEventArgs e)
    {
        var selected = _cleanupCategories.Where(c => c.IsSelected).ToList();
        if (selected.Count == 0)
        {
            CleanupStatusText.Text = "Ничего не выбрано";
            return;
        }

        if (_settings.ConfirmControversialCleanup && selected.Any(c => c.RequiresConfirmation))
        {
            var ans = System.Windows.MessageBox.Show(
                "Очистить выбранные категории, включая Корзину?\n(Настройки и пароли браузеров не затрагиваются)",
                "Desktop Island",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (ans != MessageBoxResult.Yes) return;
        }

        CleanNowBtn.IsEnabled = false;
        CleanupStatusText.Text = "Очистка...";
        var res = await CleanupService.CleanAsync(selected);
        await RefreshCleanupScanAsync();
        CleanupStatusText.Text = res.Summary;
        ShowToast("🧹", res.Summary);
        CleanNowBtn.IsEnabled = true;
    }

    #endregion

    #region Header & Tab Navigation + Public Hooks

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;

        AppsPanel.Visibility = TabApps.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        RemotePanel.Visibility = TabRemote.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SystemPanel.Visibility = TabSystem.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ScreenTimePanel.Visibility = TabScreenTime.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        CleanerPanel.Visibility = TabCleaner.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        EditAppsBtn.Visibility = TabApps.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        if (TabRemote.IsChecked == true) _ = RefreshMediaAndDevicesAsync(forceBluetooth: true);
        if (TabSystem.IsChecked == true) RefreshDrives();
        if (TabScreenTime.IsChecked == true) RefreshScreenTimeUi();
        if (TabCleaner.IsChecked == true) _ = RefreshCleanupScanAsync();

        ScheduleExpandedResize(animate: true);
    }

    private void PinBtn_Click(object sender, RoutedEventArgs e)
    {
        _isPinned = PinBtn.IsChecked == true;
        if (_isPinned) _collapseDelayTimer.Stop();
    }

    public void ToggleExpandPin()
    {
        _isPinned = !_isExpanded;
        PinBtn.IsChecked = _isPinned;
        SetExpanded(!_isExpanded);
    }

    public void SetAlwaysOnTop(bool top)
    {
        _settings.AlwaysOnTop = top;
        if (top)
        {
            // Bump Z-order above borderless/fullscreen windows
            Topmost = false;
            Topmost = true;
        }
        else
        {
            Topmost = false;
        }
        UpdateEyeButtonVisual();
        SettingsService.Save(_settings);
    }

    private void ToggleTopmostFromHotkey()
    {
        bool next = !_settings.AlwaysOnTop;
        SetAlwaysOnTop(next);
        if (next)
        {
            Show();
            FlashIslandBorder(MediaColor.FromRgb(56, 189, 248));
            ShowToast("👁", "Островок поверх окон (Ctrl+Alt+D — скрыть)", durationMs: 2500);
        }
        else
        {
            ShowToast("👁", "Островок не поверх (Ctrl+Alt+D — вернуть)", durationMs: 2500);
        }
    }

    private void EyeTopmostBtn_Click(object sender, RoutedEventArgs e)
    {
        bool next = !_settings.AlwaysOnTop;
        SetAlwaysOnTop(next);
        ShowToast("👁", next ? "Островок поверх всех окон" : "Скрыт за окнами (Ctrl+Alt+D — вернуть)", durationMs: 2800);
    }

    private void UpdateEyeButtonVisual()
    {
        if (_settings.AlwaysOnTop)
        {
            EyeOuterPath.Stroke = new SolidColorBrush(MediaColor.FromRgb(56, 189, 248));
            EyeOuterPath.Fill = new SolidColorBrush(MediaColor.FromArgb(65, 56, 189, 248));
            EyePupil.Fill = new SolidColorBrush(MediaColor.FromRgb(248, 250, 252));
            EyeBtnBorder.BorderBrush = new SolidColorBrush(MediaColor.FromRgb(56, 189, 248));
            EyeTopmostBtn.ToolTip = "Поверх всех окон: ВКЛ (Ctrl+Alt+D — скрыть / вернуть)";
        }
        else
        {
            EyeOuterPath.Stroke = new SolidColorBrush(MediaColor.FromRgb(100, 116, 139));
            EyeOuterPath.Fill = MediaBrushes.Transparent;
            EyePupil.Fill = new SolidColorBrush(MediaColor.FromRgb(100, 116, 139));
            EyeBtnBorder.BorderBrush = MediaBrushes.Transparent;
            EyeTopmostBtn.ToolTip = "Поверх всех окон: ВЫКЛ (Ctrl+Alt+D — вернуть поверх игры)";
        }
    }

    public void OpenTab(string tabKey)
    {
        _isPinned = true;
        PinBtn.IsChecked = true;
        switch (tabKey)
        {
            case "Remote":
            case "Media": TabRemote.IsChecked = true; break;
            case "System": TabSystem.IsChecked = true; break;
            case "ScreenTime": TabScreenTime.IsChecked = true; break;
            case "Cleaner": TabCleaner.IsChecked = true; break;
            default: TabApps.IsChecked = true; break;
        }
        SetExpanded(true);
    }

    public void BringToFront()
    {
        if (!_settings.AlwaysOnTop)
            SetAlwaysOnTop(true);
        else
        {
            Topmost = false;
            Topmost = true;
        }
        Show();
        Activate();
        SetExpanded(true);
    }

    private void SettingsBtn_Click(object sender, RoutedEventArgs e) => OpenSettingsWindow();

    private void RestartAsAdmin_Click(object sender, RoutedEventArgs e) => RestartAsAdmin();

    private void RestartAsAdmin()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                exePath = IOPath.Combine(AppContext.BaseDirectory, "DesktopIsland.exe");

            var psi = new ProcessStartInfo(exePath, "--wait-mutex")
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = IOPath.GetDirectoryName(exePath) ?? AppContext.BaseDirectory
            };

            Process.Start(psi);
            (System.Windows.Application.Current as App)?.ShutdownApplication();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            ShowToast("🛡", "Запуск от админа отменён", durationMs: 2500);
        }
        catch (Exception ex)
        {
            LoggerService.Error("Failed to restart as administrator.", ex);
            ShowToast("⚠", "Не удалось перезапустить от админа", durationMs: 3000);
        }
    }

    public void OpenSettingsWindow()
    {
        var win = new SettingsWindow(
            _settings,
            onSave: updated =>
            {
                _settings = updated;
                SettingsService.Save(_settings);
                SettingsService.ApplyAutostart(_settings.StartWithWindows);
                ApplySettingsVisuals();
                RebuildAppTiles();
                _ = RefreshWeatherAsync();
                ScheduleExpandedResize(animate: true);
                if (!_isExpanded) UpdateCompactWidth(animate: true);
            },
            onExitApp: () => (System.Windows.Application.Current as App)?.ShutdownApplication());
        win.ShowDialog();
    }

    public void CleanupBeforeExit()
    {
        _clockTimer.Stop();
        _monitorTimer.Stop();
        _collapseDelayTimer.Stop();
        _eqTimer.Stop();
        _ramLabelResetTimer.Stop();
        _toastTimer.Stop();
        _weatherTimer.Stop();
        _gameInputTimer.Stop();
        _borderAlertResetTimer.Stop();
        SetWindowClickThrough(false);
        QuickToolsService.RestorePreviousPowerPlan();
        SettingsService.Save(_settings);
        _sysMonitor.StopFpsMonitor();
        if (_ownHwnd != IntPtr.Zero)
            UnregisterHotKey(_ownHwnd, HotkeyToggleTopmostId);
        _hwndSource?.RemoveHook(WindowWndProc);
        _hwndSource = null;
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e) => CleanupBeforeExit();

    #endregion
}
