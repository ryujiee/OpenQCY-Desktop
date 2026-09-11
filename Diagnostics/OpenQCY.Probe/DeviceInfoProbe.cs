using System.Text.Json;
using OpenQCY_Desktop.Bluetooth;
using OpenQCY_Desktop.Protocol;

namespace OpenQCY_Desktop.Diagnostics;

/// <summary>
/// Opt-in HT08 diagnostic: at most two direct ATT reads on the QCY control
/// service. It performs no proprietary command, no write of any kind, no CCCD
/// change, and no subscription. Results are printed for review and are never
/// persisted, so no capture enters the repository.
/// </summary>
internal static class DeviceInfoProbe
{
    public static async Task<int> RunAsync()
    {
        Console.WriteLine("OpenQCY Probe · HT08 read-only device info · opt-in");
        Console.WriteLine("Two direct ATT reads only: A001/0008 then A001/0007.");
        Console.WriteLine("No proprietary command, no write, no CCCD change, no subscription, no retry.");
        Console.WriteLine("Decoded values are unconfirmed candidates, not supported features.");

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

            var targets = devices
                .Where(device => QcyDeviceInfoReads.IsModelAllowed(
                    QcyUuids.CompanyId, QcyAdvertisement.ParseVendorId(device.ManufacturerData.Span)))
                .ToArray();

            if (targets.Length != 1)
            {
                Console.Error.WriteLine(targets.Length == 0
                    ? "No hardware-confirmed HT08 (vendor 19786) advertisement. Nothing was read; " +
                        "other QCY models are refused by the identity gate."
                    : "Multiple HT08 endpoints are advertising. Turn off the others and retry; nothing was read.");
                return 2;
            }

            var target = targets[0];
            var profile = target.ModelProfile;
            Console.WriteLine();
            Console.WriteLine("Identity");
            Console.WriteLine($"  Name: {JsonSerializer.Serialize(target.Name)}");
            Console.WriteLine($"  Catalog: {profile.Name} · model {profile.ModelCode}");
            Console.WriteLine($"  Company 0x{QcyUuids.CompanyId:X4} · vendor {target.VendorId} / 0x{target.VendorId:X4}");
            Console.WriteLine($"  Identity evidence: {profile.IdentityEvidence}");
            Console.WriteLine($"  N70 control permitted: {profile.SupportsN70Control}");
            Console.WriteLine($"  RSSI {target.SignalStrength} dBm · address type {target.AddressType}");

            var result = await WindowsGattDiscovery.ReadDeviceInfoAsync(target, cancellation.Token);
            Console.WriteLine();
            Console.WriteLine($"Read session: {result.Status}");

            foreach (var read in result.Reads)
            {
                Console.WriteLine();
                Console.WriteLine($"Reading {QcyDeviceInfoReads.Service:D} / {read.Characteristic:D}");
                Console.WriteLine($"  ATT status: {read.Status}");
                if (read.Value is null)
                {
                    Console.WriteLine("  No payload. No alternative method attempted.");
                    continue;
                }

                Console.WriteLine($"  Payload length: {read.Value.Length}");
                Console.WriteLine($"  Raw: {FormatBytes(read.Value)}");
                if (read.Characteristic == QcyDeviceInfoReads.Battery)
                {
                    PrintBattery(read.Value, profile);
                }
                else if (read.Characteristic == QcyDeviceInfoReads.Firmware)
                {
                    PrintFirmware(read.Value);
                }
            }

            Console.WriteLine();
            Console.WriteLine("No proprietary writes performed.");
            Console.WriteLine("No subscriptions created.");
            Console.WriteLine("No CCCD modified.");
            Console.WriteLine("Nothing was written to disk; no fixture was created.");
            return result.Status == "Success" && result.Reads.All(read => read.Value is not null) ? 0 : 4;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Canceled or 90-second budget exceeded. Windows may delay a pending GATT call.");
            return 5;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Read session failed ({exception.GetType().Name}, 0x{exception.HResult:X8}).");
            return 4;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
    }

    private static void PrintBattery(byte[] value, QcyModelProfile profile)
    {
        var reading = QcyBatteryReading.Parse(value, profile);
        if (reading is null)
        {
            Console.WriteLine("  Battery interpretation: rejected.");
            Console.WriteLine(value.Length < 3
                ? "    Payload shorter than the 3 bytes the confirmed layout requires."
                : "    A level above 100% means the confirmed layout does not hold here.");
            Console.WriteLine("    No other layout was attempted. Report the raw bytes above.");
            return;
        }

        Console.WriteLine("  Battery interpretation:");
        Console.WriteLine($"    Left:  {reading.Left}%{Charging(reading.LeftCharging)}");
        Console.WriteLine($"    Right: {reading.Right}%{Charging(reading.RightCharging)}");
        Console.WriteLine(profile.SupportsCaseBattery
            ? $"    Case:  {reading.Case}%{Charging(reading.CaseCharging)}"
            : $"    Case:  — not supported by the {profile.ModelCode}; the official QCY application does not " +
                "show one for this model either.");

        if (reading.UndecodedBytes.Count > 0)
        {
            Console.WriteLine($"    Undecoded trailing bytes: {FormatBytes([.. reading.UndecodedBytes])} " +
                "(meaning unknown; deliberately not presented as a level)");
        }

        Console.WriteLine($"    Evidence: {QcyBatteryReading.Evidence} — left and right confirmed on HT08 hardware. " +
            "The charging bit has not been observed set, so that bit remains unverified.");
    }

    private static void PrintFirmware(byte[] value)
    {
        var reading = QcyFirmwareReading.Parse(value);
        if (reading is null)
        {
            Console.WriteLine($"  Firmware interpretation: unknown format ({value.Length} bytes).");
            Console.WriteLine("    Only 3-byte and 6-byte layouts are decoded. Nothing was inferred.");
            return;
        }

        Console.WriteLine($"  Firmware interpretation: {reading.Display}");
        Console.WriteLine($"    Left:  {reading.Left}");
        Console.WriteLine($"    Right: {reading.Right ?? "not reported by this layout"}");
        Console.WriteLine($"    Evidence: {reading.Evidence}");
    }

    private static string Charging(bool charging) => charging ? " (charging bit set)" : "";

    private static string FormatBytes(byte[] value) =>
        string.Join(" ", value.Select(item => item.ToString("X2")));
}
