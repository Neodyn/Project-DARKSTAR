using System.Globalization;

namespace Darkstar;

/// <summary>What a request needs the radio to be able to do.</summary>
public enum RadioCapability
{
    /// <summary>Bogey dope, picture, threat check, threat circle.</summary>
    Tactical,

    /// <summary>Runway in use and ATIS.</summary>
    Airfield,

    /// <summary>Telling a pilot where another player is.</summary>
    FriendlyPosition,
}

/// <summary>
/// Splitting the bot's jobs across frequencies, the way a real radio plan does: the AWACS on one,
/// the tower on another.
///
/// The pieces here are the ones worth testing on their own - deciding which radio serves what,
/// and phrasing the handoff when a pilot calls the wrong one. Everything that needs a live
/// connection stays in BotService.
/// </summary>
public static class RadioRoles
{
    /// <summary>A radio that can serve a given kind of request.</summary>
    /// <param name="FrequencyHz">Its frequency, for telling the pilot where to go.</param>
    /// <param name="Callsign">What it answers to, for the same reason.</param>
    public sealed record Handler(double FrequencyHz, string Callsign);

    /// <summary>
    /// Finds the one other radio that handles this kind of request.
    ///
    /// Deliberately only answers when there is exactly one, and never points at the radio the
    /// pilot is already on. With two towers configured there is no single right answer, and
    /// naming one of them would be a guess dressed up as instruction.
    /// </summary>
    /// <param name="radios">All configured radios, with the capabilities each one has.</param>
    /// <param name="currentFrequencyHz">The frequency the request came in on.</param>
    public static Handler? FindHandoff(
        IEnumerable<(double FrequencyHz, string Callsign, bool Tactical, bool Airfield, bool FriendlyPosition)> radios,
        double currentFrequencyHz,
        RadioCapability needed)
    {
        Handler? found = null;

        foreach (var radio in radios)
        {
            var serves = needed switch
            {
                RadioCapability.Tactical => radio.Tactical,
                RadioCapability.Airfield => radio.Airfield,
                RadioCapability.FriendlyPosition => radio.FriendlyPosition,
                _ => false
            };
            if (!serves) continue;

            // Same frequency, rounded the way the rest of the bot keys radios.
            if (Math.Round(radio.FrequencyHz) == Math.Round(currentFrequencyHz)) continue;

            if (found != null) return null; // more than one candidate - don't guess
            found = new Handler(radio.FrequencyHz, radio.Callsign);
        }

        return found;
    }

    /// <summary>
    /// "Contact Tower on two five one decimal zero." Fills the template's {callsign} and
    /// {frequency} placeholders; returns null when no template is configured, in which case the
    /// request should simply fall through to the phrase list as any other transmission would.
    /// </summary>
    public static string? BuildHandoffReply(string? template, Handler handler, bool spellOutDigits)
    {
        if (string.IsNullOrWhiteSpace(template)) return null;

        return template
            .Replace("{callsign}", handler.Callsign, StringComparison.OrdinalIgnoreCase)
            .Replace("{frequency}", SpeakFrequency(handler.FrequencyHz, spellOutDigits), StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    /// <summary>
    /// A frequency as it is said on the radio: digit by digit with "decimal" for the point, and
    /// trailing zeros dropped, so 251000000 Hz becomes "two five one decimal zero" rather than
    /// "two hundred fifty one point zero zero zero".
    /// </summary>
    public static string SpeakFrequency(double frequencyHz, bool spellOutDigits = true)
    {
        var megahertz = frequencyHz / 1_000_000.0;

        // Three decimals is the finest SRS tunes to; trailing zeros are not spoken, but one
        // digit after the point always is ("251.0", never a bare "251").
        var text = megahertz.ToString("0.000", CultureInfo.InvariantCulture).TrimEnd('0');
        if (text.EndsWith('.')) text += "0";

        if (!spellOutDigits) return text;

        var spoken = new List<string>();
        foreach (var c in text)
        {
            if (char.IsDigit(c)) spoken.Add(DcsIntelService.SpeakDigit(c - '0'));
            else if (c == '.') spoken.Add("decimal");
        }

        return string.Join(" ", spoken);
    }
}
