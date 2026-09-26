using System.Text.Json.Serialization;

namespace Darkstar;

/// <summary>Which unit the altimeter setting is read out in (config: DcsAirfieldPressureUnit).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PressureUnit
{
    /// <summary>QNH in hectopascals and then the altimeter setting in inches - for a mixed flight.</summary>
    Both,

    /// <summary>QNH only, as most of the world and every eastern type uses.</summary>
    Hectopascals,

    /// <summary>Altimeter setting only, as US types use.</summary>
    InchesHg,
}

/// <summary>What kind of airfield information a transmission asked for.</summary>
public enum AirfieldRequestKind
{
    None,

    /// <summary>Just the runway: "Batumi, runway in use".</summary>
    RunwayInUse,

    /// <summary>The full weather picture: wind, temperature, pressure and the runway.</summary>
    Atis,
}

/// <summary>One runway strip as DCS describes it.</summary>
public sealed class Runway
{
    /// <summary>DCS's own designation for the strip, e.g. "13". May be empty on some terrains.</summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// True heading of the strip in degrees. Derived from DCS's <c>course</c> field, which is in
    /// radians and has the opposite sign (see DcsAirfieldService.HeadingFromCourse).
    /// </summary>
    public double TrueHeadingDegrees { get; init; }

    public double LengthMeters { get; init; }
    public double WidthMeters { get; init; }
}

/// <summary>An airfield, its position, and the runways it has.</summary>
public sealed class Airfield
{
    /// <summary>The name DCS knows it by - the key everything else is matched against.</summary>
    public string Name { get; init; } = "";

    /// <summary>The prettier name, when DCS supplies one. Falls back to <see cref="Name"/>.</summary>
    public string DisplayName { get; init; } = "";

    public double Lat { get; init; }
    public double Lon { get; init; }
    public double ElevationMeters { get; init; }

    public List<Runway> Runways { get; init; } = new();

    public string SpokenName => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName;
}

/// <summary>
/// One usable direction of a runway - a strip gives two of these, 180° apart, and the wind
/// decides which one is in use.
/// </summary>
public sealed class RunwayEnd
{
    /// <summary>What the pilot reads on the threshold, e.g. "13" or "04".</summary>
    public string Designator { get; init; } = "";

    public double TrueHeadingDegrees { get; init; }
    public double MagneticHeadingDegrees { get; init; }
    public double LengthMeters { get; init; }

    /// <summary>
    /// Wind component along the runway in knots. Positive is headwind, which is what you want;
    /// negative means landing on this end would be a tailwind.
    /// </summary>
    public double HeadwindKnots { get; init; }

    /// <summary>Crosswind component in knots, unsigned - only its size matters for a tie-break.</summary>
    public double CrosswindKnots { get; init; }
}

/// <summary>Everything needed to read out an ATIS, plus whether it could be worked out at all.</summary>
public sealed class AirfieldConditions
{
    public Airfield? Airfield { get; init; }

    /// <summary>Where the wind is coming from, in degrees magnetic (what an ATIS reports).</summary>
    public int WindFromMagnetic { get; init; }

    public double WindKnots { get; init; }
    public double TemperatureCelsius { get; init; }
    public double QnhHectopascals { get; init; }
    public double QnhInchesHg { get; init; }

    /// <summary>The runway ends, best first. Empty when DCS gave us no runway data.</summary>
    public List<RunwayEnd> RunwayEnds { get; init; } = new();

    public RunwayEnd? Best => RunwayEnds.Count > 0 ? RunwayEnds[0] : null;
}

/// <summary>The spoken answer, plus why it says what it says.</summary>
public sealed class AirfieldResult
{
    /// <summary>The sentence to transmit. Empty when the request wasn't handled at all.</summary>
    public string Reply { get; init; } = "";

    /// <summary>True when the reply carries real information rather than an apology.</summary>
    public bool Handled { get; init; }

    /// <summary>For the log and the GUI's test button: where the data came from, what was chosen and why.</summary>
    public string Diagnostics { get; init; } = "";

    public AirfieldConditions? Conditions { get; init; }
}
