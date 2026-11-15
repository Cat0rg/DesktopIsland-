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
    private const double ExpandedWidth = 560.0;

    private const int WmDeviceChange = 0x0219;
    private const int DbtDeviceArrival = 0x8000;
    private const int DbtDevtypVolume = 0x00000002;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

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

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    private static uint GetUserIdleMs()
    {
        var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref lii)) return 0;
        uint now = unchecked((uint)Environment.TickCount);
        uint diff = unchecked(now - lii.dwTime);
        // Guard against 1-tick cross-core skew where dwTime appears slightly ahead of TickCount
        return diff > 0x80000000u ? 0u : diff;
    }

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
    private readonly ObservableCollection<NotificationCenterItem> _notifications = new();
    private readonly ObservableCollection<BluetoothDeviceItem> _btDevices = new();
    private readonly ObservableCollection<ScreenTimeItem> _screenTimeItems = new();
    private readonly ObservableCollection<DropShelfItem> _dropShelfItems = new();
    private readonly ObservableCollection<AudioSessionItem> _audioSessions = new();
    private readonly ClipboardService _clipboardService = new();
    private readonly ScreenRecorderService _screenRecorder = new();
    private readonly Dictionary<string, ImageSource?> _iconCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly Queue<double> _cpuHistory = new(16);
    private readonly Queue<double> _gpuHistory = new(16);
    private readonly Queue<double> _ramHistory = new(16);
    private readonly Queue<double> _netHistory = new(16);
    private DateTime? _dndAutoOffEndUtc;
    private string _lastCompactGameExePath = string.Empty;
    private bool _isSystemDetailsExpanded;
    private bool _isRemoteExtrasExpanded;
    private FileSystemWatcher? _downloadsWatcher;
    private string _lastDownloadToastName = string.Empty;
    private DateTime _lastDownloadToastAt = DateTime.MinValue;
    private DateTime _toastStartedUtc = DateTime.MinValue;
    private int _toastTotalMs = 3000;
    private bool _isToastHovered;

    private sealed class ScreenTimeAccumulator
    {
        public double Seconds;
        public bool IsGame;
        public double FpsSum;
        public int FpsSamples;
        public readonly List<double> FpsValues = new();
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
    private readonly DispatcherTimer _eqTimer = new() { Interval = TimeSpan.FromMilliseconds(45) };
    private double _musicOrbAngle;
    private double _musicMarqueeOffset;
    private int _musicMarqueePauseTicks = 30;
    private string _lastMusicBannerTitle = string.Empty;
    private readonly DispatcherTimer _idleEyesTimer = new() { Interval = TimeSpan.FromMilliseconds(45) };
    private double _eyeCurX;
    private double _eyeCurY;
    private double _eyeTargetX;
    private double _eyeTargetY;
    private int _eyeNextGazeTicks = 40;
    private int _eyeBlinkCounter = 75;
    private int _eyeBlinkFrame;
    private bool _eyeDoubleBlinkPending;
    private readonly Random _eyeRng = new();
    private readonly DispatcherTimer _ramLabelResetTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3.2) };
    private readonly DispatcherTimer _weatherTimer = new() { Interval = TimeSpan.FromMinutes(30) };
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromMinutes(10) };
    private string _lastNotifiedUpdateTag = string.Empty;
    private string _pendingUpdateReleaseUrl = string.Empty;
    private string _pendingUpdateZipUrl = string.Empty;
    private bool _isInstallingUpdate;
    private readonly DispatcherTimer _gameInputTimer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private readonly DispatcherTimer _borderAlertResetTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _magnetTimer = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private readonly DispatcherTimer _inputStateTimer = new() { Interval = TimeSpan.FromMilliseconds(260) };

    private MediaColor? _currentMediaDominantColor;
    private bool _isDedicatedMusicSession;
    private bool _isMicMutedCached;
    private string _lastTrackChangeKey = string.Empty;
    private string _lastKnownLayout = string.Empty;
    private bool? _lastKnownCaps;
    private string? _desktopAudioDeviceIdBeforeGame;
    private bool _isAppMixerExpanded;
    private bool _isStreamPrivacyManual;
    private bool _isStreamPrivacyActive;
    private bool _isSuperMiniIdle;
    private bool _isUpdatingBrightnessSlider;
    private DateTime _lastUserInteractionUtc = DateTime.UtcNow;
    private NativePoint _lastCheckedCursor;
    private DateTime _lastThermalAlertAt = DateTime.MinValue;
    private DateTime _lastDiskAlertAt = DateTime.MinValue;
    private DateTime _lastRainAlertAt = DateTime.MinValue;
    private DateTime _lastNetWarnAt = DateTime.MinValue;
    private readonly HashSet<string> _lowBatteryAlertedDevices = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<int> _knownGamepads = new();
    private readonly HashSet<int> _lowBatteryGamepads = new();
    private MediaPoint _shelfDragStart;
    private DropShelfItem? _shelfDragItem;
    private string _lastCalcResult = string.Empty;
    private string _lastTranslateOriginal = string.Empty;
    private string _lastTranslatedText = string.Empty;

    private HwndSource? _hwndSource;
    private uint _taskbarCreatedMsg;
    private IntPtr _ownHwnd;
    private IntPtr _lastExternalHwnd;

    private bool _isExpanded;
    private bool _isPinned;
    private bool _isOptimizingRam;
    private bool _isShowingRamResult;
    private bool _isAppsEditMode;
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
    private bool _timerShutdownOnFinish;

    // Drag X/Y + Edge Dock + Magnetic Pull
    private bool _isWindowDragging;
    private bool _didHorizontalDrag;
    private NativePoint _dragStartCursor;
    private double _dragStartOffset;
    private double _dragStartVerticalOffset;
    private double _dragStartFreeTopPx;
    private double _dragStartWindowLeft;
    private bool _isMagnetActive;
    private double _pulsePhase;
    private string? _activeDockProcessKey;

    private Border? _reorderingTile;
    private PinnedApp? _reorderingApp;
    private MediaPoint _reorderStartPoint;
    private bool _isReorderingApp;
    private bool _suppressReorderLostCapture;
    private bool _isRamHovered;
    private bool _isMicHovered;
    private bool _isReplayHovered;

    public bool IsAlwaysOnTop => _settings.AlwaysOnTop;

    public MainWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        DrivesItemsControl.ItemsSource = _drives;
        TopProcessesList.ItemsSource = _topProcesses;
        RecentDownloadsList.ItemsSource = _recentDownloads;
        NotificationsListControl.ItemsSource = _notifications;
        BtDevicesList.ItemsSource = _btDevices;
        ScreenTimeListControl.ItemsSource = _screenTimeItems;
        CleanupItemsControl.ItemsSource = _cleanupCategories;
        DropShelfList.ItemsSource = _dropShelfItems;
        ClipboardHistoryList.ItemsSource = _clipboardService.Entries;
        AppMixerList.ItemsSource = _audioSessions;
        SyncTranslateDirUi();
        SyncHomeAppsModeUi();

        _clipboardService.ClipboardTextCopied += OnClipboardTextCopied;
        _screenRecorder.RecordingError += err => Dispatcher.BeginInvoke(() =>
        {
            SyncRecordingUi();
            ShowToast("⚠", $"Ошибка записи: {err}", durationMs: 4200);
        });

        _clockTimer.Tick += ClockTimer_Tick;
        _monitorTimer.Tick += MonitorTimer_Tick;
        _collapseDelayTimer.Tick += CollapseDelayTimer_Tick;
        _eqTimer.Tick += EqTimer_Tick;
        _idleEyesTimer.Tick += IdleEyesTimer_Tick;
        _ramLabelResetTimer.Tick += RamLabelResetTimer_Tick;
        _toastTimer.Tick += ToastTimer_Tick;
        _weatherTimer.Tick += async (_, _) => await RefreshWeatherAsync();
        _updateTimer.Tick += async (_, _) => await CheckForUpdatesAsync(manual: false);
        _gameInputTimer.Tick += GameInputTimer_Tick;
        _magnetTimer.Tick += MagnetTimer_Tick;
        _inputStateTimer.Tick += InputStateTimer_Tick;
        _borderAlertResetTimer.Tick += (_, _) =>
        {
            _borderAlertResetTimer.Stop();
            _isBorderAlertActive = false;
            ApplyCurrentIslandBorder();
        };

        ApplySettingsVisuals();
        RebuildAppTiles();
        UpdateRamPillState(animate: false);
        SyncPlayerBindingPills();
        SyncQuickToolStates();
        SyncRecordingUi();
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

        _clipboardService.Attach(this, _settings.EnableClipboardHistory, _settings.ClipboardMaxItems, _settings.PinnedSnippets);
        InitDownloadsWatcher();
        SeedInitialNotifications();
        QuickCalcBox.PreviewMouseLeftButtonDown += (_, _) => Activate();

        PositionTopCenter();
        UpdateClockDisplay();
        _clockTimer.Start();

        _monitorTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(_settings.PollingIntervalMs, 1000, 10000));
        _monitorTimer.Start();
        _eqTimer.Start();
        _weatherTimer.Start();
        _updateTimer.Start();
        _magnetTimer.Start();
        _inputStateTimer.Start();

        _ = RefreshSystemAndMediaAsync();
        RefreshDrives();
        _ = RefreshWeatherAsync();
        _ = HardwareAndUtilityService.EnsureExchangeRatesAsync();
        _ = RefreshCleanupScanAsync();
        _ = CheckUpdatesOnStartupAsync();

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
                            AddNotification("💾", "Устройства", $"Подключен диск ({driveLetter}:)", root, "Открыть", () => OpenShellTarget(root));
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

        UpdateCenterButtonVisibility();
        PositionTopCenter();
    }

    private string CurrentDockEdge =>
        (!string.IsNullOrEmpty(_activeDockProcessKey) &&
         _settings.ProcessDockProfiles.TryGetValue(_activeDockProcessKey, out var prof))
            ? prof.DockEdge
            : _settings.DockEdge;

    private double CurrentHorizontalOffsetPx =>
        (!string.IsNullOrEmpty(_activeDockProcessKey) &&
         _settings.ProcessDockProfiles.TryGetValue(_activeDockProcessKey, out var prof))
            ? prof.HorizontalOffsetPx
            : _settings.HorizontalOffsetPx;

    private double CurrentVerticalOffsetPx =>
        (!string.IsNullOrEmpty(_activeDockProcessKey) &&
         _settings.ProcessDockProfiles.TryGetValue(_activeDockProcessKey, out var prof))
            ? prof.VerticalOffsetPx
            : _settings.VerticalOffsetPx;

    private void MutateActiveDockProfile(Action<IslandDockProfile> update)
    {
        if (!string.IsNullOrEmpty(_activeDockProcessKey))
        {
            if (!_settings.ProcessDockProfiles.TryGetValue(_activeDockProcessKey, out var prof))
            {
                prof = new IslandDockProfile
                {
                    DockEdge = _settings.DockEdge,
                    HorizontalOffsetPx = _settings.HorizontalOffsetPx,
                    VerticalOffsetPx = _settings.VerticalOffsetPx
                };
                _settings.ProcessDockProfiles[_activeDockProcessKey] = prof;
            }
            update(prof);
        }
        else
        {
            var desk = new IslandDockProfile
            {
                DockEdge = _settings.DockEdge,
                HorizontalOffsetPx = _settings.HorizontalOffsetPx,
                VerticalOffsetPx = _settings.VerticalOffsetPx
            };
            update(desk);
            _settings.DockEdge = desk.DockEdge;
            _settings.HorizontalOffsetPx = desk.HorizontalOffsetPx;
            _settings.VerticalOffsetPx = desk.VerticalOffsetPx;
        }
    }

    private string EffectiveDockEdge =>
        CurrentDockEdge is "Left" or "Right" or "Free" ? CurrentDockEdge : "Top";

    private bool IsSideDocked => EffectiveDockEdge is "Left" or "Right";

    private bool IsDockedToTop =>
        EffectiveDockEdge == "Top" && (_settings.DockToTopEdge || (_isInFullscreenGame && _settings.GameModeAutoProfile));

    private bool IsGameNotchCollapsed => _isInFullscreenGame && _settings.GameModeAutoProfile && !_isExpanded;

    private Thickness GetDockBorderThickness() => EffectiveDockEdge switch
    {
        "Left" => new Thickness(0, 1, 1, 1),
        "Right" => new Thickness(1, 1, 0, 1),
        _ => IsDockedToTop ? new Thickness(1, 0, 1, 1) : new Thickness(1)
    };

    private CornerRadius GetExpandedCornerRadius() => EffectiveDockEdge switch
    {
        "Left" => new CornerRadius(0, 22, 22, 0),
        "Right" => new CornerRadius(22, 0, 0, 22),
        _ => IsDockedToTop ? new CornerRadius(0, 0, 22, 22) : new CornerRadius(22)
    };

    private void ApplyGameVisualChrome()
    {
        bool gameNotch = IsGameNotchCollapsed;
        bool dockTop = IsDockedToTop;
        bool side = IsSideDocked;
        bool gameMode = _isInFullscreenGame && _settings.GameModeAutoProfile;

        string edge = EffectiveDockEdge;
        var align = edge switch
        {
            "Left" => System.Windows.HorizontalAlignment.Left,
            "Right" => System.Windows.HorizontalAlignment.Right,
            _ => System.Windows.HorizontalAlignment.Center
        };

        OuterDockStack.Orientation = side ? System.Windows.Controls.Orientation.Vertical : System.Windows.Controls.Orientation.Horizontal;
        OuterDockStack.HorizontalAlignment = align;
        IslandHoverZone.HorizontalAlignment = side ? align : System.Windows.HorizontalAlignment.Left;
        SideActionStrip.HorizontalAlignment = side ? align : System.Windows.HorizontalAlignment.Left;
        SideActionStrip.Margin = side
            ? new Thickness(edge == "Left" ? 6 : 0, 5, edge == "Right" ? 6 : 0, 0)
            : new Thickness(0);

        if (!_isExpanded)
            SyncCompactStripVisibility();

        CompactContentGrid.Height = side ? double.NaN : (gameNotch ? 30.0 : CompactHeight);
        CompactWeatherText.Visibility = (!side && !gameNotch && _settings.CompactShowWeather && _currentWeather != null)
            ? Visibility.Visible : Visibility.Collapsed;

        SideActionStrip.Visibility = Visibility.Collapsed;
        RamQuickPill.Visibility = Visibility.Collapsed;
        GameMicPill.Visibility = Visibility.Collapsed;
        ReplayClipPill.Visibility = Visibility.Collapsed;

        UpdateRamPillState(animate: false);
        UpdateMicPillState(animate: false);
        UpdateReplayPillState(animate: false);
        SyncMicMuteUi();

        IslandBorder.BorderThickness = _settings.BorderStyle == "Minimal" && !_isBorderAlertActive
            ? new Thickness(0)
            : GetDockBorderThickness();
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
            if (enable && (_isRamHovered || _isMicHovered || _isReplayHovered))
            {
                _isRamHovered = false;
                _isMicHovered = false;
                _isReplayHovered = false;
                RamQuickPill.BorderBrush = new SolidColorBrush(MediaColor.FromRgb(46, 50, 69));
                RamLightningPath.Stroke = MediaBrushes.Transparent;
                ReplayClipPill.BorderBrush = new SolidColorBrush(MediaColor.FromRgb(46, 50, 69));
                SyncMicMuteUi();
                UpdateRamPillState(animate: true);
                UpdateMicPillState(animate: true);
                UpdateReplayPillState(animate: true);
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
        {
            IslandBorder.BorderBrush = new SolidColorBrush(MediaColor.FromRgb(56, 189, 248));
            return;
        }
        if (_screenRecorder.IsRecording)
        {
            IslandBorder.BorderBrush = new SolidColorBrush(MediaColor.FromRgb(239, 68, 68));
            return;
        }
        if (_isInFullscreenGame && _settings.GameModeAutoProfile && _isMicMutedCached && _settings.GameShowMicMute)
        {
            IslandBorder.BorderBrush = new SolidColorBrush(MediaColor.FromRgb(220, 38, 38));
            return;
        }
        if (_settings.BorderStyle == "Neon")
        {
            var (_, bright) = ThemeHelper.GetAccentColors(_settings.AccentTheme);
            IslandBorder.BorderBrush = new SolidColorBrush(MediaColor.FromArgb(190, bright.R, bright.G, bright.B));
            return;
        }
        if (_settings.BorderStyle == "Minimal")
        {
            IslandBorder.BorderBrush = MediaBrushes.Transparent;
            return;
        }
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
        bool side = IsSideDocked;
        bool fullHeight = _isMicHovered || _isExpanded;
        double expandedTopHeight = _isExpanded ? 36.0 : 30.0;
        double targetHeight = side ? (fullHeight ? 28.0 : 20.0) : (fullHeight ? expandedTopHeight : 15.0);
        double targetWidth = side ? (fullHeight ? 28.0 : 26.0) : (_isExpanded ? 36.0 : 30.0);
        double radius = targetHeight / 2.0;

        if (side)
        {
            GameMicPill.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
            GameMicPill.Margin = new Thickness(5, 0, 0, 0);
            GameMicPill.CornerRadius = new CornerRadius(radius);
            GameMicPill.BorderThickness = new Thickness(1);
        }
        else
        {
            GameMicPill.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
            GameMicPill.Margin = dockTop ? new Thickness(5, 0, 0, 0) : new Thickness(5, 2, 0, 0);
            GameMicPill.CornerRadius = dockTop
                ? new CornerRadius(0, 0, radius, radius)
                : new CornerRadius(radius);
            GameMicPill.BorderThickness = dockTop ? new Thickness(1, 0, 1, 1) : new Thickness(1);
        }

        GameMicPill.Width = targetWidth;
        GameMicViewbox.Width = _isExpanded ? 14.5 : (fullHeight ? 12.5 : 9.5);
        GameMicViewbox.Height = _isExpanded ? 14.5 : (fullHeight ? 12.5 : 9.5);

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
        _isMicMutedCached = muted;
        GameMicWarnIcon.Visibility = (muted && _isInFullscreenGame && _settings.GameShowMicMute && !_isSuperMiniIdle)
            ? Visibility.Visible : Visibility.Collapsed;

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
        ApplyCurrentIslandBorder();
    }

    private void UpdateCenterButtonVisibility()
    {
        CenterPosBtn.Visibility = (EffectiveDockEdge != "Top" || Math.Abs(CurrentHorizontalOffsetPx) > 8)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ReplayClipPill_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _isReplayHovered = true;
        ReplayClipPill.SetResourceReference(Border.BorderBrushProperty, "AccentBrightBrush");
        UpdateReplayPillState(animate: true);
    }

    private void ReplayClipPill_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _isReplayHovered = false;
        ReplayClipPill.BorderBrush = new SolidColorBrush(MediaColor.FromRgb(46, 50, 69));
        UpdateReplayPillState(animate: true);
    }

    private void UpdateReplayPillState(bool animate)
    {
        if (ReplayClipPill.Visibility != Visibility.Visible) return;
        bool dockTop = IsDockedToTop;
        bool side = IsSideDocked;
        bool fullHeight = _isReplayHovered || _isExpanded;
        double expandedTopHeight = _isExpanded ? 36.0 : 30.0;
        double targetHeight = side ? (fullHeight ? 28.0 : 20.0) : (fullHeight ? expandedTopHeight : 15.0);
        double targetWidth = side ? (fullHeight ? 28.0 : 26.0) : (_isExpanded ? 36.0 : 30.0);
        double r = targetHeight / 2.0;

        ReplayClipPill.Width = targetWidth;
        ReplayClipViewbox.Width = _isExpanded ? 14.5 : (fullHeight ? 13.5 : 9.5);
        ReplayClipViewbox.Height = _isExpanded ? 14.5 : (fullHeight ? 13.5 : 9.5);

        if (dockTop && !side)
        {
            ReplayClipPill.Margin = new Thickness(5, 0, 0, 0);
            ReplayClipPill.CornerRadius = new CornerRadius(0, 0, r, r);
            ReplayClipPill.BorderThickness = new Thickness(1, 0, 1, 1);
        }
        else
        {
            ReplayClipPill.Margin = side ? new Thickness(5, 0, 0, 0) : new Thickness(5, 2, 0, 0);
            ReplayClipPill.CornerRadius = new CornerRadius(r);
            ReplayClipPill.BorderThickness = new Thickness(1);
        }

        var dur = _settings.EnableAnimations && animate
            ? TimeSpan.FromMilliseconds(180)
            : TimeSpan.Zero;

        if (dur == TimeSpan.Zero)
        {
            ReplayClipPill.BeginAnimation(HeightProperty, null);
            ReplayClipPill.Height = targetHeight;
        }
        else
        {
            var anim = new DoubleAnimation(targetHeight, dur)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            ReplayClipPill.BeginAnimation(HeightProperty, anim);
        }
    }

    private void UpdateWindowOpacity()
    {
        double targetOpacity;
        if (_isExpanded || OuterDockStack.IsMouseOver)
            targetOpacity = 1.0;
        else if (_isInFullscreenVideo && _settings.DimOnFullscreenVideo)
            targetOpacity = 0.12;
        else
            targetOpacity = Math.Clamp(_settings.CapsuleOpacity, 0.72, 1.0);

        if (_settings.EnableAnimations && Math.Abs(RootCanvasContainer.Opacity - targetOpacity) > 0.02)
            RootCanvasContainer.BeginAnimation(OpacityProperty, new DoubleAnimation(targetOpacity, TimeSpan.FromMilliseconds(220)));
        else
        {
            RootCanvasContainer.BeginAnimation(OpacityProperty, null);
            RootCanvasContainer.Opacity = targetOpacity;
        }
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
            double screenHeight = screen.Bounds.Height / scaleY;

            string edge = EffectiveDockEdge;
            bool dockTop = IsDockedToTop;

            if (edge == "Left")
            {
                RootCanvasContainer.Margin = new Thickness(0, 6, 6, 6);
                IslandHoverZone.Padding = new Thickness(0, 2, 2, 2);

                double activeH = ActualHeight > 20 ? ActualHeight : 140;
                double maxVert = Math.Max(40, (screenHeight - activeH) / 2.0 - 16);
                double clampedY = Math.Clamp(CurrentVerticalOffsetPx, -maxVert, maxVert);
                if (_isWindowDragging)
                    MutateActiveDockProfile(p => p.VerticalOffsetPx = clampedY);

                Left = screenLeft;
                Top = screenTop + (screenHeight - activeH) / 2.0 + clampedY;
            }
            else if (edge == "Right")
            {
                RootCanvasContainer.Margin = new Thickness(6, 6, 0, 6);
                IslandHoverZone.Padding = new Thickness(2, 2, 0, 2);

                double activeW = ActualWidth > 20 ? ActualWidth : (_isExpanded ? ExpandedWidth + 12 : 76);
                double activeH = ActualHeight > 20 ? ActualHeight : 140;
                double maxVert = Math.Max(40, (screenHeight - activeH) / 2.0 - 16);
                double clampedY = Math.Clamp(CurrentVerticalOffsetPx, -maxVert, maxVert);
                if (_isWindowDragging)
                    MutateActiveDockProfile(p => p.VerticalOffsetPx = clampedY);

                Left = screenLeft + screenWidth - activeW;
                Top = screenTop + (screenHeight - activeH) / 2.0 + clampedY;
            }
            else
            {
                bool freePos = edge == "Free";
                RootCanvasContainer.Margin = dockTop ? new Thickness(6, 0, 6, 6) : new Thickness(6);
                IslandHoverZone.Padding = dockTop ? new Thickness(2, 0, 2, 2) : new Thickness(2);

                double ramW = RamQuickPill.Visibility == Visibility.Visible
                    ? (double.IsNaN(RamQuickPill.Width) ? 74 : RamQuickPill.Width)
                    : 0;
                double extraPillsW = ramW +
                                     (GameMicPill.Visibility == Visibility.Visible ? 42 : 0) +
                                     (ReplayClipPill.Visibility == Visibility.Visible ? 38 : 0);
                double activeContentWidth = (_isExpanded ? ExpandedWidth : IslandBorder.Width) + extraPillsW + 22;
                if (activeContentWidth < 260) activeContentWidth = 420;

                double maxOffset = Math.Max(40, (screenWidth - activeContentWidth) / 2.0 - 16);
                double clampedOffset = Math.Clamp(CurrentHorizontalOffsetPx, -maxOffset, maxOffset);
                if (_isWindowDragging)
                    MutateActiveDockProfile(p => p.HorizontalOffsetPx = clampedOffset);

                Left = screenLeft + (screenWidth - ActualWidth) / 2.0 + clampedOffset;
                if (freePos)
                {
                    double activeH = ActualHeight > 20 ? ActualHeight : (_isExpanded ? 420 : CompactHeight + 16);
                    double maxTop = Math.Max(4.0, screenHeight - activeH - 4.0);
                    double clampedTop = Math.Clamp(CurrentVerticalOffsetPx, 4.0, maxTop);
                    if (_isWindowDragging)
                        MutateActiveDockProfile(p => p.VerticalOffsetPx = clampedTop);
                    Top = screenTop + clampedTop;
                }
                else
                {
                    Top = dockTop ? screenTop : screenTop + Math.Clamp(_settings.TopOffsetPx, 0, 120);
                }
            }
        }
        catch { }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        PositionTopCenter();
    }

    private bool ShouldShowFullMusicMode =>
        _settings.CompactShowMedia && _isMediaPlaying && !IsSideDocked &&
        (!_settings.MusicFilterOnlyMusicApps || _isDedicatedMusicSession);

    private void SyncCompactStripVisibility()
    {
        if (IsSideDocked)
        {
            CompactMetricsStrip.Visibility = Visibility.Collapsed;
            CompactMusicStrip.Visibility = Visibility.Collapsed;
            CompactToastStrip.Visibility = Visibility.Collapsed;
            CompactIdleEyesStrip.Visibility = Visibility.Collapsed;
            CompactSideStrip.Visibility = Visibility.Visible;
            RamQuickPill.Visibility = Visibility.Visible;
            return;
        }

        CompactSideStrip.Visibility = Visibility.Collapsed;
        FrameworkElement nextVisible;
        if (_toastTimer.IsEnabled)
        {
            CompactMetricsStrip.Visibility = Visibility.Collapsed;
            CompactMusicStrip.Visibility = Visibility.Collapsed;
            CompactIdleEyesStrip.Visibility = Visibility.Collapsed;
            nextVisible = CompactToastStrip;
        }
        else if (ShouldShowFullMusicMode)
        {
            CompactMetricsStrip.Visibility = Visibility.Collapsed;
            CompactToastStrip.Visibility = Visibility.Collapsed;
            CompactIdleEyesStrip.Visibility = Visibility.Collapsed;
            nextVisible = CompactMusicStrip;
        }
        else if (_isSuperMiniIdle && _settings.SuperMiniOnIdle && !_isInFullscreenGame && !_screenRecorder.IsRecording)
        {
            CompactMetricsStrip.Visibility = Visibility.Collapsed;
            CompactMusicStrip.Visibility = Visibility.Collapsed;
            CompactToastStrip.Visibility = Visibility.Collapsed;
            nextVisible = CompactIdleEyesStrip;
        }
        else
        {
            CompactMusicStrip.Visibility = Visibility.Collapsed;
            CompactToastStrip.Visibility = Visibility.Collapsed;
            CompactIdleEyesStrip.Visibility = Visibility.Collapsed;
            nextVisible = CompactMetricsStrip;
        }

        // Side quick pills disabled — keep single clean main island only
        SideActionStrip.Visibility = Visibility.Collapsed;
        RamQuickPill.Visibility = Visibility.Collapsed;
        GameMicPill.Visibility = Visibility.Collapsed;
        ReplayClipPill.Visibility = Visibility.Collapsed;
        if (nextVisible != CompactMetricsStrip)
            CompactLoadBarHost.Visibility = Visibility.Collapsed;

        bool wasCollapsed = nextVisible.Visibility != Visibility.Visible;
        nextVisible.Visibility = Visibility.Visible;
        if (wasCollapsed && _settings.EnableAnimations)
        {
            nextVisible.BeginAnimation(OpacityProperty, null);
            nextVisible.Opacity = 0.0;
            nextVisible.BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(160)));
        }
        else
        {
            nextVisible.BeginAnimation(OpacityProperty, null);
            nextVisible.Opacity = 1.0;
        }
    }

    private double MeasureCompactWidth()
    {
        double contentW = 190;
        if (CompactToastStrip.Visibility == Visibility.Visible)
        {
            CompactToastStrip.Measure(new System.Windows.Size(double.PositiveInfinity, CompactHeight));
            contentW = Math.Max(220, CompactToastStrip.DesiredSize.Width + 32);
        }
        else if (CompactMusicStrip.Visibility == Visibility.Visible)
        {
            return 222.0;
        }
        else if (CompactIdleEyesStrip.Visibility == Visibility.Visible)
        {
            return 84.0;
        }
        else
        {
            CompactMetricsStrip.Measure(new System.Windows.Size(double.PositiveInfinity, CompactHeight));
            double measured = CompactMetricsStrip.DesiredSize.Width + 12;
            double minDesktopW = _isInFullscreenGame ? 190 : 226;
            contentW = Math.Max(minDesktopW, measured);
        }
        return Math.Clamp(Math.Ceiling(contentW), 190, 560);
    }

    private double MeasureSideHeight()
    {
        CompactSideStrip.Measure(new System.Windows.Size(68, double.PositiveInfinity));
        return Math.Clamp(Math.Ceiling(CompactSideStrip.DesiredSize.Height + 6), 68, 190);
    }

    private void UpdateCompactWidth(bool animate)
    {
        if (_isExpanded) return;
        SyncCompactStripVisibility();

        bool superMini = CompactIdleEyesStrip.Visibility == Visibility.Visible;
        CompactMetricsStrip.Margin = new Thickness(16, 0, 16, 0);

        string edge = EffectiveDockEdge;
        if (edge is "Left" or "Right")
        {
            CompactContentGrid.Height = double.NaN;
            double sideW = 68.0;
            double sideH = MeasureSideHeight();
            var sideCorner = edge == "Left" ? new CornerRadius(0, 18, 18, 0) : new CornerRadius(18, 0, 0, 18);
            AnimateIslandSize(sideW, sideH, sideCorner, animate);
            return;
        }

        bool dockTop = IsDockedToTop;
        bool gameNotch = IsGameNotchCollapsed;
        double h = superMini ? 28.0 : (gameNotch ? 30.0 : CompactHeight);
        CompactContentGrid.Height = h;

        double targetW = MeasureCompactWidth();
        double radius = h / 2.0;
        var corner = dockTop ? new CornerRadius(0, 0, radius, radius) : new CornerRadius(radius);
        AnimateIslandSize(targetW, h, corner, animate);
    }

    private void SetExpanded(bool expand)
    {
        if (_isExpanded == expand) return;
        _isExpanded = expand;
        if (expand)
        {
            _lastUserInteractionUtc = DateTime.UtcNow;
            if (_isSuperMiniIdle)
                ExitSuperMiniIdle(updateWidth: false);
            ResetMagnetImmediate();
            SyncMicMuteUi();
        }

        bool gameMode = _isInFullscreenGame && _settings.GameModeAutoProfile;
        SideActionStrip.Visibility = Visibility.Collapsed;
        RamQuickPill.Visibility = Visibility.Collapsed;
        GameMicPill.Visibility = Visibility.Collapsed;
        ReplayClipPill.Visibility = Visibility.Collapsed;

        UpdateWindowOpacity();
        UpdateRamPillState(animate: true);
        UpdateMicPillState(animate: true);
        UpdateReplayPillState(animate: true);
        if (_isInFullscreenGame && _settings.GameModeAutoProfile)
            SetWindowClickThrough(!expand && _settings.GameClickThrough && !_isAltHeldInGame);

        IslandBorder.BorderThickness = _settings.BorderStyle == "Minimal" && !_isBorderAlertActive
            ? new Thickness(0)
            : GetDockBorderThickness();

        if (expand)
        {
            _collapseDelayTimer.Stop();
            CompactContentGrid.Visibility = Visibility.Collapsed;
            CompactSideStrip.Visibility = Visibility.Collapsed;
            ExpandedContentGrid.Visibility = Visibility.Visible;
            if (_settings.EnableAnimations)
            {
                AnimateTabBodyEntrance();
            }
            else
            {
                ExpandedContentGrid.BeginAnimation(OpacityProperty, null);
                ExpandedContentGrid.Opacity = 1.0;
                ExpandedTabBody.BeginAnimation(OpacityProperty, null);
                ExpandedTabBody.Opacity = 1.0;
                TabBodyTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                TabBodyTranslate.Y = 0;
                TabBodyScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                TabBodyScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                TabBodyScale.ScaleX = 1.0;
                TabBodyScale.ScaleY = 1.0;
            }

            if (TabApps.IsChecked == true)
            {
                RebuildAppTiles();
                SyncHomeAppsModeUi();
                SyncNotificationCenterUi();
                _ = RefreshMediaAndDevicesAsync(forceBluetooth: false);
            }

            if (TabSystem.IsChecked == true)
            {
                RefreshDrives();
                _ = RefreshMediaAndDevicesAsync(forceBluetooth: true);
            }
            if (TabScreenTime.IsChecked == true) RefreshScreenTimeUi();
            if (TabRemote.IsChecked == true) _ = RefreshMediaAndDevicesAsync(forceBluetooth: true);
            if (TabUtils.IsChecked == true) RefreshUtilsTab();
            if (TabCleaner.IsChecked == true) _ = RefreshCleanupScanAsync();

            ScheduleExpandedResize(animate: true);
        }
        else
        {
            _isAppsEditMode = false;
            EditAppsBtn.IsChecked = false;
            RebuildAppTiles();

            ExpandedContentGrid.BeginAnimation(OpacityProperty, null);
            ExpandedContentGrid.Opacity = 1.0;
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
        double contentW = ExpandedWidth - 38.0; // Margin L(18)+R(18)=36 + Border 2px
        ExpandedContentGrid.Width = contentW;
        ExpandedContentGrid.Height = double.NaN;
        ExpandedContentGrid.InvalidateMeasure();
        ExpandedContentGrid.Measure(new System.Windows.Size(ExpandedWidth - 2.0, double.PositiveInfinity));

        double measuredH = ExpandedContentGrid.DesiredSize.Height;
        double borderH = IslandBorder.BorderThickness.Top + IslandBorder.BorderThickness.Bottom;
        double targetHeight = Math.Clamp(Math.Ceiling(measuredH + borderH + 2.0), 120, 780);
        AnimateIslandSize(ExpandedWidth, targetHeight, GetExpandedCornerRadius(), animate);
    }

    private void AnimateIslandSize(double targetW, double targetH, CornerRadius targetCorner, bool animate)
    {
        var dur = _settings.EnableAnimations && animate
            ? TimeSpan.FromMilliseconds(Math.Clamp(_settings.AnimationDurationMs, 100, 450))
            : TimeSpan.Zero;

        double curW = IslandBorder.ActualWidth > 0 ? IslandBorder.ActualWidth : IslandBorder.Width;
        double curH = IslandBorder.ActualHeight > 0 ? IslandBorder.ActualHeight : IslandBorder.Height;
        bool growing = targetW >= curW - 1 && targetH >= curH - 1;
        IEasingFunction ease = animate && growing
            ? new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.16 }
            : new QuinticEase { EasingMode = EasingMode.EaseOut };

        IslandHoverZone.CornerRadius = new CornerRadius(
            targetCorner.TopLeft > 0 ? targetCorner.TopLeft + 2 : 0,
            targetCorner.TopRight > 0 ? targetCorner.TopRight + 2 : 0,
            targetCorner.BottomRight > 0 ? targetCorner.BottomRight + 2 : 0,
            targetCorner.BottomLeft > 0 ? targetCorner.BottomLeft + 2 : 0);

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

    private void AnimateTabBodyEntrance()
    {
        if (!_settings.EnableAnimations) return;
        ExpandedContentGrid.BeginAnimation(OpacityProperty, null);
        ExpandedContentGrid.Opacity = 1.0;

        ExpandedTabBody.BeginAnimation(OpacityProperty, null);
        ExpandedTabBody.Opacity = 0.0;
        ExpandedTabBody.BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });

        TabBodyTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        TabBodyTranslate.Y = 10.0;
        TabBodyTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(250))
        {
            EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut }
        });

        TabBodyScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        TabBodyScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        TabBodyScale.ScaleX = 0.985;
        TabBodyScale.ScaleY = 0.985;
        var scaleEase = new QuinticEase { EasingMode = EasingMode.EaseOut };
        TabBodyScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(240)) { EasingFunction = scaleEase });
        TabBodyScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(240)) { EasingFunction = scaleEase });
    }

    private void AnimatePanelReveal(FrameworkElement panel, bool show, Func<bool>? isStillClosedCheck = null, ScaleTransform? scale = null, TranslateTransform? translate = null)
    {
        if (!_settings.EnableAnimations)
        {
            panel.BeginAnimation(OpacityProperty, null);
            panel.Opacity = 1.0;
            if (translate != null) { translate.BeginAnimation(TranslateTransform.YProperty, null); translate.Y = 0; }
            if (scale != null) { scale.BeginAnimation(ScaleTransform.ScaleYProperty, null); scale.ScaleY = 1.0; }
            panel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            ScheduleExpandedResize(animate: false);
            return;
        }

        if (show)
        {
            panel.Visibility = Visibility.Visible;
            panel.BeginAnimation(OpacityProperty, null);
            panel.Opacity = 0.0;
            panel.BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(210))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });

            if (translate != null)
            {
                translate.BeginAnimation(TranslateTransform.YProperty, null);
                translate.Y = -8.0;
                translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(230))
                {
                    EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut }
                });
            }
            if (scale != null)
            {
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                scale.ScaleY = 0.95;
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(230))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
            }
            ScheduleExpandedResize(animate: true);
        }
        else
        {
            if (panel.Visibility != Visibility.Visible) return;
            var fadeOut = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(145))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            fadeOut.Completed += (_, _) =>
            {
                if (isStillClosedCheck == null || isStillClosedCheck())
                {
                    panel.Visibility = Visibility.Collapsed;
                    panel.BeginAnimation(OpacityProperty, null);
                    panel.Opacity = 1.0;
                    if (translate != null) { translate.BeginAnimation(TranslateTransform.YProperty, null); translate.Y = 0; }
                    if (scale != null) { scale.BeginAnimation(ScaleTransform.ScaleYProperty, null); scale.ScaleY = 1.0; }
                    ScheduleExpandedResize(animate: true);
                }
            };
            panel.BeginAnimation(OpacityProperty, fadeOut);
            if (translate != null)
                translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-6.0, TimeSpan.FromMilliseconds(145)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
            if (scale != null)
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, TimeSpan.FromMilliseconds(145)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
        }
    }

    private void UpdateRamPillState(bool animate)
    {
        bool dockTop = IsDockedToTop;
        bool sideDocked = IsSideDocked;
        bool gameNotch = IsGameNotchCollapsed;
        bool showText = (_isExpanded && !sideDocked) || _isShowingRamResult;
        bool fullHeight = _isRamHovered || _isExpanded || _isShowingRamResult;

        RamQuickText.Visibility = showText ? Visibility.Visible : Visibility.Collapsed;
        if (!_isShowingRamResult)
            RamQuickText.Text = "ОЗУ";

        double normalHeight = sideDocked ? 28.0 : ((gameNotch && !_isShowingRamResult) ? 30.0 : 36.0);
        double targetHeight = fullHeight ? normalHeight : (sideDocked ? 20.0 : normalHeight / 2.0);
        double targetWidth = showText ? (_isShowingRamResult ? 76.0 : 70.0) : (sideDocked ? (fullHeight ? 28.0 : 26.0) : normalHeight);

        RamQuickPill.Margin = sideDocked
            ? new Thickness(0)
            : (dockTop ? new Thickness(6, 0, 0, 0) : new Thickness(6, 2, 0, 0));

        if (dockTop && !sideDocked)
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
            var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.28 };
            RamQuickPill.BeginAnimation(WidthProperty, new DoubleAnimation(targetWidth, dur) { EasingFunction = ease });
            RamQuickPill.BeginAnimation(HeightProperty, new DoubleAnimation(targetHeight, dur) { EasingFunction = ease });
        }
    }

    #endregion

    #region Hover, Magnet, Wheel Volume, Drag Dock & Anti-Misclick

    private void MagnetTimer_Tick(object? sender, EventArgs e)
    {
        if (!_settings.EnableAnimations || _isExpanded || _isWindowDragging || _isInFullscreenGame)
        {
            if (_isMagnetActive) ResetMagnetImmediate();
            return;
        }

        if (!GetCursorPos(out var pt))
            return;

        var dpi = VisualTreeHelper.GetDpi(this);
        double dpiX = dpi.DpiScaleX <= 0 ? 1.0 : dpi.DpiScaleX;
        double dpiY = dpi.DpiScaleY <= 0 ? 1.0 : dpi.DpiScaleY;
        double cx = pt.X / dpiX;
        double cy = pt.Y / dpiY;

        double w = IslandBorder.ActualWidth > 0 ? IslandBorder.ActualWidth : 240;
        double h = IslandBorder.ActualHeight > 0 ? IslandBorder.ActualHeight : 36;
        double left = Left;
        double top = Top;
        double right = left + w;
        double bottom = top + h;

        double nearX = Math.Clamp(cx, left, right);
        double nearY = Math.Clamp(cy, top, bottom);
        double dx = cx - nearX;
        double dy = cy - nearY;
        double dist = Math.Sqrt(dx * dx + dy * dy);

        const double maxRadius = 56.0;
        if (dist <= 75.0 && _isSuperMiniIdle)
        {
            _lastUserInteractionUtc = DateTime.UtcNow;
            ExitSuperMiniIdle();
        }
        if (dist > 0.5 && dist <= maxRadius)
        {
            double strength = 1.0 - (dist / maxRadius);
            double ease = strength * strength;
            double ux = dx / dist;
            double uy = dy / dist;

            double targetTx = ux * 3.8 * ease;
            double targetTy = uy * 3.2 * ease;
            double targetSx = 1.0 + Math.Abs(ux) * 0.032 * ease;
            double targetSy = 1.0 + Math.Abs(uy) * 0.038 * ease;

            IslandMagnetTranslate.X += (targetTx - IslandMagnetTranslate.X) * 0.28;
            IslandMagnetTranslate.Y += (targetTy - IslandMagnetTranslate.Y) * 0.28;
            IslandMagnetScale.ScaleX += (targetSx - IslandMagnetScale.ScaleX) * 0.28;
            IslandMagnetScale.ScaleY += (targetSy - IslandMagnetScale.ScaleY) * 0.28;
            _isMagnetActive = true;
        }
        else if (_isMagnetActive)
        {
            IslandMagnetTranslate.X *= 0.72;
            IslandMagnetTranslate.Y *= 0.72;
            IslandMagnetScale.ScaleX += (1.0 - IslandMagnetScale.ScaleX) * 0.28;
            IslandMagnetScale.ScaleY += (1.0 - IslandMagnetScale.ScaleY) * 0.28;

            if (Math.Abs(IslandMagnetTranslate.X) < 0.05 && Math.Abs(IslandMagnetTranslate.Y) < 0.05 &&
                Math.Abs(IslandMagnetScale.ScaleX - 1.0) < 0.001 && Math.Abs(IslandMagnetScale.ScaleY - 1.0) < 0.001)
            {
                ResetMagnetImmediate();
            }
        }
    }

    private void ResetMagnetImmediate()
    {
        _isMagnetActive = false;
        IslandMagnetTranslate.X = 0;
        IslandMagnetTranslate.Y = 0;
        IslandMagnetScale.ScaleX = 1.0;
        IslandMagnetScale.ScaleY = 1.0;
    }

    private void IslandHoverZone_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _collapseDelayTimer.Stop();
        _lastUserInteractionUtc = DateTime.UtcNow;
        bool interactiveToastShown = _toastTimer.IsEnabled && _toastAction != null && CompactToastStrip.Visibility == Visibility.Visible;
        bool willExpand = !interactiveToastShown
                          && !(_isInFullscreenGame && _settings.GameModeAutoProfile && !_isPinned)
                          && _settings.ExpandOnHover && !_isExpanded;
        if (_isSuperMiniIdle)
            ExitSuperMiniIdle(updateWidth: !willExpand);
        UpdateWindowOpacity();

        if (willExpand)
            SetExpanded(true);
    }

    private void SideActionStrip_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _collapseDelayTimer.Stop();
        _lastUserInteractionUtc = DateTime.UtcNow;
        if (_isSuperMiniIdle)
            ExitSuperMiniIdle();
        UpdateWindowOpacity();
    }

    private void IslandHoverZone_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        UpdateWindowOpacity();
        if (_isReorderingApp || _isWindowDragging) return;
        if (_isExpanded && !_isPinned)
            _collapseDelayTimer.Start();
    }

    private bool IsCursorOverIslandArea()
    {
        try
        {
            if (!GetCursorPos(out var pt)) return false;
            var dpi = VisualTreeHelper.GetDpi(this);
            double dpiX = dpi.DpiScaleX <= 0 ? 1.0 : dpi.DpiScaleX;
            double dpiY = dpi.DpiScaleY <= 0 ? 1.0 : dpi.DpiScaleY;
            double cx = pt.X / dpiX;
            double cy = pt.Y / dpiY;

            double w = OuterDockStack.ActualWidth > 0 ? OuterDockStack.ActualWidth : ActualWidth;
            double h = OuterDockStack.ActualHeight > 0 ? OuterDockStack.ActualHeight : ActualHeight;
            const double pad = 16.0;
            return cx >= Left - pad && cx <= Left + w + pad &&
                   cy >= Top - pad && cy <= Top + h + pad;
        }
        catch { return false; }
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
        if (_isReorderingApp || _isWindowDragging || _isPinned) return;

        if (OuterDockStack.IsMouseOver || IsCursorOverIslandArea() || (_isInFullscreenGame && _isAltHeldInGame))
        {
            // Keep expanded while cursor stays over island/side pills (⚡/🎙/🎬) or Alt is held in game
            _collapseDelayTimer.Start();
            return;
        }

        SetExpanded(false);
    }

    private void Window_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        _lastUserInteractionUtc = DateTime.UtcNow;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            if (e.Delta > 0)
            {
                _ = MediaControlService.NextAsync(_settings.PreferredMediaPlayer);
                ShowToast("⏭", "Следующий трек", durationMs: 1600);
            }
            else
            {
                _ = MediaControlService.PreviousAsync(_settings.PreferredMediaPlayer);
                ShowToast("⏮", "Предыдущий трек", durationMs: 1600);
            }
            e.Handled = true;
            return;
        }

        int delta = e.Delta > 0 ? 3 : -3;
        int vol = CoreAudioService.ChangeMasterVolume(delta);
        SyncVolumeUi(vol);
        string icon = vol == 0 ? "🔇" : (vol < 40 ? "🔈" : (vol < 75 ? "🔉" : "🔊"));
        ShowToast(icon, $"Громкость: {vol}%", durationMs: 2200, progressRatio: vol / 100.0);
        e.Handled = true;
    }

    private async void Island_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle)
        {
            e.Handled = true;
            await MediaControlService.PlayPauseAsync(_settings.PreferredMediaPlayer);
            ShowToast(_isMediaPlaying ? "⏸" : "▶", _isMediaPlaying ? "Пауза" : "Воспроизведение", durationMs: 1500);
            await Task.Delay(220);
            await RefreshMediaAndDevicesAsync(false);
        }
    }

    private void Island_DragEnter(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop) ||
            e.Data.GetDataPresent(System.Windows.DataFormats.UnicodeText))
        {
            e.Effects = System.Windows.DragDropEffects.Copy;
            FlashIslandBorder(MediaColor.FromRgb(56, 189, 248));
        }
        else
        {
            e.Effects = System.Windows.DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void Island_DragLeave(object sender, System.Windows.DragEventArgs e)
    {
        ApplyCurrentIslandBorder();
    }

    private void Island_Drop(object sender, System.Windows.DragEventArgs e)
    {
        try
        {
            if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop) &&
                e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                int added = 0;
                foreach (var path in files.Take(8))
                {
                    if (AddFileToDropShelf(path)) added++;
                }
                ShowToast("📎", $"На полку: +{added} ({_dropShelfItems.Count})", "Открыть", () => OpenTab("Utils"), durationMs: 3000);
            }
            else if (e.Data.GetDataPresent(System.Windows.DataFormats.UnicodeText) &&
                     e.Data.GetData(System.Windows.DataFormats.UnicodeText) is string txt && !string.IsNullOrWhiteSpace(txt))
            {
                var trimmed = txt.Trim();
                _dropShelfItems.Insert(0, new DropShelfItem
                {
                    Name = trimmed.Length > 28 ? trimmed[..28] + "…" : trimmed,
                    FullPath = trimmed,
                    IsFile = false
                });
                while (_dropShelfItems.Count > 10) _dropShelfItems.RemoveAt(_dropShelfItems.Count - 1);
                SyncDropShelfUi();
                ShowToast("📝", "Текст закреплён на полке", "Открыть", () => OpenTab("Utils"), durationMs: 2500);
            }
        }
        catch { }
        ApplyCurrentIslandBorder();
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
        _dragStartOffset = CurrentHorizontalOffsetPx;
        _dragStartVerticalOffset = CurrentVerticalOffsetPx;
        var dpiStart = VisualTreeHelper.GetDpi(this);
        double scaleYStart = dpiStart.DpiScaleY <= 0 ? 1.0 : dpiStart.DpiScaleY;
        double startScreenTop = GetTargetScreen().Bounds.Top / scaleYStart;
        _dragStartFreeTopPx = EffectiveDockEdge == "Free"
            ? CurrentVerticalOffsetPx
            : Math.Max(4.0, Top - startScreenTop);
        _dragStartWindowLeft = Left;
        _isWindowDragging = true;
        _didHorizontalDrag = false;
        ResetMagnetImmediate();
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
        var dpi = VisualTreeHelper.GetDpi(this);
        double dpiX = dpi.DpiScaleX <= 0 ? 1.0 : dpi.DpiScaleX;
        double dpiY = dpi.DpiScaleY <= 0 ? 1.0 : dpi.DpiScaleY;
        double dx = (now.X - _dragStartCursor.X) / dpiX;
        double dy = (now.Y - _dragStartCursor.Y) / dpiY;

        if (!_didHorizontalDrag && (Math.Abs(dx) >= 5 || Math.Abs(dy) >= 5))
        {
            _didHorizontalDrag = true;
            IslandBorder.CaptureMouse();
        }

        if (_didHorizontalDrag)
        {
            _collapseDelayTimer.Stop();
            var screen = GetTargetScreen();
            double sLeft = screen.Bounds.Left / dpiX;
            double sTop = screen.Bounds.Top / dpiY;
            double sWidth = screen.Bounds.Width / dpiX;
            double sHeight = screen.Bounds.Height / dpiY;
            double cx = now.X / dpiX;
            double cy = now.Y / dpiY;
            string prevEdge = EffectiveDockEdge;
            double activeH = ActualHeight > 20 ? ActualHeight : (_isExpanded ? 420 : CompactHeight + 16);

            // Dock ONLY when intentionally dragged right up against a screen edge; anywhere else stays Free X/Y!
            if (cy <= sTop + 14 && cx > sLeft + 75 && cx < sLeft + sWidth - 75)
            {
                if (prevEdge != "Top")
                {
                    double targetOffset = prevEdge is "Left" or "Right"
                        ? Math.Clamp(cx - (sLeft + sWidth / 2.0), -(sWidth / 2.0 - 150), sWidth / 2.0 - 150)
                        : _dragStartOffset + dx;
                    MutateActiveDockProfile(p => { p.DockEdge = "Top"; p.HorizontalOffsetPx = targetOffset; });
                    _dragStartOffset = targetOffset - dx;
                }
                else
                {
                    MutateActiveDockProfile(p => p.HorizontalOffsetPx = _dragStartOffset + dx);
                }
            }
            else if (cx <= sLeft + 18)
            {
                if (prevEdge != "Left")
                {
                    double clampedY = Math.Clamp(cy - (sTop + sHeight / 2.0), -(sHeight / 2.0 - 90), sHeight / 2.0 - 90);
                    MutateActiveDockProfile(p => { p.DockEdge = "Left"; p.VerticalOffsetPx = clampedY; });
                    _dragStartVerticalOffset = clampedY - dy;
                }
                else
                {
                    MutateActiveDockProfile(p => p.VerticalOffsetPx = _dragStartVerticalOffset + dy);
                }
            }
            else if (cx >= sLeft + sWidth - 18)
            {
                if (prevEdge != "Right")
                {
                    double clampedY = Math.Clamp(cy - (sTop + sHeight / 2.0), -(sHeight / 2.0 - 90), sHeight / 2.0 - 90);
                    MutateActiveDockProfile(p => { p.DockEdge = "Right"; p.VerticalOffsetPx = clampedY; });
                    _dragStartVerticalOffset = clampedY - dy;
                }
                else
                {
                    MutateActiveDockProfile(p => p.VerticalOffsetPx = _dragStartVerticalOffset + dy);
                }
            }
            else
            {
                if (prevEdge is "Left" or "Right")
                {
                    double targetXOffset = cx - (sLeft + sWidth / 2.0);
                    double targetTopPx = Math.Clamp(cy - sTop - 18.0, 4.0, Math.Max(4.0, sHeight - activeH - 4.0));
                    _dragStartOffset = targetXOffset - dx;
                    _dragStartFreeTopPx = targetTopPx - dy;
                }

                double freeTop = Math.Clamp(_dragStartFreeTopPx + dy, 4.0, Math.Max(4.0, sHeight - activeH - 4.0));
                MutateActiveDockProfile(p =>
                {
                    p.DockEdge = "Free";
                    p.HorizontalOffsetPx = _dragStartOffset + dx;
                    p.VerticalOffsetPx = freeTop;
                });
            }

            if (EffectiveDockEdge != prevEdge)
            {
                ApplyGameVisualChrome();
                if (_isExpanded)
                    UpdateExpandedBounds(animate: false);
                else
                    UpdateCompactWidth(animate: false);
                UpdateRamPillState(animate: false);
                UpdateMicPillState(animate: false);
                UpdateReplayPillState(animate: false);
            }

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
        MutateActiveDockProfile(p =>
        {
            p.DockEdge = "Top";
            p.HorizontalOffsetPx = 0;
            p.VerticalOffsetPx = 0;
        });
        SettingsService.Save(_settings);
        ApplyGameVisualChrome();
        UpdateCenterButtonVisibility();
        UpdateCompactWidth(animate: false);
        UpdateRamPillState(animate: false);
        UpdateMicPillState(animate: false);
        PositionTopCenter();
        ShowToast("⌖", "Островок по центру", durationMs: 2000);
    }

    private static DependencyObject? SafeGetParent(DependencyObject? d) => d switch
    {
        null => null,
        Visual or System.Windows.Media.Media3D.Visual3D => VisualTreeHelper.GetParent(d),
        _ => LogicalTreeHelper.GetParent(d)
    };

    private bool IsInteractiveAncestor(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is System.Windows.Controls.Primitives.ButtonBase ||
                d is System.Windows.Controls.Slider ||
                d is System.Windows.Controls.TextBox ||
                (d is Border b && (b.Tag is PinnedApp || b == ScreenRecordBadge || b == SideRecordBadge)))
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

    private void ShowToast(
        string icon,
        string message,
        string? actionLabel = null,
        Action? onAction = null,
        int durationMs = 3000,
        double? progressRatio = null,
        MediaColor? swatchColor = null,
        string? title = null,
        bool showCountdown = false)
    {
        if (_isSuperMiniIdle)
            ExitSuperMiniIdle(updateWidth: false);

        CompactToastIcon.Text = icon;
        CompactToastText.Text = message;
        SideToastText.Text = $"{icon} {message}";
        _toastAction = onAction;
        _toastTotalMs = Math.Max(800, durationMs);
        _toastStartedUtc = DateTime.UtcNow;

        if (!string.IsNullOrWhiteSpace(title))
        {
            CompactToastTitle.Text = title;
            CompactToastTitle.Visibility = Visibility.Visible;
            CompactToastIconBadge.Background = new SolidColorBrush(MediaColor.FromRgb(20, 48, 34));
            CompactToastIconBadge.BorderBrush = new SolidColorBrush(MediaColor.FromRgb(42, 92, 62));
        }
        else
        {
            CompactToastTitle.Visibility = Visibility.Collapsed;
            CompactToastIconBadge.Background = new SolidColorBrush(MediaColor.FromRgb(30, 34, 48));
            CompactToastIconBadge.BorderBrush = new SolidColorBrush(MediaColor.FromRgb(50, 56, 78));
        }

        if (swatchColor.HasValue)
        {
            ToastColorSwatch.Fill = new SolidColorBrush(swatchColor.Value);
            ToastColorSwatch.Visibility = Visibility.Visible;
            CompactToastIcon.Visibility = Visibility.Collapsed;
        }
        else
        {
            ToastColorSwatch.Visibility = Visibility.Collapsed;
            CompactToastIcon.Visibility = Visibility.Visible;
        }

        if (!string.IsNullOrEmpty(actionLabel) && onAction != null)
        {
            CompactToastActionBtn.Content = actionLabel;
            CompactToastActionBtn.Visibility = Visibility.Visible;
            ExpandedToastActionBtn.Content = actionLabel;
            ExpandedToastActionBtn.Visibility = Visibility.Visible;
        }
        else
        {
            CompactToastActionBtn.Visibility = Visibility.Collapsed;
            ExpandedToastActionBtn.Visibility = Visibility.Collapsed;
        }

        ToastProgressFill.BeginAnimation(WidthProperty, null);
        if (progressRatio.HasValue)
        {
            ToastProgressHost.Visibility = Visibility.Visible;
            ToastProgressFill.Width = 54.0 * Math.Clamp(progressRatio.Value, 0.0, 1.0);
        }
        else if (showCountdown)
        {
            ToastProgressHost.Visibility = Visibility.Visible;
            ToastProgressFill.Width = 220.0;
            ToastProgressFill.BeginAnimation(WidthProperty, new DoubleAnimation(220.0, 0.0, TimeSpan.FromMilliseconds(_toastTotalMs)));
        }
        else
        {
            ToastProgressHost.Visibility = Visibility.Collapsed;
        }

        if (IsSideDocked && !_isExpanded)
            SideToastText.Visibility = Visibility.Visible;

        ExpandedToastText.Text = string.IsNullOrWhiteSpace(title) ? $"{icon} {message}" : $"{icon} {title}: {message}";
        ExpandedToastPill.Visibility = Visibility.Visible;

        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromMilliseconds(_toastTotalMs);
        _toastTimer.Start();

        SyncCompactStripVisibility();
        if (!_isExpanded)
        {
            UpdateCompactWidth(animate: true);
            if (_settings.EnableAnimations)
            {
                var scaleEase = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 };
                CompactToastScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                CompactToastScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                CompactToastScale.ScaleX = 0.94;
                CompactToastScale.ScaleY = 0.94;
                CompactToastScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(220)) { EasingFunction = scaleEase });
                CompactToastScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(220)) { EasingFunction = scaleEase });
            }
        }
    }

    private void ToastTimer_Tick(object? sender, EventArgs e)
    {
        _toastTimer.Stop();
        _isToastHovered = false;
        ToastProgressFill.BeginAnimation(WidthProperty, null);
        ToastProgressHost.Visibility = Visibility.Collapsed;
        ToastColorSwatch.Visibility = Visibility.Collapsed;
        CompactToastIcon.Visibility = Visibility.Visible;
        CompactToastTitle.Visibility = Visibility.Collapsed;
        SideToastText.Visibility = Visibility.Collapsed;
        ExpandedToastPill.Visibility = Visibility.Collapsed;
        ExpandedToastActionBtn.Visibility = Visibility.Collapsed;
        _toastAction = null;
        SyncCompactStripVisibility();
        if (!_isExpanded) UpdateCompactWidth(animate: true);
    }

    private void CompactToastStrip_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_toastTimer.IsEnabled) return;
        _isToastHovered = true;
        int elapsed = (int)(DateTime.UtcNow - _toastStartedUtc).TotalMilliseconds;
        _toastTotalMs = Math.Max(2400, _toastTotalMs - elapsed);
        _toastTimer.Stop();
    }

    private void CompactToastStrip_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isToastHovered || CompactToastStrip.Visibility != Visibility.Visible) return;
        _isToastHovered = false;
        _toastStartedUtc = DateTime.UtcNow;
        _toastTimer.Interval = TimeSpan.FromMilliseconds(_toastTotalMs);
        _toastTimer.Start();
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
                if (_timerShutdownOnFinish)
                {
                    ShowToast("⏻", "Ночной авто-стоп: выключение ПК...", durationMs: 2500);
                    QuickToolsService.ShutdownPc();
                }
                else
                {
                    ShowToast("🌙", "Ночной авто-стоп: переход в сон...", durationMs: 2500);
                    QuickToolsService.SleepPc();
                }
            }
            else
            {
                var fmt = left.TotalHours >= 1
                    ? left.ToString(@"h\:mm\:ss")
                    : left.ToString(@"mm\:ss");
                CompactTimerText.Text = $"🌙 {fmt}";
                CompactTimerPill.Visibility = Visibility.Visible;
                TimerStatusText.Text = fmt;
            }
        }

        if (_dndAutoOffEndUtc.HasValue)
        {
            var dndLeft = _dndAutoOffEndUtc.Value - DateTime.UtcNow;
            if (dndLeft <= TimeSpan.Zero)
            {
                _dndAutoOffEndUtc = null;
                QuickToolsService.SetDoNotDisturbEnabled(false);
                SyncQuickToolStates();
                ShowToast("🔔", "Таймер «Не беспокоить» завершён", durationMs: 2600);
            }
            else if (_isExpanded && TabSystem.IsChecked == true)
            {
                string dFmt = dndLeft.TotalHours >= 1 ? dndLeft.ToString(@"h\:mm\:ss") : dndLeft.ToString(@"mm\:ss");
                DndLabelText.Text = $"Вкл · {dFmt}";
            }
        }

        if (_screenRecorder.IsRecording)
        {
            var el = _screenRecorder.Elapsed;
            string rFmt = el.TotalHours >= 1 ? el.ToString(@"h\:mm\:ss") : el.ToString(@"mm\:ss");
            ScreenRecordTimerText.Text = $"● REC {rFmt}";
            SideRecordTimerText.Text = $"● {rFmt}";
            ScreenRecordBtn.Content = $"⏹ Стоп ({rFmt})";
            double dotOp = ((int)el.TotalSeconds % 2 == 0) ? 1.0 : 0.35;
            StatusDot.Opacity = dotOp;
            ExpandedStatusDot.Opacity = dotOp;
        }
        else if (StatusDot.Opacity < 0.99)
        {
            StatusDot.Opacity = 1.0;
            ExpandedStatusDot.Opacity = 1.0;
        }
    }

    private void InputStateTimer_Tick(object? sender, EventArgs e)
    {
        // Track keyboard + mouse activity via GetUserIdleMs so typing or pausing briefly never falsely triggers idle eyes
        uint idleMs = GetUserIdleMs();
        if (GetCursorPos(out var cur))
        {
            if (Math.Abs(cur.X - _lastCheckedCursor.X) > 3 || Math.Abs(cur.Y - _lastCheckedCursor.Y) > 3)
            {
                _lastCheckedCursor = cur;
                _lastUserInteractionUtc = DateTime.UtcNow;
            }
        }

        if (idleMs < 1500)
        {
            _lastUserInteractionUtc = DateTime.UtcNow;
            if (_isSuperMiniIdle)
                ExitSuperMiniIdle();
        }
        else if (_settings.SuperMiniOnIdle && !_isSuperMiniIdle && !_isExpanded && !_screenRecorder.IsRecording &&
                 !_isInFullscreenGame && !_isMediaPlaying && !_toastTimer.IsEnabled &&
                 !IsSideDocked && !OuterDockStack.IsMouseOver && !IsCursorOverIslandArea() &&
                 idleMs >= 45_000 && (DateTime.UtcNow - _lastUserInteractionUtc).TotalMilliseconds >= 45_000)
        {
            EnterSuperMiniIdle();
        }

        if (!_settings.ShowLayoutAndCapsIndicator) return;

        string layout = HardwareAndUtilityService.GetForegroundKeyboardLayoutCode();
        if (!string.IsNullOrEmpty(layout))
        {
            if (!string.IsNullOrEmpty(_lastKnownLayout) && !string.Equals(_lastKnownLayout, layout, StringComparison.Ordinal))
                ShowToast("⌨", $"Раскладка: {layout}", durationMs: 1350);
            _lastKnownLayout = layout;
        }

        bool caps = HardwareAndUtilityService.IsCapsLockOn();
        if (_lastKnownCaps.HasValue && _lastKnownCaps.Value != caps)
            ShowToast("⇪", caps ? "CAPS LOCK: ВКЛ" : "Caps Lock: выкл", durationMs: 1350);
        _lastKnownCaps = caps;
    }

    private void EnterSuperMiniIdle()
    {
        if (_isSuperMiniIdle) return;
        _isSuperMiniIdle = true;

        _eyeCurX = 0;
        _eyeCurY = 0;
        _eyeTargetX = 0;
        _eyeTargetY = 0;
        _eyeBlinkFrame = 0;
        _eyeDoubleBlinkPending = false;
        _eyeNextGazeTicks = 35;
        _eyeBlinkCounter = 55;
        ApplyIdleEyesVisuals(scaleX: 1.0, scaleY: 1.0);
        UpdateCompactWidth(animate: true);

        if (_settings.EnableAnimations && CompactIdleEyesStrip.Visibility == Visibility.Visible)
            _idleEyesTimer.Start();
    }

    private void ExitSuperMiniIdle(bool updateWidth = true)
    {
        if (!_isSuperMiniIdle) return;
        _isSuperMiniIdle = false;
        _idleEyesTimer.Stop();
        _eyeBlinkFrame = 0;
        _eyeCurX = 0;
        _eyeCurY = 0;
        ApplyIdleEyesVisuals(scaleX: 1.0, scaleY: 1.0);

        if (updateWidth)
            UpdateCompactWidth(animate: true);
        else
            SyncCompactStripVisibility();
    }

    private void ApplyIdleEyesVisuals(double scaleX, double scaleY)
    {
        // Subtle inward focus convergence when looking directly at the user near center
        double focusOffset = Math.Max(0, 1.0 - (Math.Abs(_eyeCurX) + Math.Abs(_eyeCurY)) / 2.0) * 0.25;
        IdleLeftPupilTransform.X = _eyeCurX + focusOffset;
        IdleLeftPupilTransform.Y = _eyeCurY;
        IdleRightPupilTransform.X = _eyeCurX - focusOffset;
        IdleRightPupilTransform.Y = _eyeCurY;

        IdleLeftEyeBlink.ScaleX = scaleX;
        IdleLeftEyeBlink.ScaleY = scaleY;
        IdleRightEyeBlink.ScaleX = scaleX;
        IdleRightEyeBlink.ScaleY = scaleY;
    }

    private void IdleEyesTimer_Tick(object? sender, EventArgs e)
    {
        if (!_isSuperMiniIdle || CompactIdleEyesStrip.Visibility != Visibility.Visible)
        {
            _idleEyesTimer.Stop();
            return;
        }

        // Choose a new gaze direction periodically: straight at you (~52%) or looking away curiously (~48%)
        if (--_eyeNextGazeTicks <= 0)
        {
            int roll = _eyeRng.Next(100);
            if (roll < 52)
            {
                _eyeTargetX = (_eyeRng.NextDouble() - 0.5) * 0.5;
                _eyeTargetY = (_eyeRng.NextDouble() - 0.5) * 0.4;
                _eyeNextGazeTicks = _eyeRng.Next(48, 96);
            }
            else if (roll < 66)
            {
                _eyeTargetX = -3.0;
                _eyeTargetY = -0.2;
                _eyeNextGazeTicks = _eyeRng.Next(28, 55);
            }
            else if (roll < 80)
            {
                _eyeTargetX = 3.0;
                _eyeTargetY = -0.2;
                _eyeNextGazeTicks = _eyeRng.Next(28, 55);
            }
            else if (roll < 90)
            {
                _eyeTargetX = _eyeRng.Next(2) == 0 ? -2.2 : 2.2;
                _eyeTargetY = -2.2;
                _eyeNextGazeTicks = _eyeRng.Next(24, 46);
            }
            else
            {
                _eyeTargetX = (_eyeRng.NextDouble() - 0.5) * 1.4;
                _eyeTargetY = 2.4;
                _eyeNextGazeTicks = _eyeRng.Next(28, 52);
            }
        }

        _eyeCurX += (_eyeTargetX - _eyeCurX) * 0.16;
        _eyeCurY += (_eyeTargetY - _eyeCurY) * 0.16;

        // Smooth squash-and-stretch blink cycle
        double scaleX = 1.0;
        double scaleY = 1.0;
        if (_eyeBlinkFrame > 0)
        {
            switch (_eyeBlinkFrame)
            {
                case 1:
                    scaleX = 1.05;
                    scaleY = 0.52;
                    _eyeBlinkFrame = 2;
                    break;
                case 2:
                    scaleX = 1.12;
                    scaleY = 0.08;
                    _eyeBlinkFrame = 3;
                    break;
                case 3:
                    scaleX = 1.04;
                    scaleY = 0.55;
                    _eyeBlinkFrame = 4;
                    break;
                default:
                    scaleX = 1.0;
                    scaleY = 1.0;
                    _eyeBlinkFrame = 0;
                    if (_eyeDoubleBlinkPending)
                    {
                        _eyeDoubleBlinkPending = false;
                        _eyeBlinkCounter = 3;
                    }
                    else
                    {
                        _eyeBlinkCounter = _eyeRng.Next(58, 140);
                    }
                    break;
            }
        }
        else if (--_eyeBlinkCounter <= 0)
        {
            _eyeBlinkFrame = 1;
            _eyeDoubleBlinkPending = _eyeRng.Next(100) < 22;
        }

        ApplyIdleEyesVisuals(scaleX, scaleY);
    }

    private void UpdateClockDisplay()
    {
        var now = DateTime.Now;
        string hhmm = now.ToString("HH:mm");
        CompactTimeText.Text = hhmm;
        SideTimeText.Text = hhmm;
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
            var fgHwnd = GetForegroundWindow();
            bool skipContextUpdate = fgHwnd == _ownHwnd || _isWindowDragging || _isExpanded;
            bool gameChanged = false;
            bool videoChanged = false;
            if (!skipContextUpdate)
            {
                var fgContext = PrivacyAndGameService.DetectFullscreenContext(_isMediaPlaying, Environment.ProcessId);
                gameChanged = _isInFullscreenGame != fgContext.IsFullscreenGame;
                videoChanged = _isInFullscreenVideo != fgContext.IsFullscreenVideo;
                _isInFullscreenGame = fgContext.IsFullscreenGame;
                _isInFullscreenVideo = fgContext.IsFullscreenVideo;
            }
            bool includeGpu = _settings.GameShowGpuTemp &&
                              (_isInFullscreenGame || (_isExpanded && TabSystem.IsChecked == true));
            var metrics = await Task.Run(() => _sysMonitor.SampleSystemAndNetwork(includeGpu));

            // Switch per-process island dock position when entering/leaving games or processes with saved layouts
            bool profileChanged = false;
            if (!skipContextUpdate)
            {
                string activeProc = metrics.ActiveWindowTitle?.Trim() ?? string.Empty;
                bool isSystemShell = string.IsNullOrEmpty(activeProc)
                    || activeProc.Equals("DesktopIsland", StringComparison.OrdinalIgnoreCase)
                    || activeProc.Equals("explorer", StringComparison.OrdinalIgnoreCase)
                    || activeProc.Equals("Idle", StringComparison.OrdinalIgnoreCase);

                string? nextKey = null;
                if (!isSystemShell && (_isInFullscreenGame || _settings.ProcessDockProfiles.ContainsKey(activeProc)))
                    nextKey = activeProc;

                if (!string.Equals(_activeDockProcessKey, nextKey, StringComparison.OrdinalIgnoreCase))
                {
                    _activeDockProcessKey = nextKey;
                    profileChanged = true;
                }
            }

            if (gameChanged || videoChanged || profileChanged)
            {
                if ((_isInFullscreenGame || IsSideDocked) && _isSuperMiniIdle)
                    ExitSuperMiniIdle(updateWidth: false);
                UpdateWindowOpacity();
                ApplyGameVisualChrome();
                UpdateCenterButtonVisibility();
                UpdateRamPillState(animate: false);
                UpdateMicPillState(animate: false);
                UpdateReplayPillState(animate: false);
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

                    if (!string.IsNullOrEmpty(_activeDockProcessKey) &&
                        _settings.GameAudioProfiles.TryGetValue(_activeDockProcessKey, out var gameDevId) &&
                        !string.IsNullOrEmpty(gameDevId))
                    {
                        string curDef = CoreAudioService.GetDefaultPlaybackDeviceId();
                        if (!string.Equals(curDef, gameDevId, StringComparison.OrdinalIgnoreCase))
                        {
                            _desktopAudioDeviceIdBeforeGame = curDef;
                            if (CoreAudioService.SetDefaultPlaybackDevice(gameDevId))
                                ShowToast("🎧", $"Аудио для игры ({_activeDockProcessKey})", durationMs: 2200);
                        }
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

                    if (!string.IsNullOrEmpty(_desktopAudioDeviceIdBeforeGame))
                    {
                        var restoreId = _desktopAudioDeviceIdBeforeGame;
                        _desktopAudioDeviceIdBeforeGame = null;
                        if (CoreAudioService.SetDefaultPlaybackDevice(restoreId))
                            ShowToast("🔊", "Возвращён звук рабочего стола", durationMs: 2000);
                    }
                }
            }

            if (_isInFullscreenGame && _settings.GameModeAutoProfile && _gameSessionStartUtc.HasValue && !_isSuperMiniIdle)
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
            SyncMicMuteUi();

            bool autoRec = _settings.StreamPrivacyAuto && await Task.Run(HardwareAndUtilityService.IsRecordingSoftwareActive);
            _isStreamPrivacyActive = _isStreamPrivacyManual || autoRec;
            StreamRecBadge.Visibility = (_isStreamPrivacyActive && !_isSuperMiniIdle) ? Visibility.Visible : Visibility.Collapsed;

            // Sample connected XInput gamepads for wireless connect & low battery toast
            var pads = await Task.Run(HardwareAndUtilityService.GetConnectedGamepads);
            var padIds = pads.Select(p => p.Index).ToHashSet();
            foreach (var p in pads)
            {
                if (_knownGamepads.Add(p.Index))
                    ShowToast("🎮", p.IsWired ? $"Геймпад #{p.Index} подключен" : $"Геймпад #{p.Index} · 🔋 {p.BatteryPercent}%", durationMs: 2800);
                if (!p.IsWired && p.BatteryPercent <= 20 && _lowBatteryGamepads.Add(p.Index))
                    ShowToast("🪫", $"Геймпад #{p.Index}: низкий заряд ({p.BatteryPercent}%)", durationMs: 3600);
                else if (p.BatteryPercent > 30)
                    _lowBatteryGamepads.Remove(p.Index);
            }
            foreach (var old in _knownGamepads.Where(k => !padIds.Contains(k)).ToList())
                _knownGamepads.Remove(old);

            if (_settings.EnableSystemMonitor)
            {
                bool inGame = _isInFullscreenGame;

                // Desktop compact pill stays ultra-clean (Dot · Time · Colored Weather);
                // Fullscreen game pill switches to [Circle Game Icon] [FPS] · [CPU] · [GPU] · [Temp°] + bottom load bar
                CompactTimeText.Visibility = inGame ? Visibility.Collapsed : Visibility.Visible;
                CompactWeatherText.Visibility = (!inGame && !_isSuperMiniIdle && _settings.CompactShowWeather && _currentWeather != null)
                    ? Visibility.Visible : Visibility.Collapsed;
                CompactWeatherGroup.Visibility = (!inGame && !_isSuperMiniIdle && _settings.CompactShowWeather && _currentWeather != null)
                    ? Visibility.Visible : Visibility.Collapsed;

                if (inGame && !_isSuperMiniIdle)
                {
                    UpdateCompactGameIconBadge(metrics.ActivePid);
                    CompactGameIconBadge.Visibility = Visibility.Visible;
                }
                else
                {
                    CompactGameIconBadge.Visibility = Visibility.Collapsed;
                }

                // FPS: > 60 green (#4ADE80), <= 60 red (#F87171)
                if (inGame)
                {
                    if (_sysMonitor.IsEtwFpsActive || metrics.ActiveFps >= 1)
                    {
                        int fpsInt = (int)Math.Round(metrics.ActiveFps);
                        CompactFpsText.Text = $"{fpsInt} FPS";
                        SideFpsText.Text = $"{fpsInt} FPS";
                        var fpsColor = fpsInt > 60
                            ? new SolidColorBrush(MediaColor.FromRgb(74, 222, 128))   // Green (>60)
                            : new SolidColorBrush(MediaColor.FromRgb(248, 113, 113)); // Red (<=60)
                        CompactFpsText.Foreground = fpsColor;
                        SideFpsText.Foreground = fpsColor;
                        FpsDetailText.Foreground = fpsColor;
                        CompactFpsText.ToolTip = "Игровой режим (ховер отключён, клик открывает). ETW FPS игры";
                        FpsDetailText.Text = string.IsNullOrEmpty(metrics.ActiveWindowTitle)
                            ? $"{fpsInt} FPS"
                            : $"{fpsInt} FPS · {metrics.ActiveWindowTitle}";
                    }
                    else
                    {
                        CompactFpsText.Text = "— FPS";
                        SideFpsText.Text = "— FPS";
                        var warnCol = new SolidColorBrush(MediaColor.FromRgb(248, 113, 113));
                        CompactFpsText.Foreground = warnCol;
                        SideFpsText.Foreground = warnCol;
                        FpsDetailText.Foreground = warnCol;
                        CompactFpsText.ToolTip = "Для показа точного FPS игр запустите Enable_Game_FPS_Access.bat с правами Админа";
                        FpsDetailText.Text = string.IsNullOrEmpty(metrics.ActiveWindowTitle)
                            ? "Нет прав ETW"
                            : $"Нет прав ETW · {metrics.ActiveWindowTitle}";
                    }
                }
                else
                {
                    FpsDetailText.Foreground = new SolidColorBrush(MediaColor.FromRgb(142, 144, 153));
                    FpsDetailText.Text = string.IsNullOrEmpty(metrics.ActiveWindowTitle)
                        ? $"{metrics.ActiveFps:F0} FPS"
                        : $"{metrics.ActiveFps:F0} FPS · {metrics.ActiveWindowTitle}";
                }

                CompactFpsText.Visibility = (inGame && _settings.CompactShowFps && !_isSuperMiniIdle)
                    ? Visibility.Visible : Visibility.Collapsed;
                SideFpsText.Visibility = (_settings.CompactShowFps && inGame) ? Visibility.Visible : Visibility.Collapsed;

                bool highCpu = metrics.CpuPercent >= 75;
                bool highRam = metrics.RamPercent >= 80;
                bool highGpu = metrics.GpuPercent.HasValue && metrics.GpuPercent.Value >= 80;
                bool highTemp = metrics.GpuTempC.HasValue && metrics.GpuTempC.Value >= 85;
                var highBlue = new SolidColorBrush(MediaColor.FromRgb(56, 189, 248));
                var hotCol = new SolidColorBrush(MediaColor.FromRgb(248, 113, 113));
                var normalCompactCol = new SolidColorBrush(MediaColor.FromRgb(203, 213, 225));
                var normalCardCol = new SolidColorBrush(MediaColor.FromRgb(244, 244, 245));

                if (inGame && _settings.CompactShowCpuRam)
                {
                    CpuValRun.Text = $"{metrics.CpuPercent:F0}%";
                    CpuValRun.Foreground = highCpu ? highBlue : normalCardCol;

                    if (metrics.GpuPercent.HasValue)
                    {
                        GpuSepRun.Text = " · ";
                        GpuLabelRun.Text = "GPU ";
                        GpuValRun.Text = $"{metrics.GpuPercent.Value:F0}%";
                        GpuValRun.Foreground = highGpu ? highBlue : new SolidColorBrush(MediaColor.FromRgb(125, 211, 252));

                        if (metrics.GpuTempC.HasValue)
                        {
                            double tc = metrics.GpuTempC.Value;
                            GpuTempRun.Text = $" {tc:F0}°";
                            var tColor = tc >= 85
                                ? MediaColor.FromRgb(248, 113, 113)  // Red >= 85
                                : tc >= 70
                                    ? MediaColor.FromRgb(251, 191, 36) // Amber 70-84
                                    : MediaColor.FromRgb(74, 222, 128); // Green < 70
                            GpuTempRun.Foreground = new SolidColorBrush(tColor);
                        }
                        else GpuTempRun.Text = "";
                    }
                    else
                    {
                        GpuSepRun.Text = "";
                        GpuLabelRun.Text = "";
                        GpuValRun.Text = "";
                        GpuTempRun.Text = "";
                    }

                    RamSepRun.Text = "";
                    RamLabelRun.Text = "";
                    RamValRun.Text = "";
                    CompactSysText.Visibility = _isSuperMiniIdle ? Visibility.Collapsed : Visibility.Visible;
                }
                else
                {
                    CompactSysText.Visibility = Visibility.Collapsed;
                }

                bool heavyLoad = highCpu || highRam || highGpu;
                var sysCol = highTemp ? hotCol : (heavyLoad ? highBlue : normalCompactCol);
                SideSysText.Text = $"CPU {metrics.CpuPercent:F0}%\nRAM {metrics.RamPercent:F0}%";
                SideSysText.Foreground = sysCol;
                SideSysText.Visibility = _settings.CompactShowCpuRam ? Visibility.Visible : Visibility.Collapsed;

                // Animated load bar & perimeter pulse glow in game mode
                double peakLoad = Math.Max(metrics.CpuPercent, metrics.GpuPercent ?? 0.0);
                if (inGame && !_isExpanded && !_isSuperMiniIdle && CompactMetricsStrip.Visibility == Visibility.Visible)
                {
                    CompactLoadBarHost.Visibility = Visibility.Visible;
                    double barMaxW = Math.Max(40.0, IslandBorder.Width - 36.0);
                    CompactLoadBarFill.Width = Math.Clamp(peakLoad / 100.0, 0.05, 1.0) * barMaxW;
                    MediaColor barCol = (highTemp || peakLoad >= 85)
                        ? MediaColor.FromRgb(248, 113, 113)
                        : peakLoad >= 65
                            ? MediaColor.FromRgb(251, 191, 36)
                            : MediaColor.FromRgb(74, 222, 128);
                    CompactLoadBarFill.Background = new SolidColorBrush(barCol);
                }
                else
                {
                    CompactLoadBarHost.Visibility = Visibility.Collapsed;
                }

                // Breathing border glow on heavy load or overheating
                if (!_isBorderAlertActive && !_screenRecorder.IsRecording && !(_isInFullscreenGame && _settings.GameModeAutoProfile && _isAltHeldInGame))
                {
                    if (highTemp || peakLoad >= 75)
                    {
                        _pulsePhase += 0.9;
                        byte a = (byte)Math.Round(115 + 100 * Math.Sin(_pulsePhase));
                        IslandBorder.BorderBrush = highTemp
                            ? new SolidColorBrush(MediaColor.FromArgb(a, 248, 113, 113))
                            : new SolidColorBrush(MediaColor.FromArgb(a, 56, 189, 248));
                    }
                    else
                    {
                        ApplyCurrentIslandBorder();
                    }
                }

                if (inGame && (_settings.CompactShowPing || _isInFullscreenGame) && metrics.PingMs >= 25)
                {
                    CompactPingText.Text = $"{metrics.PingMs}ms";
                    CompactPingText.ToolTip = _sysMonitor.PingTargetLabel;
                    SidePingText.Text = $"{metrics.PingMs}ms";
                    SidePingText.ToolTip = _sysMonitor.PingTargetLabel;
                    var pingColor = metrics.PingMs < 60
                        ? MediaColor.FromRgb(74, 222, 128)
                        : metrics.PingMs < 110
                            ? MediaColor.FromRgb(250, 204, 21)
                            : MediaColor.FromRgb(248, 113, 113);
                    var pingBrush = new SolidColorBrush(pingColor);
                    CompactPingText.Foreground = pingBrush;
                    SidePingText.Foreground = pingBrush;
                    CompactPingText.Visibility = _isSuperMiniIdle ? Visibility.Collapsed : Visibility.Visible;
                    SidePingText.Visibility = Visibility.Visible;
                }
                else
                {
                    CompactPingText.Visibility = Visibility.Collapsed;
                    SidePingText.Visibility = Visibility.Collapsed;
                }

                bool anyMetric = !_isSuperMiniIdle &&
                                 (CompactFpsText.Visibility == Visibility.Visible ||
                                  CompactSysText.Visibility == Visibility.Visible ||
                                  CompactPingText.Visibility == Visibility.Visible);
                CompactSep1.Visibility = anyMetric ? Visibility.Visible : Visibility.Collapsed;

                // Sparkline history buffers (30s rolling window)
                PushSparklineSample(_cpuHistory, metrics.CpuPercent);
                PushSparklineSample(_gpuHistory, metrics.GpuPercent ?? 0.0);
                PushSparklineSample(_ramHistory, metrics.RamPercent);
                UpdateSparklinesUi();

                // Overheating alert (>= 86°C, 3-minute cooldown)
                if (metrics.GpuTempC.HasValue && metrics.GpuTempC.Value >= 86 &&
                    (DateTime.UtcNow - _lastThermalAlertAt).TotalMinutes >= 3)
                {
                    _lastThermalAlertAt = DateTime.UtcNow;
                    FlashIslandBorder(MediaColor.FromRgb(239, 68, 68));
                    ShowToast("🔥", $"Перегрев GPU: {metrics.GpuTempC.Value:F0}°C!", durationMs: 4200);
                }

                CpuValText.Text = $"{metrics.CpuPercent:F0}%";
                CpuValText.Foreground = highCpu ? highBlue : normalCardCol;
                CpuLoadBarFill.Width = Math.Clamp(metrics.CpuPercent / 100.0, 0.03, 1.0) * 124.0;

                RamValText.Text = $"{metrics.RamPercent:F0}%";
                RamValText.Foreground = highRam ? highBlue : normalCardCol;
                RamSubText.Text = $"{metrics.RamUsedGb:F1} / {metrics.RamTotalGb:F1} ГБ";
                RamLoadBarFill.Width = Math.Clamp(metrics.RamPercent / 100.0, 0.03, 1.0) * 124.0;

                SysGpuNameText.Text = HardwareAndUtilityService.GetPrimaryGpuName();
                if (metrics.GpuPercent.HasValue)
                {
                    GpuCardValText.Text = $"{metrics.GpuPercent.Value:F0}%";
                    GpuCardValText.Foreground = highTemp ? hotCol : normalCardCol;
                    GpuDetailText.Text = metrics.GpuTempC.HasValue ? $"Темп. {metrics.GpuTempC.Value:F0}°C" : "Активна";
                    GpuDetailText.Foreground = highTemp ? hotCol : new SolidColorBrush(MediaColor.FromRgb(142, 144, 153));
                    GpuLoadBarFill.Width = Math.Clamp(metrics.GpuPercent.Value / 100.0, 0.03, 1.0) * 124.0;
                    GpuLoadBarFill.Background = new SolidColorBrush(highTemp
                        ? MediaColor.FromRgb(248, 113, 113)
                        : MediaColor.FromRgb(192, 132, 252));
                }
                else
                {
                    GpuCardValText.Text = "—%";
                    GpuDetailText.Text = "Ожидание";
                    GpuLoadBarFill.Width = 0;
                }

                if (_isExpanded && TabSystem.IsChecked == true)
                    RefreshSystemControlCenterUi(pads);

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
                PingHeaderLabel.Text = metrics.PingMs >= 25
                    ? (_sysMonitor.IsGameServerPing ? $"Сеть · Игр. ping {metrics.PingMs}мс" : $"Сеть · Ping {metrics.PingMs}мс")
                    : "Сеть";

                PushSparklineSample(_netHistory, Math.Min(100, metrics.NetDownBytesPerSec / (1024.0 * 1024.0) * 5.0));

                bool netUnstable = metrics.PacketLossPercent >= 20 || metrics.PingJitterMs >= 38;
                CompactNetJitterBadge.Visibility = (netUnstable && !_isSuperMiniIdle) ? Visibility.Visible : Visibility.Collapsed;
                CompactNetJitterBadge.ToolTip = $"Нестабильная сеть: потери {metrics.PacketLossPercent}% · скачки ~{metrics.PingJitterMs}мс";
                if (netUnstable && _isInFullscreenGame && (DateTime.UtcNow - _lastNetWarnAt).TotalSeconds >= 25)
                {
                    _lastNetWarnAt = DateTime.UtcNow;
                    FlashIslandBorder(MediaColor.FromRgb(250, 204, 21));
                }

                // Speed surge indicator when downloading fast (> 2.5 MB/s) even if compact network sensor is off
                bool isSurging = !_settings.CompactShowNetwork && metrics.NetDownBytesPerSec >= 2_500_000 && !_isSuperMiniIdle;
                CompactDownloadSurgeText.Text = isSurging ? $"↓{ShortSpeed(metrics.NetDownBytesPerSec)}" : "";
                CompactDownloadSurgeText.Visibility = isSurging ? Visibility.Visible : Visibility.Collapsed;

                if (_settings.CompactShowNetwork && !_isSuperMiniIdle)
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
                if (acc.FpsValues.Count < 1800)
                    acc.FpsValues.Add(metrics.ActiveFps);
                _lastTrackedGameFps = metrics.ActiveFps;
            }
            _lastTrackedGameName = procName;
            if (metrics.GpuPercent.HasValue) _lastTrackedGameGpu = metrics.GpuPercent;
            if (metrics.GpuTempC.HasValue) _lastTrackedGameTemp = metrics.GpuTempC;
            if (metrics.PingMs >= 25) _lastTrackedGamePing = metrics.PingMs;
        }
    }

    private static double? ComputeLow1PercentFps(List<double> values)
    {
        if (values.Count < 5) return null;
        var sorted = values.OrderBy(v => v).ToList();
        int takeCount = Math.Max(1, sorted.Count / 100);
        return sorted.Take(takeCount).Average();
    }

    private static string FormatDurationShort(TimeSpan dur) => dur.TotalHours >= 1
        ? $"{(int)dur.TotalHours}ч {dur.Minutes:D2}м"
        : (dur.TotalMinutes >= 1 ? $"{(int)dur.TotalMinutes}м" : $"{(int)dur.TotalSeconds}с");

    private static Border CreateStatBadge(string text, MediaColor bg, MediaColor border, MediaColor fg)
    {
        return new Border
        {
            Background = new SolidColorBrush(bg),
            BorderBrush = new SolidColorBrush(border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(7, 2.5, 7, 2.5),
            Margin = new Thickness(0, 1, 5, 1),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(fg)
            }
        };
    }

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

        GameStatsBadgesPanel.Children.Clear();
        if (!string.IsNullOrEmpty(topGame.Key))
        {
            string gFmt = FormatDurationShort(TimeSpan.FromSeconds(topGame.Value.Seconds));
            ScreenTimeLastGameText.Text = topGame.Key;

            GameStatsBadgesPanel.Children.Add(CreateStatBadge(
                $"🎮 {topGame.Key} · {gFmt}",
                MediaColor.FromRgb(26, 36, 56),
                MediaColor.FromRgb(49, 71, 112),
                MediaColor.FromRgb(248, 250, 252)));

            if (topGame.Value.FpsSamples > 0)
            {
                double avgFps = topGame.Value.FpsSum / topGame.Value.FpsSamples;
                double? low1 = ComputeLow1PercentFps(topGame.Value.FpsValues);
                string lowPart = low1.HasValue ? $" · 1% {low1.Value:F0}" : "";
                bool goodFps = avgFps >= 60;
                GameStatsBadgesPanel.Children.Add(CreateStatBadge(
                    $"ср. {avgFps:F0} FPS{lowPart}",
                    goodFps ? MediaColor.FromRgb(18, 45, 32) : MediaColor.FromRgb(58, 20, 24),
                    goodFps ? MediaColor.FromRgb(34, 197, 94) : MediaColor.FromRgb(239, 68, 68),
                    goodFps ? MediaColor.FromRgb(74, 222, 128) : MediaColor.FromRgb(252, 165, 165)));
            }

            if (_lastTrackedGameGpu.HasValue)
            {
                double tc = _lastTrackedGameTemp ?? 0;
                string tStr = _lastTrackedGameTemp.HasValue ? $" · {tc:F0}°C" : "";
                bool hot = tc >= 80;
                GameStatsBadgesPanel.Children.Add(CreateStatBadge(
                    $"GPU {_lastTrackedGameGpu.Value:F0}%{tStr}",
                    hot ? MediaColor.FromRgb(58, 34, 18) : MediaColor.FromRgb(20, 42, 58),
                    hot ? MediaColor.FromRgb(245, 158, 11) : MediaColor.FromRgb(56, 189, 248),
                    hot ? MediaColor.FromRgb(253, 186, 116) : MediaColor.FromRgb(125, 211, 252)));
            }

            if (_lastTrackedGamePing >= 25)
            {
                bool highPing = _lastTrackedGamePing >= 100;
                GameStatsBadgesPanel.Children.Add(CreateStatBadge(
                    $"{_lastTrackedGamePing} ms",
                    highPing ? MediaColor.FromRgb(58, 42, 18) : MediaColor.FromRgb(22, 42, 34),
                    highPing ? MediaColor.FromRgb(250, 204, 21) : MediaColor.FromRgb(52, 211, 153),
                    highPing ? MediaColor.FromRgb(253, 224, 71) : MediaColor.FromRgb(110, 231, 183)));
            }

            GameStatsDetailText.Visibility = Visibility.Collapsed;
            GameStatsBadgesPanel.Visibility = Visibility.Visible;
        }
        else
        {
            ScreenTimeLastGameText.Text = "Игр не было";
            GameStatsDetailText.Text = "Полноэкранная игра в этой сессии пока не запускалась";
            GameStatsDetailText.Foreground = new SolidColorBrush(MediaColor.FromRgb(148, 163, 184));
            GameStatsDetailText.Visibility = Visibility.Visible;
            GameStatsBadgesPanel.Visibility = Visibility.Collapsed;
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
            double? low1 = kv.Value.IsGame ? ComputeLow1PercentFps(kv.Value.FpsValues) : null;
            _screenTimeItems.Add(new ScreenTimeItem
            {
                ProcessName = kv.Key,
                IsGame = kv.Value.IsGame,
                Duration = TimeSpan.FromSeconds(kv.Value.Seconds),
                SharePercent = Math.Clamp((kv.Value.Seconds / maxSec) * 100.0, 4, 100),
                AvgFps = avg,
                LowFps1Percent = low1
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
            SideStatusDot.Fill = col;
            ExpandedStatusDot.Fill = col;
            StatusDot.ToolTip = $"Камера активна: {privacy.CameraApp}";
            SideStatusDot.ToolTip = StatusDot.ToolTip;
            PrivacyStatusText.Text = $"📷 Камера: {privacy.CameraApp}";
            PrivacyStatusText.Foreground = col;
            SysPrivacySubText.Text = privacy.IsMicActive ? $"Также микрофон: {privacy.MicApp}" : "Камера используется прямо сейчас";
        }
        else if (privacy.IsMicActive)
        {
            var col = new SolidColorBrush(MediaColor.FromRgb(245, 158, 11));
            StatusDot.Fill = col;
            SideStatusDot.Fill = col;
            ExpandedStatusDot.Fill = col;
            StatusDot.ToolTip = $"Микрофон активен: {privacy.MicApp}";
            SideStatusDot.ToolTip = StatusDot.ToolTip;
            PrivacyStatusText.Text = $"🎙 Микрофон: {privacy.MicApp}";
            PrivacyStatusText.Foreground = col;
            SysPrivacySubText.Text = "Микрофон прослушивается приложением";
        }
        else
        {
            StatusDot.SetResourceReference(Shape.FillProperty, "AccentBrightBrush");
            SideStatusDot.SetResourceReference(Shape.FillProperty, "AccentBrightBrush");
            ExpandedStatusDot.SetResourceReference(Shape.FillProperty, "AccentBrightBrush");
            StatusDot.ToolTip = "Desktop Island активен";
            SideStatusDot.ToolTip = StatusDot.ToolTip;
            PrivacyStatusText.Text = "Камера и мик свободны";
            PrivacyStatusText.Foreground = new SolidColorBrush(MediaColor.FromRgb(212, 212, 216));
            SysPrivacySubText.Text = "Активных обращений нет";
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
        if (!_settings.CompactShowWeather)
        {
            CompactWeatherText.Visibility = Visibility.Collapsed;
            CompactWeatherGroup.Visibility = Visibility.Collapsed;
            return;
        }
        var w = await QuickToolsService.FetchWeatherAsync(_settings.WeatherCity);
        if (w != null)
        {
            _currentWeather = w;
            CompactWeatherText.Text = w.ShortText;
            CompactWeatherText.ToolTip = string.IsNullOrEmpty(w.RainAlertText)
                ? $"{w.City}: {w.TempC:F1}°C · {w.ConditionText}"
                : $"{w.City}: {w.TempC:F1}°C · {w.RainAlertText}";
            bool showWeather = !_isSuperMiniIdle && !_isInFullscreenGame;
            CompactWeatherText.Visibility = showWeather ? Visibility.Visible : Visibility.Collapsed;
            CompactWeatherGroup.Visibility = showWeather ? Visibility.Visible : Visibility.Collapsed;
            SideWeatherText.Text = w.ShortText;
            SideWeatherText.Visibility = Visibility.Visible;

            HomeWeatherTempText.Text = $"{Math.Round(w.TempC)}°";
            string cond = !string.IsNullOrWhiteSpace(w.RainAlertText) ? w.RainAlertText : w.ConditionText;
            HomeWeatherSubText.Text = string.IsNullOrWhiteSpace(w.City) ? cond : $"{w.ConditionText} · {w.City}";
            ApplyWeatherVectorState(w.WmoCode);

            if (!string.IsNullOrWhiteSpace(w.RainAlertText) &&
                (DateTime.UtcNow - _lastRainAlertAt).TotalMinutes >= 60)
            {
                _lastRainAlertAt = DateTime.UtcNow;
                ShowToast("🌧", w.RainAlertText, durationMs: 4200);
            }

            if (!_isExpanded) UpdateCompactWidth(animate: false);
        }
    }

    private void ApplyWeatherVectorState(int wmoCode)
    {
        bool isClear = wmoCode == 0;
        bool isPartlyCloudy = wmoCode is 1 or 2;
        bool isOvercast = wmoCode is 3 or 45 or 48;
        bool hasPrecip = wmoCode >= 51;

        CompactSunRays.Visibility = isClear ? Visibility.Visible : Visibility.Collapsed;
        CompactSunShape.Visibility = (isClear || isPartlyCloudy) ? Visibility.Visible : Visibility.Collapsed;
        if (isClear)
        {
            CompactSunShape.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
            CompactSunShape.VerticalAlignment = VerticalAlignment.Center;
            CompactSunShape.Margin = new Thickness(0);
        }
        else
        {
            CompactSunShape.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
            CompactSunShape.VerticalAlignment = VerticalAlignment.Top;
            CompactSunShape.Margin = new Thickness(1.5, 1.5, 0, 0);
        }
        CompactCloudShape.Visibility = !isClear ? Visibility.Visible : Visibility.Collapsed;
        CompactRainShape.Visibility = hasPrecip ? Visibility.Visible : Visibility.Collapsed;

        HomeSunRayRing.Visibility = isClear ? Visibility.Visible : Visibility.Collapsed;
        HomeSunCore.Visibility = (isClear || isPartlyCloudy) ? Visibility.Visible : Visibility.Collapsed;
        HomeCloudShape.Visibility = (isPartlyCloudy || isOvercast || hasPrecip) ? Visibility.Visible : Visibility.Collapsed;
        HomeRainShape.Visibility = hasPrecip ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateCompactGameIconBadge(uint pid)
    {
        if (pid == 0)
        {
            CompactGameExeIcon.Visibility = Visibility.Collapsed;
            CompactGameFallbackGlyph.Visibility = Visibility.Visible;
            return;
        }
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            string? exePath = proc.MainModule?.FileName;
            if (!string.IsNullOrEmpty(exePath))
            {
                if (!string.Equals(exePath, _lastCompactGameExePath, StringComparison.OrdinalIgnoreCase) ||
                    CompactGameExeIcon.Source == null)
                {
                    _lastCompactGameExePath = exePath;
                    if (!_iconCache.TryGetValue(exePath, out var icon))
                    {
                        icon = IconHelper.GetAppIcon(exePath);
                        _iconCache[exePath] = icon;
                    }
                    CompactGameExeIcon.Source = icon;
                }
                if (CompactGameExeIcon.Source != null)
                {
                    CompactGameExeIcon.Visibility = Visibility.Visible;
                    CompactGameFallbackGlyph.Visibility = Visibility.Collapsed;
                    return;
                }
            }
        }
        catch
        {
            // Protected/elevated process: keep green gamepad glyph cleanly
        }
        CompactGameExeIcon.Visibility = Visibility.Collapsed;
        CompactGameFallbackGlyph.Visibility = Visibility.Visible;
    }

    #endregion

    #region Remote Tab ("Пульт": Media, Audio, Mic & Bluetooth)

    private async Task RefreshMediaAndDevicesAsync(bool forceBluetooth)
    {
        var track = await MediaControlService.GetCurrentTrackAsync(_settings.PreferredMediaPlayer);
        bool wasPlaying = _isMediaPlaying;
        _isMediaPlaying = track.HasSession && track.IsPlaying;
        _isDedicatedMusicSession = track.IsDedicatedMusicSource;
        _currentMediaDominantColor = track.DominantColor;

        if (_isMediaPlaying && _isSuperMiniIdle)
            ExitSuperMiniIdle();

        CompactEqPanel.Visibility = (_settings.CompactShowMedia && _isMediaPlaying && !_isSuperMiniIdle)
            ? Visibility.Visible : Visibility.Collapsed;

        // Apply SMTC album/video thumbnail to spinning Music Orb and Hero Card
        if (track.Thumbnail != null && !_isStreamPrivacyActive)
        {
            MusicThumbBrush.ImageSource = track.Thumbnail;
            MusicThumbCircle.Visibility = Visibility.Visible;
            MusicStatusDot.Visibility = Visibility.Collapsed;
            MediaCardThumbImg.Source = track.Thumbnail;
            MediaCardThumbImg.Visibility = Visibility.Visible;
            MediaCardFallbackIcon.Visibility = Visibility.Collapsed;
            HomeMediaThumbImg.Source = track.Thumbnail;
            HomeMediaThumbImg.Visibility = Visibility.Visible;
            HomeMediaFallbackNote.Visibility = Visibility.Collapsed;
        }
        else
        {
            MusicThumbCircle.Visibility = Visibility.Collapsed;
            MusicStatusDot.Visibility = Visibility.Visible;
            MediaCardThumbImg.Visibility = Visibility.Collapsed;
            MediaCardFallbackIcon.Visibility = Visibility.Visible;
            HomeMediaThumbImg.Visibility = Visibility.Collapsed;
            HomeMediaFallbackNote.Visibility = Visibility.Visible;
        }

        // Dynamic Hero Glow from Album Dominant Color
        if (_isMediaPlaying && track.DominantColor.HasValue && !_isStreamPrivacyActive)
        {
            var dc = track.DominantColor.Value;
            MediaHeroGlowStop.Color = MediaColor.FromArgb(62, dc.R, dc.G, dc.B);
        }
        else
        {
            MediaHeroGlowStop.Color = MediaColor.FromArgb(24, 56, 189, 248);
        }

        // Track change subtle banner (when playing and Privacy Shield is off)
        string safeTitle = _isStreamPrivacyActive ? "Скрыто (Стрим-щит)" : (track.HasSession ? track.Title : "Музыка");
        string trackKey = $"{track.Title}|{track.Artist}";
        if (_isMediaPlaying && !string.IsNullOrWhiteSpace(track.Title) &&
            !string.IsNullOrEmpty(_lastTrackChangeKey) &&
            !string.Equals(_lastTrackChangeKey, trackKey, StringComparison.Ordinal))
        {
            if (!_isStreamPrivacyActive)
                FlashIslandBorder(track.DominantColor ?? MediaColor.FromRgb(56, 189, 248));
        }
        _lastTrackChangeKey = trackKey;

        // Update sleek compact Music Strip (compact glowing orb + real thumbnail + scrolling title + 7-bar spectrum)
        string newTitle = safeTitle;
        if (!string.Equals(_lastMusicBannerTitle, newTitle, StringComparison.Ordinal))
        {
            _lastMusicBannerTitle = newTitle;
            _musicMarqueeOffset = 0;
            _musicMarqueePauseTicks = 28;
            CompactMusicMarqueeTransform.X = 0;
            CompactMusicTitleText.Text = newTitle;
        }

        if (track.SourceLabel.Contains("Spotify", StringComparison.OrdinalIgnoreCase))
            MusicStatusDot.Fill = new SolidColorBrush(MediaColor.FromRgb(30, 215, 96));
        else if (track.SourceLabel.Contains("SoundCloud", StringComparison.OrdinalIgnoreCase))
            MusicStatusDot.Fill = new SolidColorBrush(MediaColor.FromRgb(255, 119, 0));
        else
            MusicStatusDot.SetResourceReference(Shape.FillProperty, "AccentBrightBrush");

        if (!_isExpanded && wasPlaying != _isMediaPlaying)
        {
            SyncCompactStripVisibility();
            UpdateCompactWidth(animate: true);
            if (!_isMediaPlaying) ApplyCurrentIslandBorder();
        }
        else if (!_isExpanded)
        {
            SyncCompactStripVisibility();
        }

        MediaTitleText.Text = _isStreamPrivacyActive
            ? "🛡 Название скрыто (Стрим-щит)"
            : (track.HasSession ? track.Title : "Ничего не играет");
        MediaArtistText.Text = _isStreamPrivacyActive
            ? "Запись / трансляция экрана активна"
            : (track.HasSession
                ? (string.IsNullOrEmpty(track.Artist) ? "Медиаплеер активен" : track.Artist)
                : "Нажмите ▶ или откройте плеер ниже");
        MediaPlayGlyph.Visibility = _isMediaPlaying ? Visibility.Collapsed : Visibility.Visible;
        MediaPauseGlyph.Visibility = _isMediaPlaying ? Visibility.Visible : Visibility.Collapsed;
        RemoteEqPanel.Visibility = _isMediaPlaying ? Visibility.Visible : Visibility.Collapsed;

        HomeMediaTitleText.Text = _isStreamPrivacyActive
            ? "Скрыто (Стрим-щит)"
            : (track.HasSession ? track.Title : "Ничего не играет");
        HomeMediaArtistText.Text = _isStreamPrivacyActive
            ? "Запись активна"
            : (track.HasSession
                ? (string.IsNullOrEmpty(track.Artist) ? (track.SourceLabel ?? "Плеер") : track.Artist)
                : "Включите музыку в плеере");
        HomeMediaPlayGlyph.Visibility = _isMediaPlaying ? Visibility.Collapsed : Visibility.Visible;
        HomeMediaPauseGlyph.Visibility = _isMediaPlaying ? Visibility.Visible : Visibility.Collapsed;

        if (track.HasSession && !string.IsNullOrEmpty(track.SourceLabel))
        {
            MediaSourceBadge.Text = track.SourceLabel;
            if (track.SourceLabel.Contains("Spotify", StringComparison.OrdinalIgnoreCase))
                MediaSourceDot.Fill = new SolidColorBrush(MediaColor.FromRgb(30, 215, 96));
            else if (track.SourceLabel.Contains("SoundCloud", StringComparison.OrdinalIgnoreCase))
                MediaSourceDot.Fill = new SolidColorBrush(MediaColor.FromRgb(255, 119, 0));
            else if (track.SourceLabel.Contains("YT", StringComparison.OrdinalIgnoreCase))
                MediaSourceDot.Fill = new SolidColorBrush(MediaColor.FromRgb(239, 68, 68));
            else
                MediaSourceDot.SetResourceReference(Shape.FillProperty, "AccentBrightBrush");
        }
        else
        {
            MediaSourceBadge.Text = "Плеер";
            MediaSourceDot.Fill = new SolidColorBrush(MediaColor.FromRgb(113, 113, 122));
        }

        if (track.HasSession && track.HasTimeline)
        {
            double barW = MediaProgressHost.ActualWidth > 20 ? MediaProgressHost.ActualWidth : 355.0;
            MediaProgressFill.Width = barW * Math.Clamp(track.ProgressRatio, 0.0, 1.0);
            MediaElapsedText.Text = FormatMediaDuration(track.PositionSeconds);
            MediaDurationText.Text = FormatMediaDuration(track.DurationSeconds);
        }
        else
        {
            MediaProgressFill.Width = 0;
            MediaElapsedText.Text = "0:00";
            MediaDurationText.Text = "0:00";
        }

        if (_isExpanded && TabRemote.IsChecked == true)
        {
            int vol = CoreAudioService.GetMasterVolumePercent();
            SyncVolumeUi(vol);

            bool micMuted = CoreAudioService.IsMicMuted();
            MicMuteBtn.Content = micMuted ? "🔇 Мик: Выкл" : "🎙 Мик: Вкл";
            MicMuteBtn.Foreground = micMuted
                ? new SolidColorBrush(MediaColor.FromRgb(252, 165, 165))
                : new SolidColorBrush(MediaColor.FromRgb(228, 228, 231));

            var outputs = CoreAudioService.GetPlaybackDevices();
            var def = outputs.FirstOrDefault(o => o.IsDefault);
            string shortDevName = def?.Name ?? "Вывод";
            int parenIdx = shortDevName.IndexOf('(');
            if (parenIdx > 2) shortDevName = shortDevName[..parenIdx].Trim();
            if (shortDevName.Length > 13) shortDevName = shortDevName[..12] + "…";

            AudioSwitchBtn.Content = def != null ? $"{def.IconGlyph} {shortDevName}" : "🔊 Вывод";
            AudioSwitchBtn.ToolTip = outputs.Count > 1
                ? $"Текущее: {def?.Name}. Нажмите, чтобы переключить ({outputs.Count} устр.)"
                : $"Вывод звука: {def?.Name ?? "По умолчанию"}";

            SyncQuickToolStates();
            SyncStreamShieldUi();
            SyncRecordingUi();

            GameAudioBindBtn.Visibility = !string.IsNullOrEmpty(_activeDockProcessKey)
                ? Visibility.Visible
                : Visibility.Collapsed;

            if (_isAppMixerExpanded)
            {
                _isUpdatingAppMixer = true;
                var sessions = CoreAudioService.GetAudioRenderSessions();
                _audioSessions.Clear();
                foreach (var s in sessions) _audioSessions.Add(s);
                _isUpdatingAppMixer = false;
            }

            int? br = HardwareAndUtilityService.GetMonitorBrightnessPercent(_ownHwnd);
            if (br.HasValue)
            {
                _isUpdatingBrightnessSlider = true;
                BrightnessSlider.Value = br.Value;
                BrightnessLabelText.Text = $"☀ {br.Value}%";
                _isUpdatingBrightnessSlider = false;
            }
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
                {
                    AddNotification("🎧", "Bluetooth", $"Подключено: {newly}", "Беспроводное устройство готово");
                    ShowToast("🎧", $"Bluetooth подключен: {newly}", durationMs: 3200);
                }
            }
            _connectedBtNames = nowConnected;

            foreach (var d in devices)
            {
                if (d.IsConnected && d.BatteryPercent.HasValue && d.BatteryPercent.Value < 20)
                {
                    if (_lowBatteryAlertedDevices.Add(d.Id))
                    {
                        AddNotification("🪫", "Батарея", $"{d.Name}: {d.BatteryPercent.Value}%", "Рекомендуется поставить на зарядку");
                        ShowToast("🪫", $"{d.Name}: низкий заряд ({d.BatteryPercent.Value}%)", durationMs: 4200);
                    }
                }
                else if (!d.IsConnected || (d.BatteryPercent.HasValue && d.BatteryPercent.Value >= 25))
                {
                    _lowBatteryAlertedDevices.Remove(d.Id);
                }
            }

            BtStateText.Text = !state.Supported ? "· нет адаптера" : (state.IsOn ? "· Вкл" : "· Выкл");
            BtToggleBtn.IsEnabled = state.Supported;
            BtToggleBtn.Content = state.IsOn ? "Выключить" : "Включить";
            BtToggleBtn.Background = new SolidColorBrush(state.IsOn
                ? MediaColor.FromRgb(30, 58, 138)
                : MediaColor.FromRgb(31, 32, 40));

            _allBtDevices = devices;
            if (_isExpanded && TabSystem.IsChecked == true)
            {
                ApplyBtListFilter();
                RefreshSystemControlCenterUi(HardwareAndUtilityService.GetConnectedGamepads());
            }
        }
    }

    private void ApplyBtListFilter()
    {
        _btDevices.Clear();
        foreach (var d in _allBtDevices.OrderByDescending(x => x.IsConnected).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            _btDevices.Add(d);

        if (_btDevices.Count == 0)
        {
            BtEmptyText.Text = BtStateText.Text.Contains("Выкл", StringComparison.OrdinalIgnoreCase)
                ? "Bluetooth выключен"
                : "Нет сопряжённых устройств";
            BtEmptyText.Visibility = Visibility.Visible;
        }
        else
        {
            BtEmptyText.Visibility = Visibility.Collapsed;
        }

        if (_isExpanded && TabSystem.IsChecked == true)
            ScheduleExpandedResize(animate: true);
    }

    private void EqTimer_Tick(object? sender, EventArgs e)
    {
        bool remoteMusicVisible = _isExpanded && TabRemote.IsChecked == true && _isMediaPlaying;
        bool eqVisible = !_isExpanded && CompactEqPanel.Visibility == Visibility.Visible && CompactMetricsStrip.Visibility == Visibility.Visible;
        bool fullMusicVisible = !_isExpanded && CompactMusicStrip.Visibility == Visibility.Visible;
        if (!eqVisible && !fullMusicVisible && !remoteMusicVisible) return;

        float rawPeak = CoreAudioService.GetRenderPeakLevel();
        double peak = rawPeak < 0.015f ? (_isMediaPlaying ? 0.35 : 0.0) : Math.Clamp(Math.Pow(rawPeak, 0.48), 0.25, 1.0);
        double t = Environment.TickCount64 / 85.0;

        if (remoteMusicVisible)
        {
            double rBar(double phase, double weight)
            {
                double wave = 0.35 + 0.65 * (0.5 + 0.5 * Math.Sin(t + phase));
                return Math.Clamp(3.0 + peak * weight * wave, 3.5, 16.0);
            }
            RemoteEqBar1.Height = rBar(0.0, 10.5);
            RemoteEqBar2.Height = rBar(1.0, 14.0);
            RemoteEqBar3.Height = rBar(2.0, 15.0);
            RemoteEqBar4.Height = rBar(3.0, 12.5);
            RemoteEqBar5.Height = rBar(4.0, 14.5);
            RemoteEqBar6.Height = rBar(5.0, 11.0);
            return;
        }

        if (_isExpanded) return;
        if (fullMusicVisible)
        {
            // Spin glowing ring smoothly + lively 7-bar spectrum wave
            _musicOrbAngle = (_musicOrbAngle + 2.6 + peak * 2.4) % 360.0;
            MusicOrbRotate.Angle = _musicOrbAngle;

            double bar(double phase, double weight)
            {
                double wave = 0.38 + 0.62 * (0.5 + 0.5 * Math.Sin(t + phase));
                return Math.Clamp(3.5 + peak * weight * wave, 4.0, 20.5);
            }

            MusicBar1.Height = bar(0.0, 12.0);
            MusicBar2.Height = bar(0.9, 16.5);
            MusicBar3.Height = bar(1.8, 18.0);
            MusicBar4.Height = bar(2.7, 15.0);
            MusicBar5.Height = bar(3.6, 18.5);
            MusicBar6.Height = bar(4.5, 16.0);
            MusicBar7.Height = bar(5.4, 12.5);

            // Bass-reactive ambient border pulse matched to dominant album color / theme accent
            if (!_isBorderAlertActive && !_screenRecorder.IsRecording &&
                _settings.BorderStyle != "Minimal" &&
                !(_isInFullscreenGame && _settings.GameModeAutoProfile && (_isAltHeldInGame || (_isMicMutedCached && _settings.GameShowMicMute))))
            {
                var baseCol = _currentMediaDominantColor ?? ThemeHelper.GetAccentColors(_settings.AccentTheme).Bright;
                byte glowAlpha = (byte)Math.Clamp(60 + peak * 155, 60, 215);
                IslandBorder.BorderBrush = new SolidColorBrush(MediaColor.FromArgb(glowAlpha, baseCol.R, baseCol.G, baseCol.B));
            }

            // Smooth horizontal marquee ONLY if title overflows the 118px viewport
            double textW = CompactMusicTitleText.ActualWidth;
            if (textW > 118.0)
            {
                if (_musicMarqueePauseTicks > 0)
                {
                    _musicMarqueePauseTicks--;
                }
                else
                {
                    _musicMarqueeOffset -= 1.05;
                    if (_musicMarqueeOffset < -(textW + 18))
                    {
                        _musicMarqueeOffset = 118.0;
                    }
                    else if (_musicMarqueeOffset <= 0.5 && _musicMarqueeOffset >= -0.6)
                    {
                        _musicMarqueeOffset = 0;
                        _musicMarqueePauseTicks = 32;
                    }
                    CompactMusicMarqueeTransform.X = _musicMarqueeOffset;
                }
            }
            else if (_musicMarqueeOffset != 0)
            {
                _musicMarqueeOffset = 0;
                CompactMusicMarqueeTransform.X = 0;
            }
        }
        else
        {
            EqBar1.Height = 3 + Math.Clamp(peak * 11 * (0.65 + 0.35 * Math.Sin(t)), 1, 11);
            EqBar2.Height = 3 + Math.Clamp(peak * 11 * (0.65 + 0.35 * Math.Sin(t + 1.5)), 1, 11);
            EqBar3.Height = 3 + Math.Clamp(peak * 11 * (0.65 + 0.35 * Math.Sin(t + 3.0)), 1, 11);
            EqBar4.Height = 3 + Math.Clamp(peak * 11 * (0.65 + 0.35 * Math.Sin(t + 4.5)), 1, 11);
        }
    }

    private void SyncVolumeUi(int percent)
    {
        _isUpdatingVolumeSlider = true;
        MasterVolumeSlider.Value = percent;
        VolumePercentText.Text = $"Громкость · {percent}%";
        _isUpdatingVolumeSlider = false;
    }

    private void MasterVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingVolumeSlider) return;
        int val = (int)Math.Round(e.NewValue);
        CoreAudioService.SetMasterVolumePercent(val);
        VolumePercentText.Text = $"Громкость · {val}%";
    }

    private static string FormatMediaDuration(double seconds)
    {
        if (seconds < 0 || double.IsNaN(seconds)) return "0:00";
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
    }

    private async void MediaProgressHost_MouseDown(object sender, MouseButtonEventArgs e)
    {
        double hostW = MediaProgressHost.ActualWidth;
        if (hostW <= 10) return;
        double x = e.GetPosition(MediaProgressHost).X;
        double ratio = Math.Clamp(x / hostW, 0.0, 1.0);
        MediaProgressFill.Width = hostW * ratio;
        await MediaControlService.SeekRatioAsync(ratio, _settings.PreferredMediaPlayer);
        await Task.Delay(180);
        await RefreshMediaAndDevicesAsync(false);
    }

    private void RemoteExtrasToggle_Click(object sender, RoutedEventArgs e)
    {
        _isRemoteExtrasExpanded = !_isRemoteExtrasExpanded;
        AnimatePanelReveal(RemoteExtrasSection, _isRemoteExtrasExpanded, () => !_isRemoteExtrasExpanded, translate: RemoteExtrasTranslate);
        RemoteExtrasToggleBtn.Content = _isRemoteExtrasExpanded
            ? "Скрыть таймер сна, питание и записи ▴"
            : "Таймер сна, питание и записи ▾";
    }

    private async void MediaPrev_Click(object sender, RoutedEventArgs e)
    {
        await MediaControlService.PreviousAsync(_settings.PreferredMediaPlayer);
        await Task.Delay(250);
        await RefreshMediaAndDevicesAsync(false);
    }

    private async void MediaPlayPause_Click(object sender, RoutedEventArgs e)
    {
        await MediaControlService.PlayPauseAsync(_settings.PreferredMediaPlayer);
        await Task.Delay(250);
        await RefreshMediaAndDevicesAsync(false);
    }

    private async void MediaNext_Click(object sender, RoutedEventArgs e)
    {
        await MediaControlService.NextAsync(_settings.PreferredMediaPlayer);
        await Task.Delay(250);
        await RefreshMediaAndDevicesAsync(false);
    }

    private void BindMediaPlayer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string player)
        {
            if (!_settings.PreferredMediaPlayer.Equals("Auto", StringComparison.OrdinalIgnoreCase))
            {
                _settings.PreferredMediaPlayer = "Auto";
                SettingsService.Save(_settings);
            }
            SyncPlayerBindingPills();

            if (!player.Equals("Auto", StringComparison.OrdinalIgnoreCase))
            {
                MediaControlService.LaunchOrFocusPlayer(player);
                string label = player == "YouTubeMusic" ? "YouTube Music" : player;
                ShowToast("🎵", $"Открываем {label}...", durationMs: 2200);
            }
        }
    }

    private void LaunchBoundPlayer_Click(object sender, RoutedEventArgs e)
    {
        MediaControlService.LaunchOrFocusPlayer("Spotify");
        ShowToast("🎵", "Открываем Spotify...", durationMs: 2200);
    }

    private void SyncPlayerBindingPills()
    {
        PlayerAutoBtn.Visibility = Visibility.Collapsed;
        HomePlayerAutoBtn.Visibility = Visibility.Collapsed;
        LaunchBoundPlayerBtn.Visibility = Visibility.Collapsed;

        var launcherBg = new SolidColorBrush(MediaColor.FromRgb(31, 32, 40));
        var launcherFg = new SolidColorBrush(MediaColor.FromRgb(212, 212, 216));

        foreach (var btn in new[]
        {
            PlayerSpotifyBtn, PlayerSoundCloudBtn, PlayerYtMusicBtn,
            HomePlayerSpotifyBtn, HomePlayerSoundCloudBtn, HomePlayerYtMusicBtn
        })
        {
            btn.Background = launcherBg;
            btn.Foreground = launcherFg;
        }
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
            ShowToast(next.IconGlyph, $"Вывод: {next.Name}");
            _ = RefreshMediaAndDevicesAsync(false);
        }
    }

    private void ToggleMicMute_Click(object sender, RoutedEventArgs e)
    {
        bool nowMuted = CoreAudioService.ToggleMicMute();
        MicMuteBtn.Content = nowMuted ? "🔇 Мик: Выкл" : "🎙 Мик: Вкл";
        SyncDiscordVoiceButtonsUi();
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
        RefreshSystemControlCenterUi(HardwareAndUtilityService.GetConnectedGamepads());
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

    private static HashSet<string> GetRunningProcessNames()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(p.ProcessName))
                        set.Add(p.ProcessName);
                }
                catch { }
                finally
                {
                    p.Dispose();
                }
            }
        }
        catch { }
        return set;
    }

    private static bool IsPinnedAppRunning(PinnedApp app, HashSet<string> runningProcs)
    {
        try
        {
            string? fileName = IOPath.GetFileNameWithoutExtension(app.ExecutablePath);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                if (runningProcs.Contains(fileName)) return true;
                string compact = fileName.Replace(" ", string.Empty);
                if (compact.Length >= 3 && runningProcs.Contains(compact)) return true;
            }
            if (!string.IsNullOrWhiteSpace(app.Name) && runningProcs.Contains(app.Name.Trim()))
                return true;
        }
        catch { }
        return false;
    }

    private UIElement CreateAppIconVisual(string path, string fallbackName, double size)
    {
        var iconSrc = ResolveAppIcon(path);
        if (iconSrc != null)
        {
            var img = new System.Windows.Controls.Image
            {
                Width = size,
                Height = size,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Source = iconSrc,
                SnapsToDevicePixels = true
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            return img;
        }

        string initial = string.IsNullOrWhiteSpace(fallbackName) ? "A" : fallbackName.Trim()[..1].ToUpperInvariant();
        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(Math.Round(size * 0.28)),
            Background = new SolidColorBrush(MediaColor.FromRgb(42, 47, 64)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = initial,
                FontSize = Math.Round(size * 0.48),
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(MediaColor.FromRgb(228, 228, 231)),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
    }

    private void RebuildAppTiles()
    {
        AppsWrapPanel.Children.Clear();
        HomeDockAppsPanel.Children.Clear();

        var runningProcs = GetRunningProcessNames();
        foreach (var app in _settings.PinnedApps)
        {
            bool running = IsPinnedAppRunning(app, runningProcs);
            AppsWrapPanel.Children.Add(CreateAppTile(app, running));
            HomeDockAppsPanel.Children.Add(CreateDockAppTile(app, running));
        }

        AppsWrapPanel.Children.Add(CreateAddAppTile());
        HomeDockAppsPanel.Children.Add(CreateDockAddAppButton());
    }

    private Border CreateDockAppTile(PinnedApp app, bool isRunning)
    {
        var scale = new ScaleTransform(1.0, 1.0);
        var tile = new Border
        {
            Width = 42,
            Height = 46,
            CornerRadius = new CornerRadius(12),
            Background = MediaBrushes.Transparent,
            BorderBrush = MediaBrushes.Transparent,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 6, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = _isAppsEditMode ? $"Перетащите «{app.Name}»" : app.Name,
            Tag = app,
            AllowDrop = _isAppsEditMode
        };

        var root = new Grid();
        var col = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Vertical,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var iconGlow = new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(11),
            Background = MediaBrushes.Transparent,
            RenderTransformOrigin = new MediaPoint(0.5, 0.5),
            RenderTransform = scale,
            Child = CreateAppIconVisual(app.ExecutablePath, app.Name, 31)
        };

        var runningDot = new System.Windows.Shapes.Ellipse
        {
            Width = 3.5,
            Height = 3.5,
            Fill = new SolidColorBrush(MediaColor.FromRgb(244, 244, 245)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 3, 0, 0),
            Opacity = isRunning ? 0.95 : 0.0
        };

        col.Children.Add(iconGlow);
        col.Children.Add(runningDot);
        root.Children.Add(col);

        if (_isAppsEditMode)
        {
            var badge = CreateRemoveAppBadge(app);
            badge.Margin = new Thickness(0, -2, -2, 0);
            root.Children.Add(badge);
        }

        tile.Child = root;

        tile.MouseEnter += (_, _) =>
        {
            iconGlow.Background = new SolidColorBrush(MediaColor.FromArgb(24, 255, 255, 255));
            if (_settings.EnableAnimations)
            {
                var anim = new DoubleAnimation(1.20, TimeSpan.FromMilliseconds(150))
                {
                    EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.36 }
                };
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
            }
        };
        tile.MouseLeave += (_, _) =>
        {
            iconGlow.Background = MediaBrushes.Transparent;
            if (_settings.EnableAnimations)
            {
                var anim = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(150))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
            }
        };

        AttachAppTileBehavior(tile, app, HomeDockAppsPanel);
        return tile;
    }

    private Border CreateDockAddAppButton()
    {
        var scale = new ScaleTransform(1.0, 1.0);
        var btn = new Border
        {
            Width = 35,
            Height = 35,
            CornerRadius = new CornerRadius(17.5),
            Background = new SolidColorBrush(MediaColor.FromRgb(42, 44, 52)),
            BorderThickness = new Thickness(0),
            Margin = new Thickness(4, 0, 0, 6),
            Cursor = System.Windows.Input.Cursors.Hand,
            RenderTransformOrigin = new MediaPoint(0.5, 0.5),
            RenderTransform = scale,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Добавить или найти приложение (+)",
            Child = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 12,6.5 L 12,17.5 M 6.5,12 L 17.5,12"),
                Stroke = new SolidColorBrush(MediaColor.FromRgb(228, 228, 231)),
                StrokeThickness = 1.9,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Width = 22,
                Height = 22,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        btn.MouseEnter += (_, _) =>
        {
            btn.Background = new SolidColorBrush(MediaColor.FromRgb(58, 61, 74));
            if (_settings.EnableAnimations)
            {
                var anim = new DoubleAnimation(1.12, TimeSpan.FromMilliseconds(140))
                {
                    EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 }
                };
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
            }
        };
        btn.MouseLeave += (_, _) =>
        {
            btn.Background = new SolidColorBrush(MediaColor.FromRgb(42, 44, 52));
            if (_settings.EnableAnimations)
            {
                var anim = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(140))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
            }
        };
        btn.MouseLeftButtonUp += (_, _) => ToggleInlineAppSearch();
        return btn;
    }

    private void ToggleInlineAppSearch()
    {
        bool opening = HomeAppSearchDrawer.Visibility != Visibility.Visible;
        if (opening)
        {
            HomeAppSearchBox.Text = string.Empty;
            HomeAppSearchPlaceholder.Visibility = Visibility.Visible;
            PopulateAppSearchResults(string.Empty);
            AnimatePanelReveal(HomeAppSearchDrawer, true, scale: HomeSearchScale, translate: HomeSearchTranslate);
            HomeAppSearchBox.Focus();
        }
        else
        {
            AnimatePanelReveal(HomeAppSearchDrawer, false, null, HomeSearchScale, HomeSearchTranslate);
        }
    }

    private void HomeAppSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string q = HomeAppSearchBox.Text ?? string.Empty;
        HomeAppSearchPlaceholder.Visibility = string.IsNullOrEmpty(q) ? Visibility.Visible : Visibility.Collapsed;
        PopulateAppSearchResults(q);
    }

    private void HomeAppSearchBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HomeAppSearchDrawer.Visibility = Visibility.Collapsed;
            ScheduleExpandedResize(animate: true);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            var top = InstalledAppsService.Search(HomeAppSearchBox.Text ?? string.Empty, 1).FirstOrDefault();
            if (top != null)
            {
                LaunchAppByTarget(top.LaunchTarget, top.Name);
                e.Handled = true;
            }
        }
    }

    private void BrowseCustomExeApp_Click(object sender, RoutedEventArgs e) => PromptAddPinnedApp();

    private void PopulateAppSearchResults(string query)
    {
        HomeAppSearchResultsList.Items.Clear();
        var results = InstalledAppsService.Search(query, 9);
        foreach (var entry in results)
        {
            var row = new Border
            {
                Background = MediaBrushes.Transparent,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(7, 5, 7, 5),
                Margin = new Thickness(0, 0, 4, 4),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = $"ЛКМ: запустить {entry.Name} · 📌: закрепить в доке"
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var iconEl = CreateAppIconVisual(entry.IconPath, entry.Name, 18);
            if (iconEl is FrameworkElement fe)
            {
                fe.Margin = new Thickness(0, 0, 7, 0);
                fe.VerticalAlignment = VerticalAlignment.Center;
            }
            Grid.SetColumn(iconEl, 0);
            grid.Children.Add(iconEl);

            var lbl = new TextBlock
            {
                Text = entry.Name,
                FontSize = 11,
                Foreground = new SolidColorBrush(MediaColor.FromRgb(228, 228, 231)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(lbl, 1);
            grid.Children.Add(lbl);

            bool alreadyPinned = _settings.PinnedApps.Any(a => string.Equals(a.ExecutablePath, entry.LaunchTarget, StringComparison.OrdinalIgnoreCase));
            var pinBtn = new System.Windows.Controls.Button
            {
                Content = alreadyPinned ? "✓" : "+",
                Padding = new Thickness(5, 1, 5, 1),
                FontSize = 10,
                Margin = new Thickness(4, 0, 0, 0),
                Background = alreadyPinned
                    ? new SolidColorBrush(MediaColor.FromRgb(22, 101, 52))
                    : new SolidColorBrush(MediaColor.FromRgb(42, 44, 54)),
                Foreground = MediaBrushes.White,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                Opacity = alreadyPinned ? 0.95 : 0.72,
                ToolTip = alreadyPinned ? "Убрать из дока" : "Добавить в док островка"
            };
            pinBtn.Click += (_, ev) =>
            {
                ev.Handled = true;
                var existing = _settings.PinnedApps.FirstOrDefault(a => string.Equals(a.ExecutablePath, entry.LaunchTarget, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    _settings.PinnedApps.Remove(existing);
                    ShowToast("🗑", $"{entry.Name} убрано из дока", durationMs: 1800);
                }
                else
                {
                    _settings.PinnedApps.Add(new PinnedApp { Name = entry.Name, ExecutablePath = entry.LaunchTarget });
                    ShowToast("📌", $"{entry.Name} добавлено в док", durationMs: 2000);
                }
                SettingsService.Save(_settings);
                RebuildAppTiles();
                PopulateAppSearchResults(HomeAppSearchBox.Text ?? string.Empty);
                ScheduleExpandedResize(animate: true);
            };
            Grid.SetColumn(pinBtn, 2);
            grid.Children.Add(pinBtn);

            row.Child = grid;
            row.MouseEnter += (_, _) =>
            {
                row.Background = new SolidColorBrush(MediaColor.FromRgb(38, 40, 50));
                pinBtn.Opacity = 1.0;
            };
            row.MouseLeave += (_, _) =>
            {
                row.Background = MediaBrushes.Transparent;
                pinBtn.Opacity = alreadyPinned ? 0.95 : 0.72;
            };
            row.MouseLeftButtonUp += (_, _) => LaunchAppByTarget(entry.LaunchTarget, entry.Name);

            HomeAppSearchResultsList.Items.Add(row);
        }
    }

    private void LaunchAppByTarget(string target, string displayName)
    {
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(target);
            Process.Start(new ProcessStartInfo(expanded)
            {
                UseShellExecute = true,
                WorkingDirectory = IOPath.GetDirectoryName(expanded) ?? string.Empty
            });
            ShowToast("🚀", $"Запуск: {displayName}", durationMs: 1800);
        }
        catch (Exception ex)
        {
            ShowToast("⚠", $"Ошибка запуска: {ex.Message}", durationMs: 2600);
        }
    }

    private System.Windows.Controls.Button CreateRemoveAppBadge(PinnedApp app)
    {
        var minusBadge = new System.Windows.Controls.Button
        {
            Width = 17,
            Height = 17,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 2, 0),
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
        return minusBadge;
    }

    private void AttachAppTileBehavior(Border tile, PinnedApp app, System.Windows.Controls.Panel hostPanel)
    {
        if (_isAppsEditMode)
        {
            tile.MouseLeftButtonDown += (_, ev) =>
            {
                if (HasButtonAncestor(ev.OriginalSource as DependencyObject))
                    return;

                _reorderingTile = tile;
                _reorderingApp = app;
                _reorderStartPoint = ev.GetPosition(hostPanel);
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

                var pos = ev.GetPosition(hostPanel);
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

                foreach (UIElement child in hostPanel.Children)
                {
                    if (child is Border targetBorder && targetBorder != tile && targetBorder.Tag is PinnedApp targetApp)
                    {
                        var topLeft = targetBorder.TranslatePoint(new MediaPoint(0, 0), hostPanel);
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
                                hostPanel.Children.Remove(tile);
                                hostPanel.Children.Insert(toIdx, tile);
                                hostPanel.UpdateLayout();
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
    }

    private Border CreateAppTile(PinnedApp app, bool isRunning)
    {
        var tile = new Border
        {
            Width = 90,
            Height = 76,
            CornerRadius = new CornerRadius(14),
            Background = MediaBrushes.Transparent,
            BorderBrush = MediaBrushes.Transparent,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 6, 6),
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

        var iconVisual = CreateAppIconVisual(app.ExecutablePath, app.Name, 34);
        if (iconVisual is FrameworkElement feIcon)
            feIcon.Margin = new Thickness(0, 0, 0, 5);

        var labelRow = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (isRunning)
        {
            labelRow.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 4,
                Height = 4,
                Fill = new SolidColorBrush(MediaColor.FromRgb(244, 244, 245)),
                Margin = new Thickness(0, 1, 4, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        var label = new TextBlock
        {
            Text = app.Name,
            FontSize = 10.5,
            Foreground = new SolidColorBrush(MediaColor.FromRgb(228, 228, 231)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            MaxWidth = isRunning ? 74 : 82,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        labelRow.Children.Add(label);

        center.Children.Add(iconVisual);
        center.Children.Add(labelRow);
        root.Children.Add(center);

        if (_isAppsEditMode)
        {
            var badge = CreateRemoveAppBadge(app);
            badge.Margin = new Thickness(0, 4, 4, 0);
            root.Children.Add(badge);
        }

        tile.Child = root;

        tile.MouseEnter += (_, _) => tile.Background = new SolidColorBrush(MediaColor.FromRgb(36, 38, 48));
        tile.MouseLeave += (_, _) => tile.Background = MediaBrushes.Transparent;

        AttachAppTileBehavior(tile, app, AppsWrapPanel);
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
            tile.BorderBrush = MediaBrushes.Transparent;
            tile.BorderThickness = new Thickness(1);
        }

        if (save && wasReordering)
            SettingsService.Save(_settings);
    }

    private Border CreateAddAppTile()
    {
        var tile = new Border
        {
            Width = 90,
            Height = 76,
            CornerRadius = new CornerRadius(14),
            Background = MediaBrushes.Transparent,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(0, 0, 6, 6),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "Добавить или найти приложение (+)"
        };

        var stack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        };
        var plusCircle = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(17),
            Background = new SolidColorBrush(MediaColor.FromRgb(42, 44, 52)),
            Margin = new Thickness(0, 0, 0, 5),
            Child = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 12,6.5 L 12,17.5 M 6.5,12 L 17.5,12"),
                Stroke = new SolidColorBrush(MediaColor.FromRgb(228, 228, 231)),
                StrokeThickness = 1.9,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Width = 22,
                Height = 22,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        stack.Children.Add(plusCircle);
        stack.Children.Add(new TextBlock
        {
            Text = "Добавить",
            FontSize = 10.5,
            Foreground = new SolidColorBrush(MediaColor.FromRgb(161, 161, 170)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        });
        tile.Child = stack;

        tile.MouseEnter += (_, _) => tile.Background = new SolidColorBrush(MediaColor.FromRgb(36, 38, 48));
        tile.MouseLeave += (_, _) => tile.Background = MediaBrushes.Transparent;
        tile.MouseLeftButtonUp += (_, _) => ToggleInlineAppSearch();

        return tile;
    }

    private void PromptAddPinnedApp()
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

            FlashIslandBorder(MediaColor.FromRgb(34, 197, 94));
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

        var sysRoot = IOPath.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        var sysDrive = _drives.FirstOrDefault(d =>
            d.RootPath.Equals(sysRoot, StringComparison.OrdinalIgnoreCase) ||
            d.Name.StartsWith("C", StringComparison.OrdinalIgnoreCase));
        if (sysDrive != null && sysDrive.FreeGb < 5.0 && (DateTime.UtcNow - _lastDiskAlertAt).TotalMinutes >= 30)
        {
            _lastDiskAlertAt = DateTime.UtcNow;
            ShowToast("💾", $"Мало места на {sysDrive.Name}: {sysDrive.FreeGb:F1} ГБ", "Очистка", () => OpenTab("Cleaner"), durationMs: 5200);
        }

        var procs = _sysMonitor.GetTopProcesses(3);
        _topProcesses.Clear();
        foreach (var p in procs) _topProcesses.Add(p);

        var dl = _sysMonitor.GetRecentDownloads(4);
        _recentDownloads.Clear();
        foreach (var f in dl) _recentDownloads.Add(f);

        UpdateSparklinesUi();

        if (_isExpanded && TabSystem.IsChecked == true)
            ScheduleExpandedResize(animate: true);
    }

    private void PushSparklineSample(Queue<double> queue, double value)
    {
        if (queue.Count >= 15) queue.Dequeue();
        queue.Enqueue(Math.Clamp(value, 0.0, 100.0));
        if (_isExpanded && TabSystem.IsChecked == true)
            UpdateSparklinesUi();
    }

    private void UpdateSparklinesUi()
    {
        ApplySparklineAndArea(CpuSparkline, CpuSparkPolygon, _cpuHistory, 128.0, 38.0);
        ApplySparklineAndArea(GpuSparkline, GpuSparkPolygon, _gpuHistory, 128.0, 38.0);
        ApplySparklineAndArea(RamSparkline, RamSparkPolygon, _ramHistory, 128.0, 38.0);
        ApplySparklinePoints(NetSparkline, _netHistory, 110.0, 22.0);
    }

    private static void ApplySparklineAndArea(Polyline line, Polygon area, Queue<double> samples, double width, double height)
    {
        var pts = new PointCollection();
        var areaPts = new PointCollection();
        int n = samples.Count;
        if (n == 0)
        {
            line.Points = pts;
            area.Points = areaPts;
            return;
        }
        int i = 0;
        double pad = 2.0;
        double usableH = Math.Max(4.0, height - pad * 2.0);
        foreach (double val in samples)
        {
            double x = n == 1 ? width : i * (width / (n - 1));
            double y = (height - pad) - (Math.Clamp(val, 0.0, 100.0) / 100.0) * usableH;
            pts.Add(new MediaPoint(x, y));
            i++;
        }
        line.Points = pts;

        areaPts.Add(new MediaPoint(0, height));
        foreach (var p in pts)
            areaPts.Add(p);
        areaPts.Add(new MediaPoint(n == 1 ? width : pts[^1].X, height));
        area.Points = areaPts;
    }

    private static void ApplySparklinePoints(Polyline line, Queue<double> samples, double width = 62.0, double height = 18.0)
    {
        var pts = new PointCollection();
        int n = samples.Count;
        if (n == 0)
        {
            line.Points = pts;
            return;
        }
        int i = 0;
        foreach (double val in samples)
        {
            double x = n == 1 ? width : i * (width / (n - 1));
            double y = height - (Math.Clamp(val, 0.0, 100.0) / 100.0) * Math.Max(4.0, height - 2.0);
            pts.Add(new MediaPoint(x, y));
            i++;
        }
        line.Points = pts;
    }

    private void InitDownloadsWatcher()
    {
        try
        {
            string dlDir = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (!Directory.Exists(dlDir)) return;

            _downloadsWatcher = new FileSystemWatcher(dlDir, "*.*")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };

            void HandleDownloadPath(string fullPath)
            {
                if (string.IsNullOrWhiteSpace(fullPath)) return;
                string ext = IOPath.GetExtension(fullPath).ToLowerInvariant();
                if (ext is ".tmp" or ".crdownload" or ".part" or ".opdownload" or ".download" or ".partial" or ".!ut" or ".bc!" or ".filepart" or ".idm" or "") return;
                string name = IOPath.GetFileName(fullPath);
                if (string.IsNullOrEmpty(name) || name.StartsWith("~") || name.StartsWith("Unconfirmed ", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".Zone.Identifier", StringComparison.OrdinalIgnoreCase))
                    return;

                _ = VerifyAndAnnounceDownloadAsync(fullPath, name);
            }

            _downloadsWatcher.Renamed += (_, e) => HandleDownloadPath(e.FullPath);
            _downloadsWatcher.Created += (_, e) => HandleDownloadPath(e.FullPath);
        }
        catch (Exception ex)
        {
            LoggerService.Error("Downloads watcher init error", ex);
        }
    }

    private async Task VerifyAndAnnounceDownloadAsync(string fullPath, string name)
    {
        try
        {
            long readyLength = 0;
            for (int attempt = 0; attempt < 6; attempt++)
            {
                await Task.Delay(attempt == 0 ? 350 : 450);
                if (!File.Exists(fullPath)) return;
                try
                {
                    var fi = new FileInfo(fullPath);
                    if (fi.Length <= 0) continue;
                    using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    readyLength = fi.Length;
                    break;
                }
                catch (IOException)
                {
                    // File is still being finalized/flushed by browser
                }
            }
            if (readyLength <= 0) return;

            string sizeFormatted = CleanupCategory.FormatBytes(readyLength);
            await Dispatcher.InvokeAsync(() =>
            {
                if (string.Equals(_lastDownloadToastName, fullPath, StringComparison.OrdinalIgnoreCase) &&
                    (DateTime.UtcNow - _lastDownloadToastAt).TotalSeconds < 6)
                    return;

                _lastDownloadToastName = fullPath;
                _lastDownloadToastAt = DateTime.UtcNow;

                AddNotification(
                    icon: "⬇",
                    appTag: "Загрузки",
                    title: name,
                    subtitle: $"Загрузка завершена · {sizeFormatted}",
                    actionLabel: "Открыть",
                    onAction: () => OpenShellTarget(fullPath),
                    targetPath: fullPath);

                ShowToast(
                    icon: "⬇",
                    message: $"{name} · {sizeFormatted}",
                    actionLabel: "Открыть",
                    onAction: () => OpenShellTarget(fullPath),
                    durationMs: 8000,
                    title: "Загрузка завершена",
                    showCountdown: true);

                var dl = _sysMonitor.GetRecentDownloads(4);
                _recentDownloads.Clear();
                foreach (var f in dl) _recentDownloads.Add(f);
            });
        }
        catch { }
    }

    private async void SeedInitialNotifications()
    {
        try
        {
            var recent = await Task.Run(() => _sysMonitor.GetRecentDownloads(3));
            if (recent.Count == 0) return;
            await Dispatcher.InvokeAsync(() =>
            {
                foreach (var item in recent)
                {
                    if (_notifications.Any(n => string.Equals(n.TargetPath, item.FullPath, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    _notifications.Add(new NotificationCenterItem
                    {
                        Icon = "⬇",
                        AppTag = "Загрузки",
                        Title = item.Name,
                        Subtitle = $"Скачано · {item.SizeFormatted}",
                        TimeLabel = item.TimeLabel,
                        ActionLabel = "Открыть",
                        TargetPath = item.FullPath,
                        OnAction = () => OpenShellTarget(item.FullPath)
                    });
                }
                SyncNotificationCenterUi();
            });
        }
        catch { }
    }

    private void AddNotification(
        string icon,
        string appTag,
        string title,
        string subtitle,
        string? actionLabel = null,
        Action? onAction = null,
        string? targetPath = null)
    {
        var existing = _notifications.FirstOrDefault(n =>
            (!string.IsNullOrEmpty(targetPath) && string.Equals(n.TargetPath, targetPath, StringComparison.OrdinalIgnoreCase)) ||
            (string.Equals(n.AppTag, appTag, StringComparison.OrdinalIgnoreCase) &&
             string.Equals(n.Title, title, StringComparison.OrdinalIgnoreCase)));
        if (existing != null)
            _notifications.Remove(existing);

        _notifications.Insert(0, new NotificationCenterItem
        {
            Icon = icon,
            AppTag = appTag,
            Title = title,
            Subtitle = subtitle,
            TimeLabel = DateTime.Now.ToString("HH:mm"),
            ActionLabel = actionLabel,
            TargetPath = targetPath,
            OnAction = onAction
        });

        while (_notifications.Count > 25)
            _notifications.RemoveAt(_notifications.Count - 1);

        SyncNotificationCenterUi();
    }

    private void SyncNotificationCenterUi()
    {
        int count = _notifications.Count;
        NotifCountText.Text = count.ToString();
        NotifCountBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearNotificationsBtn.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NotifEmptyText.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (_isExpanded && TabApps.IsChecked == true)
            ScheduleExpandedResize(animate: true);
    }

    private void NotifAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is NotificationCenterItem item)
        {
            if (item.OnAction != null)
                item.OnAction();
            else if (!string.IsNullOrEmpty(item.TargetPath))
                OpenShellTarget(item.TargetPath);
        }
    }

    private void NotifOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is NotificationCenterItem item && !string.IsNullOrEmpty(item.TargetPath))
        {
            try
            {
                if (File.Exists(item.TargetPath))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.TargetPath}\"") { UseShellExecute = true });
                else if (Directory.Exists(item.TargetPath))
                    OpenShellTarget(item.TargetPath);
            }
            catch { }
        }
    }

    private void NotifDismiss_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is NotificationCenterItem item)
        {
            _notifications.Remove(item);
            SyncNotificationCenterUi();
        }
    }

    private void ClearNotifications_Click(object sender, RoutedEventArgs e)
    {
        _notifications.Clear();
        SyncNotificationCenterUi();
    }

    private void OpenDownloadsFolder_Click(object sender, RoutedEventArgs e)
    {
        string dlDir = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (Directory.Exists(dlDir))
            OpenShellTarget(dlDir);
    }

    private void OnClipboardTextCopied(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        string trimmed = text.Trim();

        if (_isExpanded && TabUtils.IsChecked == true)
            ScheduleExpandedResize(animate: true);

        // 1. HEX Color Swatch (#RRGGBB)
        if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, "^#[0-9a-fA-F]{6}$"))
        {
            byte r = Convert.ToByte(trimmed.Substring(1, 2), 16);
            byte g = Convert.ToByte(trimmed.Substring(3, 2), 16);
            byte b = Convert.ToByte(trimmed.Substring(5, 2), 16);
            var col = MediaColor.FromRgb(r, g, b);
            ShowToast("🎨", _isStreamPrivacyActive ? "Цвет скопирован" : $"Цвет: {trimmed.ToUpperInvariant()}", swatchColor: col, durationMs: 3000);
            return;
        }

        // 2. HTTP/HTTPS Link Quick Action
        if (!trimmed.Contains('\n') &&
            (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
             trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) &&
            Uri.TryCreate(trimmed, UriKind.Absolute, out _))
        {
            string label = _isStreamPrivacyActive
                ? "Ссылка скрыта (Стрим-щит)"
                : (trimmed.Length > 28 ? trimmed[..28] + "…" : trimmed);
            ShowToast("🔗", label, "Открыть", () => OpenShellTarget(trimmed), durationMs: 3600);
            return;
        }

        // 3. Quick Math or Currency conversion toast (ignoring plain numbers like "42")
        if (trimmed.Length <= 64 && !trimmed.Contains('\n') &&
            !double.TryParse(trimmed.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _) &&
            HardwareAndUtilityService.TryEvaluateExpression(trimmed, _settings.PreferredCurrency, out var evalResult))
        {
            ShowToast("🧮", $"{trimmed} = {evalResult}", "Копировать", () =>
            {
                try { System.Windows.Clipboard.SetText(evalResult); } catch { }
            }, durationMs: 3500);
        }
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

    private bool _isUpdatingAppMixer;

    private void ToggleAppMixer_Click(object sender, RoutedEventArgs e)
    {
        _isAppMixerExpanded = !_isAppMixerExpanded;
        AppMixerList.Visibility = _isAppMixerExpanded ? Visibility.Visible : Visibility.Collapsed;
        ToggleAppMixerBtn.Content = _isAppMixerExpanded ? "Микшер ▴" : "Микшер ▾";
        if (_isAppMixerExpanded)
        {
            _isUpdatingAppMixer = true;
            var sessions = CoreAudioService.GetAudioRenderSessions();
            _audioSessions.Clear();
            foreach (var s in sessions) _audioSessions.Add(s);
            _isUpdatingAppMixer = false;
        }
        ScheduleExpandedResize(animate: true);
    }

    private void BindGameAudioDevice_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_activeDockProcessKey)) return;
        string devId = CoreAudioService.GetDefaultPlaybackDeviceId();
        if (string.IsNullOrEmpty(devId)) return;

        _settings.GameAudioProfiles[_activeDockProcessKey] = devId;
        SettingsService.Save(_settings);
        ShowToast("🎮", $"Аудио закреплено за {_activeDockProcessKey}", durationMs: 2600);
    }

    private void AppSessionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingAppMixer) return;
        if (sender is Slider sl && sl.Tag is int pid)
            CoreAudioService.SetAppSessionVolume(pid, (int)Math.Round(e.NewValue));
    }

    private void AppSessionMute_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is int pid)
        {
            CoreAudioService.ToggleAppSessionMute(pid);
            _isUpdatingAppMixer = true;
            var sessions = CoreAudioService.GetAudioRenderSessions();
            _audioSessions.Clear();
            foreach (var s in sessions) _audioSessions.Add(s);
            _isUpdatingAppMixer = false;
        }
    }

    private void ToggleStreamShield_Click(object sender, RoutedEventArgs e)
    {
        _isStreamPrivacyManual = !_isStreamPrivacyManual;
        bool autoRec = _settings.StreamPrivacyAuto && HardwareAndUtilityService.IsRecordingSoftwareActive();
        _isStreamPrivacyActive = _isStreamPrivacyManual || autoRec;
        StreamRecBadge.Visibility = (_isStreamPrivacyActive && !_isSuperMiniIdle) ? Visibility.Visible : Visibility.Collapsed;
        SyncStreamShieldUi();
        _ = RefreshMediaAndDevicesAsync(false);
        ShowToast("🛡", _isStreamPrivacyActive ? "Стрим-щит: Вкл (треки скрыты)" : "Стрим-щит: Выкл", durationMs: 2200);
    }

    private void SyncStreamShieldUi()
    {
        StreamShieldBtn.Content = _isStreamPrivacyActive ? "🛡 Щит: Вкл" : "🛡 Щит: Выкл";
        StreamShieldBtn.Background = _isStreamPrivacyActive
            ? new SolidColorBrush(MediaColor.FromRgb(127, 29, 29))
            : new SolidColorBrush(MediaColor.FromRgb(31, 32, 40));
    }

    private void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingBrightnessSlider || !IsLoaded) return;
        int val = (int)Math.Round(e.NewValue);
        BrightnessLabelText.Text = $"☀ {val}%";
        HardwareAndUtilityService.SetMonitorBrightnessPercent(_ownHwnd, val);
    }

    private void NightLightBtn_Click(object sender, RoutedEventArgs e)
        => HardwareAndUtilityService.OpenNightLightSettings();

    private void ReplayClipBtn_Click(object sender, RoutedEventArgs e)
    {
        string mode = HardwareAndUtilityService.TriggerReplayClip();
        string clipsDir = ScreenRecorderService.GetReplayClipsDirectory();
        ShowToast("🎬", $"{mode} → {ScreenRecorderService.ShortFolderLabel(clipsDir)}", "Папка", () => OpenShellTarget(clipsDir), durationMs: 4200);
    }

    private int GetSelectedRecordMonitorIndex()
    {
        var screens = Forms.Screen.AllScreens;
        if (screens.Length == 0) return 0;
        if (_settings.PreferredRecordMonitorIndex >= 0 && _settings.PreferredRecordMonitorIndex < screens.Length)
            return _settings.PreferredRecordMonitorIndex;
        if (_settings.MonitorIndex >= 0 && _settings.MonitorIndex < screens.Length)
            return _settings.MonitorIndex;
        int primary = Array.IndexOf(screens, Forms.Screen.PrimaryScreen);
        return primary >= 0 ? primary : 0;
    }

    private void SyncRecordingUi()
    {
        bool rec = _screenRecorder.IsRecording;
        var el = _screenRecorder.Elapsed;
        string rFmt = el.TotalHours >= 1 ? el.ToString(@"h\:mm\:ss") : el.ToString(@"mm\:ss");

        ScreenRecordBadge.Visibility = (rec && !_isSuperMiniIdle) ? Visibility.Visible : Visibility.Collapsed;
        SideRecordBadge.Visibility = rec ? Visibility.Visible : Visibility.Collapsed;
        ScreenRecordTimerText.Text = $"● REC {rFmt}";
        SideRecordTimerText.Text = $"● {rFmt}";

        if (rec)
        {
            var redBrush = new SolidColorBrush(MediaColor.FromRgb(239, 68, 68));
            StatusDot.Fill = redBrush;
            SideStatusDot.Fill = redBrush;
            ExpandedStatusDot.Fill = redBrush;

            ScreenRecordBtn.Content = $"⏹ Стоп ({rFmt})";
            ScreenRecordBtn.Background = new SolidColorBrush(MediaColor.FromRgb(153, 27, 27));
            HomeRecordCircle.Background = new SolidColorBrush(MediaColor.FromRgb(127, 29, 29));
            HomeRecordCircle.BorderBrush = new SolidColorBrush(MediaColor.FromRgb(239, 68, 68));
            HomeRecordCameraPath.Stroke = MediaBrushes.White;
            HomeRecordLabel.Text = rFmt;
        }
        else
        {
            StatusDot.SetResourceReference(Shape.FillProperty, "AccentBrightBrush");
            SideStatusDot.SetResourceReference(Shape.FillProperty, "AccentBrightBrush");
            ExpandedStatusDot.SetResourceReference(Shape.FillProperty, "AccentBrightBrush");
            StatusDot.Opacity = 1.0;
            ExpandedStatusDot.Opacity = 1.0;

            ScreenRecordBtn.Content = "🔴 Запись экрана";
            ScreenRecordBtn.Background = new SolidColorBrush(MediaColor.FromRgb(31, 32, 40));
            HomeRecordCircle.Background = new SolidColorBrush(MediaColor.FromRgb(44, 22, 21));
            HomeRecordCircle.BorderBrush = new SolidColorBrush(MediaColor.FromRgb(74, 33, 31));
            HomeRecordCameraPath.Stroke = new SolidColorBrush(MediaColor.FromRgb(239, 68, 68));
            HomeRecordLabel.Text = "Запись экрана";
        }

        var screens = Forms.Screen.AllScreens;
        if (screens.Length > 1)
        {
            int idx = GetSelectedRecordMonitorIndex();
            var b = screens[idx].Bounds;
            RecordMonitorBtn.Content = $"🖥 Экран {idx + 1}";
            RecordMonitorBtn.ToolTip = $"Монитор {idx + 1} ({b.Width}×{b.Height}). Нажмите для переключения";
            RecordMonitorBtn.IsEnabled = !rec;
            RecordMonitorBtn.Visibility = Visibility.Visible;
        }
        else
        {
            RecordMonitorBtn.Visibility = Visibility.Collapsed;
        }

        string recLabel = ScreenRecorderService.ShortFolderLabel(ScreenRecorderService.RecordingsDirectory);
        string clipsLabel = ScreenRecorderService.ShortFolderLabel(ScreenRecorderService.GetReplayClipsDirectory());
        RecordSaveHintText.Text = $"Запись → {recLabel}  ·  Клип 30с → {clipsLabel}";

        ApplyCurrentIslandBorder();
        if (!_isExpanded) UpdateCompactWidth(animate: false);
    }

    private void ToggleScreenRecord_Click(object sender, RoutedEventArgs e)
    {
        if (_screenRecorder.IsRecording)
        {
            string? saved = _screenRecorder.Stop();
            SyncRecordingUi();
            if (!string.IsNullOrEmpty(saved))
            {
                string fileName = IOPath.GetFileName(saved);
                AddNotification("🔴", "Запись экрана", fileName, "Видео сохранено", "Открыть", () => OpenShellTarget(saved), saved);
                ShowToast("✅", $"Сохранено: {fileName}", "Открыть", () => OpenShellTarget(saved), durationMs: 5200);
            }
            else
            {
                ShowToast("⏹", "Запись экрана остановлена", durationMs: 2200);
            }
            return;
        }

        int monIdx = GetSelectedRecordMonitorIndex();
        if (_screenRecorder.Start(monIdx, out var err))
        {
            SyncRecordingUi();
            string recLabel = ScreenRecorderService.ShortFolderLabel(ScreenRecorderService.RecordingsDirectory);
            ShowToast("🔴", $"Запись экрана {monIdx + 1} → {recLabel}", durationMs: 3200);
        }
        else
        {
            SyncRecordingUi();
            ShowToast("⚠", err ?? "Не удалось начать запись", durationMs: 3600);
        }
    }

    private void ScreenRecordBadge_Click(object sender, MouseButtonEventArgs e)
    {
        ToggleScreenRecord_Click(sender, e);
        e.Handled = true;
    }

    private void CycleRecordMonitor_Click(object sender, RoutedEventArgs e)
    {
        var screens = Forms.Screen.AllScreens;
        if (screens.Length <= 1 || _screenRecorder.IsRecording) return;

        int cur = GetSelectedRecordMonitorIndex();
        int next = (cur + 1) % screens.Length;
        _settings.PreferredRecordMonitorIndex = next;
        SettingsService.Save(_settings);
        SyncRecordingUi();
        var b = screens[next].Bounds;
        ShowToast("🖥", $"Монитор записи: Экран {next + 1} ({b.Width}×{b.Height})", durationMs: 2200);
    }

    private void OpenRecordingsFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Directory.CreateDirectory(ScreenRecorderService.RecordingsDirectory); } catch { }
        OpenShellTarget(ScreenRecorderService.RecordingsDirectory);
    }

    private void OpenReplayClipsFolder_Click(object sender, RoutedEventArgs e)
        => OpenShellTarget(ScreenRecorderService.GetReplayClipsDirectory());

    private void StartTimerPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && int.TryParse(fe.Tag?.ToString(), out int mins))
        {
            _countdownEndUtc = DateTime.UtcNow.AddMinutes(mins);
            var ts = TimeSpan.FromMinutes(mins);
            string fmt = ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"mm\:ss");
            CompactTimerText.Text = $"🌙 {fmt}";
            CompactTimerPill.Visibility = Visibility.Visible;
            TimerStatusText.Text = fmt;
            string actionDesc = _timerShutdownOnFinish ? "выключение ПК" : "сон ПК";
            ShowToast("🌙", $"Авто-стоп ({actionDesc}) через {mins} мин.");
        }
    }

    private void StopTimer_Click(object sender, RoutedEventArgs e)
    {
        _countdownEndUtc = null;
        CompactTimerPill.Visibility = Visibility.Collapsed;
        TimerStatusText.Text = "Выкл.";
    }

    private void TimerActionMode_Click(object sender, RoutedEventArgs e)
    {
        _timerShutdownOnFinish = !_timerShutdownOnFinish;
        TimerActionModeBtn.Content = _timerShutdownOnFinish ? "⏻ Выкл" : "💤 Сон";
        ShowToast(_timerShutdownOnFinish ? "⏻" : "💤",
            _timerShutdownOnFinish ? "Ночной авто-стоп: выключить ПК" : "Ночной авто-стоп: усыпить ПК",
            durationMs: 2200);
    }

    private void ToggleDnd_Click(object sender, RoutedEventArgs e)
    {
        _dndAutoOffEndUtc = null;
        bool enabled = QuickToolsService.ToggleDoNotDisturb();
        SyncQuickToolStates();
        ShowToast(enabled ? "🔕" : "🔔", enabled ? "Не беспокоить: Вкл" : "Не беспокоить: Выкл", durationMs: 2200);
    }

    private void SetDndMinutes_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && int.TryParse(fe.Tag?.ToString(), out int mins))
        {
            if (mins <= 0)
            {
                _dndAutoOffEndUtc = null;
                QuickToolsService.SetDoNotDisturbEnabled(false);
                SyncQuickToolStates();
                ShowToast("🔔", "Не беспокоить: Выключен", durationMs: 2000);
            }
            else
            {
                _dndAutoOffEndUtc = DateTime.UtcNow.AddMinutes(mins);
                QuickToolsService.SetDoNotDisturbEnabled(true);
                SyncQuickToolStates();
                ShowToast("🔕", $"Не беспокоить на {mins} мин", durationMs: 2200);
            }
        }
    }

    private void TogglePowerMode_Click(object sender, RoutedEventArgs e)
    {
        bool highPerf = QuickToolsService.ToggleHighPerformancePlan();
        SyncQuickToolStates();
        ShowToast(highPerf ? "🚀" : "🌿", highPerf ? "Питание: Высокая произв." : "Питание: Сбалансированный", durationMs: 2400);
    }

    private void SyncQuickToolStates()
    {
        bool dnd = QuickToolsService.IsDoNotDisturbEnabled();
        DndIconText.Text = dnd ? "🔕" : "🔔";
        if (_dndAutoOffEndUtc.HasValue)
        {
            var left = _dndAutoOffEndUtc.Value - DateTime.UtcNow;
            DndLabelText.Text = left > TimeSpan.Zero ? $"Вкл · {(int)Math.Ceiling(left.TotalMinutes)}м" : "Вкл";
        }
        else
        {
            DndLabelText.Text = dnd ? "Вкл" : "Выкл";
        }
        DndToggleBtn.Background = dnd
            ? new SolidColorBrush(MediaColor.FromRgb(49, 46, 129))
            : new SolidColorBrush(MediaColor.FromRgb(31, 32, 40));

        bool highPerf = QuickToolsService.IsHighPerformancePlanActive();
        PowerModeLabelText.Text = highPerf ? "⚡ Питание: Макс" : "⚡ Питание: Баланс";
        PowerModeBtn.Background = highPerf
            ? new SolidColorBrush(MediaColor.FromRgb(20, 83, 45))
            : new SolidColorBrush(MediaColor.FromRgb(31, 32, 40));
    }

    private async Task CheckUpdatesOnStartupAsync()
    {
        await Task.Delay(3500);
        await CheckForUpdatesAsync(manual: false);
    }

    public async Task CheckForUpdatesAsync(bool manual)
    {
        var (hasUpdate, latestTag, releaseUrl, zipUrl) = await QuickToolsService.CheckForGithubUpdateAsync();
        if (hasUpdate && !string.IsNullOrEmpty(latestTag))
        {
            _pendingUpdateReleaseUrl = releaseUrl;
            _pendingUpdateZipUrl = zipUrl;
            UpdateAvailableText.Text = $"Доступно обновление {latestTag}";
            if (UpdateAvailableBanner.Visibility != Visibility.Visible)
            {
                UpdateAvailableBanner.Visibility = Visibility.Visible;
                if (_isExpanded) ScheduleExpandedResize(animate: false);
            }

            if (manual || !string.Equals(latestTag, _lastNotifiedUpdateTag, StringComparison.OrdinalIgnoreCase))
            {
                _lastNotifiedUpdateTag = latestTag;
                AddNotification("⬆", "Обновление", $"Доступна версия {latestTag}", "Нажмите, чтобы установить", "Обновить", () => _ = InstallPendingUpdateAsync());
                ShowToast(
                    "⬆",
                    $"Доступно обновление {latestTag}",
                    actionLabel: "Обновить",
                    onAction: () => _ = InstallPendingUpdateAsync(),
                    durationMs: 7500);
            }
        }
        else
        {
            if (UpdateAvailableBanner.Visibility != Visibility.Collapsed)
            {
                UpdateAvailableBanner.Visibility = Visibility.Collapsed;
                if (_isExpanded) ScheduleExpandedResize(animate: false);
            }
            if (manual)
                ShowToast("✓", "Установлена актуальная версия", durationMs: 2600);
        }
    }

    private void InstallUpdateBtn_Click(object sender, RoutedEventArgs e) => _ = InstallPendingUpdateAsync();

    private async Task InstallPendingUpdateAsync()
    {
        if (_isInstallingUpdate) return;
        _isInstallingUpdate = true;
        InstallUpdateBtn.IsEnabled = false;
        InstallUpdateBtn.Content = "Загрузка...";
        ShowToast("⏳", "Скачиваю и устанавливаю обновление...", durationMs: 6000);

        try
        {
            bool started = await QuickToolsService.DownloadAndApplyGithubUpdateAsync(_pendingUpdateZipUrl);
            if (started)
            {
                (System.Windows.Application.Current as App)?.ShutdownApplication();
                return;
            }

            ShowToast("⚠", "Не удалось скачать автоматически", actionLabel: "GitHub", onAction: () =>
            {
                if (!string.IsNullOrWhiteSpace(_pendingUpdateReleaseUrl))
                    OpenShellTarget(_pendingUpdateReleaseUrl);
            });
        }
        finally
        {
            _isInstallingUpdate = false;
            InstallUpdateBtn.IsEnabled = true;
            InstallUpdateBtn.Content = "Обновить";
        }
    }

    private void ToggleDesktopIcons_Click(object sender, RoutedEventArgs e)
    {
        bool ok = QuickToolsService.ToggleDesktopIcons();
        ShowToast("🙈", ok ? "Иконки стола переключены" : "Не удалось переключить");
    }

    private void LockPc_Click(object sender, RoutedEventArgs e) => QuickToolsService.LockPc();
    private void SleepPc_Click(object sender, RoutedEventArgs e) => QuickToolsService.SleepPc();

    #endregion

    #region Utils Tab ("Буфер": Calc, QR, Drag&Drop Shelf, Clipboard)

    private void RefreshUtilsTab()
    {
        SyncDropShelfUi();
    }

    private void SyncDropShelfUi()
    {
        DropShelfEmptyText.Visibility = _dropShelfItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearShelfBtn.Visibility = _dropShelfItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void QuickCalcBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string text = QuickCalcBox.Text ?? string.Empty;
        QuickCalcPlaceholder.Visibility = string.IsNullOrEmpty(text) ? Visibility.Visible : Visibility.Collapsed;

        if (!string.IsNullOrWhiteSpace(text) &&
            HardwareAndUtilityService.TryEvaluateExpression(text, _settings.PreferredCurrency, out var res))
        {
            _lastCalcResult = res;
            QuickCalcResultText.Text = $"= {res}";
            QuickCalcResultText.Visibility = Visibility.Visible;
            CopyCalcResultBtn.Visibility = Visibility.Visible;
        }
        else
        {
            _lastCalcResult = string.Empty;
            QuickCalcResultText.Visibility = Visibility.Collapsed;
            CopyCalcResultBtn.Visibility = Visibility.Collapsed;
        }

        if (_isExpanded && TabUtils.IsChecked == true)
            ScheduleExpandedResize(animate: false);
    }

    private void CopyCalcResult_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_lastCalcResult)) return;
        try
        {
            System.Windows.Clipboard.SetText(_lastCalcResult);
            ShowToast("🧮", $"Скопировано: {_lastCalcResult}", durationMs: 1800);
        }
        catch { }
    }

    private void ShowQrCode(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            ShowToast("📱", "Нет текста для QR-кода", durationMs: 2000);
            return;
        }

        string trimmed = text.Trim();
        var bmp = QrCodeGenerator.RenderBitmap(trimmed, 5);
        if (bmp == null)
        {
            ShowToast("⚠", "Текст слишком длинный для QR", durationMs: 2400);
            return;
        }

        QrPreviewImage.Source = bmp;
        QrSourceText.Text = _isStreamPrivacyActive ? "Скрыто (Стрим-щит)" : trimmed;
        QrCodeCard.Visibility = Visibility.Visible;
        OpenTab("Utils");
        ScheduleExpandedResize(animate: true);
    }

    private void CloseQrCard_Click(object sender, RoutedEventArgs e)
    {
        QrCodeCard.Visibility = Visibility.Collapsed;
        ScheduleExpandedResize(animate: true);
    }

    private void QrFromClipboard_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string text = System.Windows.Clipboard.ContainsText()
                ? System.Windows.Clipboard.GetText()
                : string.Empty;
            ShowQrCode(text);
        }
        catch
        {
            ShowToast("⚠", "Не удалось прочитать буфер", durationMs: 2000);
        }
    }

    private void CleanClipboardFormatting_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (System.Windows.Clipboard.ContainsText())
            {
                string plain = System.Windows.Clipboard.GetText();
                System.Windows.Clipboard.SetText(plain);
                ShowToast("🧹", "Форматирование текста очищено", durationMs: 2000);
            }
            else
            {
                ShowToast("📋", "В буфере нет текста", durationMs: 1800);
            }
        }
        catch { }
    }

    private void ClearClipboardHistory_Click(object sender, RoutedEventArgs e)
    {
        _clipboardService.ClearUnpinned();
        ScheduleExpandedResize(animate: true);
    }

    private void ClipboardItemCopy_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is ClipboardEntry entry)
        {
            _clipboardService.CopyEntry(entry.Text);
            ShowToast("📋", "Скопировано в буфер", durationMs: 1600);
        }
    }

    private void ClipboardItemQr_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is ClipboardEntry entry)
            ShowQrCode(entry.Text);
    }

    private void ClipboardItemPin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is ClipboardEntry entry)
        {
            _clipboardService.TogglePin(entry);
            _settings.PinnedSnippets = _clipboardService.GetPinnedEntries();
            SettingsService.Save(_settings);
            ScheduleExpandedResize(animate: true);
        }
    }

    private void ShelfItem_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is DropShelfItem item)
        {
            _shelfDragStart = e.GetPosition(null);
            _shelfDragItem = item;

            if (e.ClickCount == 2)
            {
                _shelfDragItem = null;
                if (item.IsFile) OpenShellTarget(item.FullPath);
                else
                {
                    try { System.Windows.Clipboard.SetText(item.FullPath); ShowToast("📋", "Текст скопирован", durationMs: 1600); } catch { }
                }
            }
        }
    }

    private void ShelfItem_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _shelfDragItem == null) return;
        MediaPoint pos = e.GetPosition(null);
        Vector diff = _shelfDragStart - pos;
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var item = _shelfDragItem;
        _shelfDragItem = null;

        try
        {
            var dataObj = item.IsFile && (File.Exists(item.FullPath) || Directory.Exists(item.FullPath))
                ? new System.Windows.DataObject(System.Windows.DataFormats.FileDrop, new[] { item.FullPath })
                : new System.Windows.DataObject(System.Windows.DataFormats.UnicodeText, item.FullPath);
            DragDrop.DoDragDrop(this, dataObj, System.Windows.DragDropEffects.Copy);
        }
        catch { }
    }

    private void RemoveShelfItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is DropShelfItem item)
        {
            _dropShelfItems.Remove(item);
            SyncDropShelfUi();
            ScheduleExpandedResize(animate: true);
        }
    }

    private void ClearDropShelf_Click(object sender, RoutedEventArgs e)
    {
        _dropShelfItems.Clear();
        SyncDropShelfUi();
        ScheduleExpandedResize(animate: true);
    }

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
        UtilsPanel.Visibility = TabUtils.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SystemPanel.Visibility = TabSystem.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ScreenTimePanel.Visibility = TabScreenTime.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        CleanerPanel.Visibility = TabCleaner.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        if (TabApps.IsChecked == true)
        {
            SyncHomeAppsModeUi();
            SyncNotificationCenterUi();
            _ = RefreshMediaAndDevicesAsync(forceBluetooth: false);
        }
        if (TabRemote.IsChecked == true) _ = RefreshMediaAndDevicesAsync(forceBluetooth: false);
        if (TabUtils.IsChecked == true) SyncDropShelfUi();
        if (TabSystem.IsChecked == true)
        {
            RefreshDrives();
            SyncQuickToolStates();
            RefreshSystemControlCenterUi(HardwareAndUtilityService.GetConnectedGamepads());
            _ = RefreshMediaAndDevicesAsync(forceBluetooth: true);
        }
        if (TabScreenTime.IsChecked == true) RefreshScreenTimeUi();
        if (TabCleaner.IsChecked == true) _ = RefreshCleanupScanAsync();

        AnimateTabBodyEntrance();
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
            case "Utils":
            case "Clipboard": TabUtils.IsChecked = true; break;
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
                _clipboardService.UpdateConfig(_settings.EnableClipboardHistory, _settings.ClipboardMaxItems);
                ApplySettingsVisuals();
                RebuildAppTiles();
                _ = RefreshWeatherAsync();
                ScheduleExpandedResize(animate: true);
                if (!_isExpanded) UpdateCompactWidth(animate: true);
            },
            onExitApp: () => (System.Windows.Application.Current as App)?.ShutdownApplication());
        win.ShowDialog();
    }

    #region Home Tab Actions (Atoll 1.4 Style: Region/Screen Shot, Screen Translator RU⇄EN, Color Picker, Quick Command, Dock/Grid Switch)

    private bool AddFileToDropShelf(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (_dropShelfItems.Any(x => string.Equals(x.FullPath, path, StringComparison.OrdinalIgnoreCase)))
            return false;

        _dropShelfItems.Insert(0, new DropShelfItem
        {
            Name = IOPath.GetFileName(path),
            FullPath = path,
            IsFile = true
        });
        while (_dropShelfItems.Count > 10)
            _dropShelfItems.RemoveAt(_dropShelfItems.Count - 1);
        SyncDropShelfUi();
        return true;
    }

    private void SyncHomeAppsModeUi()
    {
        bool dock = _settings.UseHomeAppsDock;
        HomeDockContainer.Visibility = Visibility.Visible;
        HomeDockScrollViewer.Visibility = dock ? Visibility.Visible : Visibility.Collapsed;
        HomeGridContainer.Visibility = dock ? Visibility.Collapsed : Visibility.Visible;

        HomeAppsModeIcon.Data = Geometry.Parse(dock
            ? "M 4,4 H 10 V 10 H 4 Z M 14,4 H 20 V 10 H 14 Z M 4,14 H 10 V 20 H 4 Z M 14,14 H 20 V 20 H 14 Z"
            : "M 3,8 H 21 V 16 H 3 Z M 7,12 H 9 M 11,12 H 13 M 15,12 H 17");
        HomeAppsModeToggleBtn.ToolTip = dock ? "Переключить на сетку" : "Переключить на док";
        HomeAppsModeToggleBtn.VerticalAlignment = dock ? VerticalAlignment.Center : VerticalAlignment.Top;
        HomeAppsModeToggleBtn.Margin = dock ? new Thickness(6, 0, 0, 4) : new Thickness(0, 2, 0, 0);
    }

    private void HomeAppsModeToggleBtn_Click(object sender, RoutedEventArgs e)
    {
        _settings.UseHomeAppsDock = !_settings.UseHomeAppsDock;
        SettingsService.Save(_settings);
        SyncHomeAppsModeUi();
        ScheduleExpandedResize(animate: true);
    }

    private void SyncTranslateDirUi()
    {
        string label = _settings.TranslateDirection switch
        {
            "EnToRu" => "EN → RU",
            "RuToEn" => "RU → EN",
            _ => "RU ⇄ EN"
        };
        HomeTranslateCircleBtn.ToolTip = $"Выделить область экрана → перевод ({label})";
        HomeTranslateSwapBtn.Content = _settings.TranslateDirection switch
        {
            "EnToRu" => "EN → RU",
            "RuToEn" => "RU → EN",
            _ => "Авто (RU ⇄ EN)"
        };
    }

    private async Task<T?> RunWithHiddenIslandAsync<T>(Func<T?> action)
    {
        bool wasPinned = _isPinned;
        _isPinned = true;
        Hide();
        await Task.Delay(140);
        try
        {
            return action();
        }
        finally
        {
            Show();
            SetAlwaysOnTop(_settings.AlwaysOnTop);
            _isPinned = wasPinned;
            SetExpanded(true);
            OpenTab("Apps");
        }
    }

    private async void CaptureRegion_Click(object sender, RoutedEventArgs e)
    {
        var cropped = await RunWithHiddenIslandAsync<System.Drawing.Bitmap?>(() =>
        {
            var overlay = new Views.ScreenSelectionOverlay("Выделите область для снимка · Esc / ПКМ — отмена");
            return overlay.ShowDialog() == true ? overlay.CroppedResult : null;
        });

        if (cropped == null) return;
        using (cropped)
        {
            var (path, _) = ScreenCaptureOcrService.SaveAndCopyBitmap(cropped, "Region");
            AddFileToDropShelf(path);
            AddNotification("📷", "Скриншот", IOPath.GetFileName(path), "Скопировано в буфер и сохранено", "Открыть", () => OpenShellTarget(path), path);
            FlashIslandBorder(MediaColor.FromRgb(56, 189, 248));
            ShowToast("📷", $"Область скопирована и сохранена: {IOPath.GetFileName(path)}", "Открыть", () => OpenShellTarget(path), durationMs: 3600);
        }
    }

    private async void CaptureFullScreen_Click(object sender, RoutedEventArgs e)
    {
        var activeScreen = GetTargetScreen();
        string? savedPath = await RunWithHiddenIslandAsync<string?>(() =>
        {
            var (path, _) = ScreenCaptureOcrService.CaptureScreenAndCopy(activeScreen);
            return path;
        });

        if (!string.IsNullOrEmpty(savedPath))
        {
            AddFileToDropShelf(savedPath);
            AddNotification("🖥", "Скриншот", IOPath.GetFileName(savedPath), "Весь экран скопирован и сохранён", "Открыть", () => OpenShellTarget(savedPath), savedPath);
            FlashIslandBorder(MediaColor.FromRgb(167, 139, 250));
            ShowToast("🖥", $"Скриншот в буфере и на полке: {IOPath.GetFileName(savedPath)}", "Открыть", () => OpenShellTarget(savedPath), durationMs: 3600);
        }
    }

    private async void ScreenTranslate_Click(object sender, RoutedEventArgs e)
    {
        var cropped = await RunWithHiddenIslandAsync<System.Drawing.Bitmap?>(() =>
        {
            var overlay = new Views.ScreenSelectionOverlay("Выделите текст (RU / EN) для распознавания и перевода · Esc — отмена");
            return overlay.ShowDialog() == true ? overlay.CroppedResult : null;
        });

        if (cropped == null) return;

        bool wasPinned = _isPinned;
        _isPinned = true;
        PinBtn.IsChecked = true;

        HomeTranslateOriginalText.Text = "Распознавание текста с экрана (RU/EN)...";
        HomeTranslateOutputBox.Text = "⏳ Перевод...";
        HomeTranslateCard.Visibility = Visibility.Visible;
        ScheduleExpandedResize(animate: true);

        try
        {
            string recognized;
            using (cropped)
            {
                recognized = await Task.Run(() => ScreenCaptureOcrService.RecognizeTextRuEnAsync(cropped));
            }

            if (string.IsNullOrWhiteSpace(recognized))
            {
                HomeTranslateOriginalText.Text = "Текст не обнаружен";
                HomeTranslateOutputBox.Text = "Попробуйте выделить область чуть крупнее и контрастнее.";
                ShowToast("⚠", "Текст на участке не распознан", durationMs: 2600);
                ScheduleExpandedResize(animate: true);
                return;
            }

            _lastTranslateOriginal = recognized;
            await TranslateAndDisplayAsync(recognized);
        }
        catch (Exception ex)
        {
            HomeTranslateOutputBox.Text = $"Ошибка: {ex.Message}";
            ScheduleExpandedResize(animate: true);
        }
        finally
        {
            if (!wasPinned && !_isPinned)
                PinBtn.IsChecked = false;
        }
    }

    private async Task TranslateAndDisplayAsync(string text)
    {
        HomeTranslateOriginalText.Text = text;
        HomeTranslateOutputBox.Text = "⏳ Перевод...";
        HomeTranslateCard.Visibility = Visibility.Visible;
        ScheduleExpandedResize(animate: true);

        var res = await ScreenCaptureOcrService.TranslateAutoRuEnAsync(text, _settings.TranslateDirection);
        if (res.Success)
        {
            _lastTranslatedText = res.TranslatedText;
            HomeTranslateOutputBox.Text = res.TranslatedText;
            try { System.Windows.Clipboard.SetText(res.TranslatedText); } catch { }
            FlashIslandBorder(MediaColor.FromRgb(251, 191, 36));
            ShowToast("🌐", $"Переведено ({res.SourceLang.ToUpper()} → {res.TargetLang.ToUpper()}) и скопировано", durationMs: 2800);
        }
        else
        {
            _lastTranslatedText = text;
            HomeTranslateOutputBox.Text = $"Не удалось перевести ({res.Error}). Распознанный текст скопирован:";
            try { System.Windows.Clipboard.SetText(text); } catch { }
            ShowToast("⚠", res.Error ?? "Ошибка перевода", durationMs: 3000);
        }
        ScheduleExpandedResize(animate: true);
    }

    private async void SwapTranslateDirection_Click(object sender, RoutedEventArgs e)
    {
        _settings.TranslateDirection = _settings.TranslateDirection switch
        {
            "Auto" => "EnToRu",
            "EnToRu" => "RuToEn",
            _ => "Auto"
        };
        SettingsService.Save(_settings);
        SyncTranslateDirUi();

        if (!string.IsNullOrWhiteSpace(_lastTranslateOriginal) && HomeTranslateCard.Visibility == Visibility.Visible)
            await TranslateAndDisplayAsync(_lastTranslateOriginal);
    }

    private void CopyTranslateResult_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_lastTranslatedText))
        {
            try { System.Windows.Clipboard.SetText(_lastTranslatedText); } catch { }
            ShowToast("📋", "Перевод скопирован в буфер", durationMs: 1800);
        }
    }

    private void CopyTranslateOriginal_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_lastTranslateOriginal))
        {
            try { System.Windows.Clipboard.SetText(_lastTranslateOriginal); } catch { }
            ShowToast("📋", "Оригинал скопирован в буфер", durationMs: 1800);
        }
    }

    private void CloseTranslateCard_Click(object sender, RoutedEventArgs e)
    {
        HomeTranslateCard.Visibility = Visibility.Collapsed;
        ScheduleExpandedResize(animate: true);
    }

    private async void ColorPicker_Click(object sender, RoutedEventArgs e)
    {
        var result = await RunWithHiddenIslandAsync<(bool Picked, MediaColor Color, string Hex)>(() =>
        {
            var picker = new Views.ScreenColorPickerOverlay();
            return picker.ShowDialog() == true ? (true, picker.PickedColor, picker.PickedHex) : (false, MediaColor.FromRgb(0, 0, 0), string.Empty);
        });

        if (result.Picked && !string.IsNullOrEmpty(result.Hex))
        {
            try { System.Windows.Clipboard.SetText(result.Hex); } catch { }
            FlashIslandBorder(result.Color);
            ShowToast("💉", $"Цвет скопирован: {result.Hex} (RGB {result.Color.R}, {result.Color.G}, {result.Color.B})", durationMs: 3400);
        }
    }

    private void ToggleQuickCommand_Click(object sender, RoutedEventArgs e)
    {
        bool open = HomeCommandCard.Visibility != Visibility.Visible;
        HomeCommandCard.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        ScheduleExpandedResize(animate: true);
        if (open)
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => HomeCommandBox.Focus());
    }

    private void CloseQuickCommand_Click(object sender, RoutedEventArgs e)
    {
        HomeCommandCard.Visibility = Visibility.Collapsed;
        ScheduleExpandedResize(animate: true);
    }

    private void HomeCommandBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string raw = HomeCommandBox.Text ?? string.Empty;
        HomeCommandPlaceholder.Visibility = string.IsNullOrEmpty(raw) ? Visibility.Visible : Visibility.Collapsed;
        if (HardwareAndUtilityService.TryEvaluateExpression(raw, _settings.PreferredCurrency, out var calc))
        {
            HomeCommandCalcPreview.Text = $"= {calc}";
            HomeCommandCalcPreview.Visibility = Visibility.Visible;
        }
        else
        {
            HomeCommandCalcPreview.Visibility = Visibility.Collapsed;
        }
    }

    private void HomeCommandBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            RunQuickCommand_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseQuickCommand_Click(sender, e);
            e.Handled = true;
        }
    }

    private void RunQuickCommand_Click(object sender, RoutedEventArgs e)
    {
        string raw = (HomeCommandBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(raw)) return;

        if (HardwareAndUtilityService.TryEvaluateExpression(raw, _settings.PreferredCurrency, out var calc))
        {
            try { System.Windows.Clipboard.SetText(calc); } catch { }
            ShowToast("🧮", $"Скопировано: {calc}", durationMs: 2400);
            return;
        }

        try
        {
            if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                File.Exists(raw) || Directory.Exists(raw))
            {
                OpenShellTarget(raw);
            }
            else
            {
                Process.Start(new ProcessStartInfo("cmd.exe", $"/c start \"\" {raw}")
                {
                    UseShellExecute = true,
                    CreateNoWindow = true
                });
            }
            HomeCommandBox.Text = string.Empty;
            HomeCommandCard.Visibility = Visibility.Collapsed;
            ScheduleExpandedResize(animate: true);
            ShowToast("⚡", $"Выполнено: {raw}", durationMs: 2200);
        }
        catch (Exception ex)
        {
            ShowToast("⚠", $"Не удалось выполнить: {ex.Message}", durationMs: 3000);
        }
    }

    private void HomeMediaCard_Click(object sender, MouseButtonEventArgs e) => OpenTab("Remote");

    private void HomeWeatherCard_Click(object sender, MouseButtonEventArgs e)
    {
        _ = RefreshWeatherAsync();
        ShowToast("⛅", "Обновление погоды...", durationMs: 1400);
    }

    private void CollapseIslandBtn_Click(object sender, RoutedEventArgs e)
    {
        _isPinned = false;
        PinBtn.IsChecked = false;
        SetExpanded(false);
    }

    private void JumpToAudioMixer_Click(object sender, RoutedEventArgs e)
    {
        TabRemote.IsChecked = true;
        if (!_isAppMixerExpanded)
        {
            _isAppMixerExpanded = true;
            AppMixerList.Visibility = Visibility.Visible;
        }
        _ = RefreshMediaAndDevicesAsync(forceBluetooth: false);
        ScheduleExpandedResize(animate: true);
    }

    private void SystemDetailsToggle_Click(object sender, RoutedEventArgs e)
    {
        _isSystemDetailsExpanded = !_isSystemDetailsExpanded;
        AnimatePanelReveal(SystemDetailsSection, _isSystemDetailsExpanded, () => !_isSystemDetailsExpanded);
        SystemDetailsToggleBtn.Content = _isSystemDetailsExpanded
            ? "Скрыть диски, сеть и процессы ▴"
            : "Диски, сеть и процессы ▾";
        if (_isSystemDetailsExpanded)
        {
            RefreshDrives();
        }
    }

    private async void CtrlWifi_Click(object sender, RoutedEventArgs e)
    {
        var (ok, msg) = await QuickToolsService.ToggleWifiOrNetworkAsync();
        ShowToast(ok ? "📶" : "🌐", msg, durationMs: 2400);
        await Task.Delay(350);
        RefreshSystemControlCenterUi(HardwareAndUtilityService.GetConnectedGamepads());
    }

    private void CtrlAirplane_Click(object sender, RoutedEventArgs e)
    {
        QuickToolsService.OpenAirplaneSettings();
        ShowToast("✈", "Параметры режима полёта", durationMs: 2000);
    }

    private void CtrlVpn_Click(object sender, RoutedEventArgs e)
    {
        QuickToolsService.ToggleOrOpenVpn();
        ShowToast("🛡", "Открыт VPN / параметры сети", durationMs: 2400);
        RefreshSystemControlCenterUi(HardwareAndUtilityService.GetConnectedGamepads());
    }

    private void CtrlDesktop_Click(object sender, RoutedEventArgs e)
    {
        bool ok = QuickToolsService.ToggleDesktopIcons();
        CtrlDesktopCircle.Background = ok
            ? new SolidColorBrush(MediaColor.FromRgb(30, 58, 138))
            : new SolidColorBrush(MediaColor.FromRgb(37, 39, 48));
        ShowToast("🖥", ok ? "Иконки рабочего стола переключены" : "Не удалось переключить иконки", durationMs: 2000);
    }

    private void RebootPc_Click(object sender, RoutedEventArgs e)
    {
        var res = System.Windows.MessageBox.Show(
            "Перезагрузить компьютер сейчас?",
            "Desktop Island",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (res == MessageBoxResult.Yes)
            QuickToolsService.RebootPc();
    }

    private void ShutdownPc_Click(object sender, RoutedEventArgs e)
    {
        var res = System.Windows.MessageBox.Show(
            "Выключить компьютер сейчас?",
            "Desktop Island",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (res == MessageBoxResult.Yes)
            QuickToolsService.ShutdownPc();
    }

    private void RefreshSystemControlCenterUi(List<(int Index, int BatteryPercent, bool IsWired)> gamepads)
    {
        // Network / Wi-Fi / Airplane / VPN state
        var net = QuickToolsService.GetControlCenterNetworkStatus();
        bool wifiActive = net.WifiOn || net.LanConnected;
        CtrlWifiCircle.Background = wifiActive
            ? new SolidColorBrush(MediaColor.FromRgb(37, 99, 235))
            : new SolidColorBrush(MediaColor.FromRgb(37, 39, 48));
        CtrlWifiLabel.Text = net.WifiOn ? "Wi-Fi Вкл" : (net.LanConnected ? "Сеть Вкл" : "Wi-Fi Выкл");

        bool btOn = BtStateText.Text.Contains("Вкл", StringComparison.OrdinalIgnoreCase)
            || (!BtStateText.Text.Contains("Выкл", StringComparison.OrdinalIgnoreCase) && _allBtDevices.Count > 0);
        CtrlBtCircle.Background = btOn
            ? new SolidColorBrush(MediaColor.FromRgb(37, 99, 235))
            : new SolidColorBrush(MediaColor.FromRgb(37, 39, 48));
        CtrlBtLabel.Text = btOn ? "BT Вкл" : "Bluetooth";

        CtrlAirplaneCircle.Background = net.AirplaneOn
            ? new SolidColorBrush(MediaColor.FromRgb(217, 119, 6))
            : new SolidColorBrush(MediaColor.FromRgb(37, 39, 48));

        CtrlVpnCircle.Background = net.VpnActive
            ? new SolidColorBrush(MediaColor.FromRgb(22, 163, 74))  // Bright Green when VPN active (like Screenshot 5)
            : new SolidColorBrush(MediaColor.FromRgb(37, 39, 48));
        CtrlVpnLabel.Text = net.VpnActive ? net.VpnLabel : "VPN";

        // Discord voice status & interactive controls
        var (discordRunning, discordInVoice) = HardwareAndUtilityService.GetDiscordVoiceStatus();
        if (discordInVoice)
        {
            DiscordVoiceDot.Fill = new SolidColorBrush(MediaColor.FromRgb(74, 222, 128));
            DiscordStatusText.Text = "В голосовом канале · нажми для перехода";
            DiscordStatusText.Foreground = new SolidColorBrush(MediaColor.FromRgb(74, 222, 128));
            DiscordVoiceControlsPanel.Visibility = Visibility.Visible;
            SyncDiscordVoiceButtonsUi();
        }
        else if (discordRunning)
        {
            DiscordVoiceDot.Fill = new SolidColorBrush(MediaColor.FromRgb(96, 165, 250));
            DiscordStatusText.Text = "Запущен · открыть окно";
            DiscordStatusText.Foreground = new SolidColorBrush(MediaColor.FromRgb(161, 161, 170));
            DiscordVoiceControlsPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            DiscordVoiceDot.Fill = new SolidColorBrush(MediaColor.FromRgb(82, 82, 91));
            DiscordStatusText.Text = "Не запущен · открыть Discord";
            DiscordStatusText.Foreground = new SolidColorBrush(MediaColor.FromRgb(113, 113, 122));
            DiscordVoiceControlsPanel.Visibility = Visibility.Collapsed;
        }

        // Gamepads extra battery summary (Bluetooth devices are listed directly above inside BtDevicesList)
        var batParts = new List<string>();
        foreach (var pad in gamepads)
        {
            batParts.Add(pad.IsWired
                ? $"🎮 Геймпад #{pad.Index}: провод"
                : $"🎮 Геймпад #{pad.Index}: {pad.BatteryPercent}%");
        }
        SysBatterySummaryText.Text = batParts.Count > 0 ? string.Join(" · ", batParts.Take(2)) : "";
        SysBatterySummaryText.Visibility = batParts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SyncDiscordVoiceButtonsUi()
    {
        bool micMuted = CoreAudioService.IsMicMuted();
        bool discordMuted = CoreAudioService.IsProcessMuted("Discord");

        DiscordMuteMicBtn.Content = micMuted ? "🔇 Микр вкл" : "🎙 Загл. микр";
        DiscordMuteMicBtn.Background = micMuted
            ? new SolidColorBrush(MediaColor.FromRgb(127, 29, 29))
            : new SolidColorBrush(MediaColor.FromRgb(34, 36, 46));
        DiscordMuteMicBtn.Foreground = micMuted
            ? new SolidColorBrush(MediaColor.FromRgb(254, 202, 202))
            : new SolidColorBrush(MediaColor.FromRgb(228, 228, 231));

        bool deafened = discordMuted && micMuted;
        DiscordDeafenBtn.Content = deafened ? "🔊 Вкл всё" : "🔕 Микр+Звук";
        DiscordDeafenBtn.Background = deafened
            ? new SolidColorBrush(MediaColor.FromRgb(127, 29, 29))
            : new SolidColorBrush(MediaColor.FromRgb(34, 36, 46));
        DiscordDeafenBtn.Foreground = deafened
            ? new SolidColorBrush(MediaColor.FromRgb(254, 202, 202))
            : new SolidColorBrush(MediaColor.FromRgb(228, 228, 231));
    }

    private void DiscordCard_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            foreach (var procName in new[] { "Discord", "DiscordPTB", "DiscordCanary" })
            {
                var procs = Process.GetProcessesByName(procName);
                foreach (var p in procs)
                {
                    if (p.MainWindowHandle != IntPtr.Zero)
                    {
                        ShowWindow(p.MainWindowHandle, 9);
                        SetForegroundWindow(p.MainWindowHandle);
                        ShowToast("💬", "Discord открыт");
                        return;
                    }
                }
            }

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string updateExe = System.IO.Path.Combine(localAppData, "Discord", "Update.exe");
            if (File.Exists(updateExe))
            {
                Process.Start(new ProcessStartInfo(updateExe, "--processStart Discord.exe") { UseShellExecute = true });
            }
            else
            {
                Process.Start(new ProcessStartInfo("discord://") { UseShellExecute = true });
            }
            ShowToast("💬", "Запускаем Discord...");
        }
        catch (Exception ex)
        {
            LoggerService.Error("Discord launch failed", ex);
            ShowToast("⚠", "Не удалось открыть Discord");
        }
    }

    private void DiscordMuteMic_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        bool nowMuted = CoreAudioService.ToggleMicMute();
        MicMuteBtn.Content = nowMuted ? "🔇 Мик: Выкл" : "🎙 Мик: Вкл";
        SyncDiscordVoiceButtonsUi();
        ShowToast(nowMuted ? "🔇" : "🎙", nowMuted ? "Микрофон заглушён" : "Микрофон включён", durationMs: 2000);
    }

    private void DiscordDeafen_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        bool currentlyDeaf = CoreAudioService.IsProcessMuted("Discord") && CoreAudioService.IsMicMuted();
        bool targetDeaf = !currentlyDeaf;
        CoreAudioService.SetProcessMute("Discord", targetDeaf);
        CoreAudioService.SetMicMute(targetDeaf);
        MicMuteBtn.Content = targetDeaf ? "🔇 Мик: Выкл" : "🎙 Мик: Вкл";
        SyncDiscordVoiceButtonsUi();
        ShowToast(targetDeaf ? "🔕" : "🔊", targetDeaf ? "Discord: микр + звук заглушены" : "Discord: микр + звук включены", durationMs: 2200);
    }

    #endregion

    public void CleanupBeforeExit()
    {
        _clockTimer.Stop();
        _monitorTimer.Stop();
        _collapseDelayTimer.Stop();
        _eqTimer.Stop();
        _idleEyesTimer.Stop();
        _ramLabelResetTimer.Stop();
        _toastTimer.Stop();
        _weatherTimer.Stop();
        _updateTimer.Stop();
        _gameInputTimer.Stop();
        _borderAlertResetTimer.Stop();
        _magnetTimer.Stop();
        _inputStateTimer.Stop();
        _downloadsWatcher?.Dispose();
        _downloadsWatcher = null;
        _settings.PinnedSnippets = _clipboardService.GetPinnedEntries();
        _clipboardService.Dispose();
        _screenRecorder.Dispose();
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
