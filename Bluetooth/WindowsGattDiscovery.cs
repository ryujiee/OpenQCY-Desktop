using OpenQCY_Desktop.Protocol;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace OpenQCY_Desktop.Bluetooth;

public sealed record GattDiscoveryCharacteristic(
    Guid Uuid,
    GattCharacteristicProperties Properties,
    string ReadStatus,
    StandardGattValue? StandardValue);

public sealed record GattDiscoveryService(
    Guid Uuid,
    string Status,
    IReadOnlyList<GattDiscoveryCharacteristic> Characteristics);

public sealed record GattDiscoveryResult(string Status, IReadOnlyList<GattDiscoveryService> Services);

// Deliberately does not implement IBluetoothDeviceConnection: this diagnostic
// path cannot be passed to QcyDeviceClient and exposes no write/subscription API.
public static class WindowsGattDiscovery
{
    public static async Task<GattDiscoveryResult> InspectAsync(
        BluetoothDeviceInfo target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();
        // Use only the observed endpoint and address type. Embedded control
        // address layouts are not assumed for HT08 or unknown models.
        using var device = await BluetoothLEDevice.FromBluetoothAddressAsync(target.BluetoothAddress, target.AddressType);
        cancellationToken.ThrowIfCancellationRequested();
        if (device is null)
        {
            return new("Device unavailable", []);
        }

        var services = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var report = new List<GattDiscoveryService>();
            if (services.Status != GattCommunicationStatus.Success)
            {
                return new(services.Status.ToString(), report);
            }

            foreach (var service in services.Services)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var characteristics = new List<GattDiscoveryCharacteristic>();
                try
                {
                    var result = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (result.Status == GattCommunicationStatus.Success)
                    {
                        foreach (var characteristic in result.Characteristics)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var status = "Skipped (outside standard read allowlist or no Read property)";
                            StandardGattValue? value = null;
                            if (characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Read) &&
                                StandardGattReads.IsAllowed(service.Uuid, characteristic.Uuid))
                            {
                                try
                                {
                                    var read = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached);
                                    cancellationToken.ThrowIfCancellationRequested();
                                    status = read.Status.ToString();
                                    if (read.Status == GattCommunicationStatus.Success)
                                    {
                                        value = StandardGattReads.Parse(service.Uuid, characteristic.Uuid,
                                            WindowsBluetoothTransport.ReadBuffer(read.Value));
                                        if (value is null)
                                        {
                                            status = "Invalid standard value (not printed)";
                                        }
                                    }
                                }
                                catch (Exception exception) when (exception is not OperationCanceledException)
                                {
                                    status = $"Read failed ({exception.GetType().Name}, 0x{exception.HResult:X8})";
                                }
                            }

                            characteristics.Add(new(characteristic.Uuid, characteristic.CharacteristicProperties, status, value));
                        }
                    }

                    report.Add(new(service.Uuid, result.Status.ToString(), characteristics));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    report.Add(new(service.Uuid, $"Enumeration failed ({exception.GetType().Name}, 0x{exception.HResult:X8})", characteristics));
                }
            }

            return new(services.Status.ToString(), report);
        }
        finally
        {
            foreach (var service in services.Services)
            {
                service.Dispose();
            }
        }
    }
}
