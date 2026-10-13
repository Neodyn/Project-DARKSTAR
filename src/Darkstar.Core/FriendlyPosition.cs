namespace Darkstar;

/// <summary>
/// "Overlord, Punch 1-1, where is Springfield 2-1?" - telling one pilot where another one is.
///
/// THREE DECISIONS WORTH KNOWING ABOUT, because none of them is about the geometry:
///
/// 1. WHO IS BEING ASKED ABOUT is found by POSITION in the sentence, not by scanning it for names.
///    A request like this names two pilots - the caller and the target - and PilotNames deliberately
///    refuses to pick when more than one candidate is named, which is what caught a wingman being
///    reported as their flight lead. That safeguard stays. Instead only the text AFTER the trigger
///    phrase is searched, which has exactly one answer, so nothing had to be loosened.
///
/// 2. IT IS OFF BY DEFAULT. On a PvP server a bot that reads out any player's position on request
///    is the server owner's decision, not something to hand them without asking. It can also be
///    confined to one frequency, like the tower and the AWACS roles.
///
/// 3. A CALLER WHOSE COALITION IS UNKNOWN GETS NOTHING. Everywhere else in this bot an unknown
///    sender coalition falls back to the bot's own side, which is harmless when the answer is about
///    the enemy. Here it would mean somebody sitting in a spectator slot could ask where the
///    players on the bot's side are. So this one request requires a coalition that came from the SRS
///    client list, and refuses otherwise.
///
/// Human players only, by choice: AI wingmen are not in CoalitionService.GetPlayerUnits, and pulling
/// in every AI unit on the coalition would enlarge the candidate list enough to make
/// misidentification likely - which on this particular request means telling somebody the wrong
/// position with complete confidence.
/// </summary>
public static class FriendlyPosition
{
    /// <summary>What was asked for, as far as the text goes.</summary>
    /// <param name="Trigger">The phrase that fired, for the log.</param>
    /// <param name="TargetText">
    /// The part of the transmission naming the aircraft being asked about. Empty when the caller
    /// said "where is" and then nothing usable - which is a different situation from naming
    /// somebody the bot can't find, and gets a different answer.
    /// </param>
    public sealed record Request(string Trigger, string TargetText)
    {
        public bool NamesSomebody => TargetText.Length > 0;
    }

    /// <summary>
    /// Words that routinely trail the name and would only get in the way of matching it. Kept
    /// deliberately short: this is for "where is Springfield 2-1 right now", not for parsing English.
    /// </summary>
    private static readonly string[] TrailingNoise =
    {
        "right now", "now", "at the moment", "currently", "please", "over", "at present"
    };

    /// <summary>
    /// Politeness and filler that can sit between the trigger and the name.
    /// </summary>
    private static readonly string[] LeadingNoise = { "my", "the", "our" };

    /// <summary>
    /// Words that stand in for a name instead of being one. "Overlord, where is he?" names nobody,
    /// and answering "negative, no contact on he" would be worse than useless - the caller needs to
    /// be asked which aircraft they mean.
    /// </summary>
    private static readonly string[] NotANameWords =
    {
        "he", "she", "it", "they", "him", "her", "them", "that", "this", "everyone", "everybody",
        "anyone", "anybody", "someone", "somebody", "who", "everything"
    };

    /// <summary>
    /// Reads the request out of a transcript, or null when it isn't one.
    /// </summary>
    public static Request? Parse(AppConfig config, string? transcript)
    {
        if (!config.DcsIntelFriendlyPositionEnabled) return null;

        var hit = TriggerMatcher.FindMatchWithTail(transcript, config.DcsIntelFriendlyPositionTriggers);
        if (hit == null) return null;

        return new Request(hit.Trigger, CleanTarget(hit.Tail));
    }

    /// <summary>
    /// Trims the filler off the text following the trigger, so what is left is as close to a
    /// callsign as the transcript allows.
    /// </summary>
    internal static string CleanTarget(string? tail)
    {
        var text = (tail ?? "").Trim().Trim('?', '.', ',', '!', ';', ':').Trim();
        if (text.Length == 0) return "";

        // Leading filler, once each at most - "where is my wingman Springfield 2-1".
        foreach (var noise in LeadingNoise)
        {
            if (text.StartsWith(noise + " ", StringComparison.OrdinalIgnoreCase))
            {
                text = text[(noise.Length + 1)..].TrimStart();
                break;
            }
        }

        // Trailing filler, longest first so "at the moment" is stripped rather than "moment" left
        // behind by "now".
        foreach (var noise in TrailingNoise.OrderByDescending(n => n.Length))
        {
            if (text.EndsWith(" " + noise, StringComparison.OrdinalIgnoreCase))
            {
                text = text[..^(noise.Length + 1)].TrimEnd();
                break;
            }

            if (text.Equals(noise, StringComparison.OrdinalIgnoreCase))
                return "";
        }

        text = text.Trim().Trim('?', '.', ',', '!', ';', ':').Trim();

        // A pronoun is not a name. Treated as "nobody was named" so the caller is asked which
        // aircraft they mean, rather than told there is no contact on "he".
        return NotANameWords.Contains(text, StringComparer.OrdinalIgnoreCase) ? "" : text;
    }

    /// <summary>
    /// Whether this request may be answered at all for a sender on this SRS coalition.
    /// </summary>
    /// <remarks>
    /// 1 is Red and 2 is Blue in the SRS client list; anything else means the sender could not be
    /// placed on a side. See decision 3 in the class remarks for why that is a refusal here and
    /// nowhere else.
    /// </remarks>
    public static bool CoalitionIsKnown(int senderCoalition) => senderCoalition is 1 or 2;
}
