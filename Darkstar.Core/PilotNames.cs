using System.Text;
using System.Text.RegularExpressions;

namespace Darkstar;

/// <summary>
/// Everything to do with pilot names: cleaning them up for speech, and matching the name SRS
/// reports against the player name DCS reports.
///
/// WHY THIS IS ITS OWN CLASS: the bot learns who is talking from the SRS client list, but has to
/// find that same person among the units of a running mission to answer a BRAA call from their
/// aircraft. Those two names come from different places and rarely agree character for character.
/// The same pilot can appear as
///
///     [ISAF] Mobius 1 | Reaper        (SRS, with a squadron tag and a handle)
///     Mobius 1-1                      (DCS, with the flight number written differently)
///     mobius11                        (DCS, typed without spaces)
///
/// An exact comparison finds none of these, and the failure is invisible: the bot quietly falls
/// back to bullseye instead of BRAA, and nobody learns why. So names are reduced to a canonical
/// key before comparing - case, spacing, punctuation and how flight numbers are separated all
/// stop mattering.
///
/// The discipline that makes this safe: canonical keys are for MATCHING ONLY. What the bot says
/// and writes always comes from the original name, so nothing the pilot reads is mangled.
/// </summary>
public static class PilotNames
{
    /// <summary>
    /// Squadron tags and similar decoration, e.g. "[ISAF] Mobius 1" or "Mobius 1 (VF-1)".
    /// Removed before matching and before the bot says the name out loud - "bracket I S A F
    /// bracket Mobius one" is not how a radio call sounds.
    /// </summary>
    private static readonly Regex TagPattern = new(@"[\[\({<][^\]\)}>]*[\]\)}>]", RegexOptions.Compiled);

    /// <summary>
    /// Separators that split a callsign from a handle and cannot occur inside a callsign itself.
    /// The one from the configuration is tried first; these are the fallbacks, so a pilot who
    /// writes "Mobius 1 / Reaper" is still understood. Deliberately excludes "-", which is part
    /// of flight numbers like "1-1".
    /// </summary>
    private static readonly char[] FallbackSeparators = { '|', '/', '\\', ':', ';', '~' };

    /// <summary>How an SRS player name breaks down.</summary>
    /// <param name="Raw">Exactly what SRS reported, untouched.</param>
    /// <param name="Callsign">The part before the separator, tags removed - what the bot calls the pilot.</param>
    /// <param name="Handle">The part after the separator, tags removed. Empty when there is no separator.</param>
    public sealed record Parts(string Raw, string Callsign, string Handle);

    /// <summary>
    /// Splits an SRS player name into the callsign and the handle behind it. The configured
    /// separator wins; if it isn't present, the common alternatives are tried, and failing that
    /// the whole (tag-stripped) name counts as the callsign.
    /// </summary>
    public static Parts Split(string? rawName, string? configuredSeparator)
    {
        var raw = (rawName ?? "").Trim();
        if (raw.Length == 0)
            return new Parts("", "", "");

        // Try the configured separator first - it may be a multi-character string.
        if (!string.IsNullOrEmpty(configuredSeparator))
        {
            int index = raw.IndexOf(configuredSeparator, StringComparison.Ordinal);
            if (index >= 0)
                return new Parts(raw,
                    StripTags(raw[..index]),
                    StripTags(raw[(index + configuredSeparator.Length)..]));
        }

        int fallbackIndex = raw.IndexOfAny(FallbackSeparators);
        if (fallbackIndex >= 0)
            return new Parts(raw, StripTags(raw[..fallbackIndex]), StripTags(raw[(fallbackIndex + 1)..]));

        return new Parts(raw, StripTags(raw), "");
    }

    /// <summary>
    /// The name the bot addresses the pilot by: the callsign part, without squadron tags. Falls
    /// back to the raw name if stripping would leave nothing - better to say something odd than
    /// to address nobody.
    /// </summary>
    public static string DisplayCallsign(string? rawName, string? configuredSeparator)
    {
        var parts = Split(rawName, configuredSeparator);
        return parts.Callsign.Length > 0 ? parts.Callsign : parts.Raw;
    }

    /// <summary>Removes "[...]"-style decoration and tidies up the whitespace it leaves behind.</summary>
    public static string StripTags(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var withoutTags = TagPattern.Replace(value, " ");
        return Regex.Replace(withoutTags, @"\s+", " ").Trim();
    }

    /// <summary>
    /// Reduces a name to what is left when the ways of writing it stop mattering: lower case, no
    /// tags, no punctuation, no spaces. "Mobius 1-1", "MOBIUS 11" and "mobius_1_1" all become
    /// "mobius11".
    ///
    /// This is a comparison key and nothing else - never show it to anyone.
    /// </summary>
    public static string CanonicalKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";

        var stripped = StripTags(value);
        var builder = new StringBuilder(stripped.Length);

        foreach (var c in stripped)
        {
            if (char.IsLetterOrDigit(c))
                builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Makes a callsign sound like a radio call. Flight numbers are spoken digit by digit in
    /// every air force there is - "Spare 15" is "Spare One Five", never "Spare Fifteen" - so each
    /// digit is separated, and hyphens become spaces rather than being read out as "dash".
    /// Squadron tags are dropped: nobody says "bracket ISAF bracket" on the radio.
    /// </summary>
    public static string ForSpeech(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        var cleaned = StripTags(text).Replace('-', ' ').Replace('_', ' ');
        var builder = new StringBuilder(cleaned.Length * 2);
        char previous = '\0';

        foreach (var c in cleaned)
        {
            // A space between two digits, and between a letter and a digit, so "Enfield11" is
            // read as "Enfield 1 1" instead of "Enfield eleven".
            if (char.IsDigit(c) && previous != '\0' && previous != ' ')
                builder.Append(' ');
            else if (char.IsLetter(c) && char.IsDigit(previous))
                builder.Append(' ');

            builder.Append(c);
            previous = c;
        }

        return Regex.Replace(builder.ToString(), @"\s+", " ").Trim();
    }

    /// <summary>
    /// Number words to figures, so a spoken callsign can be compared with a written one. Includes
    /// the aviation forms - "niner" for 9, and "tree"/"fower"/"fife" which some pilots use and
    /// some transcribers produce.
    /// </summary>
    private static readonly (string Word, string Digit)[] SpokenDigits =
    {
        ("zero", "0"), ("oh", "0"),
        ("one", "1"), ("won", "1"),
        ("two", "2"), ("too", "2"),
        ("three", "3"), ("tree", "3"),
        ("four", "4"), ("fower", "4"), ("for", "4"),
        ("five", "5"), ("fife", "5"),
        ("six", "6"),
        ("seven", "7"),
        ("eight", "8"), ("ate", "8"),
        ("nine", "9"), ("niner", "9"),
    };

    private static readonly Regex SpokenDigitPattern = new(
        @"\b(" + string.Join("|", SpokenDigits.Select(d => d.Word)) + @")\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Rewrites spelled-out numbers as figures: "punch one one" becomes "punch 1 1".
    ///
    /// Needed because a pilot saying their callsign on the radio says the digits, and the
    /// transcriber writes what it hears - while DCS has them written as "Punch 1-1". Comparing the
    /// two requires meeting in the middle.
    ///
    /// Note that "for" maps to 4. That is wrong far more often than it is right in ordinary prose,
    /// which is why this is only ever used to build a comparison key that then has to match an
    /// actual unit - a stray 4 that matches nothing simply does nothing.
    /// </summary>
    public static string NormalizeSpokenNumbers(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        var lookup = SpokenDigits.ToDictionary(d => d.Word, d => d.Digit, StringComparer.OrdinalIgnoreCase);
        return SpokenDigitPattern.Replace(text, m => lookup[m.Value.ToLowerInvariant()]);
    }

    /// <summary>
    /// Finds the candidate whose name appears somewhere in a transcript - the pilot who said
    /// "active runway for Punch 1-1" naming themselves rather than relying on their SRS name
    /// matching their DCS name.
    ///
    /// Both sides are reduced to canonical keys after spelled-out numbers are turned into figures,
    /// so "punch one one", "Punch 1-1" and "PUNCH11" all meet. A key has to be at least five
    /// characters to be considered, and it has to match exactly one candidate - two pilots whose
    /// names both appear means we don't know which one is asking, and picking would be worse than
    /// admitting it.
    /// </summary>
    public static MatchResult<T> FindMatchInTranscript<T>(
        IEnumerable<T> candidates,
        string? transcript,
        params Func<T, string?>[] fields)
        where T : class
    {
        var list = candidates as IList<T> ?? candidates.ToList();
        if (list.Count == 0 || string.IsNullOrWhiteSpace(transcript))
            return new MatchResult<T>(null, MatchRule.None, "no candidates or no transcript");

        var haystack = CanonicalKey(NormalizeSpokenNumbers(transcript));
        if (haystack.Length == 0)
            return new MatchResult<T>(null, MatchRule.None, "nothing usable in the transcript");

        var hits = new List<(T Candidate, string Key)>();

        foreach (var candidate in list)
        {
            foreach (var field in fields)
            {
                var key = CanonicalKey(NormalizeSpokenNumbers(field(candidate)));

                // Short keys appear inside unrelated words far too easily to be trusted here -
                // and a callsign plus its flight number is comfortably longer than this.
                if (key.Length < 5) continue;

                if (haystack.Contains(key, StringComparison.Ordinal))
                {
                    hits.Add((candidate, key));
                    break; // one hit per candidate is enough
                }
            }
        }

        if (hits.Count == 1)
            return new MatchResult<T>(hits[0].Candidate, MatchRule.SpokenInTranscript,
                $"\"{hits[0].Key}\" was named in the transmission");

        if (hits.Count > 1)
            return new MatchResult<T>(null, MatchRule.None,
                $"{hits.Count} candidates were named in the transmission - too ambiguous to pick one");

        return new MatchResult<T>(null, MatchRule.None, "no candidate was named in the transmission");
    }

    /// <summary>Why a candidate was picked - logged so a wrong or missing match can be understood.</summary>
    public enum MatchRule
    {
        /// <summary>No candidate matched.</summary>
        None,

        /// <summary>The DCS field equals the full SRS name exactly. The safest case.</summary>
        ExactRaw,

        /// <summary>Equal once case, spacing and punctuation are ignored.</summary>
        CanonicalFull,

        /// <summary>The DCS field matches the handle behind the separator.</summary>
        CanonicalHandle,

        /// <summary>The DCS field matches the callsign in front of the separator.</summary>
        CanonicalCallsign,

        /// <summary>
        /// One - and only one - candidate contains the key, or is contained in it. Used last,
        /// and skipped entirely when more than one candidate would qualify.
        /// </summary>
        UniqueSubstring,

        /// <summary>
        /// The pilot named themselves in the transmission ("...for Punch 1-1") and exactly one
        /// candidate matched that name. See FindMatchInTranscript.
        /// </summary>
        SpokenInTranscript,
    }

    /// <summary>What <see cref="FindMatch{T}"/> concluded.</summary>
    public sealed record MatchResult<T>(T? Match, MatchRule Rule, string Explanation)
    {
        public bool Found => Rule != MatchRule.None;
    }

    /// <summary>
    /// Finds the candidate that is the given SRS player, trying the strictest rule first and only
    /// loosening when nothing was found. Every rule is applied across all candidates before the
    /// next one is tried, so a weaker rule can never beat a stronger one.
    /// </summary>
    /// <param name="candidates">The DCS units (or whatever else) to search.</param>
    /// <param name="srsName">The player name SRS reported.</param>
    /// <param name="configuredSeparator">PlayerNameCallsignSeparator from the configuration.</param>
    /// <param name="fields">
    /// The name-bearing fields of a candidate, most trustworthy first - e.g. the player name, then
    /// the unit callsign, then the unit name.
    /// </param>
    public static MatchResult<T> FindMatch<T>(
        IEnumerable<T> candidates,
        string? srsName,
        string? configuredSeparator,
        params Func<T, string?>[] fields)
        where T : class
    {
        var list = candidates as IList<T> ?? candidates.ToList();
        if (list.Count == 0 || string.IsNullOrWhiteSpace(srsName))
            return new MatchResult<T>(null, MatchRule.None, "no candidates or no sender name");

        var parts = Split(srsName, configuredSeparator);
        var fullKey = CanonicalKey(parts.Raw);
        var handleKey = CanonicalKey(parts.Handle);
        var callsignKey = CanonicalKey(parts.Callsign);

        // 1. Character-for-character, before anything is normalised away.
        foreach (var field in fields)
        {
            var hit = list.FirstOrDefault(c =>
                string.Equals(field(c)?.Trim(), parts.Raw, StringComparison.OrdinalIgnoreCase));
            if (hit != null)
                return new MatchResult<T>(hit, MatchRule.ExactRaw, $"exact match on \"{parts.Raw}\"");
        }

        // 2-4. Canonical keys, from the most specific part of the name to the least.
        foreach (var (key, rule, label) in new[]
                 {
                     (fullKey, MatchRule.CanonicalFull, "full name"),
                     (handleKey, MatchRule.CanonicalHandle, "handle"),
                     (callsignKey, MatchRule.CanonicalCallsign, "callsign"),
                 })
        {
            if (key.Length == 0) continue;

            foreach (var field in fields)
            {
                var hit = list.FirstOrDefault(c => CanonicalKey(field(c)) == key);
                if (hit != null)
                    return new MatchResult<T>(hit, rule, $"{label} \"{key}\" matched after normalising");
            }
        }

        // 5. Last resort: a partial match, for the common case of a handle that differs by a
        //    suffix - "Bernhard" in DCS against "Enfield 1-1 | Bernhard_S" in SRS.
        //
        //    Deliberately narrow, because a loose version of this rule is worse than no rule at
        //    all. It applies ONLY to the first field (the caller's most trustworthy one, i.e. the
        //    DCS player name) and ONLY to the handle and full-name keys. It must not see the
        //    unit's callsign or group name: those hold the FLIGHT's callsign, shared by every
        //    aircraft in it, so "Mobius" is a substring of both "Mobius 1-1" and "Mobius 1-2" -
        //    and a partial match there would confidently hand a wingman's BRAA call to the lead.
        //
        //    Nor is the callsign key used, for the same reason from the other direction.
        if (fields.Length > 0)
        {
            var playerNameField = fields[0];

            foreach (var key in new[] { handleKey, fullKey })
            {
                // Short keys match far too much to be trusted here.
                if (key.Length < 4) continue;

                var partial = list
                    .Where(c =>
                    {
                        var candidateKey = CanonicalKey(playerNameField(c));
                        return candidateKey.Length >= 4 &&
                               (candidateKey.Contains(key, StringComparison.Ordinal) ||
                                key.Contains(candidateKey, StringComparison.Ordinal));
                    })
                    .ToList();

                if (partial.Count == 1)
                    return new MatchResult<T>(partial[0], MatchRule.UniqueSubstring,
                        $"\"{key}\" partially matched exactly one player name");

                if (partial.Count > 1)
                    return new MatchResult<T>(null, MatchRule.None,
                        $"\"{key}\" partially matched {partial.Count} player names - too ambiguous to pick one");
            }
        }

        return new MatchResult<T>(null, MatchRule.None,
            $"nothing matched \"{parts.Raw}\" among {list.Count} candidate(s)");
    }
}
