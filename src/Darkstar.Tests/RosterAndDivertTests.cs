using static Darkstar.Tests.Test;

namespace Darkstar.Tests;

/// <summary>
/// Who is on station, what they were last told, and the divert call.
///
/// All of it is state and wording rather than geometry, and all of it is the kind of thing that
/// only misbehaves on the second or third transmission - which is exactly what nobody tests by
/// flying.
/// </summary>
internal static class RosterAndDivertTests
{
    public static void Run()
    {
        Section("Check in, check out");

        var config = new AppConfig();
        var roster = new PilotRoster(config);

        Check("nobody is checked in to start with", !roster.IsCheckedIn("Punch 1-1"));

        var checkedIn = roster.CheckIn("Punch 1-1");
        Check("checking in says the pilot's name back", checkedIn.Contains("Punch 1-1"), checkedIn);
        Check("and files them", roster.IsCheckedIn("Punch 1-1"));

        roster.CheckOut("Punch 1-1");
        Check("checking out clears the flag", !roster.IsCheckedIn("Punch 1-1"));

        // SRS names are typed by hand and the case varies between sessions.
        roster.CheckIn("punch 1-1");
        Check("the name is matched regardless of case", roster.IsCheckedIn("PUNCH 1-1"));

        Check("a pilot nobody has heard from is not checked in", !roster.IsCheckedIn("Springfield 2-1"));
        Check("an empty name is not a pilot", !roster.IsCheckedIn(""));

        Section("The reply template survives a missing name");

        // A pilot whose SRS name is blank would otherwise be told ", radar contact, cleared into
        // the area" - the placeholder and the comma after it both have to go.
        var nameless = new PilotRoster(config).CheckIn("");
        Check("no stray placeholder", !nameless.Contains("{pilot}"), nameless);
        Check("and no leading comma", !nameless.StartsWith(","), nameless);
        Check("the rest of the sentence is intact", nameless.Contains("radar contact"), nameless);

        Section("Unprompted calls and the gate");

        var ungated = new PilotRoster(new AppConfig { DcsIntelCheckInGatesCalls = false });
        Check("with the gate off, a pilot who never checked in may still be called",
            ungated.MayBeCalledUnprompted("Punch 1-1"));

        var gated = new PilotRoster(new AppConfig { DcsIntelCheckInGatesCalls = true });
        Check("with the gate on, they may not", !gated.MayBeCalledUnprompted("Punch 1-1"));
        gated.CheckIn("Punch 1-1");
        Check("once checked in, they may", gated.MayBeCalledUnprompted("Punch 1-1"));
        gated.CheckOut("Punch 1-1");
        Check("and checking out closes it again", !gated.MayBeCalledUnprompted("Punch 1-1"));

        // A pilot who asks for a standing watch has put themselves on station. Without this, a
        // server with the gate on would accept the request and then never make the warning.
        var watchRequested = new PilotRoster(new AppConfig { DcsIntelCheckInGatesCalls = true });
        watchRequested.MarkOnStation("Punch 1-1");
        Check("requesting a watch counts as being on station",
            watchRequested.MayBeCalledUnprompted("Punch 1-1"));
        Check("and does so silently - no reply to transmit", watchRequested.IsCheckedIn("Punch 1-1"));

        Section("Say again");

        var memory = new PilotRoster(config);
        Check("with nothing on file, the answer is an honest negative",
            memory.SayAgain("Punch 1-1") == config.DcsIntelNothingToRepeatReply,
            memory.SayAgain("Punch 1-1"));

        memory.Remember("Punch 1-1", "Bogey, bullseye zero three zero, forty five miles.");
        Check("the last reply comes back",
            memory.SayAgain("Punch 1-1") == "Bogey, bullseye zero three zero, forty five miles.");

        // Asking twice is the normal case, not an edge case: a pilot who missed it once may well
        // miss the repeat.
        Check("and comes back again on a second ask",
            memory.SayAgain("Punch 1-1") == "Bogey, bullseye zero three zero, forty five miles.");

        memory.Remember("Punch 1-1", "Picture clean.");
        Check("a newer reply replaces the older one", memory.SayAgain("Punch 1-1") == "Picture clean.");

        // One slot per pilot, not one for the server. Two pilots asking in turn must not hear
        // each other's calls.
        memory.Remember("Springfield 2-1", "Runway two two in use.");
        Check("each pilot has their own slot", memory.SayAgain("Punch 1-1") == "Picture clean.");
        Check("and the other pilot has theirs", memory.SayAgain("Springfield 2-1") == "Runway two two in use.");

        Check("an empty reply is not worth filing", memory.LastReply("Nobody 1-1") == null);
        memory.Remember("Nobody 1-1", "   ");
        Check("nor is a blank one", memory.LastReply("Nobody 1-1") == null);

        Section("The roster does not grow without limit");

        // A busy server cycles through names all evening. Without the cap this would grow for as
        // long as the process runs, which on a dedicated server is weeks.
        var crowded = new PilotRoster(config);
        for (var i = 0; i < PilotRoster.MaxPilots + 50; i++)
            crowded.Remember($"Pilot {i}", $"reply {i}");

        Check($"at most {PilotRoster.MaxPilots} pilots are kept",
            crowded.Count <= PilotRoster.MaxPilots, $"{crowded.Count}");

        // The most recent caller is the one most likely to ask for a repeat, so they are the one
        // that must survive the eviction.
        Check("the newest pilot is still on file",
            crowded.LastReply($"Pilot {PilotRoster.MaxPilots + 49}") == $"reply {PilotRoster.MaxPilots + 49}");

        Section("Classifying the three roster calls");

        var classifier = new PilotRoster(config);

        Check("\"checking in\"",
            classifier.Classify("Darkstar, Punch one one checking in", out _) == RosterRequestKind.CheckIn);
        Check("\"check in\"",
            classifier.Classify("Darkstar, Punch one one check in", out _) == RosterRequestKind.CheckIn);
        Check("\"on station\"",
            classifier.Classify("Darkstar, Punch one one on station", out _) == RosterRequestKind.CheckIn);

        Check("\"checking out\"",
            classifier.Classify("Darkstar, Punch one one checking out", out _) == RosterRequestKind.CheckOut);
        Check("\"returning to base\"",
            classifier.Classify("Darkstar, Punch one one returning to base", out _) == RosterRequestKind.CheckOut);

        // "Checking out" must not be read as a check-in: the two mean opposite things, and the
        // wrong one leaves a pilot on station who has said goodbye.
        Check("checking out is never read as checking in",
            classifier.Classify("checking out", out _) != RosterRequestKind.CheckIn);

        // With the default lists the two cannot collide, so that check passes whatever order they
        // are tested in. The order is a real guarantee all the same, and this is the case it is
        // for: a server owner who shortens their check-in trigger to "check" - which every
        // "checking out" also contains. The more specific of the two has to win.
        var overlapping = new PilotRoster(new AppConfig
        {
            DcsIntelCheckInTriggers = new List<string> { "check" },
            DcsIntelCheckOutTriggers = new List<string> { "check out", "checking out" },
        });
        Check("check out wins over a check-in trigger that also matches",
            overlapping.Classify("Darkstar, Punch one one check out", out _) == RosterRequestKind.CheckOut,
            overlapping.Classify("Darkstar, Punch one one check out", out _).ToString());
        Check("and a plain check-in still checks in",
            overlapping.Classify("Darkstar, Punch one one check", out _) == RosterRequestKind.CheckIn);

        // TriggerMatcher matches whole words, not substrings, so "checking out" never contains
        // the trigger "check" - which is why the case above had to be built with "check out".
        Check("a trigger is a word, not a substring",
            overlapping.Classify("Darkstar, Punch one one checking", out _) == RosterRequestKind.None);

        Check("\"say again\"", classifier.Classify("Darkstar, say again", out _) == RosterRequestKind.SayAgain);
        Check("\"repeat that\"", classifier.Classify("Darkstar, repeat that", out _) == RosterRequestKind.SayAgain);

        Check("an ordinary request is none of the three",
            classifier.Classify("Darkstar, bogey dope", out _) == RosterRequestKind.None);
        Check("nothing is none of the three", classifier.Classify(null, out _) == RosterRequestKind.None);

        Check("the trigger that fired is reported",
            classifier.Classify("Darkstar, say again", out var fired) == RosterRequestKind.SayAgain
                && fired == "say again",
            fired ?? "null");

        Section("The divert call");

        var options = new List<DcsAirfieldService.DivertOption>
        {
            // 52.6 rather than a round number on purpose: truncating instead of rounding gives
            // "fifty two", which is wrong by a mile and compiles perfectly well.
            new("Kutaisi", 245, 38.4, 150),
            new("Senaki", 300, 52.6, 40),
        };

        var divert = DcsAirfieldService.BuildDivert(options, config);
        Check("both airfields are named", divert.Contains("Kutaisi") && divert.Contains("Senaki"), divert);
        Check("the nearest comes first", divert.IndexOf("Kutaisi") < divert.IndexOf("Senaki"), divert);
        // Slow speech is the default, so the range is spelled out: "thirty eight", not "38". The
        // rounding is what is being checked here - 38.4 must not come out as "thirty eight point
        // four miles".
        Check("the range is rounded to whole miles", divert.Contains("thirty eight miles"), divert);
        Check("and not read out to the decimal", !divert.Contains("point"), divert);
        Check("the rounding goes up as well as down", divert.Contains("fifty three miles"), divert);

        // Elevation on the nearest field only. A pilot choosing between two of them wants the
        // bearings; the second elevation is noise at that point.
        Check("the nearest field's elevation is given", divert.Contains("150 feet"), divert);
        Check("the second one's is not", !divert.Contains("40 feet"), divert);

        var single = DcsAirfieldService.BuildDivert(options.Take(1).ToList(), config);
        Check("one option reads as a single answer", single.StartsWith("Divert:"), single);
        Check("two read as a choice", divert.StartsWith("Divert options:"), divert);

        Check("no options at all falls back to the configured negative",
            DcsAirfieldService.BuildDivert(new List<DcsAirfieldService.DivertOption>(), config)
                == config.DcsAirfieldNoVectorReply);

        // Sea-level fields exist, and "field elevation zero feet" is a phrase no controller says.
        var atSeaLevel = DcsAirfieldService.BuildDivert(
            new List<DcsAirfieldService.DivertOption> { new("Batumi", 90, 12.0, 0) }, config);
        Check("a field at sea level gets no elevation phrase",
            !atSeaLevel.Contains("elevation"), atSeaLevel);

        Section("Classifying a divert request");

        var airfieldConfig = new AppConfig { DcsAirfieldEnabled = true };
        var airfieldService = new DcsAirfieldService(airfieldConfig, new DcsIntelService(airfieldConfig));

        Check("\"vector to nearest\"",
            airfieldService.Classify("Darkstar, vector to nearest") == AirfieldRequestKind.Divert);
        Check("\"divert\"",
            airfieldService.Classify("Darkstar, divert") == AirfieldRequestKind.Divert);
        Check("\"nearest airfield\"",
            airfieldService.Classify("Darkstar, nearest airfield") == AirfieldRequestKind.Divert);

        // A pilot asking where to land often mentions a runway in the same breath, and "which
        // runway" is a runway trigger. The more specific question has to win - checked with a
        // phrase that really does match both lists, so the test fails if the order is swapped.
        Check("a divert that also matches a runway trigger is still a divert",
            airfieldService.Classify("Darkstar, divert, which runway") == AirfieldRequestKind.Divert,
            airfieldService.Classify("Darkstar, divert, which runway").ToString());

        // And against the ATIS list, which is the generous one: "weather" is an ATIS trigger, so
        // a pilot who wants a field and the conditions on the way must still get the field.
        Check("a divert that also matches an ATIS trigger is still a divert",
            airfieldService.Classify("Darkstar, divert, and the weather there") == AirfieldRequestKind.Divert,
            airfieldService.Classify("Darkstar, divert, and the weather there").ToString());

        Check("an ATIS request is still an ATIS",
            airfieldService.Classify("Darkstar, Batumi ATIS") == AirfieldRequestKind.Atis);
        Check("a runway request is still a runway request",
            airfieldService.Classify("Darkstar, runway in use") == AirfieldRequestKind.RunwayInUse);

        Check("with the airfield feature off, nothing classifies",
            new DcsAirfieldService(new AppConfig(), new DcsIntelService(new AppConfig()))
                .Classify("Darkstar, divert") == AirfieldRequestKind.None);
    }
}
