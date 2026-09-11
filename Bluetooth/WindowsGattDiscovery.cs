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

public sealed record GattReadOutcome(Guid Characteristic, string Status, byte[]? Value);

public sealed record DeviceInfoReadResult(string Status, IReadOnlyList<GattReadOutcome> Reads);

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

    /// <summary>
    /// Performs at most the two allowlisted ATT reads on the QCY control
    /// service. It never writes a characteristic, never writes a CCCD, never
    /// subscribes, never sets MaintainConnection, never retries, and never
    /// falls back to a proprietary command. A characteristic outside
    /// <see cref="QcyDeviceInfoReads.IsAllowed"/> is unreachable from here.
    /// </summary>
    public static async Task<DeviceInfoReadResult> ReadDeviceInfoAsync(
        BluetoothDeviceInfo target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();

        // The caller is responsible for the identity gate; refuse anyway rather
        // than trusting a single check to stand between the radio and a model
        // whose protocol is unverified.
        if (!QcyDeviceInfoReads.IsModelAllowed(QcyUuids.CompanyId, target.VendorId))
        {
            return new("Refused: the identity gate does not allow this model", []);
        }

        using var device = await BluetoothLEDevice.FromBluetoothAddressAsync(
            target.BluetoothAddress, target.AddressType);
        cancellationToken.ThrowIfCancellationRequested();
        if (device is null)
        {
            return new("Device unavailable", []);
        }

        var services = await device.GetGattServicesForUuidAsync(
            QcyDeviceInfoReads.Service, BluetoothCacheMode.Uncached);
        cancellationToken.ThrowIfCancellationRequested();
        if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
        {
            return new($"Control service unavailable ({services.Status})", []);
        }

        var service = services.Services[0];
        for (var index = 1; index < services.Services.Count; index++)
        {
            services.Services[index].Dispose();
        }

        try
        {
            var reads = new List<GattReadOutcome>();
            foreach (var uuid in QcyDeviceInfoReads.ReadOrder)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!QcyDeviceInfoReads.IsAllowed(service.Uuid, uuid))
                {
                    continue;
                }

                try
                {
                    var found = await service.GetCharacteristicsForUuidAsync(uuid, BluetoothCacheMode.Uncached);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (found.Status != GattCommunicationStatus.Success || found.Characteristics.Count == 0)
                    {
                        reads.Add(new(uuid, $"Characteristic unavailable ({found.Status})", null));
                        continue;
                    }

                    var characteristic = found.Characteristics[0];
                    if (!characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Read))
                    {
                        reads.Add(new(uuid, "Skipped (no Read property)", null));
                        continue;
                    }

                    var read = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached);
                    cancellationToken.ThrowIfCancellationRequested();
                    reads.Add(new(
                        uuid,
                        read.Status.ToString(),
                        read.Status == GattCommunicationStatus.Success
                            ? WindowsBluetoothTransport.ReadBuffer(read.Value)
                            : null));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // Stop after an exception: the session is no longer known to
                    // be healthy, and no alternative method may be attempted.
                    reads.Add(new(uuid, $"Read failed ({exception.GetType().Name}, 0x{exception.HResult:X8})", null));
                    return new("Stopped after a failed read", reads);
                }
            }

            return new("Success", reads);
        }
        finally
        {
            service.Dispose();
        }
    }
}
