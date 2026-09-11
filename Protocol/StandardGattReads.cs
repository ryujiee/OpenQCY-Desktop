using System.Text;

namespace OpenQCY_Desktop.Protocol;

public sealed record StandardGattValue(byte? BatteryPercentage = null, string? Text = null);

public static class StandardGattReads
{
    public static readonly Guid BatteryService = BluetoothUuid(0x180F);
    public static readonly Guid DeviceInformationService = BluetoothUuid(0x180A);
    public static readonly Guid BatteryLevel = BluetoothUuid(0x2A19);
    public static readonly Guid FirmwareRevision = BluetoothUuid(0x2A26);
    public static readonly Guid ModelNumber = BluetoothUuid(0x2A24);
    public static readonly Guid ManufacturerName = BluetoothUuid(0x2A29);

    public static bool IsAllowed(Guid service, Guid characteristic) =>
        (service == BatteryService && characteristic == BatteryLevel) ||
        (service == DeviceInformationService &&
            (characteristic == FirmwareRevision || characteristic == ModelNumber ||
                characteristic == ManufacturerName));

    public static StandardGattValue? Parse(Guid service, Guid characteristic, ReadOnlySpan<byte> value)
    {
        if (!IsAllowed(service, characteristic))
        {
            return null;
        }

        if (characteristic == BatteryLevel)
        {
            return value.Length == 1 && value[0] <= 100 ? new(BatteryPercentage: value[0]) : null;
        }

        if (value.Length is 0 or > 512)
        {
            return null;
        }

        try
        {
            var text = new UTF8Encoding(false, true).GetString(value);
            return text.Any(char.IsControl) ? null : new(Text: text);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static Guid BluetoothUuid(ushort value) =>
        Guid.Parse($"0000{value:x4}-0000-1000-8000-00805f9b34fb");
}
