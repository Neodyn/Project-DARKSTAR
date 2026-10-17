namespace Darkstar;

/// <summary>What became of one transmission.</summary>
public enum TransmissionOutcome
{
    /// <summary>The transcriber returned nothing usable. The pilot was asked to repeat.</summary>
    Unintelligible,

    /// <summary>
    /// Words came back, but nothing matched: no call, no phrase, no radio check. The reply came
    /// from Gemini or from the fallback. This is the interesting one - it is the bucket that
    /// holds every mis-heard trigger.
    /// </summary>
    Unmatched,

    /// <summary>A fixed phrase from phrases.json answered it.</summary>
    Phrase,

    /// <summary>A real call was recognised and answered. The outcome everything aims at.</summary>
    Call,
}

/// <summary>One transmission and what happened to it.</summary>
/// <param name="Transcript">What the transcriber returned. Empty for an unintelligible one.</param>
/// <param name="MatchedTrigger">The phrase that fired, where one did.</param>
public sealed record Transmission(
    DateTime AtUtc,
    string Pilot,
    double FrequencyHz,
    string Transcript,
    TransmissionOutcome Outcome,
    string? MatchedTrigger);

/// <param name="ImmediateRepeats">
/// A pilot transmitting again, soon, after something that was not answered as a call. The
/// strongest signal available that the first attempt failed, and the only one that does not
/// depend on the bot's own opinion of how it did.
/// </param>
/// <param name="TopUnmatched">The transcripts that went unmatched most often, commonest first.</param>
public sealed record SpeechReport(
    int Total,
    int Calls,
    int Phrases,
    int Unmatched,
    int Unintelligible,
    int ImmediateRepeats,
    int SayAgains,
    List<(string Transcript, int Count)> TopUnmatched);

/// <summary>
/// What the bot can work out about its own listening, from transmissions it has already handled.
///
/// "It does not understand me" is the hardest complaint to act on, because the thing that failed
/// leaves no trace a pilot can see and three different stages can produce it. The manual's answer
/// so far has been to go and measure by hand. This does it continuously instead: the bot already
/// knows, for every transmission, whether it understood, guessed or gave up, and a pilot who
/// transmits again four seconds later has just told it the last answer was no good.
///
/// Everything here is a pure function over a list of transmissions, so each figure can be stated
/// as "these inputs, that count" rather than flown for.
/// </summary>
public static class SpeechQuality
{
    /// <summary>
    /// How soon a second transmission from the same pilot counts as a repeat rather than a new
    /// request.
    ///
    /// Long enough to cover a pilot hearing the wrong answer and keying up again, short enough
    /// that a second, genuinely different call is not counted as a failure. Twenty seconds is the
    /// compromise, and it is a constant rather than a literal so it can be argued with once there
    /// are real numbers.
    /// </summary>
    public static readonly TimeSpan RepeatWindow = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Below this many transmissions the percentages say more about the sample than about the
    /// bot. One understood call out of one is not a hundred percent of anything.
    /// </summary>
    public const int EnoughToJudge = 10;

    /// <summary>How many unmatched transcripts the report names.</summary>
    public const int UnmatchedToName = 5;

    /// <summary>
    /// How many transmissions are kept. A dedicated server runs for days, and an unbounded list
    /// of every word ever said to it is a memory leak with a report attached. The report says how
    /// many it covers, so a truncated window describes itself.
    /// </summary>
    public const int MaxRemembered = 2000;

    public static SpeechReport Analyse(IReadOnlyList<Transmission>? log, int sayAgains = 0)
    {
        var entries = (log ?? Array.Empty<Transmission>())
            .OrderBy(t => t.AtUtc)
            .ToList();

        var repeats = 0;
        var lastByPilot = new Dictionary<string, Transmission>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            var pilot = string.IsNullOrWhiteSpace(entry.Pilot) ? "(unknown)" : entry.Pilot.Trim();

            if (lastByPilot.TryGetValue(pilot, out var previous)
                && previous.Outcome != TransmissionOutcome.Call
                && entry.AtUtc - previous.AtUtc <= RepeatWindow)
            {
                repeats++;
            }

            lastByPilot[pilot] = entry;
        }

        // Grouped on a folded key so "Bogey dope" and "bogey  dope" are one line, but reported
        // with the spelling as it was heard - that spelling is the thing somebody would paste
        // into the trigger list.
        var unmatched = entries
            .Where(t => t.Outcome == TransmissionOutcome.Unmatched && !string.IsNullOrWhiteSpace(t.Transcript))
            .GroupBy(t => Fold(t.Transcript))
            .Select(g => (Transcript: g.First().Transcript.Trim(), Count: g.Count()))
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.Transcript, StringComparer.OrdinalIgnoreCase)
            .Take(UnmatchedToName)
            .ToList();

        return new SpeechReport(
            Total: entries.Count,
            Calls: entries.Count(t => t.Outcome == TransmissionOutcome.Call),
            Phrases: entries.Count(t => t.Outcome == TransmissionOutcome.Phrase),
            Unmatched: entries.Count(t => t.Outcome == TransmissionOutcome.Unmatched),
            Unintelligible: entries.Count(t => t.Outcome == TransmissionOutcome.Unintelligible),
            ImmediateRepeats: repeats,
            SayAgains: sayAgains,
            TopUnmatched: unmatched);
    }

    private static string Fold(string text) =>
        string.Join(" ", text.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// The report as lines for the log. Returns an empty list when there is nothing to say, so a
    /// bot that was never spoken to does not announce that it understood none of nothing.
    /// </summary>
    public static List<string> Describe(SpeechReport report)
    {
        if (report.Total == 0) return new List<string>();

        var lines = new List<string>
        {
            $"{report.Total} transmission(s): {report.Calls} answered as a call, " +
            $"{report.Phrases} as a fixed phrase, {report.Unmatched} matched nothing, " +
            $"{report.Unintelligible} unintelligible.",
        };

        if (report.Total < EnoughToJudge)
        {
            lines.Add($"Too few to judge - percentages start at {EnoughToJudge} transmissions.");
            return lines;
        }

        var understood = 100.0 * report.Calls / report.Total;
        lines.Add($"{understood:0}% were understood as a call.");

        // The one figure that is not the bot's own opinion of how it did.
        if (report.ImmediateRepeats > 0)
            lines.Add($"{report.ImmediateRepeats} transmission(s) were a pilot trying again within " +
                      $"{RepeatWindow.TotalSeconds:0} seconds of something that was not answered as a call - " +
                      "the clearest sign of a miss, because the pilot is the one saying so.");

        if (report.SayAgains > 0)
            lines.Add($"{report.SayAgains} \"say again\" request(s).");

        if (report.TopUnmatched.Count > 0)
        {
            lines.Add("Heard but matched nothing, commonest first - a spelling here is a candidate " +
                      "for the trigger list or for KeywordVariants:");

            foreach (var (transcript, count) in report.TopUnmatched)
                lines.Add($"  {count}x  \"{transcript}\"");
        }

        return lines;
    }
}
