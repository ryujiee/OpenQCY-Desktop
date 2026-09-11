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
/// Candidate decoding of the proprietary battery characteristic. Constructed
/// only when every invariant holds; a payload that does not satisfy them is
/// rejected rather than reinterpreted under a guessed layout.
/// </summary>
public sealed record QcyBatteryCandidate(
    byte Left,
    byte Right,
    byte Case,
    bool LeftCharging,
    bool RightCharging,
    bool CaseCharging)
{
    public static QcyFormatEvidence Evidence => QcyFormatEvidence.PublicUnconfirmed;

    public static QcyBatteryCandidate? Parse(ReadOnlySpan<byte> value)
    {
        if (value.Length < 3)
        {
            return null;
        }

        // Reject instead of clamping: a level above 100 means the hypothesised
        // layout does not hold for this model, which is a result worth seeing.
        for (var index = 0; index < 3; index++)
        {
            if ((value[index] & 0x7F) > 100)
            {
                return null;
            }
        }

        return new QcyBatteryCandidate(
            (byte)(value[0] & 0x7F),
            (byte)(value[1] & 0x7F),
            (byte)(value[2] & 0x7F),
            (value[0] & 0x80) != 0,
            (value[1] & 0x80) != 0,
            (value[2] & 0x80) != 0);
    }
}

/// <summary>
/// Candidate decoding of the proprietary firmware characteristic. Only the two
/// lengths documented by public implementations are decoded; any other length
/// is reported as an unknown format, with no ASCII or heuristic fallback.
/// </summary>
public static class QcyFirmwareCandidate
{
    public static QcyFormatEvidence Evidence => QcyFormatEvidence.PublicUnconfirmed;

    public static string? Parse(ReadOnlySpan<byte> value) => value.Length switch
    {
        3 => $"{value[0]}.{value[1]}.{value[2]}",
        6 => $"L {value[0]}.{value[1]}.{value[2]} · R {value[3]}.{value[4]}.{value[5]}",
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
            !profile.SupportsN70Control;
    }
}
