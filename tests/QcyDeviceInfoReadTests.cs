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
    public void BatteryCandidateDecodesLevelsAndTheChargingBit()
    {
        var candidate = QcyBatteryCandidate.Parse([80, 70, 60]);
        Assert.IsNotNull(candidate);
        Assert.AreEqual((byte)80, candidate.Left);
        Assert.AreEqual((byte)70, candidate.Right);
        Assert.AreEqual((byte)60, candidate.Case);
        Assert.IsFalse(candidate.LeftCharging);
        Assert.IsFalse(candidate.RightCharging);
        Assert.IsFalse(candidate.CaseCharging);

        // Bit 7 marks charging and must not leak into the percentage.
        var charging = QcyBatteryCandidate.Parse([0x80 | 80, 70, 0x80 | 60]);
        Assert.IsNotNull(charging);
        Assert.AreEqual((byte)80, charging.Left);
        Assert.IsTrue(charging.LeftCharging);
        Assert.IsFalse(charging.RightCharging);
        Assert.AreEqual((byte)60, charging.Case);
        Assert.IsTrue(charging.CaseCharging);
    }

    [TestMethod]
    public void BatteryCandidateAcceptsTheBoundaryLevels()
    {
        var empty = QcyBatteryCandidate.Parse([0, 0, 0]);
        Assert.IsNotNull(empty);
        Assert.AreEqual((byte)0, empty.Left);

        var full = QcyBatteryCandidate.Parse([100, 100, 100]);
        Assert.IsNotNull(full);
        Assert.AreEqual((byte)100, full.Left);
        Assert.AreEqual((byte)100, full.Right);
        Assert.AreEqual((byte)100, full.Case);

        var fullCharging = QcyBatteryCandidate.Parse([0x80 | 100, 0x80 | 100, 0x80 | 100]);
        Assert.IsNotNull(fullCharging);
        Assert.AreEqual((byte)100, fullCharging.Left);
        Assert.IsTrue(fullCharging.CaseCharging);
    }

    [TestMethod]
    public void BatteryCandidateRejectsInvariantViolationsInsteadOfGuessing()
    {
        // A level above 100 means the hypothesised layout does not hold.
        foreach (var payload in new byte[][]
        {
            [101, 70, 60], [80, 101, 60], [80, 70, 101],
            [127, 0, 0], [0x80 | 101, 70, 60], [0xFF, 0xFF, 0xFF],
        })
        {
            Assert.IsNull(QcyBatteryCandidate.Parse(payload));
        }

        // Short payloads are rejected, never zero-filled.
        foreach (var payload in new byte[][] { [], [80], [80, 70] })
        {
            Assert.IsNull(QcyBatteryCandidate.Parse(payload));
        }
    }

    [TestMethod]
    public void BatteryCandidateIgnoresTrailingBytesItCannotExplain()
    {
        var candidate = QcyBatteryCandidate.Parse([80, 70, 60, 0xAB, 0xCD]);
        Assert.IsNotNull(candidate);
        Assert.AreEqual((byte)80, candidate.Left);
        Assert.AreEqual((byte)60, candidate.Case);
    }

    [TestMethod]
    public void FirmwareCandidateDecodesOnlyTheTwoDocumentedLengths()
    {
        Assert.AreEqual("3.0.13", QcyFirmwareCandidate.Parse([3, 0, 13]));
        Assert.AreEqual("L 3.0.13 · R 3.0.14", QcyFirmwareCandidate.Parse([3, 0, 13, 3, 0, 14]));
        Assert.AreEqual("0.0.0", QcyFirmwareCandidate.Parse([0, 0, 0]));
    }

    [TestMethod]
    public void FirmwareCandidateReportsEveryOtherLengthAsUnknown()
    {
        foreach (var payload in new byte[][]
        {
            [], [1], [1, 2], [1, 2, 3, 4], [1, 2, 3, 4, 5], [1, 2, 3, 4, 5, 6, 7], new byte[20],
        })
        {
            Assert.IsNull(QcyFirmwareCandidate.Parse(payload));
        }

        // ASCII must not be decoded as a version without evidence: "1.2.3" is
        // five bytes, so it stays an unknown format rather than a string.
        Assert.IsNull(QcyFirmwareCandidate.Parse(System.Text.Encoding.ASCII.GetBytes("1.2.3")));
    }

    [TestMethod]
    public void DecodedLayoutsAreLabelledUnconfirmedOnHardware()
    {
        Assert.AreEqual(QcyFormatEvidence.PublicUnconfirmed, QcyBatteryCandidate.Evidence);
        Assert.AreEqual(QcyFormatEvidence.PublicUnconfirmed, QcyFirmwareCandidate.Evidence);
    }

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
