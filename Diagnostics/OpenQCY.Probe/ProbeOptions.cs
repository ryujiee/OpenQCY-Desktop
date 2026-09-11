namespace OpenQCY_Desktop.Diagnostics;

internal sealed record ProbeOptions(
    bool DiscoveryOnly,
    bool DisableWearDetection,
    bool WindowsBatteryOnly,
    bool RawManufacturerData,
    bool ReadHt08DeviceInfo,
    ushort? VendorId,
    bool Help)
{
    public const string Usage = "Usage: OpenQCY.Probe [--discovery-only [--vendor-id <decimal ID>] [--raw-manufacturer-data] | --read-ht08-device-info | --windows-battery | --disable-wear-detection]\n" +
        "No options: N70 state queries (sends proprietary GATT writes). --discovery-only: enumeration and allowlisted standard reads only.\n" +
        "--read-ht08-device-info: opt-in, HT08 only. Two direct ATT reads (A001/0008 then A001/0007). No write, no CCCD, no subscription, no command.";

    public static ProbeOptions Parse(string[] args)
    {
        bool discovery = false, disable = false, battery = false, raw = false, readInfo = false, help = false;
        ushort? vendor = null;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index].ToLowerInvariant())
            {
                case "--discovery-only": discovery = true; break;
                case "--disable-wear-detection": disable = true; break;
                case "--windows-battery": battery = true; break;
                case "--raw-manufacturer-data": raw = true; break;
                case "--read-ht08-device-info": readInfo = true; break;
                case "--help": help = true; break;
                case "--vendor-id":
                    if (++index >= args.Length || !ushort.TryParse(args[index], out var id) || vendor.HasValue)
                    {
                        throw new ArgumentException("--vendor-id requires one decimal ID from 0 to 65535.");
                    }

                    vendor = id;
                    break;
                default: throw new ArgumentException("Unknown option. " + Usage);
            }
        }

        // The opt-in read is mutually exclusive with every other mode, and the
        // discovery-only flag never implies it.
        var modes = (discovery ? 1 : 0) + (disable ? 1 : 0) + (battery ? 1 : 0) + (readInfo ? 1 : 0);
        if (modes > 1 || (!discovery && (raw || vendor.HasValue)))
        {
            throw new ArgumentException("Conflicting options. " + Usage);
        }

        return new(discovery, disable, battery, raw, readInfo, vendor, help);
    }
}
