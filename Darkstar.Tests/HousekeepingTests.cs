using static Darkstar.Tests.Test;
using RurouniJones.Dcs.Grpc.V0.Common;

namespace Darkstar.Tests;

/// <summary>Retention, alpha check, radio check, voices, variants, friendly positions, docs.</summary>
internal static class HousekeepingTests
{
    public static async Task RunAsync()
    {
        // Three features that all answer the same question - "is the bot actually working?" - and all
        // three are the kind of thing that is easy to get subtly wrong and never notice:
        //
        //   * file retention, where an off-by-one deletes the log you are currently reading
        //   * alpha check, where a bearing measured the wrong way round still sounds plausible
        //   * radio check, where the whole point is that it answers on every radio





        var now = new DateTime(2026, 9, 27, 12, 0, 0);

        FileRetention.Candidate File(string name, double ageDays, double megabytes) =>
            new(name, (long)(megabytes * 1024 * 1024), now - TimeSpan.FromDays(ageDays));

        string Names(IEnumerable<FileRetention.Candidate> files) =>
            string.Join(",", files.Select(f => f.Path));

        // --- retention: the age rule -----------------------------------------------------------------

        Console.WriteLine("Retention, age rule");

        var aged = new[] { File("today", 0, 1), File("week", 7.5, 1), File("month", 31, 1) };

        var byAge = FileRetention.Decide(aged, maxAgeDays: 7, maxTotalMegabytes: 0, alwaysKeepNewest: 0, now);
        Eq("only what is past the limit goes", Names(byAge.TooOld), "week,month");
        Check("and nothing is blamed on the size budget", byAge.OverBudget.Count == 0);

        var noAgeRule = FileRetention.Decide(aged, maxAgeDays: 0, maxTotalMegabytes: 0, alwaysKeepNewest: 0, now);
        Check("zero days disables the rule instead of deleting everything", noAgeRule.Count == 0);

        var negative = FileRetention.Decide(aged, maxAgeDays: -1, maxTotalMegabytes: -1, alwaysKeepNewest: 0, now);
        Check("so does a negative value", negative.Count == 0);

        var exactly = FileRetention.Decide(new[] { File("exactly7", 7, 1) },
            maxAgeDays: 7, maxTotalMegabytes: 0, alwaysKeepNewest: 0, now);
        Check("a file exactly at the limit survives - the rule is 'older than'", exactly.Count == 0);

        // --- retention: the size rule ----------------------------------------------------------------

        Console.WriteLine();
        Console.WriteLine("Retention, size rule");

        var big = new[]
        {
            File("newest", 1, 40),
            File("middle", 2, 40),
            File("oldest", 3, 40),
        };

        var bySize = FileRetention.Decide(big, maxAgeDays: 0, maxTotalMegabytes: 100, alwaysKeepNewest: 0, now);
        Eq("oldest first, and it stops as soon as the folder fits", Names(bySize.OverBudget), "oldest");
        Check("nothing is blamed on age", bySize.TooOld.Count == 0);

        var tight = FileRetention.Decide(big, maxAgeDays: 0, maxTotalMegabytes: 45, alwaysKeepNewest: 0, now);
        Eq("a tighter budget takes the next one too", Names(tight.OverBudget), "oldest,middle");

        var fits = FileRetention.Decide(big, maxAgeDays: 0, maxTotalMegabytes: 200, alwaysKeepNewest: 0, now);
        Check("a folder inside its budget loses nothing", fits.Count == 0);

        // The interesting case: both rules at once. What the age rule already removed must count as
        // freed space, or the size rule deletes a second batch it did not need to.
        var both = FileRetention.Decide(
            new[] { File("new", 1, 40), File("old", 30, 40), File("older", 31, 40) },
            maxAgeDays: 7, maxTotalMegabytes: 50, alwaysKeepNewest: 0, now);
        Eq("the age rule runs first", Names(both.TooOld), "old,older");
        Check("and the size rule counts that space as already freed", both.OverBudget.Count == 0,
            Names(both.OverBudget));

        // --- retention: the live log must survive ----------------------------------------------------

        Console.WriteLine();
        Console.WriteLine("Retention, protecting the newest files");

        var logs = new[] { File("live", 0, 400), File("previous", 1, 400), File("older", 40, 400) };

        // Somebody sets a 1 MB budget. The file being written to is in this folder.
        var absurd = FileRetention.Decide(logs, maxAgeDays: 30, maxTotalMegabytes: 1, alwaysKeepNewest: 3, now);
        Check("an absurd budget cannot delete the protected files", absurd.Count == 0,
            Names(absurd.All));

        var partlyProtected = FileRetention.Decide(logs, maxAgeDays: 30, maxTotalMegabytes: 1, alwaysKeepNewest: 1, now);
        Check("the newest is still safe", !partlyProtected.All.Any(f => f.Path == "live"));
        Check("but the rest is fair game", partlyProtected.Count == 2, Names(partlyProtected.All));

        var keepAll = FileRetention.Decide(logs, maxAgeDays: 1, maxTotalMegabytes: 1, alwaysKeepNewest: 99, now);
        Check("keeping more files than exist is not an error", keepAll.Count == 0);

        Check("bytes freed adds up", byAge.BytesFreed == 2L * 1024 * 1024, byAge.BytesFreed.ToString());

        // --- retention: reading the timestamp out of a name ------------------------------------------

        Console.WriteLine();
        Console.WriteLine("Retention, timestamps from file names");

        var fromRecording = FileRetention.TimestampFromRecordingName(
            "/x/recordings/2026-09-27_14-32-05.123_251.000MHz_hit_Punch 1-1.wav");
        Check("a recording's name carries its own timestamp",
            fromRecording == new DateTime(2026, 9, 27, 14, 32, 5, 123), fromRecording?.ToString("O") ?? "null");

        Check("a hand-renamed file falls back to the file system instead of throwing",
            FileRetention.TimestampFromRecordingName("/x/my notes.wav") == null);
        Check("and so does a short name", FileRetention.TimestampFromRecordingName("/x/a.wav") == null);
        Check("a name that only looks like one is rejected",
            FileRetention.TimestampFromRecordingName("/x/9999-99-99_99-99-99.999_x.wav") == null);

        // --- retention: the driver on a real folder ---------------------------------------------------

        Console.WriteLine();
        Console.WriteLine("Retention, actually deleting");

        var temp = Path.Combine(Path.GetTempPath(), "darkstar-retention-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        try
        {
            void Write(string name, double ageDays, int kilobytes)
            {
                var path = Path.Combine(temp, name);
                System.IO.File.WriteAllBytes(path, new byte[kilobytes * 1024]);
                System.IO.File.SetLastWriteTime(path, DateTime.Now - TimeSpan.FromDays(ageDays));
            }

            Write("2026-09-27_10-00-00.000_fresh.wav", 0, 8);
            Write("old-by-mtime.wav", 400, 8);
            Write("keep.txt", 400, 8);

            var (deleted, freed) = FileRetention.Prune(temp, "*.wav",
                maxAgeDays: 30, maxTotalMegabytes: 0, alwaysKeepNewest: 0, label: "recording");

            Check("the search pattern is respected", System.IO.File.Exists(Path.Combine(temp, "keep.txt")));
            Check("an old file goes", !System.IO.File.Exists(Path.Combine(temp, "old-by-mtime.wav")));
            Check("a fresh one stays", System.IO.File.Exists(Path.Combine(temp, "2026-09-27_10-00-00.000_fresh.wav")));
            Check("the count is reported", deleted == 1 && freed == 8 * 1024, $"{deleted} file(s), {freed} bytes");

            var missing = FileRetention.Prune(Path.Combine(temp, "nope"), "*.wav", 1, 1, 0, "recording");
            Check("a folder that does not exist is not an error", missing.Deleted == 0);

            var disabled = FileRetention.Prune(temp, "*.wav", 0, 0, 0, "recording");
            Check("both limits off means no work at all", disabled.Deleted == 0);
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }

        // --- alpha check: classification --------------------------------------------------------------

        Console.WriteLine();
        Console.WriteLine("Alpha check, classification");

        var config = new AppConfig();
        var intel = new DcsIntelService(config);

        Check("\"alpha check\" is an alpha check",
            intel.Classify("Overlord, Punch 1-1, alpha check bullseye") == IntelRequestKind.AlphaCheck);
        Check("so is \"position check\"", intel.Classify("Overlord, position check") == IntelRequestKind.AlphaCheck);
        Check("and \"say my position\"", intel.Classify("say my position") == IntelRequestKind.AlphaCheck);

        // The bug this guards against: an alpha check that fell through to the bogey dope classifier
        // and came back with an enemy position instead of the pilot's own.
        Check("a bogey dope is still a bogey dope", intel.Classify("bogey dope") == IntelRequestKind.BogeyDope);
        Check("an alpha check naming the bullseye is not a bullseye request",
            intel.Classify("alpha check bullseye") == IntelRequestKind.AlphaCheck);
        Check("nothing is not an alpha check", intel.Classify("good morning") == IntelRequestKind.None);

        intel.Classify("alpha check", out var alphaTrigger);
        Eq("the trigger that fired is reported for the log", alphaTrigger ?? "null", "alpha check");

        // --- alpha check: the reply --------------------------------------------------------------------

        Console.WriteLine();
        Console.WriteLine("Alpha check, the reply");

        // Batumi is at roughly 41.6 N, 41.6 E. A unit due north of the bullseye must read 360, and one
        // due east 090 - the direction is from the bullseye to the aircraft, not the other way round.
        static Unit At(double lat, double lon, double altMeters, string name = "Punch 1-1") =>
            new() { Name = name, Position = new Position { Lat = lat, Lon = lon, Alt = altMeters } };

        var bullseye = new Position { Lat = 41.6, Lon = 41.6, Alt = 0 };

        var plain = new AppConfig { DcsIntelMagneticBearings = false, DcsIntelSlowSpeech = false };

        var due_north = DcsIntelService.BuildAlphaCheck(At(42.6, 41.6, 6096), bullseye, 0, plain);
        Check("a unit north of the bullseye reads 360, not 180", due_north.Contains("bullseye 360"), due_north);

        var due_east = DcsIntelService.BuildAlphaCheck(At(41.6, 43.0, 6096), bullseye, 0, plain);
        Check("and one to the east reads 090", due_east.Contains("bullseye 090"), due_east);

        // 1 degree of latitude is 60 nm by definition, which makes this an independent check of the range.
        Check("the range is in nautical miles", due_north.Contains(" 60 miles"), due_north);
        Check("the altitude is in thousands of feet", due_north.Contains("20"), due_north);

        var spelled = DcsIntelService.BuildAlphaCheck(At(42.6, 41.6, 6096), bullseye, 0,
            new AppConfig { DcsIntelMagneticBearings = false, DcsIntelSlowSpeech = true });
        Check("slow speech spells the bearing out", spelled.Contains("three, six, zero"), spelled);
        Check("and it still says niner where it should",
            DcsIntelService.BuildAlphaCheck(At(41.6, 42.8, 6096), bullseye, 0,
                new AppConfig { DcsIntelMagneticBearings = false, DcsIntelSlowSpeech = true }).Contains("niner"),
            DcsIntelService.BuildAlphaCheck(At(41.6, 42.8, 6096), bullseye, 0,
                new AppConfig { DcsIntelMagneticBearings = false, DcsIntelSlowSpeech = true }));

        var magnetic = DcsIntelService.BuildAlphaCheck(At(42.6, 41.6, 6096), bullseye, 6,
            new AppConfig { DcsIntelMagneticBearings = true, DcsIntelSlowSpeech = false });
        Check("a magnetic bearing is the true one minus the declination",
            magnetic.Contains("bullseye 354"), magnetic);

        Check("declination is ignored when true bearings are configured",
            DcsIntelService.BuildAlphaCheck(At(42.6, 41.6, 6096), bullseye, 6, plain).Contains("bullseye 360"));

        // Due north is the case every bearing format gets wrong once: 000 is not a bearing anybody says.
        Eq("a bearing of zero is read as 360", DcsIntelService.BearingText(0), "360");
        Eq("and spelled out the same way", DcsIntelService.SpeakBearing(0), "three six zero");
        Eq("360 stays 360", DcsIntelService.BearingText(360), "360");
        Eq("everything else is unchanged", DcsIntelService.BearingText(90), "090");
        Eq("a wrapped bearing normalises first", DcsIntelService.BearingText(720), "360");
        Eq("and a negative one too", DcsIntelService.BearingText(-90), "270");

        Check("the reply says what it is, so a pilot knows the call was understood",
            due_north.StartsWith("Alpha check, bullseye "), due_north);

        // --- alpha check: the failure cases ------------------------------------------------------------

        Console.WriteLine();
        Console.WriteLine("Alpha check, when it cannot answer");

        Check("there is a reply for a caller who cannot be found",
            !string.IsNullOrWhiteSpace(config.DcsIntelNoPositionReply), config.DcsIntelNoPositionReply);

        var intelSource = System.IO.File.ReadAllText(Root + "Darkstar.Core/DcsIntelService.cs");

        var alphaAt = intelSource.IndexOf("kind == IntelRequestKind.AlphaCheck", StringComparison.Ordinal);
        var contactsAt = intelSource.IndexOf("GetHostileContactsAsync(channel", StringComparison.Ordinal);
        Check("the alpha check answers before the contact query, so it survives a broken sensor source",
            alphaAt > 0 && contactsAt > 0 && alphaAt < contactsAt, $"alpha at {alphaAt}, contacts at {contactsAt}");

        Check("a caller with no position gets the no-position reply, not a made-up one",
            intelSource.Contains("requester?.Position == null") &&
            intelSource.Contains("_config.DcsIntelNoPositionReply"));

        Check("no bullseye means the unavailable reply rather than a bearing from nowhere",
            intelSource.Contains("no bullseye for this coalition"));

        // --- radio check -------------------------------------------------------------------------------

        Console.WriteLine();
        Console.WriteLine("Radio check");

        Check("\"radio check\" matches", RadioCheck.Matches(config, "Overlord, Punch 1-1, radio check"));
        Check("\"comm check\" too", RadioCheck.Matches(config, "comm check"));
        Check("\"how do you read\" too", RadioCheck.Matches(config, "Overlord, how do you read me?"));
        Check("something else does not", !RadioCheck.Matches(config, "Overlord, bogey dope"));
        Check("an empty transmission does not", !RadioCheck.Matches(config, ""));
        Check("neither does null", !RadioCheck.Matches(config, null));

        Check("turning it off turns it off",
            !RadioCheck.Matches(new AppConfig { RadioCheckEnabled = false }, "radio check"));
        Check("and then no trigger is reported either",
            RadioCheck.MatchedTrigger(new AppConfig { RadioCheckEnabled = false }, "radio check") == null);

        Eq("the trigger is reported for the log",
            RadioCheck.MatchedTrigger(config, "Overlord, radio check")!, "radio check");

        // Word boundaries, the same rule the other triggers use: a word that merely contains a trigger
        // must not fire it.
        Check("a trigger is matched on word boundaries", !RadioCheck.Matches(config, "radiochecking"));

        Eq("no mission data means no claim about radar",
            RadioCheck.Reply(config, RadioCheck.ScopeState.Unknown), config.RadioCheckReply);
        Eq("on scope says so", RadioCheck.Reply(config, RadioCheck.ScopeState.Contact),
            config.RadioCheckReplyWithContact);
        Eq("not on scope says that instead", RadioCheck.Reply(config, RadioCheck.ScopeState.NoContact),
            config.RadioCheckReplyNoContact);

        Check("every one of the three replies is set by default",
            new[] { config.RadioCheckReply, config.RadioCheckReplyWithContact, config.RadioCheckReplyNoContact }
                .All(r => !string.IsNullOrWhiteSpace(r)));
        Check("all three confirm the radio works before qualifying anything",
            new[] { config.RadioCheckReply, config.RadioCheckReplyWithContact, config.RadioCheckReplyNoContact }
                .All(r => r.StartsWith("Loud and clear", StringComparison.OrdinalIgnoreCase)));
        Check("it is on by default - it costs nothing and needs no mission", config.RadioCheckEnabled);

        // --- radio check: the wiring --------------------------------------------------------------------

        Console.WriteLine();
        Console.WriteLine("Radio check, the wiring");

        var bot = System.IO.File.ReadAllText(Root + "BotService.cs");

        var radioAt = bot.IndexOf("RadioCheck.Matches(config, text)", StringComparison.Ordinal);
        var tacticalAt = bot.IndexOf("intel.Classify(text, out var matchedTrigger)", StringComparison.Ordinal);
        var airfieldAt = bot.IndexOf("airfields != null && session.AnswersAirfield", StringComparison.Ordinal);

        Check("a radio check is answered before the tactical classifier",
            radioAt > 0 && tacticalAt > 0 && radioAt < tacticalAt, $"radio at {radioAt}, tactical at {tacticalAt}");
        Check("and before the airfield one", radioAt > 0 && airfieldAt > 0 && radioAt < airfieldAt);
        Check("it is not gated on the radio's role - every radio answers it",
            !bot.Contains("session.AnswersTactical && RadioCheck") &&
            !bot.Contains("RadioCheck.Matches(config, text) && session.Answers"));

        var sayAgainAt = bot.IndexOf("nothing intelligible in the transmission", StringComparison.Ordinal);
        Check("but after the empty-transcript check, so silence is not answered with \"loud and clear\"",
            sayAgainAt > 0 && sayAgainAt < radioAt);

        Check("an entry in phrases.json still wins, so an upgrade does not change a customised reply",
            bot.Contains("PhraseBook.TryMatch(text, phrases) == null"));

        var phraseBook = System.IO.File.ReadAllText(Root + "Darkstar.Core/PhraseBook.cs");
        Check("and the default phrases no longer claim the trigger",
            !phraseBook.Contains("Trigger = \"radio check\""));

        Check("the scope lookup is skipped entirely without DCS-gRPC",
            bot.Contains("intel != null") && bot.Contains("RadioCheck.ScopeState.Unknown"));
        Check("the tactical branch only runs when the radio check did not answer",
            bot.Contains("intelReply == null && intel != null"));

        var scopeSource = intelSource.Substring(intelSource.IndexOf("LookUpScopeStateAsync", StringComparison.Ordinal));
        Check("a failed lookup answers Unknown, never NoContact - a working radio is not told otherwise",
            scopeSource.Contains("return RadioCheck.ScopeState.Unknown"));
        Check("the lookup does not query contacts, so a broken sensor source cannot delay it",
            !scopeSource[..scopeSource.IndexOf("// ---", StringComparison.Ordinal)].Contains("GetHostileContactsAsync"));

        // --- housekeeping wiring -------------------------------------------------------------------------

        Console.WriteLine();
        Console.WriteLine("Housekeeping, the wiring");

        Check("the bot prunes its own folders", bot.Contains("PruneOwnFiles"));
        Check("at startup", bot.Contains("PruneOwnFiles(config)"));
        Check("and again while it runs, so a long session does not fill the disk", bot.Contains("nextPrune"));
        Check("the live log is protected by keeping the newest few",
            bot.Contains("alwaysKeepNewest: 3") || bot.Contains(", 3, \"log"), "logs keep 3");

        var appConfigSource = System.IO.File.ReadAllText(Root + "Darkstar.Core/AppConfig.cs");
        Check("the limits are configurable rather than hard-coded",
            appConfigSource.Contains("RecordingRetentionDays") && appConfigSource.Contains("LogRetentionMaxMb"));

        var defaults = new AppConfig();
        Check("recordings are kept a week by default", defaults.RecordingRetentionDays == 7);
        Check("logs a month, since they are small and worth having", defaults.LogRetentionDays == 30);
        Check("and both have a size backstop for a busy server",
            defaults.RecordingRetentionMaxMb > 0 && defaults.LogRetentionMaxMb > 0);

        // --- vocabulary conflicts cover the new triggers too --------------------------------------------

        Console.WriteLine();
        Console.WriteLine("The new triggers join the vocabulary conflict check");

        var conflicts = AppConfig.FindVocabularyTriggerConflicts(defaults,
            new[] { "Punch", "alpha check", "radio check", "Batumi" });
        Check("an alpha check trigger in vocabulary.json is reported",
            conflicts.Any(c => c.Equals("alpha check", StringComparison.OrdinalIgnoreCase)));
        Check("a radio check trigger too",
            conflicts.Any(c => c.Equals("radio check", StringComparison.OrdinalIgnoreCase)));
        Check("a genuine proper noun is left alone",
            !conflicts.Any(c => c is "Punch" or "Batumi"), string.Join(",", conflicts));

        // --- friendly position ---------------------------------------------------------------------------

        Console.WriteLine();
        Console.WriteLine("\"Where is Springfield 2-1?\" - finding the target by position in the sentence");

        var friendlyOn = new AppConfig { DcsIntelFriendlyPositionEnabled = true };

        // The whole point: a request like this names TWO pilots, and PilotNames refuses to choose when more
        // than one is named. Taking only what follows the trigger has exactly one answer.
        var twoNames = FriendlyPosition.Parse(friendlyOn, "Overlord, Punch 1-1, where is Springfield 2-1");
        Check("a request naming caller and target is understood", twoNames != null);
        Eq("and only the target is extracted", twoNames!.TargetText, "Springfield 2-1");
        Eq("the trigger is reported for the log", twoNames.Trigger, "where is");

        // Proof that the old rule would indeed have refused this - the safeguard is still in place.
        var bothNamed = PilotNames.FindMatchInTranscript(
            new[] { new FakeNamedUnit("Punch 1-1"), new FakeNamedUnit("Springfield 2-1") },
            "Punch 1-1, where is Springfield 2-1", u => u.Name);
        Check("scanning the whole sentence still refuses to guess between two pilots", !bothNamed.Found,
            bothNamed.Explanation);
        Check("while scanning only the tail finds exactly one",
            PilotNames.FindMatchInTranscript(
                new[] { new FakeNamedUnit("Punch 1-1"), new FakeNamedUnit("Springfield 2-1") },
                twoNames.TargetText, u => u.Name).Found);

        Eq("\"where's\" works too",
            FriendlyPosition.Parse(friendlyOn, "Overlord where's Springfield 2-1")!.TargetText, "Springfield 2-1");
        Eq("so does \"position of\"",
            FriendlyPosition.Parse(friendlyOn, "Overlord, position of Colt 4-1")!.TargetText, "Colt 4-1");
        Eq("the longer trigger wins, so no leftover word lands in the name",
            FriendlyPosition.Parse(friendlyOn, "Overlord, say position of Colt 4-1")!.TargetText, "Colt 4-1");

        Check("it is off unless switched on - the phrases are generic enough to matter",
            FriendlyPosition.Parse(new AppConfig(), "where is Springfield 2-1") == null);
        Check("an unrelated transmission is not one", FriendlyPosition.Parse(friendlyOn, "Overlord, bogey dope") == null);

        Console.WriteLine();
        Console.WriteLine("Trimming the filler off the name");

        Eq("a question mark goes", FriendlyPosition.CleanTarget("Springfield 2-1?"), "Springfield 2-1");
        Eq("trailing filler goes", FriendlyPosition.CleanTarget("Springfield 2-1 right now"), "Springfield 2-1");
        Eq("the longest trailing match is used, not the shortest",
            FriendlyPosition.CleanTarget("Springfield 2-1 at the moment"), "Springfield 2-1");
        Eq("leading filler goes", FriendlyPosition.CleanTarget("my wingman Springfield 2-1"), "wingman Springfield 2-1");
        Eq("\"the\" too", FriendlyPosition.CleanTarget("the tanker"), "tanker");
        Eq("nothing but filler leaves nothing", FriendlyPosition.CleanTarget("right now"), "");
        Eq("an empty tail stays empty", FriendlyPosition.CleanTarget(""), "");
        Eq("and so does a null one", FriendlyPosition.CleanTarget(null), "");

        var noName = FriendlyPosition.Parse(friendlyOn, "Overlord, where is he?");
        Check("a request naming nobody is still recognised as the request", noName != null);
        Check("but it knows nobody was named - a pronoun is not a name",
            !noName!.NamesSomebody, $"\"{noName.TargetText}\"");
        Check("naming nobody is not the same as naming somebody unfindable",
            FriendlyPosition.Parse(friendlyOn, "Overlord, where is they")!.NamesSomebody == false);

        Console.WriteLine();
        Console.WriteLine("The coalition guard");

        // Everywhere else an unknown sender coalition falls back to the bot's own side, which is harmless
        // when the answer is about the enemy. Here it would let a spectator ask where the players are.
        Check("Red is a known coalition", FriendlyPosition.CoalitionIsKnown(1));
        Check("Blue too", FriendlyPosition.CoalitionIsKnown(2));
        Check("a spectator is not", !FriendlyPosition.CoalitionIsKnown(0));
        Check("nor is anything else", !FriendlyPosition.CoalitionIsKnown(3) && !FriendlyPosition.CoalitionIsKnown(-1));

        var intelSourceFriendly = System.IO.File.ReadAllText(Root + "Darkstar.Core/DcsIntelService.cs");
        var friendlyMethod = intelSourceFriendly[intelSourceFriendly.IndexOf("AnswerFriendlyPositionAsync(GrpcChannel", StringComparison.Ordinal)..];
        friendlyMethod = friendlyMethod[..friendlyMethod.IndexOf("/// <summary>", StringComparison.Ordinal)];

        var guardAt = friendlyMethod.IndexOf("CoalitionIsKnown", StringComparison.Ordinal);
        var lookupAt = friendlyMethod.IndexOf("GetPlayerUnitsAsync", StringComparison.Ordinal);
        Check("the coalition is checked before any player is looked up at all",
            guardAt > 0 && lookupAt > 0 && guardAt < lookupAt, $"guard at {guardAt}, lookup at {lookupAt}");
        Check("and the lookup is restricted to the caller's own side",
            friendlyMethod.Contains("Coalition = friendlyCoalition"));
        Check("only human players are considered - no AI units are pulled in",
            !friendlyMethod.Contains("GetGroupsAsync"));
        Check("the target is matched against the tail, not the whole transmission",
            friendlyMethod.Contains("FindMatchInTranscript(response.Units, request.TargetText"));

        Console.WriteLine();
        Console.WriteLine("The spoken answer");

        static Unit AtWithHeading(double lat, double lon, double altMeters, string callsign, double heading) =>
            new()
            {
                Name = callsign,
                Callsign = callsign,
                PlayerName = callsign,
                Position = new Position { Lat = lat, Lon = lon, Alt = altMeters },
                Orientation = new Orientation { Heading = heading }
            };

        var plainFriendly = new AppConfig
        {
            DcsIntelMagneticBearings = false,
            DcsIntelSlowSpeech = false,
            DcsIntelFriendlySayHeading = true
        };

        var caller = AtWithHeading(41.6, 41.6, 6096, "Punch 1-1", 0);
        var mate = AtWithHeading(42.6, 41.6, 6096, "Springfield 2-1", 90);

        var braa = DcsIntelService.BuildFriendlyPosition(mate, caller, null, 0, plainFriendly);
        // ForSpeech separates the digits rather than spelling them as words - "2 1" is what makes TTS
        // say "two one" instead of "twenty one".
        Check("the name comes first, with its digits separated for TTS", braa.StartsWith("Springfield 2 1,"), braa);
        Check("and the hyphen is gone, which TTS would otherwise read as a pause or \"twenty one\"",
            !braa.Contains("2-1"), braa);
        Check("it is a bearing when the caller's aircraft is known", braa.Contains("bearing 360"), braa);
        Check("1 degree of latitude is 60 nm, which checks the range independently", braa.Contains("60 miles"), braa);
        Check("the altitude is in thousands of feet", braa.Contains("20 thousand"), braa);
        Check("and the friendly's own heading is there, for the rejoin", braa.Contains("heading 090"), braa);

        // Aspect is an enemy concept and must never appear here.
        Check("no aspect is given about a friendly",
            !braa.Contains("hot") && !braa.Contains("cold") && !braa.Contains("flanking") && !braa.Contains("beaming"),
            braa);

        var bullseyeForm = DcsIntelService.BuildFriendlyPosition(mate, null,
            new Position { Lat = 41.6, Lon = 41.6, Alt = 0 }, 0, plainFriendly);
        Check("without the caller's aircraft it falls back to the bullseye, like a bogey dope does",
            bullseyeForm.Contains("bullseye 360"), bullseyeForm);

        var noHeading = DcsIntelService.BuildFriendlyPosition(mate, caller, null, 0,
            new AppConfig { DcsIntelMagneticBearings = false, DcsIntelSlowSpeech = false, DcsIntelFriendlySayHeading = false });
        Check("the heading can be turned off", !noHeading.Contains("heading"), noHeading);

        var spelledFriendly = DcsIntelService.BuildFriendlyPosition(mate, caller, null, 0,
            new AppConfig { DcsIntelMagneticBearings = false, DcsIntelSlowSpeech = true, DcsIntelFriendlySayHeading = true });
        Check("slow speech spells the bearing out", spelledFriendly.Contains("three, six, zero"), spelledFriendly);
        Check("and says niner in the heading",
            DcsIntelService.BuildFriendlyPosition(AtWithHeading(41.6, 42.8, 6096, "Colt 4-1", 90), caller, null, 0,
                new AppConfig { DcsIntelMagneticBearings = false, DcsIntelSlowSpeech = true, DcsIntelFriendlySayHeading = true })
                .Contains("niner"));

        Console.WriteLine();
        Console.WriteLine("Classification and the per-radio role");

        var intelFriendly = new DcsIntelService(friendlyOn);
        Check("\"where is\" classifies as a friendly position request",
            intelFriendly.Classify("Overlord, where is Springfield 2-1") == IntelRequestKind.FriendlyPosition);
        Check("and is checked before the contact requests",
            intelFriendly.Classify("Overlord, where is Springfield 2-1, and bogey dope") == IntelRequestKind.FriendlyPosition);
        Check("a bogey dope is still a bogey dope",
            intelFriendly.Classify("Overlord, bogey dope") == IntelRequestKind.BogeyDope);

        // "position check" (alpha check) and "position of" (friendly) must not collide.
        Check("\"position check\" is still an alpha check",
            intelFriendly.Classify("Overlord, position check") == IntelRequestKind.AlphaCheck);
        Check("and \"say my position\" too",
            intelFriendly.Classify("Overlord, say my position") == IntelRequestKind.AlphaCheck);

        // Switched off, the generic phrases must not be classified at all.
        var intelFriendlyOff = new DcsIntelService(new AppConfig());
        Check("while the feature is off, \"where is\" classifies as nothing",
            intelFriendlyOff.Classify("Overlord, where is Springfield 2-1") == IntelRequestKind.None);
        Check("and \"locate\" doesn't swallow anything either",
            intelFriendlyOff.Classify("Overlord, locate the tanker") == IntelRequestKind.None);

        Check("a radio starts with no opinion, so nothing changes for existing configurations",
            new RadioConfig().AnswerFriendlyPositionRequests == null);
        Check("the feature is off globally by default - this is the server owner's call",
            !new AppConfig().DcsIntelFriendlyPositionEnabled);
        Check("so a radio left alone does not answer it either",
            !RadioConfig.Answers(new RadioConfig().AnswerFriendlyPositionRequests, false));

        var roleDescription = new RadioConfig { AnswerFriendlyPositionRequests = true }
            .DescribeRole(tacticalGloballyOn: true, airfieldGloballyOn: false, friendlyGloballyOn: true);
        Check("the startup log names the role", roleDescription.Contains("friendly positions"), roleDescription);

        Console.WriteLine();
        Console.WriteLine("Handoff to the frequency that does serve it");

        var threeRadios = new[]
        {
            (FrequencyHz: 251_000_000.0, Callsign: "Overlord", Tactical: true,  Airfield: false, FriendlyPosition: false),
            (FrequencyHz: 133_000_000.0, Callsign: "Tower",    Tactical: false, Airfield: true,  FriendlyPosition: false),
            (FrequencyHz: 140_000_000.0, Callsign: "Base",     Tactical: false, Airfield: false, FriendlyPosition: true),
        };

        var friendlyHandoff = RadioRoles.FindHandoff(threeRadios, 251_000_000, RadioCapability.FriendlyPosition);
        Check("a position request on the AWACS frequency is handed to the one that serves it",
            friendlyHandoff?.Callsign == "Base", friendlyHandoff?.Callsign ?? "none");
        Check("the other two capabilities still route correctly",
            RadioRoles.FindHandoff(threeRadios, 140_000_000, RadioCapability.Tactical)?.Callsign == "Overlord" &&
            RadioRoles.FindHandoff(threeRadios, 140_000_000, RadioCapability.Airfield)?.Callsign == "Tower");
        Check("and a radio is never handed off to itself",
            RadioRoles.FindHandoff(threeRadios, 140_000_000, RadioCapability.FriendlyPosition) == null);

        var twoServe = threeRadios.Append((139_000_000.0, "Base Two", false, false, true)).ToArray();
        Check("with two radios serving it, none is named rather than one guessed at",
            RadioRoles.FindHandoff(twoServe, 251_000_000, RadioCapability.FriendlyPosition) == null);

        Console.WriteLine();
        Console.WriteLine("Friendly positions, the wiring");

        Check("the role decides per request kind, not per branch",
            bot.Contains("IntelRequestKind.FriendlyPosition => session.AnswersFriendlyPosition"));
        Check("the role is resolved once at startup",
            bot.Contains("RadioConfig.Answers(radioConfig.AnswerFriendlyPositionRequests, friendlyGloballyOn)"));
        Check("and it needs DCS-gRPC and the tactical replies underneath it",
            bot.Contains("friendlyGloballyOn = tacticalGloballyOn && config.DcsIntelFriendlyPositionEnabled"));
        Check("a request on a radio that doesn't serve it is handed off",
            bot.Contains("wanted = RadioCapability.FriendlyPosition"));

        var friendlyAt = intelSourceFriendly.IndexOf("kind == IntelRequestKind.FriendlyPosition", StringComparison.Ordinal);
        var hostileAt = intelSourceFriendly.IndexOf("GetHostileContactsAsync(channel", StringComparison.Ordinal);
        Check("it answers before the hostile contact query, so a broken sensor source cannot stop it",
            friendlyAt > 0 && hostileAt > 0 && friendlyAt < hostileAt, $"friendly at {friendlyAt}, hostile at {hostileAt}");

        Check("asking about yourself is answered as an alpha check rather than \"bearing 000, 0 miles\"",
            friendlyMethod.Contains("caller asked about themselves"));

        // --- wake word variants -------------------------------------------------------------------------

        Console.WriteLine();
        Console.WriteLine("Accepting more than one spelling of the wake word");

        static string Joined(IEnumerable<string> phrases) => string.Join("|", phrases);

        Eq("the wake word itself is always accepted",
            Joined(HotwordVariants.Resolve("Overlord", null)), "overlord");
        Eq("variants come after it, normalised",
            Joined(HotwordVariants.Resolve("Overlord", new[] { "  OVER  LORD ", "oberlord" })),
            "overlord|over lord|oberlord");
        Eq("the wake word is not repeated when it is also listed as a variant",
            Joined(HotwordVariants.Resolve("Overlord", new[] { "overlord", "oberlord" })), "overlord|oberlord");
        Eq("nor is a variant listed twice",
            Joined(HotwordVariants.Resolve("Overlord", new[] { "oberlord", "OberLord" })), "overlord|oberlord");

        // The inheritance rule, which is the part that could go wrong quietly: a tanker on "Texaco" must
        // not start answering to a variant configured for the AWACS.
        Eq("a radio on the global wake word inherits the global variants",
            Joined(HotwordVariants.Resolve("", null, "Overlord", new[] { "over lord" })), "overlord|over lord");
        Eq("a radio with its own wake word inherits nothing",
            Joined(HotwordVariants.Resolve("Texaco", null, "Overlord", new[] { "over lord" })), "texaco");
        Eq("but it can have its own variants",
            Joined(HotwordVariants.Resolve("Texaco", new[] { "texas co" }, "Overlord", new[] { "over lord" })),
            "texaco|texas co");
        Eq("its own variants replace the global ones rather than adding to them",
            Joined(HotwordVariants.Resolve("", new[] { "oberlord" }, "Overlord", new[] { "over lord" })),
            "overlord|oberlord");

        Console.WriteLine();
        Console.WriteLine("The matching regex");

        var regex = HotwordVariants.BuildRegex(HotwordVariants.Resolve("Overlord", new[] { "over lord", "oberlord" }));

        Check("the wake word matches", regex.IsMatch("overlord bogey dope"));
        Check("case does not matter", regex.IsMatch("OVERLORD, bogey dope"));
        Check("a variant matches - this is the whole point",
            regex.IsMatch("over lord bogey dope"));
        Check("and the other one", regex.IsMatch("oberlord request picture"));
        Check("extra whitespace inside a variant is tolerated", regex.IsMatch("over   lord picture"));

        // Still whole words: the reason the single-keyword version was anchored that way.
        Check("a longer word merely containing the wake word does not fire", !regex.IsMatch("overlordship"));
        Check("nor one containing a variant", !regex.IsMatch("myoberlordly"));
        Check("something unrelated does not fire", !regex.IsMatch("texaco bogey dope"));

        Check("which spelling fired is available for the log",
            regex.Match("say oberlord now").Value == "oberlord", regex.Match("say oberlord now").Value);

        // A detector with no wake word at all must match nothing - an empty alternation would match
        // everything, which would turn every transmission into a trigger.
        Check("an empty accepted list matches nothing, not everything",
            !HotwordVariants.BuildRegex(new List<string>()).IsMatch("anything at all"));
        Check("and neither does a list of blanks",
            !HotwordVariants.BuildRegex(new[] { "", "   " }).IsMatch("anything at all"));

        // Regex metacharacters in a wake word must be literal, or "Over.lord" would match "Overxlord".
        var escaped = HotwordVariants.BuildRegex(HotwordVariants.Resolve("Over.lord", null));
        Check("a dot in a wake word is a dot", escaped.IsMatch("over.lord"));
        Check("and not any character", !escaped.IsMatch("overxlord"));

        Console.WriteLine();
        Console.WriteLine("Proposing variants from what the model actually heard");

        // The case this whole feature exists for: a German speaker says "Overlord", the English model
        // splits it in two, and the whole-word match fails on the space.
        var split = HotwordVariants.Suggest("Overlord", new[] { "over lord bogey dope" });
        Check("a wake word split in two is proposed", split.Any(s => s.Phrase == "over lord"),
            Joined(split.Select(s => s.Phrase)));
        Check("and costs zero edits, since only the spacing differed",
            split.First(s => s.Phrase == "over lord").Distance == 0);

        var mangled = HotwordVariants.Suggest("Overlord", new[]
        {
            "oberlord bogey dope",
            "oberlord picture",
            "over lord threat check",
        });
        Eq("the most frequent candidate comes first", mangled[0].Phrase, "oberlord");
        Check("with its count", mangled[0].Count == 2, mangled[0].Count.ToString());

        Check("the wake word itself is never proposed",
            !HotwordVariants.Suggest("Overlord", new[] { "overlord bogey dope" }).Any(s => s.Phrase == "overlord"));
        Check("nor is a spelling already accepted",
            !HotwordVariants.Suggest("Overlord", new[] { "oberlord picture" }, null, new[] { "oberlord" })
                .Any(s => s.Phrase == "oberlord"));

        // The filter that keeps this from proposing every phrase anybody said.
        var unrelated = HotwordVariants.Suggest("Overlord", new[] { "request bogey dope immediately" });
        Check("a phrase that doesn't resemble the wake word is not proposed", unrelated.Count == 0,
            Joined(unrelated.Select(s => s.Phrase)));

        Check("a candidate is counted once per recording, not once per mention",
            HotwordVariants.Suggest("Overlord", new[] { "oberlord oberlord oberlord" })
                .First(s => s.Phrase == "oberlord").Count == 1);

        // The safety half, and the reason this is a measurement rather than a guess: a candidate that also
        // appears where nobody called the bot would buy hits at the price of false triggers.
        var risky = HotwordVariants.Suggest(
            keyword: "Overlord",
            missedTranscripts: new[] { "over lord bogey dope", "oberlord picture" },
            unwantedTranscripts: new[] { "and then he said over lord or something" });

        var overLord = risky.First(s => s.Phrase == "over lord");
        var oberLord = risky.First(s => s.Phrase == "oberlord");
        Check("a candidate heard when nobody called is flagged", overLord.AlsoWhenNobodyCalled);
        Check("and not recommended", !overLord.Recommended);
        Check("while a clean one is", oberLord.Recommended && !oberLord.AlsoWhenNobodyCalled);
        Check("the flagged ones sort last, so the safe ones are read first",
            risky.First().Recommended && !risky.Last().Recommended);

        Check("an empty wake word proposes nothing rather than everything",
            HotwordVariants.Suggest("", new[] { "anything" }).Count == 0);
        Check("no recordings means no proposals",
            HotwordVariants.Suggest("Overlord", Array.Empty<string>()).Count == 0);

        Console.WriteLine();
        Console.WriteLine("The pieces underneath");

        Eq("normalising collapses whitespace and case", HotwordVariants.Normalize("  OVER   Lord "), "over lord");
        Eq("compacting also drops the spaces", HotwordVariants.Compact(" Over  Lord "), "overlord");
        Check("which is what makes a split word cost nothing",
            HotwordVariants.EditDistance(HotwordVariants.Compact("over lord"), HotwordVariants.Compact("Overlord")) == 0);

        Check("edit distance counts a substitution as one", HotwordVariants.EditDistance("oberlord", "overlord") == 1);
        Check("an insertion too", HotwordVariants.EditDistance("overlords", "overlord") == 1);
        Check("and a deletion", HotwordVariants.EditDistance("overlrd", "overlord") == 1);
        Check("identical strings are zero apart", HotwordVariants.EditDistance("overlord", "overlord") == 0);
        Check("an empty string costs the other's length", HotwordVariants.EditDistance("", "overlord") == 8);

        var candidates = HotwordVariants.Candidates("over lord bogey dope");
        Check("single words are candidates", candidates.Contains("over") && candidates.Contains("lord"));
        Check("so are pairs", candidates.Contains("over lord") && candidates.Contains("bogey dope"));
        Check("and triples", candidates.Contains("over lord bogey"));
        Check("but nothing longer, or every transmission would become a candidate",
            candidates.All(c => c.Split(' ').Length <= HotwordVariants.MaxCandidateWords));
        Check("an empty transcript yields none", HotwordVariants.Candidates("").Count == 0);

        Console.WriteLine();
        Console.WriteLine("Wake word variants, the wiring");

        var cfgVariants = new AppConfig();
        Check("no variants are configured by default - the list is meant to be measured, not guessed",
            cfgVariants.VoskKeywordVariants.Count == 0);
        Check("and none per radio either, so nothing changes for existing configurations",
            new RadioConfig().KeywordVariants.Count == 0);

        Check("the bot resolves the variants with the inheritance rule, not by hand",
            bot.Contains("HotwordVariants.Resolve("));
        Check("and hands them to the detector",
            bot.Contains("acceptedPhrases.Skip(1)"));
        Check("the startup log names the accepted variants, since they are what makes a radio react to something else",
            bot.Contains("(also: {string.Join"));

        var runner = System.IO.File.ReadAllText(Root + "HotwordTestRunner.cs");
        Check("the test runner can propose variants", runner.Contains("--suggest-variants"));
        Check("it only looks at recordings that were supposed to trigger and didn't",
            runner.Contains("r.Expected == true && !r.Detected"));
        Check("and checks them against the ones where nobody called",
            runner.Contains("r.Expected == false"));
        Check("it prints a line ready to paste into config.json",
            runner.Contains("\"VoskKeywordVariants\\\""));
        Check("and it never fails the build over having found misses - that is the point of the run",
            runner.Contains("Always 0: this mode exists to look at the misses"));

        var detector = System.IO.File.ReadAllText(Root + "VoskHotwordDetector.cs");
        Check("the detector matches the whole accepted set", detector.Contains("HotwordVariants.BuildRegex"));
        Check("and remembers which spelling fired", detector.Contains("LastMatchedPhrase = match.Value"));

        // --- TTS voices -------------------------------------------------------------------------------

        Console.WriteLine();
        Console.WriteLine("Reading the voice list out of DCS-SR-ExternalAudio.exe");

        // Copied from the tool's own source (Program.cs, HandleParseErrorAsync), down to the two spaces
        // before "Gender:" - the point of this test is that the real format parses, not a tidied one.
        const string RealOutput = """
        Example:
         --text="I want this read out over this frequency - hello world! " --freqs=251.0 --modulations=AM --coalition=1

        Currently compatible voices on this system:

        Name: Microsoft David Desktop, Culture: en-US,  Gender: Male, Age: Adult, Desc: Microsoft David Desktop - English (United States)
        Name: Microsoft Zira Desktop, Culture: en-US,  Gender: Female, Age: Adult, Desc: Microsoft Zira Desktop - English (United States)
        Name: Microsoft Hazel Desktop, Culture: en-GB,  Gender: Female, Age: Adult, Desc: Microsoft Hazel Desktop - English (Great Britain)

        Example:
         --text="I want a specific voice " --freqs=251.0 --modulations=AM --coalition=1 --voice="Microsoft David Desktop"
        Example:
         --text="I want any female voice " --freqs=251.0 --modulations=AM --coalition=1 --gender=female
        """;

        var parsed = TtsVoices.Parse(RealOutput);
        Check("the three voices are found", parsed.Count == 3, $"{parsed.Count} found");
        Eq("the name is taken verbatim, ready for --voice", parsed[0].Name, "Microsoft David Desktop");
        Eq("the culture comes along", parsed[0].Culture, "en-US");
        Eq("so does the gender", parsed[0].Gender, "Male");
        Eq("the order is the tool's", parsed[2].Name, "Microsoft Hazel Desktop");
        Eq("and a voice reads well in a picker", parsed[2].Describe(), "Microsoft Hazel Desktop (en-GB, Female)");

        // The examples underneath the list contain a voice name in --voice="...". If those were picked up,
        // the list would gain a phantom entry every time.
        Check("the example lines are not mistaken for voices",
            !parsed.Any(v => v.Name.Contains("--") || v.Name.Contains("freqs")));

        // A real trap: several shipped SAPI voices have a comma inside their own name.
        var comma = TtsVoices.Parse(
            "Name: Microsoft Server Speech Text to Speech Voice (en-US, ZiraPro), Culture: en-US,  Gender: Female, Age: Adult, Desc: x");
        Check("a voice name containing a comma survives intact", comma.Count == 1, $"{comma.Count} found");
        Eq("all of it, not just up to the first comma", comma[0].Name,
            "Microsoft Server Speech Text to Speech Voice (en-US, ZiraPro)");

        var duplicates = TtsVoices.Parse(
            "Name: A, Culture: en-US, Gender: Male, Age: Adult, Desc: x\nName: A, Culture: en-US, Gender: Male, Age: Adult, Desc: x");
        Check("the same voice twice is listed once", duplicates.Count == 1);

        Check("a single space before Gender parses too - the double space is not depended on",
            TtsVoices.Parse("Name: X, Culture: en-GB, Gender: Female, Age: Adult, Desc: y").Count == 1);
        Check("a line without Age or Desc still parses",
            TtsVoices.Parse("Name: X, Culture: en-GB, Gender: Female").Count == 1);
        Check("no output means no voices, not a crash", TtsVoices.Parse(null).Count == 0);
        Check("output with no voice lines means no voices", TtsVoices.Parse("nothing to see here").Count == 0);
        Check("a nameless line is skipped", TtsVoices.Parse("Name: , Culture: en-GB, Gender: Female").Count == 0);

        // Windows-style line endings: the tool runs on Windows, so this is the normal case, not the edge one.
        Check("CRLF output parses",
            TtsVoices.Parse("Name: A, Culture: en-US,  Gender: Male, Age: Adult, Desc: x\r\nName: B, Culture: en-US,  Gender: Female, Age: Adult, Desc: y").Count == 2);

        Console.WriteLine();
        Console.WriteLine("Asking for the voice list when it cannot be asked");

        // No Windows and no SRS here, so what is checked is that every failure comes back as a message
        // rather than an exception - these are called from a GUI button.
        var noPath = await TtsVoices.ListAsync("");
        Check("an empty path is reported, not thrown", !noPath.Ok && noPath.Error!.Contains("configured"), noPath.Error ?? "");
        Check("and no voices are claimed", noPath.Voices.Count == 0);

        var noExe = await TtsVoices.ListAsync("/definitely/not/here/DCS-SR-ExternalAudio.exe");
        Check("a missing executable is reported with its path",
            !noExe.Ok && noExe.Error!.Contains("not found"), noExe.Error ?? "");
        Check("the message says where to fix it", noExe.Error!.Contains("CH1"), noExe.Error!);

        // --- the voice belongs to the radio -------------------------------------------------------------

        Console.WriteLine();
        Console.WriteLine("A voice per radio");

        Check("a radio starts with no voice of its own, so nothing changes for existing configurations",
            new RadioConfig().Voice == "");

        var sender = System.IO.File.ReadAllText(Root + "ExternalAudioSender.cs");
        Check("the voice is passed per transmission, not fixed when the sender is built",
            sender.Contains("string? voice = null"));
        Check("an empty per-call voice falls back to the global one",
            sender.Contains("string.IsNullOrWhiteSpace(voice) ? _voiceName : voice.Trim()"));
        Check("and an empty global voice means no --voice flag at all, rather than an empty one",
            sender.Contains("string.IsNullOrWhiteSpace(effectiveVoice) ? \"\" :"));

        Check("the radio's voice is resolved once at startup, like its wake word and callsign",
            bot.Contains("string.IsNullOrWhiteSpace(radioConfig.Voice) ? config.VoiceName : radioConfig.Voice.Trim()"));
        Check("and travels with the radio that answers",
            bot.Contains("session.Modulation, session.Voice"));
        Check("the startup log names it, because a wrong voice name is otherwise silent",
            bot.Contains("voice \\\"{effectiveVoice}\\\""));

        // --- rate limiting -----------------------------------------------------------------------

        Section("Stopping one pilot from using the whole quota");

        var start = new DateTime(2026, 9, 27, 12, 0, 0);
        var limiter = new RateLimiter(maxRequests: 3, windowSeconds: 60);

        Check("the limiter is on when a limit is configured", limiter.Enabled);
        Check("the first request is allowed",
            limiter.Check("Punch 1-1", start) == RateLimiter.Verdict.Allow);
        Check("and so are the next two, up to the limit",
            limiter.Check("Punch 1-1", start.AddSeconds(1)) == RateLimiter.Verdict.Allow &&
            limiter.Check("Punch 1-1", start.AddSeconds(2)) == RateLimiter.Verdict.Allow);

        Check("the fourth inside the window is refused, and told so",
            limiter.Check("Punch 1-1", start.AddSeconds(3)) == RateLimiter.Verdict.RejectAndSay);
        Check("the fifth is refused silently - repeating it would occupy the frequency",
            limiter.Check("Punch 1-1", start.AddSeconds(4)) == RateLimiter.Verdict.RejectSilently);
        Check("and so is every one after that",
            limiter.Check("Punch 1-1", start.AddSeconds(5)) == RateLimiter.Verdict.RejectSilently);

        // The failure that would turn a limit into a ban: if refused requests were counted, the
        // window would move forward every time the pilot tried again and they could never return.
        Check("a refused request does not extend the window",
            limiter.Check("Punch 1-1", start.AddSeconds(61)) == RateLimiter.Verdict.Allow,
            "back in after the first request aged out");

        Check("and being let back in re-arms the spoken warning",
            limiter.Check("Punch 1-1", start.AddSeconds(62)) == RateLimiter.Verdict.Allow &&
            limiter.Check("Punch 1-1", start.AddSeconds(63)) == RateLimiter.Verdict.Allow &&
            limiter.Check("Punch 1-1", start.AddSeconds(64)) == RateLimiter.Verdict.RejectAndSay);

        Section("The limit is per pilot, not per server");

        var perPilot = new RateLimiter(maxRequests: 1, windowSeconds: 60);
        Check("one pilot's burst does not affect another",
            perPilot.Check("Punch 1-1", start) == RateLimiter.Verdict.Allow &&
            perPilot.Check("Punch 1-1", start) == RateLimiter.Verdict.RejectAndSay &&
            perPilot.Check("Springfield 2-1", start) == RateLimiter.Verdict.Allow);
        Check("the name is matched case-insensitively, since SRS names are typed by hand",
            perPilot.Check("PUNCH 1-1", start) == RateLimiter.Verdict.RejectSilently);
        Check("both pilots are tracked", perPilot.TrackedPilots == 2, perPilot.TrackedPilots.ToString());

        // An empty name would lump every unidentified transmission into one bucket and starve them all.
        var unnamed = new RateLimiter(maxRequests: 1, windowSeconds: 60);
        Check("a transmission with no sender name is never limited",
            unnamed.Check("", start) == RateLimiter.Verdict.Allow &&
            unnamed.Check("", start) == RateLimiter.Verdict.Allow &&
            unnamed.Check(null, start) == RateLimiter.Verdict.Allow);

        Section("Turning it off, and the wait it reports");

        var off = new RateLimiter(maxRequests: 0, windowSeconds: 60);
        Check("zero requests means no limit rather than no requests", !off.Enabled);
        Check("so everything is allowed",
            Enumerable.Range(0, 50).All(i => off.Check("Punch 1-1", start) == RateLimiter.Verdict.Allow));
        Check("and it reports no wait", off.RetryAfter("Punch 1-1", start) == TimeSpan.Zero);

        var waiting = new RateLimiter(maxRequests: 2, windowSeconds: 60);
        waiting.Check("Punch 1-1", start);
        waiting.Check("Punch 1-1", start.AddSeconds(10));

        var wait = waiting.RetryAfter("Punch 1-1", start.AddSeconds(20));
        Check("the wait counts from the oldest counted request, not the newest",
            Math.Abs(wait.TotalSeconds - 40) < 0.001, $"{wait.TotalSeconds:0.#}s");
        Check("a pilot under the limit is told to wait no time at all",
            waiting.RetryAfter("Springfield 2-1", start) == TimeSpan.Zero);
        Check("nor is an unknown pilot",
            waiting.RetryAfter("Nobody At All", start) == TimeSpan.Zero);

        Section("What the pilot hears");

        Eq("the name and the wait are filled in",
            RateLimiter.BuildReply("{pilot}, standby {seconds} seconds.", "Punch 1-1", TimeSpan.FromSeconds(12)),
            "Punch 1 1, standby 12 seconds.");
        Eq("the wait is rounded up - being refused again at 4.6 seconds reads as broken",
            RateLimiter.BuildReply("{seconds}", "Punch 1-1", TimeSpan.FromSeconds(4.2)), "5");
        Eq("and never says zero",
            RateLimiter.BuildReply("{seconds}", "Punch 1-1", TimeSpan.Zero), "1");
        Eq("an empty template says nothing at all",
            RateLimiter.BuildReply("", "Punch 1-1", TimeSpan.FromSeconds(5)), "");

        Section("Forgetting pilots who stopped transmitting");

        var pruning = new RateLimiter(maxRequests: 5, windowSeconds: 60);
        pruning.Check("Punch 1-1", start);
        pruning.Check("Springfield 2-1", start.AddSeconds(50));
        Check("both are tracked to begin with", pruning.TrackedPilots == 2);

        Check("nobody is forgotten while their requests are still inside the window",
            pruning.Prune(start.AddSeconds(30)) == 0 && pruning.TrackedPilots == 2);
        Check("the one whose requests aged out is forgotten",
            pruning.Prune(start.AddSeconds(70)) == 1 && pruning.TrackedPilots == 1,
            $"{pruning.TrackedPilots} left");
        Check("and eventually all of them, so a long session doesn't grow forever",
            pruning.Prune(start.AddSeconds(200)) == 1 && pruning.TrackedPilots == 0);

        Section("Rate limiting, the wiring");

        var defaultConfig = new AppConfig();
        Check("a limit is on by default - an unprotected quota is the worse default",
            defaultConfig.RateLimitMaxRequests > 0, defaultConfig.RateLimitMaxRequests.ToString());
        Check("generous enough for real radio work",
            defaultConfig.RateLimitMaxRequests >= 5 && defaultConfig.RateLimitWindowSeconds >= 60,
            $"{defaultConfig.RateLimitMaxRequests} per {defaultConfig.RateLimitWindowSeconds:0}s");
        Check("and it says something rather than going quiet",
            defaultConfig.RateLimitReply.Contains("{pilot}"), defaultConfig.RateLimitReply);

        var limitAt = bot.IndexOf("rateLimiter.Check(senderRawName", StringComparison.Ordinal);
        var geminiAt = bot.IndexOf("gemini.TranscribeAndReplyAsync", StringComparison.Ordinal);
        Check("the limit is checked before the Gemini call, which is what it protects",
            limitAt > 0 && geminiAt > 0 && limitAt < geminiAt, $"limit at {limitAt}, Gemini at {geminiAt}");

        var coalitionAt = bot.IndexOf("[Coalition Check] {freqLabel}", StringComparison.Ordinal);
        Check("but after the coalition check, which refuses more cheaply still",
            coalitionAt > 0 && coalitionAt < limitAt);

        Check("a limited transmission cancels the pending standby, rather than promising an answer",
            bot.Contains("if (verdict != RateLimiter.Verdict.Allow)") &&
            bot[bot.IndexOf("if (verdict != RateLimiter.Verdict.Allow)", StringComparison.Ordinal)..]
               .StartsWith("if (verdict != RateLimiter.Verdict.Allow)\n            {\n                CancelPendingAck",
                   StringComparison.Ordinal));
        Check("a silently dropped transmission is still logged",
            bot.Contains("transmission ignored, already advised"));
        Check("pilots are forgotten from the watchdog loop, not per request",
            bot.Contains("rateLimiter.Prune(DateTime.UtcNow)"));
        Check("the startup log says whether the limit is on",
            bot.Contains("Rate limit active:") && bot.Contains("Rate limit disabled"));

        // --- what ships in the installer --------------------------------------------------------------

        Console.WriteLine();
        Console.WriteLine("Only what the bot needs goes into the installer");

        // Setup.iss packs the publish folders wholesale, so these two properties are the only thing
        // standing between a NuGet package's extras and somebody's disk. They are easy to lose in a
        // merge and nothing else would notice.
        foreach (var project in new[] { "Darkstar.csproj", "Darkstar.Gui/Darkstar.Gui.csproj" })
        {
            var text = System.IO.File.ReadAllText(Root + project);
            Check($"{project} ships one language only",
                text.Contains("<SatelliteResourceLanguages>en</SatelliteResourceLanguages>"));
            Check($"{project} leaves the XML API docs behind but keeps the symbols",
                text.Contains("<AllowedReferenceRelatedFileExtensions>.pdb</AllowedReferenceRelatedFileExtensions>"));
        }

        var iss = System.IO.File.ReadAllText(Root + "installer/Setup.iss");
        Check("the installer packs the publish output, not the build output",
            iss.Contains("Source: \"publish\\bot\\*\"") && !iss.Contains("bin\\Release"));
        Check("and nothing else beyond the model and the three dependency installers",
            iss.Split('\n').Count(l => l.TrimStart().StartsWith("Source:", StringComparison.Ordinal)) == 6,
            iss.Split('\n').Count(l => l.TrimStart().StartsWith("Source:", StringComparison.Ordinal)) + " Source lines");

        var buildScript = System.IO.File.ReadAllText(Root + "build-installer.ps1");
        Check("publishing is pinned to win-x64, so no foreign native libraries travel along",
            buildScript.Contains("-r win-x64 --self-contained false"));
        Check("the publish target is wiped first, so an older build cannot survive into the installer",
            buildScript.Contains("Remove-Item -LiteralPath $outputPath -Recurse -Force"));
        Check("the build says what it is about to pack", buildScript.Contains("Show-PublishInventory"));
        Check("and -Clean really cleans", buildScript.Contains("\"obj\", \"bin\""));

        Console.WriteLine();
        Console.WriteLine("A missing payload cannot end the build in the compiler");

        // What this is about: Inno Setup does not warn about a Source: line matching nothing, it
        // stops with an error. A Vosk download that failed four steps earlier - a warning that
        // scrolls past - therefore ended the whole build with "No files found matching
        // ...\vosk-model\*" and no installer at all. Every wildcard Setup.iss packs has to be
        // checked before the compiler is called, and the model's absence has to turn into the slim
        // build rather than into a failure.
        var measureAt = buildScript.IndexOf("Measure-PayloadFolder $voskModelDir", StringComparison.Ordinal);
        var compileAt = buildScript.IndexOf("& $iscc @isccArgs", StringComparison.Ordinal);

        Check("the payload is checked before the compiler is called",
            measureAt > 0 && compileAt > 0 && measureAt < compileAt,
            $"check at {measureAt}, compile at {compileAt}");
        Check("both publish folders are checked, not just the model",
            buildScript.Contains("Measure-PayloadFolder $payload.Path", StringComparison.Ordinal) &&
            buildScript.Contains("$publishBot; What", StringComparison.Ordinal) &&
            buildScript.Contains("$publishGui; What", StringComparison.Ordinal));
        Check("a half-extracted model counts as no model",
            buildScript.Contains("Test-VoskModelFolder $voskModelDir", StringComparison.Ordinal));
        Check("the slim fallback is what decides the compiler flag, not what the caller asked for",
            buildScript.Contains("if ($buildSlim)   { $isccArgs += \"/DNoVoskModel\" }", StringComparison.Ordinal) &&
            !buildScript.Contains("if ($SkipVoskModel) { $isccArgs +=", StringComparison.Ordinal));
        Check("and the file name follows the variant that was actually built",
            buildScript.Contains("if ($buildSlim -ne [bool]$SkipVoskModel)", StringComparison.Ordinal));
        Check("an empty publish folder is its own message rather than a compiler error",
            buildScript.Contains("the publish step produced no files", StringComparison.Ordinal));

        // The other half of the same rule, on the Setup.iss side: every Source: glob must either be
        // removed by a #define or be allowed to be missing. A new unguarded one would reintroduce
        // exactly the failure above.
        var unguarded = new List<string>();
        var insideModelGuard = false;

        foreach (var line in iss.Split('\n').Select(l => l.TrimEnd('\r')))
        {
            if (line.StartsWith("#ifndef NoVoskModel", StringComparison.Ordinal)) insideModelGuard = true;
            else if (line.StartsWith("#endif", StringComparison.Ordinal)) insideModelGuard = false;
            else if (line.StartsWith("Source:", StringComparison.Ordinal) &&
                     !line.Contains("skipifsourcedoesntexist", StringComparison.Ordinal) &&
                     !insideModelGuard &&
                     !line.Contains("publish\\", StringComparison.Ordinal))
                unguarded.Add(line.Split(';')[0]);
        }

        Check("every packed file either always exists, can be left out, or may be missing",
            unguarded.Count == 0, string.Join(" | ", unguarded));

        Console.WriteLine();
        Console.WriteLine("Only what is needed to run is installed");

        Check("debug symbols and XML docs are excluded by default",
            iss.Contains("#define PayloadExcludes \"*.pdb,*.xml\"", StringComparison.Ordinal));
        Check("both published folders use that exclusion",
            iss.Split('\n').Count(l => l.StartsWith("Source: \"publish\\", StringComparison.Ordinal) &&
                                       l.Contains("Excludes: \"{#PayloadExcludes}\"", StringComparison.Ordinal)) == 2);
        Check("keeping them is one switch away, for a stack trace worth more than the megabytes",
            iss.Contains("#ifdef WithSymbols", StringComparison.Ordinal) &&
            buildScript.Contains("if ($WithSymbols) { $isccArgs += \"/DWithSymbols\" }", StringComparison.Ordinal) &&
            buildScript.Contains("[switch]$WithSymbols", StringComparison.Ordinal));

        Console.WriteLine();
        Console.WriteLine("Installing over an existing installation finds it first");

        Check("the destination page is always shown", iss.Contains("DisableDirPage=no", StringComparison.Ordinal));
        Check("prefilled by code rather than by a fixed path",
            iss.Contains("DefaultDirName={code:DefaultInstallDir}", StringComparison.Ordinal));
        Check("Inno Setup's own previous-directory memory stays on as well",
            iss.Contains("UsePreviousAppDir=yes", StringComparison.Ordinal));

        // Two sources, because they fail in different situations: the uninstall entry is gone if
        // somebody deleted it, and the service's ImagePath is all that is left on a server where
        // only the service was ever set up.
        Check("an existing installation is looked for in the uninstall entry",
            iss.Contains("RegQueryStringValue(HKLM64, UninstallRegKey(), 'InstallLocation'", StringComparison.Ordinal));
        Check("and in the Windows Service's registered path",
            iss.Contains("'ImagePath', ImagePath", StringComparison.Ordinal) &&
            iss.Contains("RemoveQuotes(Trim(ImagePath))", StringComparison.Ordinal));
        Check("a path that no longer exists is not offered as the default",
            iss.Contains("if not DirExists(Result) then", StringComparison.Ordinal));
        Check("and the page says that this is an update rather than letting it look like a fresh install",
            iss.Contains("CurPageID = wpSelectDir", StringComparison.Ordinal) &&
            iss.Contains("already installed in the folder below", StringComparison.Ordinal));

        Console.WriteLine();
        Console.WriteLine("Inno Setup is found where it is really installed");

        Check("the registry is consulted first - winget can install it per user",
            buildScript.Contains(@"Uninstall\Inno Setup 6_is1", StringComparison.Ordinal) &&
            buildScript.Contains("HKCU:", StringComparison.Ordinal));
        Check("the per-user program folder is searched too",
            buildScript.Contains(@"$env:LOCALAPPDATA\Programs", StringComparison.Ordinal));
        Check("and the folder match is wildcarded, so a future version is still found",
            buildScript.Contains("-Filter \"Inno Setup*\"", StringComparison.Ordinal));

        // --- one tower per airfield -----------------------------------------------------------------

        Section("Planning one tower per airfield");

        static Airfield Field(string name, string display = "", double lat = 41.6, double lon = 41.6) =>
            new() { Name = name, DisplayName = display, Lat = lat, Lon = lon, ElevationMeters = 10 };

        var caucasus = new[]
        {
            Field("Batumi"),
            Field("Kobuleti"),
            Field("Senaki-Kolkhi"),
            Field("Kutaisi"),
        };

        var planConfig = new AppConfig { TowerPlanBaseMHz = 133.000, TowerPlanStepMHz = 0.5 };
        var plan = TowerPlan.Plan(caucasus, planConfig);

        Check("one tower per airfield", plan.Towers.Count == 4, $"{plan.Towers.Count} planned");
        Eq("the first gets the base frequency", (plan.Towers[0].FrequencyHz / 1_000_000).ToString("0.000"), "133.000");
        Eq("and they step upwards", (plan.Towers[1].FrequencyHz / 1_000_000).ToString("0.000"), "133.500");

        // Pilots write these numbers down. Running the planner again must not move them.
        var again = TowerPlan.Plan(caucasus.Reverse().ToArray(), planConfig);
        Check("the plan is stable regardless of the order the mission reports airfields in",
            plan.Towers.Select(t => $"{t.AirfieldName}:{t.FrequencyHz}").SequenceEqual(
                again.Towers.Select(t => $"{t.AirfieldName}:{t.FrequencyHz}")));

        Eq("the callsign names the airfield", plan.Towers[0].Callsign, "Batumi Tower");
        Eq("a hyphenated name is shortened to what a controller would say",
            TowerPlan.CallsignFor(Field("Senaki-Kolkhi"), "Tower"), "Senaki Tower");
        Eq("the prettier name wins when DCS supplies one",
            TowerPlan.CallsignFor(Field("Nalchik", "Nalchik Airport"), "Tower"), "Nalchik Airport Tower");
        Eq("an empty suffix leaves the bare name", TowerPlan.CallsignFor(Field("Batumi"), ""), "Batumi");

        // Two radios on one frequency means the second silently replaces the first, because sessions
        // are keyed by frequency. A hand-configured AWACS must cost one slot, not break the plan.
        var withAwacs = TowerPlan.Plan(caucasus, planConfig, new[] { 133_500_000.0 });
        Check("a frequency already in use is skipped, not reused",
            withAwacs.Towers.All(t => Math.Abs(t.FrequencyHz - 133_500_000) > 1),
            string.Join(", ", withAwacs.Towers.Select(t => (t.FrequencyHz / 1_000_000).ToString("0.000"))));
        Check("and every airfield still gets one", withAwacs.Towers.Count == 4);

        Check("duplicate airfield names collapse to one tower",
            TowerPlan.Plan(new[] { Field("Batumi"), Field("Batumi") }, planConfig).Towers.Count == 1);
        Check("an airfield with no name is ignored",
            TowerPlan.Plan(new[] { Field(""), Field("Batumi") }, planConfig).Towers.Count == 1);

        var noFields = TowerPlan.Plan(Array.Empty<Airfield>(), planConfig);
        Check("no airfields is explained rather than silently empty",
            !noFields.Any && noFields.Notes.Any(n => n.Contains("no airfields", StringComparison.OrdinalIgnoreCase)));

        var many = TowerPlan.Plan(
            Enumerable.Range(0, 40).Select(i => Field($"Field{i:00}")).ToArray(), planConfig);
        Check("an absurd airfield count is capped rather than registering 40 radios",
            many.Towers.Count == TowerPlan.MaxTowers, $"{many.Towers.Count} towers");
        Check("and the cap is explained", many.Notes.Any(n => n.Contains("Stopped at", StringComparison.Ordinal)));

        Section("What the generated radios answer");

        var radios = TowerPlan.ToRadios(plan);
        Check("every tower answers airfield requests", radios.All(r => r.AnswerAirfieldRequests == true));
        Check("and none of them answers tactical ones - a dozen radios querying contacts buys nothing",
            radios.All(r => r.AnswerTacticalRequests == false));
        Check("nor friendly positions", radios.All(r => r.AnswerFriendlyPositionRequests == false));

        // The decisive one: the wake word must NOT become the airfield name, or the pronunciation
        // problem the position lookup exists to avoid lands back on the critical path.
        Check("the wake word is left to the global one, never the airfield name",
            radios.All(r => r.Keyword == ""),
            "an unpronounceable wake word means the bot never answers at all");

        Section("Taking the generated towers back out");

        // Generating is additive, so changing map means removing the old towers first. What must never
        // happen is a hand-built radio disappearing with them.
        var mixed = new List<RadioConfig>
        {
            new() { FrequencyHz = 251_000_000, Callsign = "Overlord",
                    AnswerTacticalRequests = true, AnswerAirfieldRequests = false },
            new() { FrequencyHz = 127_500_000, Callsign = "Texaco", Keyword = "Texaco" },
            new() { FrequencyHz = 133_000_000, Callsign = "Batumi Tower",
                    AnswerAirfieldRequests = true, AnswerTacticalRequests = false,
                    AnswerFriendlyPositionRequests = false },
            new() { FrequencyHz = 133_500_000, Callsign = "Kobuleti Tower",
                    AnswerAirfieldRequests = true, AnswerTacticalRequests = false,
                    AnswerFriendlyPositionRequests = false },
        };

        var found = TowerPlan.GeneratedRadios(mixed, planConfig);
        Check("the generated towers are found", found.Generated.Count == 2,
            string.Join(", ", found.Generated.Select(r => r.Callsign)));
        Check("the AWACS is left alone", found.Generated.All(r => r.Callsign != "Overlord"));
        Check("and so is the tanker", found.Generated.All(r => r.Callsign != "Texaco"));

        // Each part of the fingerprint on its own must not be enough.
        Check("a tower that also answers tactical requests is not treated as generated",
            !TowerPlan.LooksGenerated(new RadioConfig
            {
                Callsign = "Batumi Tower", AnswerAirfieldRequests = true,
                AnswerTacticalRequests = true, AnswerFriendlyPositionRequests = false
            }, planConfig),
            "somebody configured that deliberately");

        Check("nor one with its own wake word",
            !TowerPlan.LooksGenerated(new RadioConfig
            {
                Callsign = "Batumi Tower", Keyword = "Batumi", AnswerAirfieldRequests = true,
                AnswerTacticalRequests = false, AnswerFriendlyPositionRequests = false
            }, planConfig));

        Check("nor one whose callsign does not end in the suffix",
            !TowerPlan.LooksGenerated(new RadioConfig
            {
                Callsign = "Batumi Approach", AnswerAirfieldRequests = true,
                AnswerTacticalRequests = false, AnswerFriendlyPositionRequests = false
            }, planConfig));

        Check("nor one left on the global defaults, which answers everything",
            !TowerPlan.LooksGenerated(new RadioConfig { Callsign = "Batumi Tower" }, planConfig));

        // The dangerous case: with no suffix there is no fingerprint left, and guessing would mean
        // deleting somebody's configuration on a hunch.
        var noSuffix = TowerPlan.GeneratedRadios(mixed, new AppConfig { TowerPlanCallsignSuffix = "" });
        Check("an empty suffix finds nothing rather than guessing", noSuffix.Generated.Count == 0);
        Check("and says why", noSuffix.Notes.Any(n => n.Contains("cannot be told apart", StringComparison.Ordinal)),
            string.Join(" ", noSuffix.Notes));

        Check("the suffix match ignores case, since it is typed by hand",
            TowerPlan.LooksGenerated(new RadioConfig
            {
                Callsign = "Batumi TOWER", AnswerAirfieldRequests = true,
                AnswerTacticalRequests = false, AnswerFriendlyPositionRequests = false
            }, planConfig));

        // What the whole feature is for: generate, remove, generate again for another map.
        var roundTrip = new List<RadioConfig> { mixed[0], mixed[1] };
        roundTrip.AddRange(TowerPlan.ToRadios(TowerPlan.Plan(caucasus, planConfig)));
        var removable = TowerPlan.GeneratedRadios(roundTrip, planConfig).Generated;
        Check("everything the planner produced is recognised again afterwards",
            removable.Count == 4, $"{removable.Count} of 4");
        foreach (var r in removable) roundTrip.Remove(r);
        Check("and removing them leaves exactly the hand-built radios",
            roundTrip.Count == 2 && roundTrip.All(r => r.Callsign is "Overlord" or "Texaco"),
            string.Join(", ", roundTrip.Select(r => r.Callsign)));

        var panel = ReadSource("Darkstar.Gui/Pages/RadiosPanel.razor");
        Check("the panel offers the removal next to the generation",
            panel.Contains("RemoveGeneratedTowers", StringComparison.Ordinal));
        Check("and says nothing is saved until the operator says so",
            panel.Contains("Not saved yet", StringComparison.Ordinal));

        Section("Announcing the frequencies in game");

        var announceConfig = new AppConfig { BotCallsign = "Overlord", VoskKeyword = "Overlord" };
        var others = new[] { (251_000_000.0, "Overlord"), (127_500_000.0, "Texaco") };

        var marker = FrequencyAnnouncer.MarkerText("Batumi", 133_000_000, others, announceConfig);
        Check("the marker names the airfield", marker.Contains("Batumi", StringComparison.Ordinal), marker.Split('\n')[0]);
        Check("it gives the tower frequency in MHz", marker.Contains("133.000 MHz", StringComparison.Ordinal));
        Check("and the radios that are not tied to an airfield", marker.Contains("Texaco: 127.500 MHz", StringComparison.Ordinal));
        Check("the wake word is on the marker - a frequency alone is useless without it",
            marker.Contains("\"Overlord\"", StringComparison.Ordinal));

        var noTower = FrequencyAnnouncer.MarkerText("Batumi", null, others, announceConfig);
        Check("an airfield with no tower gets a marker without an invented frequency",
            !noTower.Contains("Tower / ATIS", StringComparison.Ordinal), noTower.Replace("\n", " | "));

        var startup = FrequencyAnnouncer.StartupMessage(
            new[] { (251_000_000.0, "Overlord", false), (133_000_000.0, "Batumi Tower", true),
                    (133_500_000.0, "Kobuleti Tower", true) },
            airfieldMarkers: 2, config: announceConfig);
        Check("the on-screen message names the non-airfield radios in full",
            startup.Contains("Overlord: 251.000 MHz", StringComparison.Ordinal), startup.Replace("\n", " | "));
        Check("but summarises the towers rather than scrolling a dozen off the screen",
            startup.Contains("2 tower frequencies", StringComparison.Ordinal));
        Check("and points at the markers, which hold the detail",
            startup.Contains("F10", StringComparison.Ordinal));

        Section("Markers replace rather than stack");

        // A server that restarts the bot three times an evening must not end up with three
        // overlapping markers per airfield.
        Check("marker ids are deterministic, not allocated",
            FrequencyAnnouncer.MarkerIdFor(0) == FrequencyAnnouncer.MarkerIdBase &&
            FrequencyAnnouncer.MarkerIdFor(5) == FrequencyAnnouncer.MarkerIdBase + 5);
        Check("the id range is far from what a mission would number its own marks",
            FrequencyAnnouncer.MarkerIdBase > 100_000, FrequencyAnnouncer.MarkerIdBase.ToString());

        var announcer = ReadSource("Darkstar.Core/FrequencyAnnouncer.cs");
        Check("the whole id range is cleared first, so a smaller mission leaves no orphans",
            announcer.Contains("RemoveMarkAsync", StringComparison.Ordinal) &&
            announcer.Contains("for (var i = 0; i < MaxMarkers; i++)", StringComparison.Ordinal));
        Check("markers go to the bot's own coalition only",
            announcer.Contains("MarkToCoalitionAsync", StringComparison.Ordinal) &&
            !announcer.Contains("MarkToAllAsync", StringComparison.Ordinal));
        Check("they are read-only, so a pilot cannot edit the frequency list",
            announcer.Contains("ReadOnly = true", StringComparison.Ordinal));

        Check("announcing is switched off by one flag and does nothing at all then",
            announcer.Contains("if (!config.AnnounceFrequenciesEnabled)", StringComparison.Ordinal));

        Section("Announcing, the wiring");

        var announceAt = bot.IndexOf("FrequencyAnnouncer.AnnounceAsync", StringComparison.Ordinal);
        var connectAt = bot.IndexOf("await srs.ConnectAsync", StringComparison.Ordinal);
        Check("the announcement comes after the SRS connection - promising frequencies the bot does " +
              "not monitor would be worse than silence",
            announceAt > 0 && connectAt > 0 && connectAt < announceAt);
        Check("it does not delay the first transmission", bot.Contains("_ = Task.Run(async () =>", StringComparison.Ordinal));
        Check("each airfield is paired with the tower that names it",
            bot.Contains("TowerFrequencyByAirfield", StringComparison.Ordinal));

        var announceDefaults = new AppConfig();
        Check("announcing is on by default - the generated frequencies exist nowhere else",
            announceDefaults.AnnounceFrequenciesEnabled);
        Check("both halves are on", announceDefaults.AnnounceFrequenciesMarkers && announceDefaults.AnnounceFrequenciesMessage);
        Check("the tower plan starts somewhere plausible for airfield traffic",
            announceDefaults.TowerPlanBaseMHz is >= 118 and <= 150, announceDefaults.TowerPlanBaseMHz.ToString("0.000"));

        // --- greeting a pilot who tunes in ----------------------------------------------------------

        Section("Telling a tuned radio apart from one that is switched off");

        // SRS reports every radio slot a client has, powered up or not. A slot that is off carries a
        // placeholder frequency of 1 Hz, and treating that as a tuning would have the bot greet
        // everybody on the server at once.
        Check("a radio tuned to 251.000 counts", TuneInGreeting.IsTuned(251_000_000, 0));
        Check("a disabled slot does not, whatever frequency it claims",
            !TuneInGreeting.IsTuned(251_000_000, TuneInGreeting.ModulationDisabled));
        Check("nor does the 1 Hz placeholder", !TuneInGreeting.IsTuned(1, 0));
        Check("nor an intercom-style frequency below a megahertz", !TuneInGreeting.IsTuned(100_000, 0));

        Section("Who gets greeted, and who has to be left alone");

        // Everything below is about NOT talking. SRS has no unicast: a greeting that fires twice is
        // the bot transmitting over somebody else's BRAA call, which is worse than never greeting
        // anybody. So the interesting assertions here are all the false ones.
        var greetingState = new TuneInGreeting.State();
        var tuneIn = new DateTime(2026, 9, 27, 18, 0, 0);
        var gap = TimeSpan.FromSeconds(90);

        Check("the first pilot on a frequency is greeted",
            greetingState.ShouldGreet("guid-a", 251_000_000, tuneIn, gap));
        Check("the same pilot on the same frequency never again - not an hour later either",
            !greetingState.ShouldGreet("guid-a", 251_000_000, tuneIn.AddHours(1), gap));

        // A flight of four checks in within seconds of each other. One greeting, not four.
        Check("the second aircraft of a flight hears nothing",
            !greetingState.ShouldGreet("guid-b", 251_000_000, tuneIn.AddSeconds(3), gap));
        Check("nor the third",
            !greetingState.ShouldGreet("guid-c", 251_000_000, tuneIn.AddSeconds(8), gap));
        Check("somebody arriving after the gap is greeted again",
            greetingState.ShouldGreet("guid-d", 251_000_000, tuneIn.AddSeconds(91), gap));

        // The ordering inside ShouldGreet matters: the already-greeted check comes first on purpose.
        // If a pilot flipping back to a preset refreshed the per-frequency gap, one bored pilot
        // could keep everybody else silent indefinitely.
        var ordering = new TuneInGreeting.State();
        Check("first pilot greeted", ordering.ShouldGreet("guid-a", 251_000_000, tuneIn, gap));
        Check("the same pilot tuning back in is refused", !ordering.ShouldGreet("guid-a", 251_000_000, tuneIn.AddSeconds(100), gap));
        Check("and that refusal did not push the gap out for the next arrival",
            ordering.ShouldGreet("guid-b", 251_000_000, tuneIn.AddSeconds(100), gap));

        // The gap is per frequency, because a tower and the tactical radio are different channels
        // with different listeners.
        var perFrequency = new TuneInGreeting.State();
        Check("a pilot tuning the tower is greeted", perFrequency.ShouldGreet("guid-a", 133_000_000, tuneIn, gap));
        Check("and again on the tactical radio, which nobody on it has heard yet",
            perFrequency.ShouldGreet("guid-a", 251_000_000, tuneIn.AddSeconds(1), gap));
        Check("a greeting on one frequency does not silence another",
            perFrequency.ShouldGreet("guid-b", 265_000_000, tuneIn.AddSeconds(2), gap));
        Check("all three are counted", perFrequency.Sent == 3, perFrequency.Sent.ToString());

        Check("a client with no guid is never greeted - there would be no way to remember it",
            !perFrequency.ShouldGreet("", 243_000_000, tuneIn, gap));

        Section("Forgetting pilots who left");

        var forgetting = new TuneInGreeting.State();
        forgetting.ShouldGreet("guid-a", 251_000_000, tuneIn, gap);
        forgetting.ShouldGreet("guid-a", 133_000_000, tuneIn, gap);
        forgetting.ShouldGreet("guid-b", 265_000_000, tuneIn, gap);
        Check("three greetings are remembered", forgetting.Tracked == 3, forgetting.Tracked.ToString());

        forgetting.Forget("guid-a");
        Check("a disconnect drops every frequency that pilot was greeted on, and nobody else's",
            forgetting.Tracked == 1, forgetting.Tracked.ToString());
        Check("so somebody who rejoins is greeted again",
            forgetting.ShouldGreet("guid-a", 251_000_000, tuneIn.AddSeconds(500), gap));
        Check("forgetting an unknown guid is harmless", forgetting.Tracked == 2);
        forgetting.Forget("");
        Check("and so is forgetting nothing at all", forgetting.Tracked == 2);

        Section("What the pilot hears when they tune in");

        var greetingConfig = new AppConfig();

        Eq("the channel they reached, then where the tactical radio is",
            TuneInGreeting.Build("Punch 1-1", "Batumi Tower",
                new[] { (251_000_000.0, "Overlord") }, greetingConfig),
            "Punch 1 1, Batumi Tower. Overlord is on two five one decimal zero. Say my callsign to be heard.");

        // The one that would be pure noise: telling somebody listening on Overlord where Overlord is.
        Eq("the channel the pilot is already on is not announced to them",
            TuneInGreeting.Build("Punch 1-1", "Overlord",
                new[] { (251_000_000.0, "Overlord") }, greetingConfig),
            "Punch 1 1, Overlord. Say my callsign to be heard.");

        Eq("several tactical radios come out in frequency order",
            TuneInGreeting.Build("Punch 1-1", "Batumi Tower",
                new[] { (265_000_000.0, "Magic"), (251_000_000.0, "Overlord") }, greetingConfig),
            "Punch 1 1, Batumi Tower. Overlord is on two five one decimal zero, " +
            "Magic is on two six five decimal zero. Say my callsign to be heard.");

        // Both placeholders can be empty on a real server - a client with no usable name, or no
        // DCS-gRPC and therefore no tactical radios at all. Neither may leave punctuation behind.
        Eq("an unnamed pilot does not get a sentence starting with a comma",
            TuneInGreeting.Build("", "Batumi Tower",
                new[] { (251_000_000.0, "Overlord") }, greetingConfig),
            "Batumi Tower. Overlord is on two five one decimal zero. Say my callsign to be heard.");
        Eq("and a server with no tactical radio leaves no gap or double full stop",
            TuneInGreeting.Build("Punch 1-1", "Batumi Tower",
                Array.Empty<(double, string)>(), greetingConfig),
            "Punch 1 1, Batumi Tower. Say my callsign to be heard.");
        Eq("with neither, what is left still reads as a sentence",
            TuneInGreeting.Build(null, "Batumi Tower", Array.Empty<(double, string)>(), greetingConfig),
            "Batumi Tower. Say my callsign to be heard.");

        Check("a placeholder frequency in the radio list is ignored rather than spoken",
            !TuneInGreeting.Build("Punch 1-1", "Batumi Tower",
                new[] { (1.0, "Broken"), (251_000_000.0, "Overlord") }, greetingConfig)
                .Contains("Broken", StringComparison.Ordinal));

        Eq("an operator who empties the template switches the greeting off by doing so",
            TuneInGreeting.Build("Punch 1-1", "Batumi Tower", new[] { (251_000_000.0, "Overlord") },
                new AppConfig { TuneInGreetingText = "" }), "");

        Eq("tidying removes the space a missing placeholder leaves in front of punctuation",
            TuneInGreeting.Tidy("Punch 1 1 , Batumi Tower ."), "Punch 1 1, Batumi Tower.");
        Eq("and collapses what two empty placeholders leave behind",
            TuneInGreeting.Tidy(", Batumi Tower. ."), "Batumi Tower.");

        Section("The greeting, the wiring");

        var greetDefaults = new AppConfig();
        Check("off by default - SRS has no unicast, so this talks to everybody on the frequency",
            !greetDefaults.TuneInGreetingEnabled);
        Check("the template offers all three placeholders",
            greetDefaults.TuneInGreetingText.Contains("{pilot}") &&
            greetDefaults.TuneInGreetingText.Contains("{callsign}") &&
            greetDefaults.TuneInGreetingText.Contains("{tactical}"));
        Check("and the default gap is long enough to cover a flight checking in",
            greetDefaults.TuneInGreetingGapSeconds >= 30,
            greetDefaults.TuneInGreetingGapSeconds.ToString("0"));

        var greetAt = bot.IndexOf("srs.OnClientRadiosChanged +=", StringComparison.Ordinal);
        var srsConnectAt = bot.IndexOf("await srs.ConnectAsync", StringComparison.Ordinal);
        Check("the subscription happens before connecting, so the first SYNC - everybody already " +
              "on the server - is not missed",
            greetAt > 0 && srsConnectAt > 0 && greetAt < srsConnectAt,
            $"subscribe at {greetAt}, connect at {srsConnectAt}");

        var greetHandler = greetAt > 0 ? bot[greetAt..] : "";
        Check("speaking does not block the TCP receive loop, which would stall the client list for everybody",
            greetHandler.IndexOf("_ = Task.Run", StringComparison.Ordinal) > 0 &&
            greetHandler.IndexOf("_ = Task.Run", StringComparison.Ordinal) <
            greetHandler.IndexOf("ShouldGreet", StringComparison.Ordinal));
        Check("the greeting goes through the same transmit path as a reply, so it queues instead of talking over it",
            greetHandler.Contains("await TransmitAsync(session, text, stoppingToken)", StringComparison.Ordinal));
        Check("the opposing coalition is not greeted either",
            greetHandler.Contains("client.Coalition != config.Coalition", StringComparison.Ordinal));
        Check("a frequency the bot does not monitor is skipped rather than greeted into the void",
            greetHandler.Contains("if (!sessions.TryGetValue(key, out var session)) continue;", StringComparison.Ordinal));
        Check("a disconnect is forgotten", bot.Contains("greetings.Forget(guid)", StringComparison.Ordinal));
        Check("the gap cannot be configured down to nothing",
            bot.Contains("Math.Max(5, config.TuneInGreetingGapSeconds)", StringComparison.Ordinal));

        var srsSource = ReadSource("SrsConnection.cs");
        var ownGuidAt = srsSource.IndexOf("string.Equals(guid, _clientGuid", StringComparison.Ordinal);
        var raiseAt = srsSource.IndexOf("OnClientRadiosChanged?.Invoke", StringComparison.Ordinal);
        Check("the bot's own client is filtered out before the event, so it cannot greet itself",
            ownGuidAt > 0 && raiseAt > 0 && ownGuidAt < raiseAt, $"own guid at {ownGuidAt}, event at {raiseAt}");
        Check("only a changed frequency set counts as an arrival - SRS sends radio updates for " +
              "volume and encryption too",
            srsSource.Contains("previous.SetEquals(key)) return;", StringComparison.Ordinal));
        Check("a radio slot with no modulation field is treated as switched off, not as tuned",
            srsSource.Contains(": TuneInGreeting.ModulationDisabled;", StringComparison.Ordinal));
        Check("a disconnect is reported", srsSource.Contains("OnClientDisconnected?.Invoke", StringComparison.Ordinal));

        // --- the config editor's navigation ---------------------------------------------------------

        Section("Every panel is reachable from the sidebar");

        // The sidebar is the only way into a panel - there is no address bar and no menu. A panel
        // that is in the project but not in the navigation is invisible, and nothing else notices:
        // it compiles, it works, and no user can get to it.
        var main = ReadSource("Darkstar.Gui/Pages/Main.razor");

        var panels = System.IO.Directory
            .GetFiles(Root + System.IO.Path.Combine("Darkstar.Gui", "Pages"), "*Panel.razor")
            .Select(System.IO.Path.GetFileNameWithoutExtension)
            .Cast<string>()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Check("the panels were found", panels.Count >= 9, string.Join(", ", panels));

        var unreachable = panels.Where(p => !main.Contains($"<{p} ", StringComparison.Ordinal) &&
                                            !main.Contains($"<{p}/>", StringComparison.Ordinal) &&
                                            !main.Contains($"<{p} />", StringComparison.Ordinal)).ToList();
        Check($"all {panels.Count} panels are rendered by Main.razor", unreachable.Count == 0,
            unreachable.Count == 0 ? "" : "orphaned: " + string.Join(", ", unreachable));

        // The channel numbers are the one thing in this editor that may not drift: ~90 places in the
        // documentation say "on CH2 Radios" or "the test button on CH8", and so does everybody who
        // has used it before. Panels may be regrouped and reordered; a number belongs to a panel.
        var navNumbers = System.Text.RegularExpressions.Regex
            .Matches(main, @"CH([1-9])")
            .Select(m => int.Parse(m.Groups[1].Value))
            .Distinct()
            .OrderBy(n => n)
            .ToList();

        Check("all nine channels are in the sidebar", navNumbers.SequenceEqual(Enumerable.Range(1, 9)),
            string.Join(",", navNumbers));

        var duplicated = System.Text.RegularExpressions.Regex
            .Matches(main, @"new NavEntry\(""[^""]*"", ""(CH[1-9])""")
            .Select(m => m.Groups[1].Value)
            .GroupBy(n => n)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        Check("and no number is given to two panels", duplicated.Count == 0, string.Join(", ", duplicated));

        Section("Every setting can actually be reached in the editor");

        // The documentation test below proves a setting is WRITTEN DOWN. That is not the same as
        // being reachable: the whole frequency announcer - four settings and a feature pilots depend
        // on to learn the generated frequencies at all - was documented in three places and editable
        // in none, so the only way to switch it off was a text editor. Worse, CH2 pointed at CH8 for
        // it, where it had never been. Documentation cannot catch that; this can.
        var panelMarkup = string.Join("\n", System.IO.Directory
            .GetFiles(Root + System.IO.Path.Combine("Darkstar.Gui", "Pages"), "*.razor")
            .Select(System.IO.File.ReadAllText));

        var unreachableSettings = typeof(AppConfig).GetProperties()
            .Where(p => p.CanRead && p.CanWrite)
            .Select(p => p.Name)
            .Where(name => !panelMarkup.Contains($"Config.{name}", StringComparison.Ordinal))
            .ToList();

        Check($"all {typeof(AppConfig).GetProperties().Count(p => p.CanRead && p.CanWrite)} settings are bound somewhere in the GUI",
            unreachableSettings.Count == 0,
            unreachableSettings.Count == 0 ? "" : "only in config.json: " + string.Join(", ", unreachableSettings));

        // A radio's own settings are bound through the loop variable rather than through Store.Config.
        var unreachablePerRadio = typeof(RadioConfig).GetProperties()
            .Where(p => p.CanRead && p.CanWrite)
            .Select(p => p.Name)
            .Where(name => !panelMarkup.Contains($".{name}", StringComparison.Ordinal))
            .ToList();

        Check("and so are all the per-radio ones", unreachablePerRadio.Count == 0,
            unreachablePerRadio.Count == 0 ? "" : "only in config.json: " + string.Join(", ", unreachablePerRadio));

        // The other half of that failure: a card telling somebody to go to a channel the setting is
        // not on. Panels name channels in their own prose, and nothing links the two.
        var panelFiles = System.IO.Directory.GetFiles(
            Root + System.IO.Path.Combine("Darkstar.Gui", "Pages"), "*.razor");

        var channelOf = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ConnectionPanel"] = "CH1", ["RadiosPanel"] = "CH2", ["SpeechPanel"] = "CH3",
            ["PhrasesPanel"] = "CH4", ["VocabularyPanel"] = "CH5", ["DiscordPanel"] = "CH6",
            ["LoggingPanel"] = "CH7", ["DcsGrpcPanel"] = "CH8", ["ServicePanel"] = "CH9",
        };

        var selfReferences = new List<string>();

        foreach (var file in panelFiles)
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(file)!;
            if (!channelOf.TryGetValue(name, out var own)) continue;

            // A panel pointing at its own channel is a leftover from before the setting moved.
            if (System.IO.File.ReadAllText(file).Contains($"({own})", StringComparison.Ordinal))
                selfReferences.Add($"{name} points at {own}, which is itself");
        }

        Check("no panel sends the operator to the channel they are already on",
            selfReferences.Count == 0, string.Join("; ", selfReferences));

        Section("CH8's three entries and its three sections are the same three");

        // CH8 is one component shown in three pieces. The sidebar names a section, the panel decides
        // what to render from it - and a section named in one place and not the other fails silently:
        // an entry that shows a blank page, or a block of settings nothing can reach any more.
        var grpc = ReadSource("Darkstar.Gui/Pages/DcsGrpcPanel.razor");

        var declared = System.Text.RegularExpressions.Regex
            .Matches(grpc, @"public const string (Section\w+) = ""(\w+)"";")
            .Select(m => m.Groups[1].Value)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        var guarded = System.Text.RegularExpressions.Regex
            .Matches(grpc, @"@if \(Section == (Section\w+)\)")
            .Select(m => m.Groups[1].Value)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        var referenced = System.Text.RegularExpressions.Regex
            .Matches(main, @"DcsGrpcPanel\.(Section\w+)")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Check("three sections are declared", declared.Count == 3, string.Join(", ", declared));
        Check("each one guards a block of the panel", guarded.SequenceEqual(declared),
            $"declared [{string.Join(",", declared)}], guarded [{string.Join(",", guarded)}]");
        Check("and each one has an entry in the sidebar", referenced.SequenceEqual(declared),
            $"declared [{string.Join(",", declared)}], in the sidebar [{string.Join(",", referenced)}]");
        Check("the sidebar names them by constant rather than by string, so a typo cannot compile",
            !System.Text.RegularExpressions.Regex.IsMatch(main, @"Section = ""\w+"""));

        // Markup left outside the three blocks would appear on all three pages - and the one place
        // that is easy to produce is the end of a block, by closing a brace one line too early.
        var markup = grpc[..grpc.IndexOf("@code {", StringComparison.Ordinal)].Split('\n');
        var depth = 0;
        var orphans = new List<string>();

        for (var i = 0; i < markup.Length; i++)
        {
            var line = markup[i].TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith(" ", StringComparison.Ordinal)) continue; // inside something

            if (line == "{") { depth++; continue; }
            if (line == "}") { depth--; continue; }

            if (depth == 0 && !line.StartsWith("@if (Section ==", StringComparison.Ordinal) &&
                !line.StartsWith("@", StringComparison.Ordinal) && !line.StartsWith("<!--", StringComparison.Ordinal))
                orphans.Add($"line {i + 1}: {line.Trim()}");
        }

        Check("no markup sits outside a section, which would show it on every one of the three pages",
            orphans.Count == 0, orphans.Count == 0 ? "" : string.Join(" | ", orphans.Take(3)));

        Section("The sidebar's styling exists");

        // Four class names used in one file and defined in another - a rename in either one alone is
        // invisible until somebody looks at the window, which on this project means the user.
        var css = ReadSource("Darkstar.Gui/wwwroot/css/app.css");
        foreach (var cssClass in new[] { "navitem", "ch", "navlabel", "navflag", "sub", "heading" })
            Check($".{cssClass} is styled", css.Contains($".{cssClass} ", StringComparison.Ordinal) ||
                                            css.Contains($".{cssClass}:", StringComparison.Ordinal) ||
                                            css.Contains($".{cssClass}{{", StringComparison.Ordinal));

        Section("The documented channels are the channels there are");

        var guiDoc = ReadSource("docs/gui.md");
        var documented = System.Text.RegularExpressions.Regex
            .Matches(guiDoc, @"^###+ CH([1-9])", System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(m => int.Parse(m.Groups[1].Value))
            .Distinct()
            .OrderBy(n => n)
            .ToList();

        Check("gui.md describes exactly the nine channels the sidebar offers",
            documented.SequenceEqual(navNumbers),
            $"documented [{string.Join(",", documented)}], in the sidebar [{string.Join(",", navNumbers)}]");

        // --- nothing private can be committed by accident -----------------------------------------

        Section("The .gitignore covers everything the bot writes");

        var gitignore = ReadSource(".gitignore");

        // The bot creates these next to its executable while it runs. Each one holds either a secret
        // of the operator's or personal data belonging to other players - voice recordings and names
        // of people who flew on the server. A commit is permanent, so this is checked rather than
        // remembered. recordings/ was in fact missing until somebody asked.
        //
        // Adding a new output folder? Add it here and to .gitignore in the same change.
        var writtenAtRuntime = new (string Path, string Holds)[]
        {
            ("config.json",  "GeminiApiKey, DcsGrpcApiKey and DiscordWebhookUrl in plain text"),
            ("recordings/",  "recorded voices of other players, with their names in the file name"),
            ("logs/",        "transcripts of what pilots said, plus names and positions"),
            ("Backup/",      "timestamped copies of all of the above"),
            ("grpc-dumps/",  "mission data: unit names, player names, positions"),
            ("models/",      "the speech model - hundreds of MB, and not ours to redistribute"),
            ("phrases.json", "per-installation edits"),
            ("vocabulary.json", "per-installation edits"),
        };

        foreach (var (path, holds) in writtenAtRuntime)
        {
            Check($".gitignore excludes {path}",
                gitignore.Split('\n').Any(line => line.Trim() == path),
                holds);
        }

        // The build writes into these, and they are large rather than private.
        foreach (var path in new[] { "installer/publish/", "installer/output/", "installer/vosk-model/", "publish/" })
            Check($".gitignore excludes {path}", gitignore.Contains(path, StringComparison.Ordinal));

        // Machine-specific editor state, which is where an absolute path - and with it a Windows user
        // name - leaks into a public repository.
        foreach (var pattern in new[] { ".vs/", "*.user", "*.suo", "*.pubxml", ".history/", "_ReSharper*/" })
            Check($".gitignore excludes {pattern}", gitignore.Contains(pattern, StringComparison.Ordinal));

        Check(".gitignore says what a .gitignore cannot do - the commit author",
            gitignore.Contains("noreply", StringComparison.OrdinalIgnoreCase),
            "the author name and e-mail in every commit is the actual identity leak");

        // --- no project compiles another project's sources -----------------------------------------

        Section("No project swallows another project's sources");

        // The failure this guards against: Darkstar.csproj sits in the repository root, so every
        // other project lives underneath it and the SDK's default glob compiles them all into the
        // bot. The symptoms point nowhere near the cause - "Duplicate
        // System.Reflection.AssemblyTitleAttribute" from the other project's generated obj\ files,
        // and "Only one compilation unit can have top-level statements" from two Program.cs files.
        //
        // It happened once, when Darkstar.Tests was added next to a hand-maintained list of one
        // exclusion per sibling project. Checked generally rather than for that one case: any project
        // with another project beneath it has to exclude subfolder sources.
        var projectFiles = System.IO.Directory
            .GetFiles(Root, "*.csproj", System.IO.SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{System.IO.Path.DirectorySeparatorChar}obj{System.IO.Path.DirectorySeparatorChar}",
                        StringComparison.Ordinal))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Check("the solution's projects were found", projectFiles.Count == 4,
            string.Join(", ", projectFiles.Select(System.IO.Path.GetFileName)));

        foreach (var projectFile in projectFiles)
        {
            var folder = System.IO.Path.GetDirectoryName(projectFile)!;
            var name = System.IO.Path.GetFileName(projectFile);

            var nested = projectFiles
                .Where(other => other != projectFile &&
                                other.StartsWith(folder + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                .Select(other => System.IO.Path.GetFileName(other))
                .ToList();

            if (nested.Count == 0) continue;

            var text = System.IO.File.ReadAllText(projectFile);

            // Either the blanket exclusion, or an explicit one per nested project. Both are correct;
            // the blanket one is what stops this from going stale when a project is added.
            var excludesEverySubfolder = text.Contains(@"<Compile Remove=""*\**\*.cs"" />", StringComparison.Ordinal);
            var excludesEachNested = nested.All(other =>
                text.Contains($@"<Compile Remove=""{System.IO.Path.GetFileNameWithoutExtension(other)}\**\*.cs""",
                    StringComparison.Ordinal));

            Check($"{name} does not compile the {nested.Count} project(s) beneath it",
                excludesEverySubfolder || excludesEachNested,
                excludesEverySubfolder ? "excludes every subfolder"
                    : excludesEachNested ? "excludes each one by name"
                    : "nested: " + string.Join(", ", nested));
        }

        // The bot's own sources all sit in the root, which is what makes the blanket exclusion safe.
        // If that ever stops being true, the exclusion silently drops the new folder.
        var botSources = System.IO.Directory
            .GetFiles(Root, "*.cs", System.IO.SearchOption.TopDirectoryOnly)
            .Length;
        Check("the bot still keeps all its own sources in the root", botSources >= 10,
            $"{botSources} file(s) directly in the repository root");

        var cleanScript = System.IO.File.ReadAllText(Root + "build-installer.ps1");
        Check("-Clean discovers the project folders rather than listing them",
            cleanScript.Contains("Filter *.csproj"),
            "a hard-coded list is what went stale last time");

        // --- the documentation keeps up with the code ---------------------------------------------------

        // Documentation drifts silently: a field added to AppConfig works perfectly and is simply never
        // written down, and nothing notices for months. These three checks are the cheap fix - they compare
        // the code against the documents rather than trusting that both were updated.

        Console.WriteLine();
        Console.WriteLine("Every setting is documented");

        static bool Mentions(string document, string name) =>
            System.Text.RegularExpressions.Regex.IsMatch(document, $@"\b{System.Text.RegularExpressions.Regex.Escape(name)}\b");

        var references = new[] { "docs/manual-en.md", "docs/manual-de.md", "docs/configuration.md" }
            .ToDictionary(path => path, path => System.IO.File.ReadAllText(Root + path));

        // Reflection rather than a regex over the source: this is exactly the set of names a user can put
        // in config.json, with no chance of the pattern and the language disagreeing.
        var settings = new[] { typeof(AppConfig), typeof(RadioConfig) }
            .SelectMany(type => type.GetProperties()
                .Where(p => p.CanRead && p.CanWrite)
                .Select(p => (Type: type.Name, p.Name)))
            .ToList();

        Check("there are settings to check at all", settings.Count > 50, $"{settings.Count} found");

        foreach (var document in references.Keys.OrderBy(k => k))
        {
            var undocumented = settings
                .Where(s => !Mentions(references[document], s.Name))
                .Select(s => $"{s.Type}.{s.Name}")
                .ToList();

            Check($"{System.IO.Path.GetFileName(document)} covers all {settings.Count} settings",
                undocumented.Count == 0,
                undocumented.Count == 0 ? "" : "missing: " + string.Join(", ", undocumented));
        }

        Console.WriteLine();
        Console.WriteLine("Every switch of the build script is documented");

        // Taken from the top-level param() block only - the helper functions further down have
        // parameters of their own, and those are nobody's business but the script's.
        var scriptSource = ReadSource("build-installer.ps1");
        var paramStart = scriptSource.IndexOf("\nparam(", StringComparison.Ordinal);
        var paramEnd = scriptSource.IndexOf("\n)", paramStart, StringComparison.Ordinal);
        Check("the parameter block was found", paramStart > 0 && paramEnd > paramStart);

        var switches = System.Text.RegularExpressions.Regex
            .Matches(scriptSource[paramStart..paramEnd], @"\[(?:switch|string|int)\]\s*\$(\w+)")
            .Select(m => "-" + m.Groups[1].Value)
            .Distinct()
            .ToList();

        Check("the switches were found", switches.Count >= 7, string.Join(" ", switches));

        var buildDoc = ReadSource("docs/building-the-installer.md");
        var undocumentedSwitches = switches.Where(s => !buildDoc.Contains(s, StringComparison.Ordinal)).ToList();
        Check($"building-the-installer.md covers all {switches.Count} of them",
            undocumentedSwitches.Count == 0,
            undocumentedSwitches.Count == 0 ? "" : "missing: " + string.Join(", ", undocumentedSwitches));

        Console.WriteLine();
        Console.WriteLine("Every command-line option is documented");

        // Taken from the parser itself, so an option added there without a mention in the manual is caught.
        var runnerSource = System.IO.File.ReadAllText(Root + "HotwordTestRunner.cs");
        var options = System.Text.RegularExpressions.Regex
            .Matches(runnerSource, @"case ""(--[a-z-]+)"":")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();

        Check("the options were found in the parser", options.Count >= 8, string.Join(" ", options));

        foreach (var manual in new[] { "docs/manual-en.md", "docs/manual-de.md" })
        {
            var text = references[manual];
            var undocumented = options.Where(o => !text.Contains(o, StringComparison.Ordinal)).ToList();
            Check($"{System.IO.Path.GetFileName(manual)} documents all {options.Count} options",
                undocumented.Count == 0,
                undocumented.Count == 0 ? "" : "missing: " + string.Join(", ", undocumented));
        }

        Console.WriteLine();
        Console.WriteLine("Every source file is on the map for contributors");

        var contributing = System.IO.File.ReadAllText(Root + "docs/contributing.md");

        var sourceFiles = System.IO.Directory.GetFiles(Root, "*.cs")
            .Concat(System.IO.Directory.GetFiles(Root + "Darkstar.Core", "*.cs"))
            .Select(System.IO.Path.GetFileName)
            .Where(name => name != null)
            .Cast<string>()
            .ToList();

        var unmapped = sourceFiles.Where(name => !contributing.Contains(name, StringComparison.Ordinal)).ToList();
        Check($"contributing.md names all {sourceFiles.Count} source files",
            unmapped.Count == 0,
            unmapped.Count == 0 ? "" : "missing: " + string.Join(", ", unmapped));

        Console.WriteLine();
        Console.WriteLine("Every tag the log writes is explained in the manuals");

        // The log is the support channel for this project: a server operator reads it, or pastes it
        // into a bug report. A tag nobody can look up is a line that means nothing to the person who
        // needs it most - and tags get added casually, one Logger.Log at a time.
        var logTags = new List<string>();

        foreach (var file in System.IO.Directory.GetFiles(Root, "*.cs")
                     .Concat(System.IO.Directory.GetFiles(Root + "Darkstar.Core", "*.cs")))
        {
            foreach (System.Text.RegularExpressions.Match tag in System.Text.RegularExpressions.Regex
                         .Matches(System.IO.File.ReadAllText(file), @"Logger\.Log\(\s*\$?""\[([A-Za-z0-9 ._-]+)\]"))
            {
                if (!logTags.Contains(tag.Groups[1].Value)) logTags.Add(tag.Groups[1].Value);
            }
        }

        logTags.Sort(StringComparer.Ordinal);
        Check("the tags were found in the sources", logTags.Count >= 20, $"{logTags.Count} tags");

        foreach (var manual in new[] { "docs/manual-en.md", "docs/manual-de.md" })
        {
            var text = references[manual];
            var unexplained = logTags.Where(t => !text.Contains($"[{t}]", StringComparison.Ordinal)).ToList();
            Check($"{System.IO.Path.GetFileName(manual)} explains all {logTags.Count} log tags",
                unexplained.Count == 0,
                unexplained.Count == 0 ? "" : "missing: " + string.Join(", ", unexplained));
        }

        Console.WriteLine();
        Console.WriteLine("The choices the installer offers are the choices the manual describes");

        // The installer asks one question before anything else - which setup type - and the answer
        // decides whether a machine ends up with a window on it. Nobody reading only the manual
        // should discover "Bot only (headless server)" by running the installer on a game server.
        var setupTypes = System.Text.RegularExpressions.Regex
            .Matches(iss, @"^Name: ""\w+""; Description: ""([^""]+)""",
                System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .Where(d => !d.StartsWith("Install and start", StringComparison.Ordinal) &&
                        !d.StartsWith("Launch", StringComparison.Ordinal))
            .Distinct()
            .ToList();

        Check("the installer's offered choices were found", setupTypes.Count >= 6, string.Join(" | ", setupTypes));

        foreach (var manual in new[] { "docs/manual-en.md", "docs/manual-de.md" })
        {
            // The headless type is the one that matters for a server, and the one whose wording a
            // rename would quietly break - so it is compared verbatim against Setup.iss.
            var headless = setupTypes.FirstOrDefault(t => t.Contains("Bot only", StringComparison.Ordinal));
            Check($"{System.IO.Path.GetFileName(manual)} names the headless setup type exactly as the installer does",
                headless != null && references[manual].Contains(headless, StringComparison.Ordinal),
                headless ?? "no 'Bot only' type in Setup.iss");
        }

        Console.WriteLine();
        Console.WriteLine("Every request a pilot can make has prose in the chapter pilots read");

        // A feature reaches the settings table automatically - the test above sees to that. What it
        // does not reach automatically is the chapter somebody reads to find out what to SAY, and a
        // request documented only as a trigger-phrase setting is a request nobody discovers.
        static string Chapter7(string manual)
        {
            var start = manual.IndexOf("\n## 7.", StringComparison.Ordinal);
            var end = manual.IndexOf("\n## 8.", StringComparison.Ordinal);
            return start >= 0 && end > start ? manual[start..end] : "";
        }

        // Each entry: what a pilot would call it, and a phrase that must appear in that chapter.
        var onTheRadio = new (string Feature, string English, string German)[]
        {
            ("bogey dope", "bogey dope", "bogey dope"),
            ("picture", "picture", "picture"),
            ("threat check", "threat check", "threat check"),
            ("alpha check", "alpha check", "alpha check"),
            ("radio check", "radio check", "radio check"),
            ("runway in use", "runway in use", "runway in use"),
            ("ATIS", "ATIS", "ATIS"),
            ("threat circle", "threat circle", "threat circle"),
            ("friendly position", "where is", "where is"),
            ("the standby acknowledgement", "standby", "standby"),
            ("the rate limit", "working other traffic", "working other traffic"),
            ("the tune-in greeting", "tunes in", "aufschaltet"),
        };

        foreach (var manual in new[] { "docs/manual-en.md", "docs/manual-de.md" })
        {
            var chapter = Chapter7(references[manual]);
            Check($"{System.IO.Path.GetFileName(manual)} has a chapter 7 to check", chapter.Length > 2000,
                $"{chapter.Length} characters");

            var english = manual.EndsWith("en.md", StringComparison.Ordinal);
            var absent = onTheRadio
                .Where(f => !chapter.Contains(english ? f.English : f.German, StringComparison.OrdinalIgnoreCase))
                .Select(f => f.Feature)
                .ToList();

            Check($"{System.IO.Path.GetFileName(manual)} chapter 7 covers all {onTheRadio.Length} of them",
                absent.Count == 0, absent.Count == 0 ? "" : "missing: " + string.Join(", ", absent));
        }

        Console.WriteLine();
        Console.WriteLine("Every cross-reference in the documentation points somewhere");

        // A renamed heading leaves the links to it silently dead, and a translated manual makes that twice
        // as likely - the German chapter 8.4 is called something else, so a link copied across languages
        // looks right and isn't. Cheap to check, invisible otherwise.
        static string Slug(string heading)
        {
            var text = heading.Trim().ToLowerInvariant();
            text = System.Text.RegularExpressions.Regex.Replace(text, @"[`*_]", "");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"[^\w\s-]", "");
            return System.Text.RegularExpressions.Regex.Replace(text.Trim(), @"\s+", "-");
        }

        var markdown = System.IO.Directory.GetFiles(Root, "*.md")
            .Concat(System.IO.Directory.GetFiles(Root + "docs", "*.md"))
            .OrderBy(p => p)
            .ToList();

        var headings = markdown.ToDictionary(
            path => System.IO.Path.GetFullPath(path),
            path => new HashSet<string>(
                System.Text.RegularExpressions.Regex
                    .Matches(System.IO.File.ReadAllText(path), @"^#{1,6}\s+(.*)$",
                        System.Text.RegularExpressions.RegexOptions.Multiline)
                    .Select(m => Slug(m.Groups[1].Value)),
                StringComparer.Ordinal));

        Check("the documents were found", markdown.Count >= 8, $"{markdown.Count} files");

        var brokenLinks = new List<string>();

        foreach (var path in markdown)
        {
            var text = System.IO.File.ReadAllText(path);
            var folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!;

            foreach (System.Text.RegularExpressions.Match link in
                     System.Text.RegularExpressions.Regex.Matches(text, @"\[[^\]]*\]\(([^)]+)\)"))
            {
                var target = link.Groups[1].Value;
                if (target.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
                    target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) continue;

                var hash = target.IndexOf('#');
                if (hash < 0) continue; // a plain file link, not an anchor

                var filePart = target[..hash];
                var fragment = target[(hash + 1)..];
                if (fragment.Length == 0) continue;

                var owner = filePart.Length == 0
                    ? System.IO.Path.GetFullPath(path)
                    : System.IO.Path.GetFullPath(System.IO.Path.Combine(folder, filePart));

                var name = System.IO.Path.GetFileName(path);
                if (!headings.TryGetValue(owner, out var available))
                    brokenLinks.Add($"{name} -> unknown file \"{filePart}\"");
                else if (!available.Contains(fragment))
                    brokenLinks.Add($"{name} -> #{fragment} (not in {System.IO.Path.GetFileName(owner)})");
            }
        }

        Check("no cross-reference is dead", brokenLinks.Count == 0,
            brokenLinks.Count == 0 ? "" : string.Join("; ", brokenLinks.Take(5)));
    }
}

/// <summary>A stand-in for a DCS unit where only the name matters, so PilotNames' matching rules can
/// be exercised without building protobuf objects.</summary>
internal sealed record FakeNamedUnit(string Name);
