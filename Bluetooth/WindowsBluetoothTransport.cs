using System.Collections.Concurrent;
using OpenQCY_Desktop.Protocol;
using Windows.Devices.Enumeration;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace OpenQCY_Desktop.Bluetooth;

public sealed class WindowsBluetoothTransport : IBluetoothTransport
{
    private const string BatteryLifeProperty = "System.Devices.BatteryLife";
    private const string IsConnectedProperty = "System.Devices.Aep.IsConnected";

    public async Task<BluetoothBatteryInfo?> FindWindowsBatteryAsync(
        CancellationToken cancellationToken = default)
    {
        var selector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
        var properties = new[] { BatteryLifeProperty, IsConnectedProperty };
        var pairedDevices = await DeviceInformation.FindAllAsync(selector, properties);
        cancellationToken.ThrowIfCancellationRequested();

        return pairedDevices
            .Where(device => QcyModelProfile.IsN70Name(device.Name))
            .Select(device => new
            {
                device.Name,
                Battery = ReadByteProperty(device, BatteryLifeProperty),
                IsConnected = ReadBooleanProperty(device, IsConnectedProperty),
            })
            .Where(device => device.Battery is <= 100)
            .OrderByDescending(device => device.IsConnected)
            .Select(device => new BluetoothBatteryInfo(
                device.Name,
                device.Battery!.Value,
                device.IsConnected))
            .FirstOrDefault();
    }

    public async Task<IReadOnlyList<BluetoothDeviceInfo>> FindPairedQcyDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        var devices = new List<BluetoothDeviceInfo>();
        var serviceSelector = GattDeviceService.GetDeviceSelectorFromUuid(QcyUuids.MainService);
        var knownServices = await DeviceInformation.FindAllAsync(serviceSelector);
        foreach (var knownService in knownServices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GattDeviceService? service = null;
            try
            {
                service = await GattDeviceService.FromIdAsync(knownService.Id);
                cancellationToken.ThrowIfCancellationRequested();
                if (service is null)
                {
                    continue;
                }

                var device = await CreateKnownDeviceAsync(
                    service.Session.DeviceId.Id,
                    knownService.Name,
                    cancellationToken);
                if (device is not null)
                {
                    devices.Add(device);
                }
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
            }
            finally
            {
                service?.Dispose();
            }
        }

        var pairedSelector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
        var pairedDevices = await DeviceInformation.FindAllAsync(pairedSelector);
        foreach (var pairedDevice in pairedDevices.Where(device => QcyModelProfile.IsN70Name(device.Name)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var device = await CreateKnownDeviceAsync(
                    pairedDevice.Id,
                    pairedDevice.Name,
                    cancellationToken);
                if (device is not null)
                {
                    devices.Add(device);
                }
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // A cached Windows entry may be stale. Other candidates can still be valid.
            }
        }

        return devices
            .DistinctBy(device => device.BluetoothAddress)
            .ToArray();
    }

    public async Task<IReadOnlyList<BluetoothDeviceInfo>> ScanForQcyDevicesAsync(
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        var devices = new ConcurrentDictionary<ulong, BluetoothDeviceInfo>();
        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active,
            AllowExtendedAdvertisements = true,
        };

        void OnReceived(BluetoothLEAdvertisementWatcher _, BluetoothLEAdvertisementReceivedEventArgs eventArgs)
        {
            foreach (var manufacturerData in eventArgs.Advertisement.ManufacturerData)
            {
                if (manufacturerData.CompanyId != QcyUuids.CompanyId)
                {
                    continue;
                }

                var rawData = ReadBuffer(manufacturerData.Data);
                var info = BluetoothDeviceInfo.FromAdvertisement(
                    eventArgs.Advertisement.LocalName,
                    eventArgs.BluetoothAddress,
                    eventArgs.BluetoothAddressType,
                    eventArgs.RawSignalStrengthInDBm,
                    eventArgs.Timestamp,
                    rawData);

                devices.AddOrUpdate(eventArgs.BluetoothAddress, info, (_, previous) =>
                    info with
                    {
                        Name = string.IsNullOrWhiteSpace(eventArgs.Advertisement.LocalName) && info.VendorId == previous.VendorId
                            ? previous.Name : info.Name,
                        ControlAddress = info.ModelProfile.SupportsN70Control && info.VendorId == previous.VendorId
                            ? info.ControlAddress ?? previous.ControlAddress : info.ControlAddress,
                        OtherAddress = info.ModelProfile.SupportsN70Control && info.VendorId == previous.VendorId
                            ? info.OtherAddress ?? previous.OtherAddress : info.OtherAddress,
                    });
            }
        }

        watcher.Received += OnReceived;
        try
        {
            watcher.Start();
            await Task.Delay(duration, cancellationToken);
        }
        finally
        {
            watcher.Stop();
            watcher.Received -= OnReceived;
        }

        return devices.Values
            .OrderByDescending(device => QcyUuids.IsN70(device.VendorId))
            .ThenByDescending(device => device.SignalStrength)
            .ToArray();
    }

    public async Task<IBluetoothDeviceConnection> ConnectAsync(
        BluetoothDeviceInfo device,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!device.ModelProfile.SupportsN70Control)
        {
            throw new NotSupportedException("This model supports discovery only. Use the Probe --discovery-only mode.");
        }

        var candidates = new ulong?[]
            {
                device.ControlAddress,
                device.BluetoothAddress,
                device.OtherAddress,
            }
            .Where(address => address.HasValue)
            .Select(address => address!.Value)
            .Distinct()
            .ToArray();

        var diagnostics = new List<string>();
        foreach (var address in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BluetoothLEDevice? bluetoothDevice = null;
            try
            {
                bluetoothDevice = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
                cancellationToken.ThrowIfCancellationRequested();
                if (bluetoothDevice is null)
                {
                    diagnostics.Add($"{QcyAdvertisement.FormatAddress(address)}: BLE device unavailable");
                    continue;
                }

                // Cached/remembered addresses used to be unconditionally labelled N70.
                // Refuse a command session when the resolved endpoint names itself as a
                // different model. A control endpoint often reports no name at all, so an
                // empty name is not treated as a mismatch; identification still comes from
                // the vendor ID in QcyModelProfile and from CreateKnownDeviceAsync.
                if (device.SignalStrength == short.MinValue &&
                    !string.IsNullOrWhiteSpace(bluetoothDevice.Name) &&
                    !QcyModelProfile.IsN70Name(bluetoothDevice.Name))
                {
                    diagnostics.Add("Cached endpoint reports a non-N70 identity; scan for a fresh N70 advertisement.");
                    bluetoothDevice.Dispose();
                    bluetoothDevice = null;
                    continue;
                }

                var services = await bluetoothDevice.GetGattServicesForUuidAsync(
                    QcyUuids.MainService,
                    BluetoothCacheMode.Uncached);
                cancellationToken.ThrowIfCancellationRequested();

                if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
                {
                    diagnostics.Add($"{QcyAdvertisement.FormatAddress(address)}: A001 service not found ({services.Status})");
                    bluetoothDevice.Dispose();
                    bluetoothDevice = null;
                    continue;
                }

                var service = services.Services[0];
                for (var index = 1; index < services.Services.Count; index++)
                {
                    services.Services[index].Dispose();
                }

                return await WindowsBluetoothDeviceConnection.CreateAsync(
                    bluetoothDevice,
                    service,
                    device.ModelProfile,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                bluetoothDevice?.Dispose();
                diagnostics.Add($"{QcyAdvertisement.FormatAddress(address)}: {exception.Message}");
            }
        }

        throw new InvalidOperationException(
            diagnostics.Count == 0
                ? "No QCY control address was advertised."
                : string.Join(Environment.NewLine, diagnostics));
    }

    internal static byte[] ReadBuffer(IBuffer buffer)
    {
        using var reader = DataReader.FromBuffer(buffer);
        var value = new byte[reader.UnconsumedBufferLength];
        reader.ReadBytes(value);
        return value;
    }

    private static async Task<BluetoothDeviceInfo?> CreateKnownDeviceAsync(
        string deviceId,
        string? fallbackName,
        CancellationToken cancellationToken)
    {
        using var bluetoothDevice = await BluetoothLEDevice.FromIdAsync(deviceId);
        cancellationToken.ThrowIfCancellationRequested();
        if (bluetoothDevice is null)
        {
            return null;
        }

        var name = string.IsNullOrWhiteSpace(bluetoothDevice.Name)
            ? fallbackName
            : bluetoothDevice.Name;
        if (!QcyModelProfile.IsN70Name(name))
        {
            return null;
        }

        return new BluetoothDeviceInfo(
            deviceId,
            name!,
            bluetoothDevice.BluetoothAddress,
            null,
            null,
            QcyUuids.N70BlackVendorId,
            short.MinValue,
            0,
            0,
            0,
            false,
            false,
            false,
            DateTimeOffset.UtcNow);
    }

    private static byte? ReadByteProperty(DeviceInformation device, string propertyName)
    {
        if (!device.Properties.TryGetValue(propertyName, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            byte byteValue => byteValue,
            ushort ushortValue when ushortValue <= byte.MaxValue => (byte)ushortValue,
            uint uintValue when uintValue <= byte.MaxValue => (byte)uintValue,
            int intValue when intValue is >= byte.MinValue and <= byte.MaxValue => (byte)intValue,
            _ => null,
        };
    }

    private static bool ReadBooleanProperty(DeviceInformation device, string propertyName) =>
        device.Properties.TryGetValue(propertyName, out var value) && value is true;
}
