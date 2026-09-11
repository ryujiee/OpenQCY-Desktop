using OpenQCY_Desktop.Diagnostics;
using OpenQCY_Desktop.Protocol;

namespace OpenQCY.Desktop.Tests;

/// <summary>
/// Every payload here is synthetic. No byte came from a device capture.
/// </summary>
[TestClass]
public sealed class QcyDeviceInfoReadTests
{
    [TestMethod]
    public void GateAllowsOnlyTheHardwareConfirmedHt08()
    {
        Assert.IsTrue(QcyDeviceInfoReads.IsModelAllowed(QcyUuids.CompanyId, QcyModelProfile.Ht08VendorId));
    }

    [TestMethod]
    [DataRow(19785)] // Public-catalog HT08 listing, never confirmed on hardware.
    [DataRow(19790)] // MeloBuds Pro Plus.
    [DataRow(23872)] // N70.
    [DataRow(23877)] // N70 alternate.
    [DataRow(21020)] // Shared external modelId, not a vendor ID.
    [DataRow(0)]
    [DataRow(65535)]
    public void GateRefusesEveryOtherModel(int vendorId) =>
        Assert.IsFalse(QcyDeviceInfoReads.IsModelAllowed(QcyUuids.CompanyId, (ushort)vendorId));

    [TestMethod]
    public void GateRefusesAMissingVendorIdAndAForeignCompanyId()
    {
        Assert.IsFalse(QcyDeviceInfoReads.IsModelAllowed(QcyUuids.CompanyId, null));

        // The right vendor ID under someone else's company ID is not an HT08.
        foreach (var companyId in new ushort[] { 0x0000, 0x004C, 0x00E0, 0x521D })
        {
            Assert.IsFalse(QcyDeviceInfoReads.IsModelAllowed(companyId, QcyModelProfile.Ht08VendorId));
        }
    }

    [TestMethod]
    public void GateRequiresEveryIdentityConditionSimultaneously()
    {
        var profile = QcyModelProfile.FromVendorId(QcyModelProfile.Ht08VendorId);
        Assert.AreEqual(QcyModelProfile.Ht08, profile);
        Assert.AreEqual("HT08", profile.ModelCode);
        Assert.AreEqual(QcyIdentityEvidence.HardwareConfirmed, profile.IdentityEvidence);
        Assert.IsFalse(profile.SupportsN70Control);

        // The refused public-catalog profile shares the model code, so the
        // model code alone must not be what opens the gate.
        var catalog = QcyModelProfile.FromVendorId(QcyModelProfile.Ht08CatalogVendorId);
        Assert.AreEqual("HT08", catalog.ModelCode);
        Assert.AreEqual(QcyIdentityEvidence.PublicCatalog, catalog.IdentityEvidence);
        Assert.IsFalse(QcyDeviceInfoReads.IsModelAllowed(QcyUuids.CompanyId, QcyModelProfile.Ht08CatalogVendorId));
    }

    [TestMethod]
    public void OnlyBatteryAndFirmwareOnTheControlServiceAreReadable()
    {
        Assert.IsTrue(QcyDeviceInfoReads.IsAllowed(QcyUuids.MainService, QcyUuids.Battery));
        Assert.IsTrue(QcyDeviceInfoReads.IsAllowed(QcyUuids.MainService, QcyUuids.FirmwareVersion));

        // Battery is attempted first, and only two reads exist.
        CollectionAssert.AreEqual(
            new[] { QcyUuids.Battery, QcyUuids.FirmwareVersion },
            QcyDeviceInfoReads.ReadOrder.ToArray());
    }

    [TestMethod]
    public void EveryOtherProprietaryCharacteristicStaysUnreadable()
    {
        foreach (var characteristic in new[]
        {
            QcyUuids.Command,        // 1001
            QcyUuids.Notification,   // 1002
            QcyUuids.Equalizer,      // 000B
            QcyUuids.KeyFunctions,   // 000D
            Guid.Parse("00002a25-0000-1000-8000-00805f9b34fb"), // Serial number.
            Guid.Parse("00002001-0000-1000-8000-00805f9b34fb"), // 7033 service.
            Guid.Parse("00002002-0000-1000-8000-00805f9b34fb"),
        })
        {
            Assert.IsFalse(QcyDeviceInfoReads.IsAllowed(QcyUuids.MainService, characteristic));
        }

        // The allowed characteristics are not readable under another service,
        // including the out-of-scope 7033 service.
        foreach (var service in new[]
        {
            Guid.Parse("00007033-0000-1000-8000-00805f9b34fb"),
            StandardGattReads.BatteryService,
            StandardGattReads.DeviceInformationService,
        })
        {
            Assert.IsFalse(QcyDeviceInfoReads.IsAllowed(service, QcyUuids.Battery));
            Assert.IsFalse(QcyDeviceInfoReads.IsAllowed(service, QcyUuids.FirmwareVersion));
        }
    }

    [TestMethod]
    public void BatteryMatchesThePayloadObservedOnHt08Hardware()
    {
        // Observed on a contributor's HT08 on 2026-09-11: A001/0008 returned
        // three bytes, 5F 5F 00, with both earbuds reporting 95%.
        var candidate = QcyBatteryReading.Parse([0x5F, 0x5F, 0x00], QcyModelProfile.Ht08);
        Assert.IsNotNull(candidate);
        Assert.AreEqual((byte)95, candidate.Left);
        Assert.AreEqual((byte)95, candidate.Right);
        Assert.IsFalse(candidate.LeftCharging);
        Assert.IsFalse(candidate.RightCharging);

        // The third byte must stay undecoded. Reporting it as a case level
        // would have fabricated a 0% case while both earbuds were at 95%.
        CollectionAssert.AreEqual(new byte[] { 0x00 }, candidate.UndecodedBytes.ToArray());
    }

    [TestMethod]
    public void BatteryDecodesLevelsAndTheChargingBit()
    {
        var candidate = QcyBatteryReading.Parse([80, 70, 60], QcyModelProfile.Ht08);
        Assert.IsNotNull(candidate);
        Assert.AreEqual((byte)80, candidate.Left);
        Assert.AreEqual((byte)70, candidate.Right);
        Assert.IsFalse(candidate.LeftCharging);
        Assert.IsFalse(candidate.RightCharging);

        // Bit 7 marks charging in the QCY family and must not leak into the
        // percentage. This bit has not yet been observed set on HT08 hardware.
        var charging = QcyBatteryReading.Parse([0x80 | 80, 70, 0x00], QcyModelProfile.Ht08);
        Assert.IsNotNull(charging);
        Assert.AreEqual((byte)80, charging.Left);
        Assert.IsTrue(charging.LeftCharging);
        Assert.AreEqual((byte)70, charging.Right);
        Assert.IsFalse(charging.RightCharging);

        var bothCharging = QcyBatteryReading.Parse([0x80 | 80, 0x80 | 70, 0x00], QcyModelProfile.Ht08);
        Assert.IsNotNull(bothCharging);
        Assert.IsTrue(bothCharging.LeftCharging);
        Assert.IsTrue(bothCharging.RightCharging);
    }

    [TestMethod]
    public void BatteryAcceptsTheBoundaryLevels()
    {
        var empty = QcyBatteryReading.Parse([0, 0, 0], QcyModelProfile.Ht08);
        Assert.IsNotNull(empty);
        Assert.AreEqual((byte)0, empty.Left);
        Assert.AreEqual((byte)0, empty.Right);

        var full = QcyBatteryReading.Parse([100, 100, 0], QcyModelProfile.Ht08);
        Assert.IsNotNull(full);
        Assert.AreEqual((byte)100, full.Left);
        Assert.AreEqual((byte)100, full.Right);

        var fullCharging = QcyBatteryReading.Parse([0x80 | 100, 0x80 | 100, 0x00], QcyModelProfile.Ht08);
        Assert.IsNotNull(fullCharging);
        Assert.AreEqual((byte)100, fullCharging.Left);
        Assert.AreEqual((byte)100, fullCharging.Right);
        Assert.IsTrue(fullCharging.LeftCharging);
    }

    [TestMethod]
    public void BatteryRejectsInvariantViolationsInsteadOfGuessing()
    {
        // A level above 100 in a decoded byte means the layout does not hold.
        foreach (var payload in new byte[][]
        {
            [101, 70, 0], [80, 101, 0], [127, 0, 0],
            [0x80 | 101, 70, 0], [0xFF, 0xFF, 0xFF],
        })
        {
            Assert.IsNull(QcyBatteryReading.Parse(payload, QcyModelProfile.Ht08));
        }

        // Short payloads are rejected, never zero-filled.
        foreach (var payload in new byte[][] { [], [80], [80, 70] })
        {
            Assert.IsNull(QcyBatteryReading.Parse(payload, QcyModelProfile.Ht08));
        }
    }

    [TestMethod]
    public void BatteryNeverInterpretsTheThirdByteAsACaseLevel()
    {
        // The third byte is not validated as a level and not decoded, so a
        // value above 100 there is preserved verbatim rather than rejected or
        // presented as a case percentage.
        var candidate = QcyBatteryReading.Parse([80, 70, 0xFF], QcyModelProfile.Ht08);
        Assert.IsNotNull(candidate);
        Assert.AreEqual((byte)80, candidate.Left);
        Assert.AreEqual((byte)70, candidate.Right);
        CollectionAssert.AreEqual(new byte[] { 0xFF }, candidate.UndecodedBytes.ToArray());

        Assert.IsNull(candidate.Case);
        Assert.IsFalse(candidate.CaseCharging);

        // Trailing bytes beyond the third are preserved too.
        var longer = QcyBatteryReading.Parse([80, 70, 60, 0xAB, 0xCD], QcyModelProfile.Ht08);
        Assert.IsNotNull(longer);
        Assert.IsNull(longer.Case);
        CollectionAssert.AreEqual(new byte[] { 60, 0xAB, 0xCD }, longer.UndecodedBytes.ToArray());
    }

    [TestMethod]
    public void CaseSupportIsAProfileFactNotAPayloadHeuristic()
    {
        // The HT08 does not report a case level, so 0x00 in the third byte is
        // never surfaced as 0%.
        Assert.IsFalse(QcyModelProfile.Ht08.SupportsCaseBattery);
        var ht08 = QcyBatteryReading.Parse([0x5F, 0x5F, 0x00], QcyModelProfile.Ht08);
        Assert.IsNotNull(ht08);
        Assert.IsNull(ht08.Case);

        // A model that does report a case must keep reporting it, including a
        // genuine 0%. The rule is per-profile, never "a zero byte means null".
        Assert.IsTrue(QcyModelProfile.N70.SupportsCaseBattery);
        var n70 = QcyBatteryReading.Parse([0x5F, 0x5F, 0x00], QcyModelProfile.N70);
        Assert.IsNotNull(n70);
        Assert.AreEqual((byte)0, n70.Case);
        Assert.IsEmpty(n70.UndecodedBytes.ToArray());

        // A profile without the level capabilities decodes nothing at all.
        foreach (var profile in new[] { QcyModelProfile.Unknown, QcyModelProfile.Ht08Catalog })
        {
            Assert.IsFalse(profile.SupportsLeftBattery);
            Assert.IsNull(QcyBatteryReading.Parse([0x5F, 0x5F, 0x00], profile));
        }
    }

    [TestMethod]
    public void Ht08CapabilitiesDescribeReadOnlySupportWithoutControl()
    {
        var profile = QcyModelProfile.Ht08;
        Assert.IsTrue(profile.SupportsLeftBattery);
        Assert.IsTrue(profile.SupportsRightBattery);
        Assert.IsFalse(profile.SupportsCaseBattery);
        Assert.IsTrue(profile.SupportsFirmwareRead);
        Assert.IsFalse(profile.SupportsN70Control);

        // The N70 keeps every capability it had, including the case level.
        Assert.IsTrue(QcyModelProfile.N70.SupportsN70Control);
        Assert.IsTrue(QcyModelProfile.N70.SupportsCaseBattery);
        Assert.IsTrue(QcyModelProfile.N70.SupportsFirmwareRead);

        // Unconfirmed and unknown models gain nothing.
        foreach (var other in new[] { QcyModelProfile.Ht08Catalog, QcyModelProfile.Unknown })
        {
            Assert.IsFalse(other.SupportsLeftBattery);
            Assert.IsFalse(other.SupportsRightBattery);
            Assert.IsFalse(other.SupportsCaseBattery);
            Assert.IsFalse(other.SupportsFirmwareRead);
            Assert.IsFalse(other.SupportsN70Control);
        }
    }

    [TestMethod]
    public void FirmwareMatchesThePayloadObservedOnHt08Hardware()
    {
        // Observed on a contributor's HT08 on 2026-09-11: A001/0007 returned
        // six bytes, 02 00 06 02 00 06. The official QCY application reports
        // 2.0.6 as the installed version, which confirms the decoding.
        var reading = QcyFirmwareReading.Parse([0x02, 0x00, 0x06, 0x02, 0x00, 0x06]);
        Assert.IsNotNull(reading);
        Assert.AreEqual("2.0.6", reading.Left);
        Assert.AreEqual("2.0.6", reading.Right);
        Assert.AreEqual("L 2.0.6 · R 2.0.6", reading.Display);
        Assert.AreEqual(QcyFormatEvidence.HardwareConfirmed, reading.Evidence);
    }

    [TestMethod]
    public void FirmwareDecodesBothSidesIndependently()
    {
        var reading = QcyFirmwareReading.Parse([3, 0, 13, 3, 0, 14]);
        Assert.IsNotNull(reading);
        Assert.AreEqual("3.0.13", reading.Left);
        Assert.AreEqual("3.0.14", reading.Right);
        Assert.AreEqual("L 3.0.13 · R 3.0.14", reading.Display);
    }

    [TestMethod]
    public void ThreeByteFirmwareKeepsTheWeakerEvidenceLevel()
    {
        // This layout comes from public sources and was never observed on an
        // HT08, so it must not inherit the six-byte hardware confirmation.
        var reading = QcyFirmwareReading.Parse([3, 0, 13]);
        Assert.IsNotNull(reading);
        Assert.AreEqual("3.0.13", reading.Left);
        Assert.IsNull(reading.Right);
        Assert.AreEqual("3.0.13", reading.Display);
        Assert.AreEqual(QcyFormatEvidence.PublicUnconfirmed, reading.Evidence);

        var zero = QcyFirmwareReading.Parse([0, 0, 0]);
        Assert.IsNotNull(zero);
        Assert.AreEqual("0.0.0", zero.Left);
    }

    [TestMethod]
    public void FirmwareReportsEveryOtherLengthAsUnknown()
    {
        foreach (var payload in new byte[][]
        {
            [], [1], [1, 2], [1, 2, 3, 4], [1, 2, 3, 4, 5], [1, 2, 3, 4, 5, 6, 7], new byte[20],
        })
        {
            Assert.IsNull(QcyFirmwareReading.Parse(payload));
        }

        // ASCII must not be decoded as a version without evidence: "1.2.3" is
        // five bytes, so it stays an unknown format rather than a string.
        Assert.IsNull(QcyFirmwareReading.Parse(System.Text.Encoding.ASCII.GetBytes("1.2.3")));
    }

    [TestMethod]
    public void BatteryLayoutIsLabelledHardwareConfirmed() =>
        Assert.AreEqual(QcyFormatEvidence.HardwareConfirmed, QcyBatteryReading.Evidence);

    [TestMethod]
    public void TheOptInReadIsItsOwnModeAndIsNeverImpliedByDiscovery()
    {
        var readInfo = ProbeOptions.Parse(["--read-ht08-device-info"]);
        Assert.IsTrue(readInfo.ReadHt08DeviceInfo);
        Assert.IsFalse(readInfo.DiscoveryOnly);

        // Discovery must never turn the proprietary read on, and neither the
        // default N70 mode nor the Windows battery mode may enable it.
        Assert.IsFalse(ProbeOptions.Parse(["--discovery-only"]).ReadHt08DeviceInfo);
        Assert.IsFalse(ProbeOptions.Parse([]).ReadHt08DeviceInfo);
        Assert.IsFalse(ProbeOptions.Parse(["--windows-battery"]).ReadHt08DeviceInfo);

        foreach (var args in new[]
        {
            new[] { "--read-ht08-device-info", "--discovery-only" },
            new[] { "--read-ht08-device-info", "--windows-battery" },
            new[] { "--read-ht08-device-info", "--disable-wear-detection" },
            new[] { "--read-ht08-device-info", "--vendor-id", "19786" },
            new[] { "--read-ht08-device-info", "--raw-manufacturer-data" },
        })
        {
            Assert.Throws<ArgumentException>(() => ProbeOptions.Parse(args));
        }
    }
}
