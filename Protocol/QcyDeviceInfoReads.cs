namespace OpenQCY_Desktop.Protocol;

/// <summary>
/// How well a decoded payload layout is established for the model being read.
/// This is separate from <see cref="QcyIdentityEvidence"/>, which only covers
/// identity: knowing which device this is says nothing about its byte layouts.
/// </summary>
public enum QcyFormatEvidence
{
    /// <summary>
    /// The layout comes from public implementations of the QCY family and has
    /// not been confirmed against this model's hardware. Values decoded at this
    /// level are candidates for review, never a supported feature.
    /// </summary>
    PublicUnconfirmed,

    /// <summary>The layout was confirmed on a contributor's hardware.</summary>
    HardwareConfirmed,
}

/// <summary>
/// Decoding of the HT08 proprietary battery characteristic. Constructed only
/// when every invariant holds; a payload that does not satisfy them is rejected
/// rather than reinterpreted under a guessed layout.
/// </summary>
/// <remarks>
/// Which sides exist is decided by the model profile, never by the payload. A
/// model that does not report a case level yields <see cref="Case"/> as
/// <see langword="null"/> and keeps the undecoded bytes verbatim, so a model
/// where 0% is a genuine case state keeps reporting it.
/// </remarks>
public sealed record QcyBatteryReading(
    byte? Left,
    byte? Right,
    byte? Case,
    bool LeftCharging,
    bool RightCharging,
    bool CaseCharging,
    IReadOnlyList<byte> UndecodedBytes)
{
    /// <summary>
    /// The left and right levels were confirmed on a contributor's HT08 on
    /// 2026-09-11. The charging bit was <b>not</b> exercised: bit 7 was clear in
    /// every observed byte, so its meaning remains carried over from the QCY
    /// family and is still unverified on this model.
    /// </summary>
    public static QcyFormatEvidence Evidence => QcyFormatEvidence.HardwareConfirmed;

    public static QcyBatteryReading? Parse(ReadOnlySpan<byte> value, QcyModelProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        // The confirmed payload is three bytes; a shorter one is not the known
        // layout and is not zero-filled to fit.
        if (value.Length < 3 || !profile.SupportsLeftBattery || !profile.SupportsRightBattery)
        {
            return null;
        }

        // Reject instead of clamping: a level above 100 means the layout does
        // not hold for this payload, which is a result worth seeing. Only bytes
        // this model actually reports are validated; a byte the profile does
        // not claim is a level carries no invariant.
        if (Level(value[0]) > 100 || Level(value[1]) > 100)
        {
            return null;
        }

        if (profile.SupportsCaseBattery && Level(value[2]) > 100)
        {
            return null;
        }

        return new QcyBatteryReading(
            Level(value[0]),
            Level(value[1]),
            profile.SupportsCaseBattery ? Level(value[2]) : null,
            Charging(value[0]),
            Charging(value[1]),
            profile.SupportsCaseBattery && Charging(value[2]),
            profile.SupportsCaseBattery ? value[3..].ToArray() : value[2..].ToArray());
    }

    private static byte Level(byte value) => (byte)(value & 0x7F);

    private static bool Charging(byte value) => (value & 0x80) != 0;
}

/// <summary>
/// Decoding of the proprietary firmware characteristic. Only the two documented
/// lengths are decoded; any other length is reported as an unknown format, with
/// no ASCII or heuristic fallback. Each layout carries its own evidence level.
/// </summary>
public sealed record QcyFirmwareReading(string Left, string? Right, QcyFormatEvidence Evidence)
{
    public string Display => Right is null ? Left : $"L {Left} · R {Right}";

    public static QcyFirmwareReading? Parse(ReadOnlySpan<byte> value) => value.Length switch
    {
        // Confirmed on a contributor's HT08 on 2026-09-11: 02 00 06 02 00 06
        // for firmware 2.0.6 on both earbuds.
        6 => new(
            $"{value[0]}.{value[1]}.{value[2]}",
            $"{value[3]}.{value[4]}.{value[5]}",
            QcyFormatEvidence.HardwareConfirmed),

        // Documented by public sources only; not observed on an HT08, so this
        // layout must not inherit the six-byte confirmation.
        3 => new($"{value[0]}.{value[1]}.{value[2]}", null, QcyFormatEvidence.PublicUnconfirmed),

        _ => null,
    };
}

/// <summary>
/// The explicit, opt-in allowlist for the two direct ATT reads used to
/// investigate the HT08. It grants no write, no subscription, and no command.
/// </summary>
public static class QcyDeviceInfoReads
{
    /// <summary>The QCY control service, <c>0000A001</c>.</summary>
    public static Guid Service => QcyUuids.MainService;

    /// <summary>Proprietary battery characteristic, <c>00000008</c>.</summary>
    public static Guid Battery => QcyUuids.Battery;

    /// <summary>Proprietary firmware characteristic, <c>00000007</c>.</summary>
    public static Guid Firmware => QcyUuids.FirmwareVersion;

    /// <summary>
    /// The reads this diagnostic may perform, in the order they are attempted.
    /// Battery comes first because it is the smaller, better-documented value.
    /// </summary>
    public static IReadOnlyList<Guid> ReadOrder { get; } = [Battery, Firmware];

    public static bool IsAllowed(Guid service, Guid characteristic) =>
        service == Service && (characteristic == Battery || characteristic == Firmware);

    /// <summary>
    /// Identity gate for the opt-in read. Every condition must hold: the QCY
    /// company ID, the hardware-confirmed HT08 vendor ID, the HT08 profile with
    /// hardware-confirmed identity, and the absence of N70 control permission.
    /// The public-catalog ID 19785, the Pro Plus ID 19790, unknown IDs, and the
    /// N70 are all refused.
    /// </summary>
    public static bool IsModelAllowed(ushort companyId, ushort? vendorId)
    {
        if (companyId != QcyUuids.CompanyId || vendorId != QcyModelProfile.Ht08VendorId)
        {
            return false;
        }

        var profile = QcyModelProfile.FromVendorId(vendorId);
        return ReferenceEquals(profile, QcyModelProfile.Ht08) &&
            profile.ModelCode == "HT08" &&
            profile.IdentityEvidence == QcyIdentityEvidence.HardwareConfirmed &&
            !profile.SupportsN70Control &&
            profile.SupportsLeftBattery &&
            profile.SupportsRightBattery &&
            profile.SupportsFirmwareRead;
    }
}
