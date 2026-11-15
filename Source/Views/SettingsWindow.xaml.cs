using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DesktopIsland.Models;
using Forms = System.Windows.Forms;
using Win32 = Microsoft.Win32;

namespace DesktopIsland.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _workingCopy;
    private readonly ObservableCollection<PinnedApp> _apps;
    private readonly Action<AppSettings> _onSave;
    private readonly Action _onExitApp;
    private bool _isUpdatingWeatherCombos;

    private static readonly Dictionary<string, string[]> WeatherCountries = new()
    {
        ["Авто (по IP)"] = Array.Empty<string>(),
        ["Украина"] = new[] { "Киев", "Харьков", "Одесса", "Днепр", "Львов", "Запорожье", "Винница", "Полтава", "Николаев", "Черкассы", "Сумы", "Житомир", "Ровно", "Ивано-Франковск", "Тернополь", "Черновцы", "Кривой Рог" },
        ["Польша"] = new[] { "Варшава", "Краков", "Вроцлав", "Гданьск", "Познань", "Лодзь", "Катовице", "Люблин", "Щецин" },
        ["Германия"] = new[] { "Берлин", "Мюнхен", "Гамбург", "Франкфурт", "Кёльн", "Штутгарт", "Дюссельдорф", "Лейпциг" },
        ["Россия"] = new[] { "Москва", "Санкт-Петербург", "Новосибирск", "Екатеринбург", "Казань", "Нижний Новгород", "Краснодар", "Ростов-на-Дону" },
        ["Казахстан"] = new[] { "Астана", "Алматы", "Шымкент", "Караганда", "Актобе" },
        ["Беларусь"] = new[] { "Минск", "Брест", "Гомель", "Гродно", "Витебск" },
        ["Чехия"] = new[] { "Прага", "Брно", "Острава", "Пльзень" },
        ["Великобритания"] = new[] { "Лондон", "Манчестер", "Бирмингем", "Ливерпуль", "Эдинбург" },
        ["США"] = new[] { "Нью-Йорк", "Лос-Анджелес", "Чикаго", "Майами", "Сан-Франциско", "Сиэтл", "Остин" },
        ["Канада"] = new[] { "Торонто", "Ванкувер", "Монреаль", "Калгари", "Оттава" },
        ["Турция"] = new[] { "Стамбул", "Анталья", "Анкара", "Измир", "Бодрум" },
        ["Испания"] = new[] { "Мадрид", "Барселона", "Валенсия", "Севилья", "Малага" },
        ["Италия"] = new[] { "Рим", "Милан", "Неаполь", "Турин", "Флоренция" },
        ["Франция"] = new[] { "Париж", "Лион", "Марсель", "Ницца" },
        ["Нидерланды"] = new[] { "Амстердам", "Роттердам", "Гаага", "Эйндховен" }
    };

    public SettingsWindow(AppSettings current, Action<AppSettings> onSave, Action onExitApp)
    {
        InitializeComponent();
        _onSave = onSave;
        _onExitApp = onExitApp;

        var json = JsonSerializer.Serialize(current);
        _workingCopy = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        _apps = new ObservableCollection<PinnedApp>(_workingCopy.PinnedApps);
        AppsListBox.ItemsSource = _apps;

        var screens = Forms.Screen.AllScreens;
        for (int i = 0; i < screens.Length; i++)
        {
            var s = screens[i];
            string tag = s.Primary ? " (Основной)" : "";
            MonitorCombo.Items.Add($"Монитор {i + 1}: {s.Bounds.Width}x{s.Bounds.Height}{tag}");
        }
        MonitorCombo.SelectedIndex = (_workingCopy.MonitorIndex >= 0 && _workingCopy.MonitorIndex < screens.Length)
            ? _workingCopy.MonitorIndex
            : 0;

        OpacitySlider.Value = _workingCopy.CapsuleOpacity;
        TopOffsetBox.Text = _workingCopy.TopOffsetPx.ToString();
        ThemeCombo.SelectedIndex = _workingCopy.AccentTheme switch
        {
            "Emerald" => 1,
            "Violet" => 2,
            "Amber" => 3,
            "Rose" => 4,
            _ => 0
        };
        BorderStyleCombo.SelectedIndex = _workingCopy.BorderStyle switch
        {
            "Neon" => 1,
            "Minimal" => 2,
            _ => 0
        };
        CurrencyCombo.SelectedIndex = _workingCopy.PreferredCurrency switch
        {
            "USD" => 1,
            "EUR" => 2,
            "PLN" => 3,
            "RUB" => 4,
            _ => 0
        };
        DockModeCombo.SelectedIndex = _workingCopy.DockEdge switch
        {
            "Left" => 2,
            "Right" => 3,
            "Free" => 4,
            _ => _workingCopy.DockToTopEdge ? 0 : 1
        };
        AlwaysOnTopChk.IsChecked = _workingCopy.AlwaysOnTop;
        ExpandOnHoverChk.IsChecked = _workingCopy.ExpandOnHover;
        EnableAnimationsChk.IsChecked = _workingCopy.EnableAnimations;
        GameModeAutoChk.IsChecked = _workingCopy.GameModeAutoProfile;
        GameClickThroughChk.IsChecked = _workingCopy.GameClickThrough;
        GameAutoBoostChk.IsChecked = _workingCopy.GameAutoBoost;
        GameGpuTempChk.IsChecked = _workingCopy.GameShowGpuTemp;
        GameMicMuteChk.IsChecked = _workingCopy.GameShowMicMute;
        GameReplayBtnChk.IsChecked = _workingCopy.ShowReplayButtonInGame;
        DimVideoChk.IsChecked = _workingCopy.DimOnFullscreenVideo;
        SuperMiniIdleChk.IsChecked = _workingCopy.SuperMiniOnIdle;
        MusicOnlyFilterChk.IsChecked = _workingCopy.MusicFilterOnlyMusicApps;
        LayoutCapsChk.IsChecked = _workingCopy.ShowLayoutAndCapsIndicator;
        StreamPrivacyAutoChk.IsChecked = _workingCopy.StreamPrivacyAuto;

        EnableSysMonChk.IsChecked = _workingCopy.EnableSystemMonitor;
        EnableDiskMonChk.IsChecked = _workingCopy.EnableDiskMonitor;
        EnableNetMonChk.IsChecked = _workingCopy.EnableNetworkMonitor;
        CompactCpuRamChk.IsChecked = _workingCopy.CompactShowCpuRam;
        CompactFpsChk.IsChecked = _workingCopy.CompactShowFps;
        CompactPingChk.IsChecked = _workingCopy.CompactShowPing;
        CompactNetChk.IsChecked = _workingCopy.CompactShowNetwork;
        CompactMediaChk.IsChecked = _workingCopy.CompactShowMedia;
        CompactWeatherChk.IsChecked = _workingCopy.CompactShowWeather;
        WeatherCityBox.Text = _workingCopy.WeatherCity;
        InitWeatherSelectors(_workingCopy.WeatherCity);

        PollingBox.Text = _workingCopy.PollingIntervalMs.ToString();

        AutoRamChk.IsChecked = _workingCopy.AutoOptimizeRamOnThreshold;
        RamThresholdBox.Text = _workingCopy.RamThresholdPercent.ToString();
        ConfirmCleanupChk.IsChecked = _workingCopy.ConfirmControversialCleanup;
        AutostartChk.IsChecked = _workingCopy.StartWithWindows;
    }

    private void AddApp_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Win32.OpenFileDialog
        {
            Filter = "Программы (*.exe)|*.exe|Все файлы (*.*)|*.*",
            Title = "Выберите программу для быстрого запуска"
        };
        if (dlg.ShowDialog() == true)
        {
            var name = Path.GetFileNameWithoutExtension(dlg.FileName);
            _apps.Add(new PinnedApp { Name = name, ExecutablePath = dlg.FileName });
        }
    }

    private void RemoveApp_Click(object sender, RoutedEventArgs e)
    {
        if (AppsListBox.SelectedIndex >= 0)
            _apps.RemoveAt(AppsListBox.SelectedIndex);
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        int idx = AppsListBox.SelectedIndex;
        if (idx > 0)
        {
            _apps.Move(idx, idx - 1);
            AppsListBox.SelectedIndex = idx - 1;
        }
    }

    private void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        int idx = AppsListBox.SelectedIndex;
        if (idx >= 0 && idx < _apps.Count - 1)
        {
            _apps.Move(idx, idx + 1);
            AppsListBox.SelectedIndex = idx + 1;
        }
    }

    private void InitWeatherSelectors(string? savedCity)
    {
        _isUpdatingWeatherCombos = true;
        try
        {
            WeatherCountryCombo.ItemsSource = WeatherCountries.Keys.ToList();
            var trimmed = savedCity?.Trim() ?? string.Empty;

            if (string.IsNullOrEmpty(trimmed))
            {
                WeatherCountryCombo.SelectedItem = "Авто (по IP)";
                WeatherCityCombo.ItemsSource = null;
                WeatherCityCombo.IsEnabled = false;
                return;
            }

            foreach (var kv in WeatherCountries)
            {
                var match = kv.Value.FirstOrDefault(c => c.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    WeatherCountryCombo.SelectedItem = kv.Key;
                    WeatherCityCombo.ItemsSource = kv.Value;
                    WeatherCityCombo.IsEnabled = true;
                    WeatherCityCombo.SelectedItem = match;
                    return;
                }
            }

            // Custom manual city not in catalog
            WeatherCountryCombo.SelectedIndex = -1;
            WeatherCityCombo.ItemsSource = null;
            WeatherCityCombo.IsEnabled = false;
        }
        finally
        {
            _isUpdatingWeatherCombos = false;
        }
    }

    private void WeatherCountryCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingWeatherCombos || WeatherCountryCombo.SelectedItem is not string country)
            return;

        _isUpdatingWeatherCombos = true;
        try
        {
            if (WeatherCountries.TryGetValue(country, out var cities) && cities.Length > 0)
            {
                WeatherCityCombo.ItemsSource = cities;
                WeatherCityCombo.IsEnabled = true;
                WeatherCityCombo.SelectedIndex = 0;
                WeatherCityBox.Text = cities[0];
            }
            else
            {
                WeatherCityCombo.ItemsSource = null;
                WeatherCityCombo.IsEnabled = false;
                WeatherCityBox.Text = string.Empty;
            }
        }
        finally
        {
            _isUpdatingWeatherCombos = false;
        }
    }

    private void WeatherCityCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingWeatherCombos || WeatherCityCombo.SelectedItem is not string city)
            return;

        WeatherCityBox.Text = city;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _workingCopy.PinnedApps = _apps.ToList();
        _workingCopy.MonitorIndex = Math.Max(0, MonitorCombo.SelectedIndex);
        if (ThemeCombo.SelectedItem is ComboBoxItem themeItem && themeItem.Tag is string themeTag)
            _workingCopy.AccentTheme = themeTag;
        if (BorderStyleCombo.SelectedItem is ComboBoxItem bsItem && bsItem.Tag is string bsTag)
            _workingCopy.BorderStyle = bsTag;
        if (CurrencyCombo.SelectedItem is ComboBoxItem curItem && curItem.Tag is string curTag)
            _workingCopy.PreferredCurrency = curTag;
        _workingCopy.CapsuleOpacity = OpacitySlider.Value;
        _workingCopy.TopOffsetPx = int.TryParse(TopOffsetBox.Text, out var top) ? Math.Clamp(top, 0, 100) : 8;
        if (DockModeCombo.SelectedItem is ComboBoxItem dockItem && dockItem.Tag is string dockTag)
        {
            switch (dockTag)
            {
                case "TopFloating":
                    _workingCopy.DockEdge = "Top";
                    _workingCopy.DockToTopEdge = false;
                    if (_workingCopy.TopOffsetPx == 0) _workingCopy.TopOffsetPx = 10;
                    break;
                case "Left":
                    _workingCopy.DockEdge = "Left";
                    _workingCopy.DockToTopEdge = false;
                    break;
                case "Right":
                    _workingCopy.DockEdge = "Right";
                    _workingCopy.DockToTopEdge = false;
                    break;
                case "Free":
                    _workingCopy.DockEdge = "Free";
                    _workingCopy.DockToTopEdge = false;
                    break;
                default:
                    _workingCopy.DockEdge = "Top";
                    _workingCopy.DockToTopEdge = true;
                    break;
            }
        }
        _workingCopy.AlwaysOnTop = AlwaysOnTopChk.IsChecked == true;
        _workingCopy.ExpandOnHover = ExpandOnHoverChk.IsChecked == true;
        _workingCopy.EnableAnimations = EnableAnimationsChk.IsChecked == true;
        _workingCopy.GameModeAutoProfile = GameModeAutoChk.IsChecked == true;
        _workingCopy.GameClickThrough = GameClickThroughChk.IsChecked == true;
        _workingCopy.GameAutoBoost = GameAutoBoostChk.IsChecked == true;
        _workingCopy.GameShowGpuTemp = GameGpuTempChk.IsChecked == true;
        _workingCopy.GameShowMicMute = GameMicMuteChk.IsChecked == true;
        _workingCopy.ShowReplayButtonInGame = GameReplayBtnChk.IsChecked == true;
        _workingCopy.DimOnFullscreenVideo = DimVideoChk.IsChecked == true;
        _workingCopy.SuperMiniOnIdle = SuperMiniIdleChk.IsChecked == true;
        _workingCopy.MusicFilterOnlyMusicApps = MusicOnlyFilterChk.IsChecked == true;
        _workingCopy.ShowLayoutAndCapsIndicator = LayoutCapsChk.IsChecked == true;
        _workingCopy.StreamPrivacyAuto = StreamPrivacyAutoChk.IsChecked == true;

        _workingCopy.EnableSystemMonitor = EnableSysMonChk.IsChecked == true;
        _workingCopy.EnableDiskMonitor = EnableDiskMonChk.IsChecked == true;
        _workingCopy.EnableNetworkMonitor = EnableNetMonChk.IsChecked == true;
        _workingCopy.CompactShowCpuRam = CompactCpuRamChk.IsChecked == true;
        _workingCopy.CompactShowFps = CompactFpsChk.IsChecked == true;
        _workingCopy.CompactShowPing = CompactPingChk.IsChecked == true;
        _workingCopy.CompactShowNetwork = CompactNetChk.IsChecked == true;
        _workingCopy.CompactShowMedia = CompactMediaChk.IsChecked == true;
        _workingCopy.CompactShowWeather = CompactWeatherChk.IsChecked == true;
        _workingCopy.WeatherCity = WeatherCityBox.Text?.Trim() ?? string.Empty;

        _workingCopy.PollingIntervalMs = int.TryParse(PollingBox.Text, out var poll) ? Math.Clamp(poll, 1000, 10000) : 2000;

        _workingCopy.AutoOptimizeRamOnThreshold = AutoRamChk.IsChecked == true;
        _workingCopy.RamThresholdPercent = int.TryParse(RamThresholdBox.Text, out var ramT) ? Math.Clamp(ramT, 60, 95) : 80;
        _workingCopy.ConfirmControversialCleanup = ConfirmCleanupChk.IsChecked == true;
        _workingCopy.StartWithWindows = AutostartChk.IsChecked == true;

        _onSave(_workingCopy);
        Close();
    }

    private void ResetHorizontalOffset_Click(object sender, RoutedEventArgs e)
    {
        _workingCopy.DockEdge = "Top";
        _workingCopy.HorizontalOffsetPx = 0;
        _workingCopy.VerticalOffsetPx = 0;
        _workingCopy.ProcessDockProfiles.Clear();
        System.Windows.MessageBox.Show("Позиция островка (для рабочего стола и игр) будет сброшена в центр верха экрана при сохранении.", "Desktop Island", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void ExitApp_Click(object sender, RoutedEventArgs e)
    {
        Close();
        _onExitApp();
    }
}
