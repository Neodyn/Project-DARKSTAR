using static Darkstar.Tests.Test;

namespace Darkstar.Tests;

/// <summary>
/// What the bot works out about its own listening.
///
/// Every figure here is a claim somebody will act on - change a trigger, swap an engine, buy a
/// microphone - so each one is checked against a hand-built set of transmissions where the right
/// answer is countable rather than estimated. A quality report that is itself wrong is worse than
/// none: it sends people to fix the wrong stage.
/// </summary>
internal static class SpeechQualityTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static Transmission At(int second, string pilot, string transcript,
        TransmissionOutcome outcome, string? trigger = null) =>
        new(T0.AddSeconds(second), pilot, 251_000_000, transcript, outcome, trigger);

    public static void Run()
    {
        Section("Counting what happened");

        var log = new List<Transmission>
        {
            At(0,  "Punch 1-1", "Overlord, bogey dope", TransmissionOutcome.Call, "bogey dope"),
            At(60, "Punch 1-1", "Overlord, status", TransmissionOutcome.Phrase, "status"),
            At(120, "Punch 1-1", "Overlord, boogie dope", TransmissionOutcome.Unmatched),
            At(180, "Punch 1-1", "", TransmissionOutcome.Unintelligible),
        };

        var report = SpeechQuality.Analyse(log);
        Check("every transmission is counted", report.Total == 4, $"{report.Total}");
        Check("calls", report.Calls == 1, $"{report.Calls}");
        Check("phrases", report.Phrases == 1, $"{report.Phrases}");
        Check("unmatched", report.Unmatched == 1, $"{report.Unmatched}");
        Check("unintelligible", report.Unintelligible == 1, $"{report.Unintelligible}");
        Check("the four buckets account for everything",
            report.Calls + report.Phrases + report.Unmatched + report.Unintelligible == report.Total);

        Check("nothing at all is a report of nothing", SpeechQuality.Analyse(null).Total == 0);
        Check("an empty log likewise", SpeechQuality.Analyse(new List<Transmission>()).Total == 0);

        Section("A pilot trying again is the clearest sign of a miss");

        // The only figure here that is not the bot grading its own work: the pilot keyed up again,
        // which is them saying the last answer was no good.
        var gaveUpAndRetried = new List<Transmission>
        {
            At(0, "Punch 1-1", "Overlord, boogie dope", TransmissionOutcome.Unmatched),
            At(4, "Punch 1-1", "Overlord, bogey dope", TransmissionOutcome.Call, "bogey dope"),
        };
        Check("a retry within the window counts",
            SpeechQuality.Analyse(gaveUpAndRetried).ImmediateRepeats == 1);

        // A call that worked is not something anybody retries. A second transmission after a good
        // answer is a new request, and counting it would make a busy pilot look like a failure.
        var twoGoodCalls = new List<Transmission>
        {
            At(0, "Punch 1-1", "Overlord, bogey dope", TransmissionOutcome.Call, "bogey dope"),
            At(4, "Punch 1-1", "Overlord, picture", TransmissionOutcome.Call, "picture"),
        };
        Check("but not after a call that was answered",
            SpeechQuality.Analyse(twoGoodCalls).ImmediateRepeats == 0);

        var muchLater = new List<Transmission>
        {
            At(0, "Punch 1-1", "Overlord, boogie dope", TransmissionOutcome.Unmatched),
            At(600, "Punch 1-1", "Overlord, picture", TransmissionOutcome.Call, "picture"),
        };
        Check("and not ten minutes later", SpeechQuality.Analyse(muchLater).ImmediateRepeats == 0);

        // Two pilots talking over each other are not one pilot repeating themselves.
        var twoPilots = new List<Transmission>
        {
            At(0, "Punch 1-1", "Overlord, boogie dope", TransmissionOutcome.Unmatched),
            At(3, "Springfield 2-1", "Overlord, picture", TransmissionOutcome.Call, "picture"),
        };
        Check("a different pilot is not a repeat", SpeechQuality.Analyse(twoPilots).ImmediateRepeats == 0);

        // The case above cannot tell the two apart on its own: Springfield had never spoken, so
        // there was nothing of theirs to confuse Punch's miss with. Here everybody has a history,
        // and Punch's failure sits between two of Springfield's calls - charging it to Springfield
        // would invent a repeat out of two unrelated transmissions.
        var interleaved = new List<Transmission>
        {
            At(0,  "Springfield 2-1", "Overlord, picture", TransmissionOutcome.Call, "picture"),
            At(10, "Punch 1-1", "Overlord, boogie dope", TransmissionOutcome.Unmatched),
            At(12, "Springfield 2-1", "Overlord, bogey dope", TransmissionOutcome.Call, "bogey dope"),
        };
        Check("one pilot's miss is not charged to another pilot",
            SpeechQuality.Analyse(interleaved).ImmediateRepeats == 0,
            $"{SpeechQuality.Analyse(interleaved).ImmediateRepeats}");

        // Somebody saying it a third time is two failures, not one.
        var threeTries = new List<Transmission>
        {
            At(0, "Punch 1-1", "boogie dope", TransmissionOutcome.Unmatched),
            At(4, "Punch 1-1", "boogie dope", TransmissionOutcome.Unmatched),
            At(8, "Punch 1-1", "bogey dope", TransmissionOutcome.Call, "bogey dope"),
        };
        Check("a third attempt counts twice", SpeechQuality.Analyse(threeTries).ImmediateRepeats == 2);

        Check("the window is where it is documented to be",
            SpeechQuality.Analyse(new List<Transmission>
            {
                At(0, "Punch 1-1", "x", TransmissionOutcome.Unmatched),
                At((int)SpeechQuality.RepeatWindow.TotalSeconds, "Punch 1-1", "y", TransmissionOutcome.Call),
            }).ImmediateRepeats == 1);

        // Entries can reach the analyser out of order; the timing rules must not depend on that.
        var shuffled = new List<Transmission>
        {
            At(4, "Punch 1-1", "Overlord, bogey dope", TransmissionOutcome.Call, "bogey dope"),
            At(0, "Punch 1-1", "Overlord, boogie dope", TransmissionOutcome.Unmatched),
        };
        Check("order of arrival does not matter", SpeechQuality.Analyse(shuffled).ImmediateRepeats == 1);

        Section("What was heard but matched nothing");

        // These spellings are the point of the whole exercise: they are what somebody pastes into
        // the trigger list or into KeywordVariants.
        var misheard = new List<Transmission>
        {
            At(0,  "Punch 1-1", "Overlord, boogie dope", TransmissionOutcome.Unmatched),
            At(60, "Springfield 2-1", "overlord,  BOOGIE  dope", TransmissionOutcome.Unmatched),
            At(120, "Punch 1-1", "Overlord, bogey dobe", TransmissionOutcome.Unmatched),
            At(180, "Punch 1-1", "Overlord, picture", TransmissionOutcome.Call, "picture"),
        };
        var heard = SpeechQuality.Analyse(misheard);

        Check("spelling and spacing do not split one mishearing in two",
            heard.TopUnmatched[0].Count == 2, $"{heard.TopUnmatched[0].Count}");
        Check("the commonest comes first",
            heard.TopUnmatched[0].Transcript.Contains("boogie", StringComparison.OrdinalIgnoreCase),
            heard.TopUnmatched[0].Transcript);
        Check("it is reported as it was heard, not folded",
            heard.TopUnmatched[0].Transcript == "Overlord, boogie dope", heard.TopUnmatched[0].Transcript);
        Check("the second mishearing is listed too", heard.TopUnmatched.Count == 2);
        Check("a call that worked is not in the list",
            heard.TopUnmatched.All(u => !u.Transcript.Contains("picture", StringComparison.OrdinalIgnoreCase)));

        var many = Enumerable.Range(0, 20)
            .Select(i => At(i * 60, "Punch 1-1", $"mishearing number {i}", TransmissionOutcome.Unmatched))
            .ToList();
        Check($"at most {SpeechQuality.UnmatchedToName} are named",
            SpeechQuality.Analyse(many).TopUnmatched.Count == SpeechQuality.UnmatchedToName);

        Section("The report says nothing it cannot support");

        // One understood call out of one is not a hundred percent of anything.
        var thin = SpeechQuality.Describe(SpeechQuality.Analyse(new List<Transmission>
        {
            At(0, "Punch 1-1", "Overlord, bogey dope", TransmissionOutcome.Call, "bogey dope"),
        }));
        Check("a tiny sample gets no percentage", thin.All(l => !l.Contains('%')), string.Join(" | ", thin));
        Check("and says why", thin.Any(l => l.Contains("Too few", StringComparison.Ordinal)), string.Join(" | ", thin));

        var enough = Enumerable.Range(0, SpeechQuality.EnoughToJudge)
            .Select(i => At(i * 60, "Punch 1-1", "Overlord, bogey dope",
                i < 7 ? TransmissionOutcome.Call : TransmissionOutcome.Unmatched))
            .ToList();
        var described = SpeechQuality.Describe(SpeechQuality.Analyse(enough));
        Check("a real sample gets one", described.Any(l => l.Contains("70%", StringComparison.Ordinal)),
            string.Join(" | ", described));

        Check("a bot nobody spoke to says nothing at all",
            SpeechQuality.Describe(SpeechQuality.Analyse(new List<Transmission>())).Count == 0);

        // The percentage has to be of calls, not of "anything we replied to" - a bot answering
        // everything from the fallback would otherwise look perfect.
        var allFallback = Enumerable.Range(0, 20)
            .Select(i => At(i * 60, "Punch 1-1", "something", TransmissionOutcome.Unmatched))
            .ToList();
        var bad = SpeechQuality.Describe(SpeechQuality.Analyse(allFallback));
        Check("a bot that understood nothing says 0%",
            bad.Any(l => l.Contains("0%", StringComparison.Ordinal)), string.Join(" | ", bad));

        Section("Say agains are carried through");

        Check("the count reaches the report", SpeechQuality.Analyse(log, sayAgains: 3).SayAgains == 3);
        Check("and the text", SpeechQuality.Describe(SpeechQuality.Analyse(enough, sayAgains: 2))
            .Any(l => l.Contains("say again", StringComparison.OrdinalIgnoreCase)));
        Check("none is not mentioned", SpeechQuality.Describe(SpeechQuality.Analyse(enough))
            .All(l => !l.Contains("say again", StringComparison.OrdinalIgnoreCase)));

        Section("A pilot with no name does not merge with another");

        // Two unidentified transmissions in quick succession are plausibly one pilot, and that is
        // the useful reading - but they must not drag a named pilot's entries in with them.
        var nameless = new List<Transmission>
        {
            At(0, "", "something", TransmissionOutcome.Unmatched),
            At(3, "", "something else", TransmissionOutcome.Unmatched),
            At(5, "Punch 1-1", "Overlord, picture", TransmissionOutcome.Call, "picture"),
        };
        Check("the unnamed ones count as one caller", SpeechQuality.Analyse(nameless).ImmediateRepeats == 1);
    }
}
