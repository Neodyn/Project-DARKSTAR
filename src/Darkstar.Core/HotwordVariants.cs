using System.Text.RegularExpressions;

namespace Darkstar;

/// <summary>
/// Accepting more than one spelling of a wake word, and working out from real recordings which
/// spellings are worth accepting.
///
/// THE PROBLEM THIS SOLVES: the hotword runs through a small English Vosk model, and most players
/// on a German-speaking server are not native English speakers. "Overlord" spoken with a German
/// accent comes back as "over lord", "oberlord" or "of a lord" - and the first of those already
/// fails a <c>\bOverlord\b</c> match because of the space, so the bot stays silent and nobody can
/// tell why. The request phrases have had variant lists for a while (DcsIntelBogeyDopeTriggers
/// ships "bogie dope" and "bogey dobe" on purpose); the wake word had exactly one accepted string.
///
/// WHAT THIS IS NOT: training the speech model. That would be a Kaldi training run with hours of
/// labelled audio. This accepts what the existing model already produces.
///
/// THE POINT OF <see cref="Suggest"/>: every accepted variant also raises the false-trigger rate,
/// so the list must not be guessed at. The bot already records every transmission tagged with
/// whether the wake word fired, so the variants can be read off what the model actually heard on
/// the transmissions it missed - and any candidate that also turns up on recordings where nobody
/// said the wake word is flagged rather than suggested.
/// </summary>
public static class HotwordVariants
{
    /// <summary>
    /// Longest candidate phrase considered, in words. A wake word is one or two words; allowing
    /// three covers a model that splits one into three ("of a lord") without turning the
    /// suggestion list into every phrase anybody said.
    /// </summary>
    public const int MaxCandidateWords = 3;

    /// <summary>
    /// How far a candidate may differ from the keyword, as a fraction of the keyword's length,
    /// once spaces are removed. 0.4 accepts "oberlord" for "overlord" (1 edit in 8) and rejects
    /// "bogey dope" - the aim is "a mangled version of this word", not "any phrase at all".
    /// </summary>
    public const double DefaultMaxDistanceRatio = 0.4;

    // ---------------------------------------------------------------------------------------
    // Which spellings a radio accepts
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The full set of spellings one radio accepts: its wake word first, then its variants.
    ///
    /// Variants belong to the word they are variants OF, which decides the inheritance rule: a
    /// radio that overrides the wake word does NOT inherit the global variants. Otherwise a
    /// tanker on "Texaco" would start answering to "over lord" because that was configured
    /// globally for the AWACS - a bug that would be very hard to see in a log.
    /// </summary>
    /// <summary>
    /// The accepted set for an already-resolved wake word and its own variants - for a detector,
    /// which is handed the outcome of the inheritance rule rather than deciding it.
    /// </summary>
    public static List<string> Resolve(string? keyword, IEnumerable<string>? variants) =>
        Resolve(keyword, variants, globalKeyword: null, globalVariants: null);

    public static List<string> Resolve(string? radioKeyword, IEnumerable<string>? radioVariants,
        string? globalKeyword, IEnumerable<string>? globalVariants)
    {
        var usesGlobalKeyword = string.IsNullOrWhiteSpace(radioKeyword);
        var keyword = usesGlobalKeyword ? globalKeyword : radioKeyword;

        var ownVariants = Clean(radioVariants);

        // Own variants win. Failing that, the global ones apply only while the radio is also
        // using the global wake word.
        var variants = ownVariants.Count > 0
            ? ownVariants
            : usesGlobalKeyword ? Clean(globalVariants) : new List<string>();

        var accepted = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var phrase in new[] { keyword }.Concat(variants))
        {
            if (string.IsNullOrWhiteSpace(phrase)) continue;
            var normalized = Normalize(phrase);
            if (normalized.Length > 0 && seen.Add(normalized)) accepted.Add(normalized);
        }

        return accepted;
    }

    /// <summary>
    /// One regex matching any accepted spelling, as whole words.
    /// </summary>
    /// <remarks>
    /// Still whole-word anchored, for the reason the single-keyword version was: a substring match
    /// fires on a wake word that is merely part of a longer recognized word, and makes one radio's
    /// detector sensitive to another's. Runs of whitespace inside a phrase match any whitespace,
    /// so a variant written "over lord" also matches "over  lord" from the recognizer.
    /// </remarks>
    public static Regex BuildRegex(IEnumerable<string> acceptedPhrases)
    {
        var alternatives = acceptedPhrases
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => string.Join(@"\s+", Normalize(p).Split(' ').Select(Regex.Escape)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (alternatives.Count == 0)
        {
            // Matches nothing. A detector with no wake word at all must never fire on everything,
            // which is what an empty alternation group would do.
            return new Regex(@"(?!)", RegexOptions.Compiled);
        }

        return new Regex($@"\b(?:{string.Join("|", alternatives)})\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    // ---------------------------------------------------------------------------------------
    // Working out the variants from recordings
    // ---------------------------------------------------------------------------------------

    /// <param name="Phrase">What the recognizer heard, ready to paste into the variant list.</param>
    /// <param name="Count">On how many missed recordings it appeared.</param>
    /// <param name="Distance">Edit distance to the wake word, spaces removed. 0 = only the spacing differed.</param>
    /// <param name="AlsoWhenNobodyCalled">
    /// True when the phrase also appears on recordings where the wake word was not said. Accepting
    /// it would buy hits at the price of false triggers, so it is reported but not recommended.
    /// </param>
    public sealed record Suggestion(string Phrase, int Count, int Distance, bool AlsoWhenNobodyCalled)
    {
        public bool Recommended => !AlsoWhenNobodyCalled;
    }

    /// <summary>
    /// Proposes wake-word variants from what the recognizer actually produced.
    /// </summary>
    /// <param name="keyword">The configured wake word.</param>
    /// <param name="missedTranscripts">
    /// What was heard on recordings where the pilot did say the wake word and the bot did not react.
    /// </param>
    /// <param name="unwantedTranscripts">
    /// What was heard on recordings where nobody said it. Any candidate found here is flagged.
    /// </param>
    /// <param name="alreadyAccepted">Spellings the radio accepts already, so they aren't re-proposed.</param>
    /// <param name="maxDistanceRatio">See <see cref="DefaultMaxDistanceRatio"/>.</param>
    /// <returns>Most frequent first, then closest to the keyword. Flagged candidates last.</returns>
    public static List<Suggestion> Suggest(
        string keyword,
        IEnumerable<string> missedTranscripts,
        IEnumerable<string>? unwantedTranscripts = null,
        IEnumerable<string>? alreadyAccepted = null,
        double maxDistanceRatio = DefaultMaxDistanceRatio)
    {
        var target = Compact(keyword);
        if (target.Length == 0) return new List<Suggestion>();

        var budget = Math.Max(1, (int)Math.Floor(target.Length * maxDistanceRatio));

        var known = new HashSet<string>(
            (alreadyAccepted ?? Enumerable.Empty<string>())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(Normalize),
            StringComparer.OrdinalIgnoreCase);
        known.Add(Normalize(keyword));

        // Everything the recognizer produced on transmissions nobody meant as a wake word. Checked
        // by compacted form, so "over lord" and "overlord" count as the same risk.
        var unwanted = new HashSet<string>(
            (unwantedTranscripts ?? Enumerable.Empty<string>())
                .SelectMany(t => Candidates(t))
                .Select(Compact),
            StringComparer.OrdinalIgnoreCase);

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var transcript in missedTranscripts)
        {
            // Per recording, not per occurrence: a phrase said three times in one transmission is
            // one piece of evidence, not three.
            foreach (var candidate in Candidates(transcript).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (known.Contains(candidate)) continue;
                if (EditDistance(Compact(candidate), target) > budget) continue;

                counts[candidate] = counts.GetValueOrDefault(candidate) + 1;
            }
        }

        return counts
            .Select(pair => new Suggestion(
                pair.Key,
                pair.Value,
                EditDistance(Compact(pair.Key), target),
                unwanted.Contains(Compact(pair.Key))))
            .OrderBy(s => s.AlsoWhenNobodyCalled)
            .ThenByDescending(s => s.Count)
            .ThenBy(s => s.Distance)
            .ThenBy(s => s.Phrase, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Every run of 1..<see cref="MaxCandidateWords"/> consecutive words in a transcript.</summary>
    internal static List<string> Candidates(string? transcript)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(transcript)) return result;

        var words = Normalize(transcript).Split(' ', StringSplitOptions.RemoveEmptyEntries);

        for (var start = 0; start < words.Length; start++)
        {
            for (var length = 1; length <= MaxCandidateWords && start + length <= words.Length; length++)
                result.Add(string.Join(' ', words, start, length));
        }

        return result;
    }

    // ---------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------

    /// <summary>Trimmed, lower-cased, runs of whitespace collapsed to one space.</summary>
    internal static string Normalize(string? phrase) =>
        string.IsNullOrWhiteSpace(phrase)
            ? ""
            : Regex.Replace(phrase.Trim().ToLowerInvariant(), @"\s+", " ");

    /// <summary>
    /// Normalized with the spaces removed, which is the form the distance is measured on. That is
    /// what makes a recognizer splitting one word into two ("over lord") come out as zero
    /// distance from "overlord" - the most common miss of all, and the cheapest to fix.
    /// </summary>
    internal static string Compact(string? phrase) => Normalize(phrase).Replace(" ", "");

    /// <summary>Levenshtein distance. Two rows rather than a full matrix - these are short strings
    /// but this runs over every candidate of every recording.</summary>
    internal static int EditDistance(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++) previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;

            for (var j = 1; j <= b.Length; j++)
            {
                var substitution = previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    private static List<string> Clean(IEnumerable<string>? phrases) =>
        (phrases ?? Enumerable.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
