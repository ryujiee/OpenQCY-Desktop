using OpenQCY_Desktop.Protocol;
using Windows.Devices.Bluetooth;

namespace OpenQCY_Desktop.Bluetooth;

public sealed record BluetoothDeviceInfo(
    string Id,
    string Name,
    ulong BluetoothAddress,
    ulong? ControlAddress,
    ulong? OtherAddress,
    ushort VendorId,
    short SignalStrength,
    byte LeftBattery,
    byte RightBattery,
    byte CaseBattery,
    bool LeftCharging,
    bool RightCharging,
    bool CaseCharging,
    DateTimeOffset LastSeen)
{
    public ReadOnlyMemory<byte> ManufacturerData { get; init; }
    public BluetoothAddressType AddressType { get; init; } = BluetoothAddressType.Unspecified;
    public QcyModelProfile ModelProfile =>
        !ManufacturerData.IsEmpty && ManufacturerData.Length < 8
            ? QcyModelProfile.Unknown
            : QcyModelProfile.FromVendorId(VendorId);

    public static BluetoothDeviceInfo FromAdvertisement(
        string? localName,
        ulong address,
        BluetoothAddressType addressType,
        short signalStrength,
        DateTimeOffset timestamp,
        ReadOnlySpan<byte> manufacturerData)
    {
        var vendorId = QcyAdvertisement.ParseVendorId(manufacturerData) ?? 0;
        // Short/unknown packets remain visible. The existing battery/control
        // address layout is interpreted only for N70, never HT08.
        var advertisement = QcyUuids.IsN70(vendorId) ? QcyAdvertisement.Parse(manufacturerData) : null;
        return new BluetoothDeviceInfo(
            QcyAdvertisement.FormatAddress(address),
            string.IsNullOrWhiteSpace(localName) ? QcyModelProfile.FromVendorId(vendorId).Name : localName,
            address,
            advertisement?.ControlAddress,
            advertisement?.OtherAddress,
            vendorId,
            signalStrength,
            advertisement?.LeftBattery ?? 0,
            advertisement?.RightBattery ?? 0,
            advertisement?.CaseBattery ?? 0,
            advertisement?.LeftCharging ?? false,
            advertisement?.RightCharging ?? false,
            advertisement?.CaseCharging ?? false,
            timestamp)
        {
            ManufacturerData = manufacturerData.ToArray(),
            AddressType = addressType,
        };
    }
}
