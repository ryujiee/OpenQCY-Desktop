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
/// Unlike the N70, only the first two bytes are decoded. The HT08 returns three
/// bytes and the third was observed as <c>0x00</c> while both earbuds reported
/// 95%, so it is <b>not</b> treated as a case level: reporting a case at 0%
/// would be a fabricated reading. Its meaning is unknown and it is surfaced as
/// a raw byte instead.
/// </remarks>
public sealed record QcyBatteryCandidate(
    byte Left,
    byte Right,
    bool LeftCharging,
    bool RightCharging,
    IReadOnlyList<byte> UnexplainedBytes)
{
    /// <summary>
    /// The left and right levels were confirmed on a contributor's HT08 on
    /// 2026-09-11. The charging bit was <b>not</b> exercised: bit 7 was clear in
    /// every observed byte, so its meaning remains carried over from the QCY
    /// family and is still unverified on this model.
    /// </summary>
    public static QcyFormatEvidence Evidence => QcyFormatEvidence.HardwareConfirmed;

    public static QcyBatteryCandidate? Parse(ReadOnlySpan<byte> value)
    {
        // The confirmed HT08 payload is three bytes; a shorter one is not the
        // known layout and is not zero-filled to fit.
        if (value.Length < 3)
        {
            return null;
        }

        // Reject instead of clamping: a level above 100 means the layout does
        // not hold for this payload, which is a result worth seeing. Only the
        // two decoded bytes are validated; the rest are not claimed to be
        // levels, so no invariant is asserted over them.
        if ((value[0] & 0x7F) > 100 || (value[1] & 0x7F) > 100)
        {
            return null;
        }

        return new QcyBatteryCandidate(
            (byte)(value[0] & 0x7F),
            (byte)(value[1] & 0x7F),
            (value[0] & 0x80) != 0,
            (value[1] & 0x80) != 0,
            value[2..].ToArray());
    }
}

/// <summary>
/// Candidate decoding of the proprietary firmware characteristic. Only the two
/// lengths documented by public implementations are decoded; any other length
/// is reported as an unknown format, with no ASCII or heuristic fallback.
/// </summary>
public static class QcyFirmwareCandidate
{
    /// <summary>
    /// The six-byte left/right layout was confirmed on a contributor's HT08 on
    /// 2026-09-11, which returned <c>02 00 06 02 00 06</c> for firmware 2.0.6.
    /// The three-byte layout is still only documented by public sources.
    /// </summary>
    public static QcyFormatEvidence Evidence => QcyFormatEvidence.HardwareConfirmed;

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
