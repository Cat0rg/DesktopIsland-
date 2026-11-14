using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using DesktopIsland.Models;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;

namespace DesktopIsland.Services;

public static class BluetoothService
{
    private const string BatteryPropKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    private const string ConnectedPropKey = "System.Devices.Aep.IsConnected";

    public static async Task<(bool Supported, bool IsOn)> GetAdapterStateAsync()
    {
        try
        {
            var access = await Radio.RequestAccessAsync();
            if (access != RadioAccessStatus.Allowed)
            {
                var radiosFallback = await Radio.GetRadiosAsync();
                var btFallback = radiosFallback.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
                if (btFallback != null)
                    return (true, btFallback.State == RadioState.On);
                return (false, false);
            }

            var radios = await Radio.GetRadiosAsync();
            var bt = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
            return bt != null ? (true, bt.State == RadioState.On) : (false, false);
        }
        catch
        {
            return (false, false);
        }
    }

    public static async Task<bool> SetAdapterStateAsync(bool turnOn)
    {
        try
        {
            await Radio.RequestAccessAsync();
            var radios = await Radio.GetRadiosAsync();
            var bt = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
            if (bt == null) return false;
            var res = await bt.SetStateAsync(turnOn ? RadioState.On : RadioState.Off);
            return res == RadioAccessStatus.Allowed;
        }
        catch (Exception ex)
        {
            LoggerService.Error("Bluetooth radio toggle failed", ex);
            return false;
        }
    }

    public static async Task<List<BluetoothDeviceItem>> GetPairedDevicesAsync()
    {
        var result = new List<BluetoothDeviceItem>();
        try
        {
            var extraProps = new[] { ConnectedPropKey, BatteryPropKey };

            // Classic Bluetooth paired devices
            var classicSelector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
            var classicInfos = await DeviceInformation.FindAllAsync(classicSelector, extraProps);

            var batteryMap = await GetBatteryMapAsync();

            foreach (var dev in classicInfos)
            {
                if (string.IsNullOrWhiteSpace(dev.Name)) continue;
                bool isConnected = dev.Properties.TryGetValue(ConnectedPropKey, out var cObj) && cObj is bool b && b;
                int? bat = TryGetBatteryFromProperties(dev.Properties);
                if (!bat.HasValue && batteryMap.TryGetValue(dev.Name.Trim(), out int mappedBat))
                    bat = mappedBat;

                result.Add(new BluetoothDeviceItem
                {
                    Id = dev.Id,
                    Name = dev.Name.Trim(),
                    IsConnected = isConnected,
                    BatteryPercent = bat
                });
            }

            // Also include paired BLE devices not already in list
            var leSelector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
            var leInfos = await DeviceInformation.FindAllAsync(leSelector, extraProps);
            foreach (var dev in leInfos)
            {
                if (string.IsNullOrWhiteSpace(dev.Name)) continue;
                var cleanName = dev.Name.Trim();
                if (result.Any(x => x.Name.Equals(cleanName, StringComparison.OrdinalIgnoreCase))) continue;

                bool isConnected = dev.Properties.TryGetValue(ConnectedPropKey, out var cObj) && cObj is bool b && b;
                int? bat = TryGetBatteryFromProperties(dev.Properties);
                if (!bat.HasValue && batteryMap.TryGetValue(cleanName, out int mappedBat))
                    bat = mappedBat;

                result.Add(new BluetoothDeviceItem
                {
                    Id = dev.Id,
                    Name = cleanName,
                    IsConnected = isConnected,
                    BatteryPercent = bat
                });
            }
        }
        catch (Exception ex)
        {
            LoggerService.Error("Failed to enumerate Bluetooth devices", ex);
        }

        return result.OrderByDescending(d => d.IsConnected).ThenBy(d => d.Name).ToList();
    }

    private static int? TryGetBatteryFromProperties(IReadOnlyDictionary<string, object> props)
    {
        if (props.TryGetValue(BatteryPropKey, out var bObj) && bObj != null)
        {
            if (bObj is byte by) return by;
            if (bObj is int i) return i;
            if (int.TryParse(bObj.ToString(), out int parsed)) return parsed;
        }
        return null;
    }

    private static async Task<Dictionary<string, int>> GetBatteryMapAsync()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var pnp = await DeviceInformation.FindAllAsync(
                "System.Devices.InterfaceClassGuid:=\"{378DE44C-56EF-420D-B3B7-19F5D6B7E43A}\" OR System.Devices.DeviceInstanceId:~~\"BTHENUM\"",
                new[] { BatteryPropKey, "System.ItemNameDisplay" });
            foreach (var item in pnp)
            {
                var bat = TryGetBatteryFromProperties(item.Properties);
                if (bat.HasValue && !string.IsNullOrWhiteSpace(item.Name))
                    map[item.Name.Trim()] = bat.Value;
            }
        }
        catch { }
        return map;
    }

    public static void OpenBluetoothSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true });
        }
        catch { }
    }

    public static async Task<string> ToggleDeviceConnectionAsync(BluetoothDeviceItem item)
    {
        try
        {
            if (item.Id.Contains("BluetoothLE", StringComparison.OrdinalIgnoreCase))
            {
                using var le = await BluetoothLEDevice.FromIdAsync(item.Id);
                if (le != null)
                {
                    if (!item.IsConnected)
                    {
                        await le.GetGattServicesAsync(BluetoothCacheMode.Uncached);
                        return $"Запрос подключения к {item.Name}";
                    }
                }
            }
            else
            {
                using var bt = await BluetoothDevice.FromIdAsync(item.Id);
                if (bt != null && !item.IsConnected)
                {
                    await bt.GetRfcommServicesAsync(BluetoothCacheMode.Uncached);
                    return $"Запрос подключения к {item.Name}";
                }
            }
        }
        catch { }

        // Windows restricts silent A2DP disconnect/connect without user action in Win32 -> open Quick Connect / BT Settings cleanly
        try
        {
            if (!item.IsConnected)
                Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true });
        }
        catch { }
        return $"Откройте выбор {item.Name} в параметрах Bluetooth";
    }
}
