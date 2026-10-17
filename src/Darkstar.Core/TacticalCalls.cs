using System.Globalization;
using System.Text.RegularExpressions;

namespace Darkstar;

/// <summary>
/// The controller calls that are a pure function of the picture: reading a request apart, and
/// saying what is there in the words a controller would use.
///
/// Everything here takes numbers and returns numbers or words. Nothing in it connects to anything,
/// which is the point - "is that bearing inside the cone", the choice between *azimuth* and
/// *range*, and the reading of "declare bullseye zero three zero for four five" are all either
/// right or wrong on paper, and were all previously only checkable by flying.
///
/// The live half - fetching contacts, finding the caller's aircraft, the declination - stays in
/// <see cref="DcsIntelService"/>, which calls into this.
/// </summary>
public static class TacticalCalls
{
    /// <summary>How far from the named point a contact still counts as being there.</summary>
    public const double DeclareRadiusNm = 5.0;

    /// <summary>
    /// How far off the called bearing a group still counts as being on it.
    ///
    /// A spike is a radar warning, and a warning receiver gives a rough direction, not a precise
    /// one - the pilot is reading a strobe off a dial. Too narrow and the honest answer "nothing
    /// known" comes back while the emitter sits just outside the cone; too wide and "spike 270"
    /// reports something behind the wing. 15 degrees either side is the compromise, and it is a
    /// constant rather than a literal so it can be argued with.
    /// </summary>
    public const double SpikeConeDegrees = 15.0;

    /// <summary>What was found at a declared point.</summary>
    public enum DeclareVerdict
    {
        /// <summary>Nothing of either side there.</summary>
        Clean,

        /// <summary>Only hostiles.</summary>
        Hostile,

        /// <summary>Only friendlies.</summary>
        Friendly,

        /// <summary>Both, close together - the one answer a pilot must not shoot into.</summary>
        Furball,
    }

    /// <summary>A point a pilot named, as bearing and range from a reference.</summary>
    /// <param name="BearingDegrees">Magnetic bearing, 0-359.</param>
    /// <param name="RangeNm">Range in nautical miles.</param>
    /// <param name="FromBullseye">
    /// True for "declare bullseye 030 45", false for a bearing and range from the caller's own
    /// aircraft. The two mean completely different places, and taking one for the other sends the
    /// pilot a verdict about empty sky.
    /// </param>
    public sealed record NamedPoint(int BearingDegrees, double RangeNm, bool FromBullseye);

    // "for" and "at" are how a pilot joins a bearing to a range out loud - "zero three zero FOR
    // four five". They have to be taken out of the sentence before the digits are read, because
    // PilotNames.NormalizeSpokenNumbers deliberately maps "for" to 4 (it exists to match spoken
    // callsigns, where that bet pays off). Left in, "zero three zero for four five" becomes
    // "0 3 0 4 4 5" and the range comes out as 445 or 44 - a confident answer about the wrong
    // place, which is the worst failure this file can have.
    private static readonly Regex JoinerWords =
        new(@"\b(?:for|at|slash)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Compound numbers have to go before single digits, or "forty five" becomes "forty 5".
    private static readonly (string Word, string Digits)[] TeenNumbers =
    {
        ("ten", "10"), ("eleven", "11"), ("twelve", "12"), ("thirteen", "13"), ("fourteen", "14"),
        ("fifteen", "15"), ("sixteen", "16"), ("seventeen", "17"), ("eighteen", "18"), ("nineteen", "19"),
    };

    private static readonly (string Word, int Tens)[] TensNumbers =
    {
        ("twenty", 20), ("thirty", 30), ("forty", 40), ("fourty", 40), ("fifty", 50),
        ("sixty", 60), ("seventy", 70), ("eighty", 80), ("ninety", 90),
    };

    private static readonly (string Word, int Digit)[] UnitNumbers =
    {
        ("one", 1), ("two", 2), ("three", 3), ("four", 4), ("five", 5),
        ("six", 6), ("seven", 7), ("eight", 8), ("nine", 9),
    };

    private static readonly Regex TensAndUnit = new(
        @"\b(?<tens>" + string.Join("|", TensNumbers.Select(t => t.Word)) + @")[\s-]*" +
        @"(?<unit>" + string.Join("|", UnitNumbers.Select(u => u.Word)) + @")?\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TeenWord = new(
        @"\b(?<teen>" + string.Join("|", TeenNumbers.Select(t => t.Word)) + @")\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Turns the numbers in a transcript into figures, the way a bearing-and-range call needs them:
    /// joiner words removed, "forty five" read as 45 rather than as two separate digits, and the
    /// single digits left to <see cref="PilotNames.NormalizeSpokenNumbers"/>.
    ///
    /// Internal rather than private so the suite can check the step on its own - a wrong number
    /// here is invisible in the finished sentence until someone flies to the wrong place.
    /// </summary>
    internal static string NumbersForGeometry(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        // The joiner becomes a slash: it keeps the two numbers apart instead of letting them run
        // together into one, which "030 45" with the word simply deleted would do.
        var cleaned = JoinerWords.Replace(text, " / ");

        cleaned = TensAndUnit.Replace(cleaned, m =>
        {
            var tens = TensNumbers.First(t => string.Equals(t.Word, m.Groups["tens"].Value, StringComparison.OrdinalIgnoreCase)).Tens;
            var unitGroup = m.Groups["unit"];
            var unit = unitGroup.Success
                ? UnitNumbers.First(u => string.Equals(u.Word, unitGroup.Value, StringComparison.OrdinalIgnoreCase)).Digit
                : 0;
            return (tens + unit).ToString(CultureInfo.InvariantCulture);
        });

        cleaned = TeenWord.Replace(cleaned, m =>
            TeenNumbers.First(t => string.Equals(t.Word, m.Groups["teen"].Value, StringComparison.OrdinalIgnoreCase)).Digits);

        return PilotNames.NormalizeSpokenNumbers(cleaned);
    }

    // A bearing is three digits, spoken either glued ("030") or one at a time ("0 3 0"); a range
    // is one to three. The lookahead stops the range swallowing a digit that belongs to whatever
    // the pilot said next.
    private static readonly Regex BearingAndRange = new(
        @"(?<bearing>\d(?:[\s/,-]*\d){2})(?![\s/,-]*\d(?![\s/,-]*\d))?" +
        @"[\s/,.-]*(?<range>\d(?:[\s]*\d){0,2})(?![\s]*\d)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Reads "declare bullseye 030 45", "declare 0 3 0 for 4 5", "declare bullseye zero three zero
    /// for forty five" and the shapes in between.
    ///
    /// Null means the pilot named no usable point, which is a request in its own right - declare
    /// on a radar lock, or declare meaning "whatever is nearest" - and not an error.
    /// </summary>
    public static NamedPoint? ParseDeclarePoint(string? transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript)) return null;

        var text = NumbersForGeometry(transcript);
        var fromBullseye = Regex.IsMatch(text, @"\bbulls?\s*eye\b", RegexOptions.IgnoreCase);

        var match = BearingAndRange.Match(text);
        if (!match.Success) return null;

        var bearing = int.Parse(OnlyDigits(match.Groups["bearing"].Value), CultureInfo.InvariantCulture);
        var range = double.Parse(OnlyDigits(match.Groups["range"].Value), CultureInfo.InvariantCulture);

        // A bearing past 360 and a range nobody flies to are both signs of a mis-heard transcript.
        // "Say again" beats a confident verdict about a point that cannot exist.
        if (bearing > 360 || range <= 0 || range > 500) return null;

        return new NamedPoint(NorthAsZero(bearing), range, fromBullseye);
    }

    /// <summary>
    /// Reads the bearing out of "spike 270" / "spiked two seven zero" / "spike zero niner zero".
    /// Null when no bearing came with it, which the caller answers by asking for one.
    /// </summary>
    public static int? ParseSpikeBearing(string? transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript)) return null;

        var text = NumbersForGeometry(transcript);

        // Anchored on the word, so a bearing elsewhere in the sentence is not mistaken for the
        // spike bearing.
        var match = Regex.Match(text, @"\bspiked?\b[^\d]{0,16}(?<bearing>\d(?:[\s/,-]*\d){2})",
            RegexOptions.IgnoreCase);
        if (!match.Success) return null;

        var bearing = int.Parse(OnlyDigits(match.Groups["bearing"].Value), CultureInfo.InvariantCulture);
        return bearing > 360 ? null : NorthAsZero(bearing);
    }

    private static string OnlyDigits(string value) => Regex.Replace(value, @"[^\d]", "");

    /// <summary>
    /// North is "three six zero" on the radio and 0 in every calculation. Rejecting 360 as out of
    /// range would turn the one bearing a pilot is most likely to say out loud into a "say again".
    /// </summary>
    private static int NorthAsZero(int bearing) => bearing == 360 ? 0 : bearing;

    /// <summary>
    /// Whether a contact at <paramref name="contactBearing"/> counts as lying on the called
    /// bearing. Wraps around 0/360, which is where a plain subtraction quietly fails: a spike
    /// called on 355 has to match a contact at 005.
    /// </summary>
    public static bool IsOnBearing(int calledBearing, int contactBearing, double coneDegrees = SpikeConeDegrees)
        => Separation(calledBearing, contactBearing) <= coneDegrees;

    /// <summary>The smaller angle between two bearings, 0-180, wrap included.</summary>
    internal static double Separation(double bearingA, double bearingB)
    {
        var difference = DcsIntelService.NormalizeBearing(bearingA - bearingB);
        return difference > 180 ? 360 - difference : difference;
    }

    /// <summary>
    /// The verdict for a declared point, from what is within <see cref="DeclareRadiusNm"/> of it.
    ///
    /// Furball exists because it is the one answer that changes what the pilot does: hostiles and
    /// friendlies mixed together means do not shoot. Reporting that as "hostile" because the
    /// hostile happened to be nearer is the kind of mistake that ends a sortie badly.
    /// </summary>
    public static DeclareVerdict Declare(bool hostilesPresent, bool friendliesPresent) =>
        (hostilesPresent, friendliesPresent) switch
        {
            (true, true) => DeclareVerdict.Furball,
            (true, false) => DeclareVerdict.Hostile,
            (false, true) => DeclareVerdict.Friendly,
            _ => DeclareVerdict.Clean,
        };

    /// <summary>
    /// How several groups sit relative to each other, in the word a controller uses.
    ///
    /// Two groups are either side by side across the line of sight (*azimuth*) or one behind the
    /// other along it (*range*). Three are a *wall* (all abreast), a *ladder* (all in trail), a
    /// *vic* (one group leading two) or *champagne* (two groups leading one). Fewer than two
    /// groups has no formation, and more than three is not called as a shape.
    ///
    /// Bearings and ranges are from the pilot, not the bullseye: the shape is what they will see
    /// on the radar, and from the bullseye the same four groups make a different picture.
    /// </summary>
    /// <param name="bearings">Each group's bearing from the caller, in degrees.</param>
    /// <param name="ranges">Each group's range from the caller, in NM, in the same order.</param>
    public static string? Formation(IReadOnlyList<int> bearings, IReadOnlyList<double> ranges)
    {
        if (bearings.Count != ranges.Count) return null;

        switch (bearings.Count)
        {
            case 2:
            {
                // Separation across the line of sight against separation along it. The across
                // figure has to be an arc rather than an angle, or the same 20 degrees would read
                // as "azimuth" at 40 miles and "range" at 5.
                var across = LateralSeparationNm(bearings[0], bearings[1], (ranges[0] + ranges[1]) / 2);
                var along = Math.Abs(ranges[0] - ranges[1]);
                return across >= along ? "azimuth" : "range";
            }

            case 3:
            {
                var along = ranges.Max() - ranges.Min();
                var across = MaxLateralSpreadNm(bearings, ranges.Average());

                // All three abreast or all three in trail come first; the factor of three is what
                // keeps a shape that is merely lopsided from being called a wall.
                if (across >= 3 * along) return "wall";
                if (along >= 3 * across) return "ladder";

                // Otherwise the shape is named by which end the odd group is on: one group out in
                // front of two (vic) or two out in front of one (champagne). Three groups evenly
                // spaced in range fall out as a vic, which is the closer of the two.
                var nearest = ranges.Min();
                var leading = ranges.Count(r => r - nearest < along / 2.0);
                return leading == 1 ? "vic" : "champagne";
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// The arc between two bearings at a given range, in nautical miles - what "20 degrees apart"
    /// actually comes to on the ground.
    /// </summary>
    internal static double LateralSeparationNm(int bearingA, int bearingB, double rangeNm)
        => rangeNm * Separation(bearingA, bearingB) * Math.PI / 180.0;

    /// <summary>
    /// The widest arc between any two of the bearings. Taking the smallest and largest number
    /// instead would under-report every set that straddles north: 350 and 010 look 340 degrees
    /// apart and are 20.
    /// </summary>
    internal static double MaxLateralSpreadNm(IReadOnlyList<int> bearings, double rangeNm)
    {
        var widest = 0.0;
        for (var i = 0; i < bearings.Count; i++)
            for (var j = i + 1; j < bearings.Count; j++)
                widest = Math.Max(widest, LateralSeparationNm(bearings[i], bearings[j], rangeNm));
        return widest;
    }

    /// <summary>
    /// The words that follow a group's position: how many, how fast, how high, and what it is.
    ///
    /// The order is a controller's, not alphabetical - size, then speed, then height, then type -
    /// because a pilot listening for one of them expects it where it belongs.
    /// </summary>
    /// <param name="contactCount">How many aircraft are in the group.</param>
    /// <param name="fastestKnots">The fastest contact's speed, in knots.</param>
    /// <param name="altitudeFeet">The group's altitude in feet above sea level.</param>
    /// <param name="altitudeAboveGroundFeet">Its height above the ground, for the "low" call.</param>
    /// <param name="type">The aircraft type, already cleaned of DCS variant suffixes. Empty leaves it out.</param>
    public static List<string> FillIns(int contactCount, double fastestKnots, double altitudeFeet,
        double altitudeAboveGroundFeet, string? type)
    {
        var words = new List<string>
        {
            contactCount switch
            {
                <= 1 => "single",
                2 => "two contacts",
                // Past two, the number matters less than the warning: "heavy" is the word that
                // makes a pilot think twice about committing alone.
                _ => $"heavy, {contactCount} contacts",
            }
        };

        if (fastestKnots >= 900) words.Add("very fast");
        else if (fastestKnots >= 600) words.Add("fast");

        if (altitudeFeet > 40_000) words.Add("high");
        else if (altitudeAboveGroundFeet < 1_000) words.Add("low");

        if (!string.IsNullOrWhiteSpace(type)) words.Add(type.Trim());

        return words;
    }
}
