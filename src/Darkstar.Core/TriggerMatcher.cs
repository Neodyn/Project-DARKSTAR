using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Darkstar;

/// <summary>
/// Decides whether a transcribed transmission contains one of a set of trigger phrases.
///
/// WHY NOT JUST Contains(): a bare substring test fires on a phrase that merely happens to sit
/// inside a longer word, and it fires just as confidently on a mis-transcription as on something
/// the pilot actually said. The same mistake was made in the wake word detector and cost real
/// false triggers there, so the same fix applies here: a trigger has to appear as whole words.
///
/// "threats" no longer matches "threatsomething", and a short trigger somebody adds by hand -
/// "dope", say - can no longer match every word ending in it.
///
/// What this can NOT do is tell a deliberate request from a mis-transcription that happens to
/// read like one. That fight is won further upstream, by not feeding the transcriber a list of
/// command phrases to snap onto - see VocabularyBook and AppConfig.WarnAboutVocabularyTriggerConflicts.
/// </summary>
public static class TriggerMatcher
{
    /// <summary>
    /// Compiled patterns, keyed by trigger phrase. Classification runs on every transmission, and
    /// building a regex per trigger per call would be wasteful; the set of triggers is small and
    /// only changes when the configuration is reloaded.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Regex> PatternCache = new();

    /// <summary>
    /// True if any of the triggers appears in the text as whole words. Case-insensitive; empty
    /// triggers are ignored rather than matching everything.
    /// </summary>
    public static bool MatchesAny(string? text, IEnumerable<string>? triggers) =>
        FindMatch(text, triggers) != null;

    /// <summary>
    /// The trigger that matched, or null. Returned rather than just a bool so the log can say
    /// which phrase fired - which is the difference between "the bot did something odd" and
    /// "the bot matched 'bogey dope' in a transcript that shouldn't have contained it".
    /// </summary>
    public static string? FindMatch(string? text, IEnumerable<string>? triggers)
    {
        if (string.IsNullOrWhiteSpace(text) || triggers == null)
            return null;

        foreach (var trigger in triggers)
        {
            if (string.IsNullOrWhiteSpace(trigger)) continue;

            var normalized = trigger.Trim();
            if (PatternFor(normalized).IsMatch(text))
                return normalized;
        }

        return null;
    }

    /// <summary>
    /// The trigger that matched, plus what follows it in the text.
    ///
    /// Needed where a request names somebody: "Punch 1-1, where is Springfield 2-1" contains two
    /// pilots, and asking "which names appear in this sentence" can only answer "too many".
    /// Asking "who is named after <c>where is</c>" has exactly one answer. The tail is what makes
    /// that possible, so this is about position in the sentence, not about loosening any matching
    /// rule - see <see cref="FriendlyPosition"/>.
    /// </summary>
    /// <param name="Trigger">The phrase that fired.</param>
    /// <param name="Tail">Everything after it, trimmed. Empty when the trigger ended the sentence.</param>
    public sealed record TriggerHit(string Trigger, string Tail);

    /// <summary>
    /// As <see cref="FindMatch"/>, but also returns the text following the trigger. The
    /// earliest-matching trigger wins, so a transcript is read the way it was spoken rather than
    /// in the order the triggers happen to be configured.
    /// </summary>
    public static TriggerHit? FindMatchWithTail(string? text, IEnumerable<string>? triggers)
    {
        if (string.IsNullOrWhiteSpace(text) || triggers == null) return null;

        string? bestTrigger = null;
        var bestStart = int.MaxValue;
        var bestEnd = 0;

        foreach (var trigger in triggers)
        {
            if (string.IsNullOrWhiteSpace(trigger)) continue;

            var normalized = trigger.Trim();
            var match = PatternFor(normalized).Match(text);
            if (!match.Success) continue;

            // Earliest wins; on a tie the longer phrase does, so "say position of" beats
            // "position of" and the tail doesn't start with a leftover word.
            if (match.Index < bestStart || (match.Index == bestStart && match.Length > bestEnd - bestStart))
            {
                bestTrigger = normalized;
                bestStart = match.Index;
                bestEnd = match.Index + match.Length;
            }
        }

        return bestTrigger == null ? null : new TriggerHit(bestTrigger, text[bestEnd..].Trim());
    }

    /// <summary>
    /// A whole-word pattern for one trigger. Word boundaries go on the outside of the whole
    /// phrase, and runs of whitespace inside it match any whitespace, so "bogey  dope" in a
    /// transcript still matches the trigger "bogey dope".
    ///
    /// A trigger that starts or ends with a non-word character (say "?") gets no boundary on that
    /// side - \b next to punctuation would never match.
    /// </summary>
    private static Regex PatternFor(string trigger) => PatternCache.GetOrAdd(trigger, t =>
    {
        var words = t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var body = string.Join(@"\s+", words.Select(Regex.Escape));

        var prefix = char.IsLetterOrDigit(t[0]) ? @"\b" : "";
        var suffix = char.IsLetterOrDigit(t[^1]) ? @"\b" : "";

        return new Regex(prefix + body + suffix,
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    });

    /// <summary>
    /// Every trigger phrase that also appears in the vocabulary hint list - the combination that
    /// makes the bot answer the wrong request. See AppConfig.WarnAboutVocabularyTriggerConflicts.
    /// </summary>
    public static List<string> FindVocabularyConflicts(IEnumerable<string>? vocabulary,
        IEnumerable<string?>? allTriggers)
    {
        var conflicts = new List<string>();
        if (vocabulary == null || allTriggers == null) return conflicts;

        var triggerSet = new HashSet<string>(
            allTriggers.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!.Trim()),
            StringComparer.OrdinalIgnoreCase);

        foreach (var term in vocabulary)
        {
            if (string.IsNullOrWhiteSpace(term)) continue;
            if (triggerSet.Contains(term.Trim()) && !conflicts.Contains(term.Trim(), StringComparer.OrdinalIgnoreCase))
                conflicts.Add(term.Trim());
        }

        return conflicts;
    }
}
