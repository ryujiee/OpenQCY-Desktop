using System.Text.Json;
using OpenQCY_Desktop.Bluetooth;
using OpenQCY_Desktop.Protocol;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace OpenQCY_Desktop.Diagnostics;

internal static class DiscoveryProbe
{
    public static async Task<int> RunAsync(ProbeOptions options)
    {
        Console.WriteLine("OpenQCY Probe · discovery only · no proprietary reads/writes or CCCD subscriptions");
        Console.WriteLine("Scan: 12 seconds. Open the case; keep earbuds nearby. Ctrl+C cancels between Windows operations.");
        Console.WriteLine("Addresses are omitted. Review names and standard strings before sharing this output.");
        if (options.RawManufacturerData)
        {
            Console.WriteLine("PRIVATE LOCAL OUTPUT: raw manufacturer data may contain addresses. Do not share or commit it.");
        }

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        ConsoleCancelEventHandler cancel = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancel;
        try
        {
            var devices = await new WindowsBluetoothTransport().ScanForQcyDevicesAsync(
                TimeSpan.FromSeconds(12), cancellation.Token);
            foreach (var device in devices)
            {
                var vendor = QcyAdvertisement.ParseVendorId(device.ManufacturerData.Span);
                var catalog = QcyModelProfile.FromVendorId(vendor);
                Console.WriteLine($"Found: {JsonSerializer.Serialize(device.Name)} · company 0x{QcyUuids.CompanyId:X4} · " +
                    $"vendor/product ID {vendor?.ToString() ?? "unknown"} · model {catalog.ModelCode ?? "unknown"} · " +
                    $"RSSI {device.SignalStrength} dBm · address type {device.AddressType}");
                Console.WriteLine($"  Catalog: {catalog.Name} · identity evidence {catalog.IdentityEvidence} · " +
                    "proprietary control disabled in discovery");
                var manufacturer = options.RawManufacturerData
                    ? Convert.ToHexString(device.ManufacturerData.Span)
                    : QcyAdvertisement.RedactManufacturerData(device.ManufacturerData.Span);
                Console.WriteLine($"  Manufacturer payload ({device.ManufacturerData.Length} bytes, company ID excluded): {manufacturer}");
            }

            var targets = devices.Where(device => !options.VendorId.HasValue ||
                QcyAdvertisement.ParseVendorId(device.ManufacturerData.Span) == options.VendorId).ToArray();
            if (targets.Length != 1)
            {
                Console.Error.WriteLine(targets.Length == 0
                    ? "No matching QCY advertisement. Reopen the case and retry; no connection attempted."
                    : "Multiple matching endpoints. Use --vendor-id or turn off other QCY devices and retry; no connection attempted.");
                return 2;
            }

            Console.WriteLine("Connecting to the observed advertisement endpoint and enumerating all GATT services…");
            var result = await WindowsGattDiscovery.InspectAsync(targets[0], cancellation.Token);
            Console.WriteLine($"GATT discovery: {result.Status} · services {result.Services.Count}");
            foreach (var service in result.Services)
            {
                Console.WriteLine($"Service {service.Uuid:D} · {service.Status}");
                foreach (var characteristic in service.Characteristics)
                {
                    var properties = characteristic.Properties;
                    Console.WriteLine($"  Characteristic {characteristic.Uuid:D} · " +
                        $"Read={properties.HasFlag(GattCharacteristicProperties.Read)} " +
                        $"Write={properties.HasFlag(GattCharacteristicProperties.Write)} " +
                        $"WriteWithoutResponse={properties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)} " +
                        $"Notify={properties.HasFlag(GattCharacteristicProperties.Notify)} " +
                        $"Indicate={properties.HasFlag(GattCharacteristicProperties.Indicate)}");
                    var value = characteristic.StandardValue;
                    var display = value?.BatteryPercentage is byte battery
                        ? $"{battery}% (standard, side unspecified)"
                        : value?.Text;
                    Console.WriteLine($"    {characteristic.ReadStatus}" +
                        (display is null ? "" : $": {JsonSerializer.Serialize(display)}"));
                }
            }

            Console.WriteLine("Discovery handles released. Windows disconnects this BLE session when no other client holds it.");
            return result.Status == "Success" && result.Services.Count > 0 &&
                result.Services.All(service => service.Status == "Success") ? 0 : 4;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Discovery canceled or 90-second operation budget exceeded. Windows may delay cancellation of a pending GATT call.");
            return 5;
        }
        catch (Exception exception)
        {
            // Windows exception messages may contain private device IDs.
            Console.Error.WriteLine($"Discovery failed ({exception.GetType().Name}, 0x{exception.HResult:X8}).");
            return 4;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
    }
}
