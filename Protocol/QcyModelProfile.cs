namespace OpenQCY_Desktop.Protocol;

/// <summary>
/// How a model identity was established. This describes the evidence for the
/// identity only; it never describes evidence that a command works.
/// </summary>
public enum QcyIdentityEvidence
{
    /// <summary>No identity was established for the advertised vendor ID.</summary>
    None,

    /// <summary>
    /// The vendor ID appears in a public interoperability catalog, but no
    /// contributor has confirmed it against the physical model.
    /// </summary>
    PublicCatalog,

    /// <summary>
    /// The vendor ID was observed by a contributor on the physical model.
    /// Recorded in docs/ht08-discovery.md and docs/protocol-research.md.
    /// </summary>
    HardwareConfirmed,
}

// Identity evidence is listed in docs/ht08-discovery.md. A product ID alone
// does not grant the capabilities of a different model's command protocol, and
// a hardware-confirmed identity does not imply a hardware-confirmed protocol.
public sealed class QcyModelProfile
{
    private QcyModelProfile(
        string name,
        string? modelCode,
        QcyIdentityEvidence identityEvidence,
        bool supportsN70Control = false,
        bool supportsLeftBattery = false,
        bool supportsRightBattery = false,
        bool supportsCaseBattery = false,
        bool supportsFirmwareRead = false)
    {
        Name = name;
        ModelCode = modelCode;
        IdentityEvidence = identityEvidence;
        SupportsN70Control = supportsN70Control;
        SupportsLeftBattery = supportsLeftBattery;
        SupportsRightBattery = supportsRightBattery;
        SupportsCaseBattery = supportsCaseBattery;
        SupportsFirmwareRead = supportsFirmwareRead;
    }

    public string Name { get; }
    public string? ModelCode { get; }
    public QcyIdentityEvidence IdentityEvidence { get; }

    /// <summary>Whether the N70 proprietary command protocol may be used.</summary>
    public bool SupportsN70Control { get; }

    public bool SupportsLeftBattery { get; }
    public bool SupportsRightBattery { get; }

    /// <summary>
    /// Whether this model reports a case level at all. It is a per-model fact,
    /// never inferred from a payload: a model where 0% is a valid case state
    /// must keep reporting it.
    /// </summary>
    public bool SupportsCaseBattery { get; }

    public bool SupportsFirmwareRead { get; }

    public static QcyModelProfile Unknown { get; } =
        new("Unknown QCY device", null, QcyIdentityEvidence.None);

    // Validated on hardware on 2026-08-03; see docs/protocol-research.md.
    public static QcyModelProfile N70 { get; } = new(
        "QCY MeloBuds N70",
        "HT18",
        QcyIdentityEvidence.HardwareConfirmed,
        supportsN70Control: true,
        supportsLeftBattery: true,
        supportsRightBattery: true,
        supportsCaseBattery: true,
        supportsFirmwareRead: true);

    // Vendor ID 19786 (0x4D4A) observed on a contributor's MeloBuds Pro on
    // 2026-09-11, along with the left/right battery and firmware layouts.
    // The case level is unsupported: A001/0008 returned 0x00 in its third byte
    // while both earbuds reported 95%, and the official QCY application does
    // not show a case percentage for this model either. No HT08 command or
    // query is enabled; the two supported reads are plain ATT reads.
    public static QcyModelProfile Ht08 { get; } = new(
        "QCY MeloBuds Pro",
        "HT08",
        QcyIdentityEvidence.HardwareConfirmed,
        supportsLeftBattery: true,
        supportsRightBattery: true,
        supportsCaseBattery: false,
        supportsFirmwareRead: true);

    // Vendor ID 19785 is listed as HT08 by a public catalog only. No
    // contributor has confirmed it, so it keeps the weaker evidence level and
    // gains no capability from the confirmation of the neighbouring ID.
    public static QcyModelProfile Ht08Catalog { get; } =
        new("QCY MeloBuds Pro", "HT08", QcyIdentityEvidence.PublicCatalog);

    public static QcyModelProfile FromVendorId(ushort? vendorId) => vendorId switch
    {
        QcyUuids.N70BlackVendorId or QcyUuids.N70AlternateVendorId => N70,
        Ht08VendorId => Ht08,
        Ht08CatalogVendorId => Ht08Catalog,
        _ => Unknown,
    };

    // Only used for the existing Windows N70 cache/reconnect path. Never infer
    // N70 from A001, an empty name, or the generic QCY/MeloBuds product family.
    public static bool IsN70Name(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        name.Contains("N70", StringComparison.OrdinalIgnoreCase) &&
        (name.Contains("QCY", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("MeloBuds", StringComparison.OrdinalIgnoreCase));

    public const ushort Ht08VendorId = 19786;
    public const ushort Ht08CatalogVendorId = 19785;
}
