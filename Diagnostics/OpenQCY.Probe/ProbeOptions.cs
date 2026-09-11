namespace OpenQCY_Desktop.Diagnostics;

internal sealed record ProbeOptions(
    bool DiscoveryOnly,
    bool DisableWearDetection,
    bool WindowsBatteryOnly,
    bool RawManufacturerData,
    ushort? VendorId,
    bool Help)
{
    public const string Usage = "Usage: OpenQCY.Probe [--discovery-only [--vendor-id <decimal ID>] [--raw-manufacturer-data] | --windows-battery | --disable-wear-detection]\n" +
        "No options: N70 state queries (sends proprietary GATT writes). --discovery-only: enumeration and allowlisted standard reads only.";

    public static ProbeOptions Parse(string[] args)
    {
        bool discovery = false, disable = false, battery = false, raw = false, help = false;
        ushort? vendor = null;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index].ToLowerInvariant())
            {
                case "--discovery-only": discovery = true; break;
                case "--disable-wear-detection": disable = true; break;
                case "--windows-battery": battery = true; break;
                case "--raw-manufacturer-data": raw = true; break;
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

        if ((discovery && (disable || battery)) || (disable && battery) || (!discovery && (raw || vendor.HasValue)))
        {
            throw new ArgumentException("Conflicting options. " + Usage);
        }

        return new(discovery, disable, battery, raw, vendor, help);
    }
}
