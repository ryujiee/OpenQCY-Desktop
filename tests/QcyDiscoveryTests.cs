using System.Globalization;
using System.Text;
using OpenQCY_Desktop.Diagnostics;
using OpenQCY_Desktop.Protocol;
using OpenQCY_Desktop.Bluetooth;
using Windows.Devices.Bluetooth;

namespace OpenQCY.Desktop.Tests;

[TestClass]
public sealed class QcyDiscoveryTests
{
    [TestMethod]
    public void DiscoveryKeepsUnknownPacketsAndDoesNotReuseN70AddressesOrBatteryForHt08()
    {
        foreach (var payload in new byte[][] { [], [0x4D], [0x4D, 0x4A],
            [0x4D, 0x4A, 0, 0, 0, 80, 70, 60, 0, 0, 0, 1, 2, 3, 4, 5, 6] })
        {
            var device = BluetoothDeviceInfo.FromAdvertisement("HT08 test", 0, BluetoothAddressType.Random,
                -42, DateTimeOffset.UnixEpoch, payload);
            CollectionAssert.AreEqual(payload, device.ManufacturerData.ToArray());
            Assert.AreEqual("HT08 test", device.Name);
            Assert.AreEqual((short)-42, device.SignalStrength);
            Assert.AreEqual(BluetoothAddressType.Random, device.AddressType);
            Assert.IsNull(device.ControlAddress);
            Assert.IsNull(device.OtherAddress);
            Assert.AreEqual((byte)0, device.LeftBattery);
            Assert.IsFalse(device.ModelProfile.SupportsN70Control);
        }
    }

    [TestMethod]
    public void ScanPreservesN70BatteryAndRequiresTheOriginalMinimumPacketLengthForControl()
    {
        var device = BluetoothDeviceInfo.FromAdvertisement(null, 0, BluetoothAddressType.Public,
            -42, DateTimeOffset.UnixEpoch, [0x5D, 0x45, 0, 0, 0, 0xD0, 70, 60]);
        Assert.AreEqual(QcyModelProfile.N70, device.ModelProfile);
        Assert.AreEqual((byte)80, device.LeftBattery);
        Assert.IsTrue(device.LeftCharging);
        Assert.AreEqual((byte)70, device.RightBattery);
        Assert.AreEqual((byte)60, device.CaseBattery);

        var shortPacket = BluetoothDeviceInfo.FromAdvertisement("QCY N70", 0, BluetoothAddressType.Public,
            -42, DateTimeOffset.UnixEpoch, [0x5D, 0x45]);
        Assert.IsFalse(shortPacket.ModelProfile.SupportsN70Control);
    }

    [TestMethod]
    [DataRow(19785, "HT08", false)]
    [DataRow(19786, "HT08", false)]
    [DataRow(23872, "HT18", true)]
    [DataRow(23877, "HT18", true)]
    [DataRow(19790, null, false)] // Pro Plus is not assumed to be the same HT08.
    [DataRow(21020, null, false)] // External modelId is shared by many products.
    [DataRow(0, null, false)]
    [DataRow(65535, null, false)]
    public void IdentityDoesNotGrantAnotherModelsCommands(int id, string? model, bool control)
    {
        var profile = QcyModelProfile.FromVendorId((ushort)id);
        Assert.AreEqual(model, profile.ModelCode);
        Assert.AreEqual(control, profile.SupportsN70Control);
    }

    [TestMethod]
    public void Ht08IsIdentifiedFromTheHardwareConfirmedCompanyAndVendorId()
    {
        // Observed on a contributor's QCY MeloBuds Pro on 2026-09-11: QCY
        // company ID 0x521C with vendor/product ID 19786 (0x4D4A). The payload
        // below is synthetic; only the two identity bytes are real.
        // Read through locals so the constants are compared at run time.
        var observedCompanyId = ushort.Parse("521C", NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        Assert.AreEqual(QcyUuids.CompanyId, observedCompanyId);

        var identity = new byte[] { 0x4D, 0x4A };
        Assert.AreEqual((ushort)19786, QcyAdvertisement.ParseVendorId(identity));
        Assert.AreEqual(QcyModelProfile.Ht08VendorId, QcyAdvertisement.ParseVendorId(identity));

        var profile = QcyModelProfile.FromVendorId(19786);
        Assert.AreEqual(QcyModelProfile.Ht08, profile);
        Assert.AreEqual("QCY MeloBuds Pro", profile.Name);
        Assert.AreEqual("HT08", profile.ModelCode);
        Assert.AreEqual(QcyIdentityEvidence.HardwareConfirmed, profile.IdentityEvidence);

        // Identification must never imply control.
        Assert.IsFalse(profile.SupportsN70Control);
    }

    [TestMethod]
    public void IdentityEvidenceIsTrackedSeparatelyFromTheModelCode()
    {
        // 19785 is a public-catalog listing only; it must not be promoted by
        // the hardware confirmation of the neighbouring ID 19786.
        Assert.AreEqual(QcyIdentityEvidence.PublicCatalog,
            QcyModelProfile.FromVendorId(QcyModelProfile.Ht08CatalogVendorId).IdentityEvidence);
        Assert.AreEqual(QcyIdentityEvidence.HardwareConfirmed,
            QcyModelProfile.FromVendorId(QcyModelProfile.Ht08VendorId).IdentityEvidence);
        Assert.AreEqual(QcyIdentityEvidence.HardwareConfirmed,
            QcyModelProfile.FromVendorId(QcyUuids.N70AlternateVendorId).IdentityEvidence);

        // Pro Plus (19790) and any other ID stay unidentified and fail closed.
        foreach (var vendorId in new ushort[] { 19790, 21020, 0, 65535 })
        {
            var profile = QcyModelProfile.FromVendorId(vendorId);
            Assert.AreEqual(QcyModelProfile.Unknown, profile);
            Assert.AreEqual(QcyIdentityEvidence.None, profile.IdentityEvidence);
            Assert.IsFalse(profile.SupportsN70Control);
        }
    }

    [TestMethod]
    public void Ht08AdvertisementIsIdentifiedWithoutReusingTheN70Layout()
    {
        // 24 synthetic payload bytes: the real HT08 advertisement length, with
        // only the two identity bytes taken from hardware. The remaining bytes
        // are filler and must never be decoded as N70 battery or addresses.
        var payload = new byte[24];
        payload[0] = 0x4D;
        payload[1] = 0x4A;
        for (var index = 2; index < payload.Length; index++)
        {
            payload[index] = 0xAB;
        }

        var device = BluetoothDeviceInfo.FromAdvertisement("QCY MeloBuds Pro", 0,
            BluetoothAddressType.Random, -42, DateTimeOffset.UnixEpoch, payload);

        Assert.AreEqual(QcyModelProfile.Ht08, device.ModelProfile);
        Assert.IsFalse(device.ModelProfile.SupportsN70Control);
        Assert.IsNull(device.ControlAddress);
        Assert.IsNull(device.OtherAddress);
        Assert.AreEqual((byte)0, device.LeftBattery);
        Assert.AreEqual((byte)0, device.RightBattery);
        Assert.AreEqual((byte)0, device.CaseBattery);

        // Everything after the identity bytes stays redacted in shared output.
        var redacted = QcyAdvertisement.RedactManufacturerData(device.ManufacturerData.Span).Split(' ');
        Assert.HasCount(24, redacted);
        Assert.IsTrue(redacted[2..].All(value => value == "XX"));
    }

    [TestMethod]
    [DataRow("QCY MeloBuds N70", true)]
    [DataRow("qcy n70", true)]
    [DataRow("MeloBuds N70", true)]
    [DataRow("QCY MeloBuds Pro", false)]
    [DataRow("QCY HT08", false)]
    [DataRow("QCY device", false)]
    [DataRow("A001", false)]
    [DataRow("", false)]
    [DataRow(null, false)]
    public void CacheRequiresN70Identity(string? name, bool expected) =>
        Assert.AreEqual(expected, QcyModelProfile.IsN70Name(name));

    [TestMethod]
    public void ManufacturerIdentityAcceptsShortDataWithoutInventingBatteryOrAddresses()
    {
        Assert.IsNull(QcyAdvertisement.ParseVendorId([]));
        Assert.IsNull(QcyAdvertisement.ParseVendorId([0x4D]));
        Assert.AreEqual((ushort)19786, QcyAdvertisement.ParseVendorId([0x4D, 0x4A]));
        Assert.IsNull(QcyAdvertisement.Parse([0x4D, 0x4A]));
        Assert.AreEqual(QcyModelProfile.Unknown, QcyModelProfile.FromVendorId(null));
    }

    [TestMethod]
    public void ManufacturerRedactionHidesAllNonIdentityBytesIncludingUnknownLayouts()
    {
        // Synthetic bytes only; not a device capture.
        var data = Enumerable.Repeat((byte)0xAB, 40).ToArray();
        data[0] = 0x4D;
        data[1] = 0x4A;
        var redacted = QcyAdvertisement.RedactManufacturerData(data).Split(' ');
        Assert.HasCount(40, redacted);
        CollectionAssert.AreEqual(new[] { "4D", "4A" }, redacted[..2]);
        Assert.IsTrue(redacted[2..].All(value => value == "XX"));
        Assert.AreEqual("XX", QcyAdvertisement.RedactManufacturerData([0xAB]));
        Assert.AreEqual("", QcyAdvertisement.RedactManufacturerData([]));
        Assert.AreEqual((byte)0xAB, data[12]); // Formatting must not modify local data.
    }

    [TestMethod]
    public void StandardReadAllowlistRequiresBothServiceAndCharacteristic()
    {
        var allowed = new[]
        {
            (StandardGattReads.BatteryService, StandardGattReads.BatteryLevel),
            (StandardGattReads.DeviceInformationService, StandardGattReads.FirmwareRevision),
            (StandardGattReads.DeviceInformationService, StandardGattReads.ModelNumber),
            (StandardGattReads.DeviceInformationService, StandardGattReads.ManufacturerName),
        };
        foreach (var (service, characteristic) in allowed)
        {
            Assert.IsTrue(StandardGattReads.IsAllowed(service, characteristic));
            Assert.IsFalse(StandardGattReads.IsAllowed(QcyUuids.MainService, characteristic));
        }

        foreach (var characteristic in new[] { QcyUuids.Command, QcyUuids.Notification, QcyUuids.Battery,
            QcyUuids.FirmwareVersion, QcyUuids.KeyFunctions, QcyUuids.Equalizer,
            Guid.Parse("00002a25-0000-1000-8000-00805f9b34fb") }) // Serial number excluded.
        {
            Assert.IsFalse(StandardGattReads.IsAllowed(QcyUuids.MainService, characteristic));
            Assert.IsFalse(StandardGattReads.IsAllowed(StandardGattReads.DeviceInformationService, characteristic));
        }
    }

    [TestMethod]
    public void StandardValuesRejectMalformedPayloads()
    {
        foreach (var percentage in new byte[] { 0, 50, 100 })
        {
            Assert.AreEqual(percentage, StandardGattReads.Parse(
                StandardGattReads.BatteryService, StandardGattReads.BatteryLevel, [percentage])?.BatteryPercentage);
        }

        foreach (var invalid in new byte[][] { [], [101], [255], [50, 50] })
        {
            Assert.IsNull(StandardGattReads.Parse(StandardGattReads.BatteryService, StandardGattReads.BatteryLevel, invalid));
        }

        Assert.AreEqual("1.2.3", StandardGattReads.Parse(StandardGattReads.DeviceInformationService,
            StandardGattReads.FirmwareRevision, Encoding.UTF8.GetBytes("1.2.3"))?.Text);
        foreach (var invalid in new byte[][] { [], [0xFF], [0xC0, 0xAF], [0x1B, 0x41], [0], new byte[513] })
        {
            Assert.IsNull(StandardGattReads.Parse(StandardGattReads.DeviceInformationService, StandardGattReads.FirmwareRevision, invalid));
        }

        Assert.IsNull(StandardGattReads.Parse(QcyUuids.MainService, QcyUuids.FirmwareVersion, [1, 2, 3]));
    }

    [TestMethod]
    public void ProbeRejectsConflictsAndTyposBeforeBluetoothAccess()
    {
        foreach (var args in new[]
        {
            new[] { "--discovery-only", "--disable-wear-detection" },
            new[] { "--discovery-only", "--windows-battery" },
            new[] { "--windows-battery", "--disable-wear-detection" },
            new[] { "--discovery-onyl" }, new[] { "--vendor-id", "19786" },
            new[] { "--raw-manufacturer-data" },
            new[] { "--discovery-only", "--vendor-id" },
            new[] { "--discovery-only", "--vendor-id", "65536" },
            new[] { "--discovery-only", "--vendor-id", "-1" },
            new[] { "--discovery-only", "--vendor-id", "19786", "--vendor-id", "19785" },
        })
        {
            Assert.Throws<ArgumentException>(() => ProbeOptions.Parse(args));
        }

        var options = ProbeOptions.Parse(["--DISCOVERY-ONLY", "--vendor-id", "19786"]);
        Assert.IsTrue(options.DiscoveryOnly);
        Assert.AreEqual((ushort)19786, options.VendorId);
        Assert.IsFalse(options.DisableWearDetection);
        Assert.IsFalse(options.RawManufacturerData);
        Assert.IsFalse(ProbeOptions.Parse([]).DiscoveryOnly); // Existing N70 mode preserved.
    }
}
