using System.Globalization;

namespace Darkstar;

/// <summary>
/// Frequencies the way pilots read them: <c>252.000</c>, in MHz with three decimals and always a
/// point - the way SRS, the kneeboard and every radio in the cockpit show them.
///
/// <c>config.json</c> keeps storing hertz (<c>252000000</c>), so nothing changes for an existing
/// installation; this is only how the editor shows a frequency and reads one back. Invariant on
/// purpose: on a German Windows the plain <c>ToString</c> gives "252,000", which is not how a
/// frequency is written anywhere a pilot looks.
/// </summary>
public static class FrequencyText
{
    /// <summary>252000000 -> "252.000".</summary>
    public static string ToMhz(double hz) =>
        (hz / 1_000_000).ToString("0.000", CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads a frequency somebody typed, in hertz. "252.000", "252,000", "252", "252.5" and
    /// "252.000 MHz" are all MHz. A bare number of a million or more is taken as hertz, because
    /// that is what the field used to hold and what is pasted from an old <c>config.json</c>.
    /// Null for anything that is not a frequency - the caller keeps the old value rather than
    /// storing a nonsense one.
    /// </summary>
    public static double? ParseToHz(string? typed)
    {
        var text = (typed ?? "").Trim().Replace(" ", "");
        if (text.EndsWith("mhz", StringComparison.OrdinalIgnoreCase)) text = text[..^3];

        // One separator only, whichever the keyboard has: "252,000" is 252 MHz, not 252000.
        text = text.Replace(',', '.');

        if (!double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
            return null;

        if (value >= 1_000_000) return Math.Round(value);

        // Above 10,000 MHz and below a million hertz there is nothing a radio is tuned to - a
        // value there is a typing mistake, not a frequency in either unit.
        if (value <= 0 || value > 10_000) return null;

        // Rounded to the hertz: 251.1 * 1,000,000 is 251099999.99999997 in floating point.
        return Math.Round(value * 1_000_000);
    }
}
