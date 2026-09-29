using static Darkstar.Tests.Test;

namespace Darkstar.Tests;

/// <summary>Matching an SRS name to a DCS pilot.</summary>
internal static class PilotNameTests
{
    public static void Run()
    {
        // Pilot name handling, checked against the shapes DCS players actually type - and, just as
        // importantly, against the pairs that must NOT be treated as the same person.




        const string Sep = "|";

        // --- splitting ------------------------------------------------------------------------------

        Console.WriteLine("Split: callsign, handle and the tags in between");

        var p = PilotNames.Split("Enfield 1-1 | neodym", Sep);
        Check("the documented form splits as before", p.Callsign == "Enfield 1-1" && p.Handle == "neodym", $"{p.Callsign} / {p.Handle}");
        Eq("the raw name is kept untouched", p.Raw, "Enfield 1-1 | neodym");

        p = PilotNames.Split("[ISAF] Mobius 1 | Reaper", Sep);
        Check("a squadron tag is removed from the callsign", p.Callsign == "Mobius 1" && p.Handle == "Reaper", $"{p.Callsign} / {p.Handle}");

        p = PilotNames.Split("Mobius 1 (VF-1) | Reaper", Sep);
        Eq("a trailing tag in brackets is removed too", p.Callsign, "Mobius 1");

        p = PilotNames.Split("Hitman 11", Sep);
        Check("a name without a separator is all callsign", p.Callsign == "Hitman 11" && p.Handle == "");

        p = PilotNames.Split("Mobius 1 / Reaper", Sep);
        Check("a slash works as a separator as well", p.Callsign == "Mobius 1" && p.Handle == "Reaper", $"{p.Callsign} / {p.Handle}");

        p = PilotNames.Split("Wardog 1-4", Sep);
        Eq("a hyphen is NOT treated as a separator (it's part of the flight number)", p.Callsign, "Wardog 1-4");

        p = PilotNames.Split("  Spare 15  ", Sep);
        Eq("surrounding whitespace is trimmed", p.Callsign, "Spare 15");

        p = PilotNames.Split("", Sep);
        Check("an empty name doesn't explode", p is { Raw: "", Callsign: "", Handle: "" });

        p = PilotNames.Split("Mobius 1 :: Reaper", "::");
        Check("a multi-character configured separator works", p.Callsign == "Mobius 1" && p.Handle == "Reaper", $"{p.Callsign} / {p.Handle}");

        Eq("[TAG] only leaves the raw name as a fallback", PilotNames.DisplayCallsign("[ISAF]", Sep), "[ISAF]");

        // --- canonical keys -------------------------------------------------------------------------

        Console.WriteLine();
        Console.WriteLine("CanonicalKey: the ways of writing a callsign that must stop mattering");

        Eq("spaces and hyphens fall away", PilotNames.CanonicalKey("Mobius 1-1"), "mobius11");
        Eq("so does capitalisation", PilotNames.CanonicalKey("MOBIUS 11"), "mobius11");
        Eq("and underscores", PilotNames.CanonicalKey("mobius_1_1"), "mobius11");
        Eq("and squadron tags", PilotNames.CanonicalKey("[ISAF] Mobius 1-1"), "mobius11");

        Check("all four are therefore the same pilot",
            new[] { "Mobius 1-1", "MOBIUS 11", "mobius_1_1", "[ISAF] Mobius 1-1" }
                .Select(PilotNames.CanonicalKey).Distinct().Count() == 1);

        // The important half: different pilots must stay different.
        Check("but 1-1 and 1-2 stay apart",
            PilotNames.CanonicalKey("Mobius 1-1") != PilotNames.CanonicalKey("Mobius 1-2"));
        Check("and different callsigns stay apart",
            PilotNames.CanonicalKey("Mobius 11") != PilotNames.CanonicalKey("Wardog 11"));
        Check("and two separate pilots aren't merged into one flight number",
            PilotNames.CanonicalKey("Spare 1") != PilotNames.CanonicalKey("Spare 15"));
        Eq("an empty value gives an empty key", PilotNames.CanonicalKey(null), "");

        // --- speech ---------------------------------------------------------------------------------

        Console.WriteLine();
        Console.WriteLine("ForSpeech: how it comes out of the speech engine");

        Eq("a hyphenated flight number becomes separate digits", PilotNames.ForSpeech("Enfield 1-1"), "Enfield 1 1");
        Eq("a two-digit number is split so it isn't read as \"fifteen\"", PilotNames.ForSpeech("Spare 15"), "Spare 1 5");
        Eq("digits stuck to the word are separated", PilotNames.ForSpeech("Enfield11"), "Enfield 1 1");
        Eq("a squadron tag is not read out", PilotNames.ForSpeech("[ISAF] Mobius 1"), "Mobius 1");
        Eq("underscores become pauses", PilotNames.ForSpeech("Hitman_1_1"), "Hitman 1 1");
        Eq("a plain callsign is left alone", PilotNames.ForSpeech("Overlord"), "Overlord");
        Eq("three-digit callsigns split too", PilotNames.ForSpeech("Chevy 123"), "Chevy 1 2 3");
        Eq("nothing in, nothing out", PilotNames.ForSpeech(null), "");

        // --- matching -------------------------------------------------------------------------------

        Console.WriteLine();
        Console.WriteLine("FindMatch: finding the SRS pilot among the DCS units");

        PilotNames.MatchResult<FakePilot> Match(string srsName, params FakePilot[] units) =>
            PilotNames.FindMatch(units, srsName, Sep, u => u.PlayerName, u => u.Callsign, u => u.Name);

        var mobius = new FakePilot("Mobius 1-1", "Mobius", "Mobius11-1");
        var wardog = new FakePilot("Wardog 1-4", "Wardog", "Wardog14-1");

        // The case that used to fail silently and send bullseye instead of BRAA.
        var r = Match("[ISAF] Mobius 1-1 | Reaper", mobius, wardog);
        Check("a squadron tag no longer prevents the match", r.Match == mobius, $"{r.Rule}: {r.Explanation}");

        r = Match("Mobius 11 | Reaper", mobius, wardog);
        Check("a flight number written without the hyphen matches", r.Match == mobius, $"{r.Rule}: {r.Explanation}");

        r = Match("mobius11|reaper", mobius, wardog);
        Check("no spaces at all still matches", r.Match == mobius, $"{r.Rule}: {r.Explanation}");

        r = Match("Mobius 1-1", mobius, wardog);
        Check("an exact name takes the strictest rule", r.Rule == PilotNames.MatchRule.ExactRaw, r.Rule.ToString());

        // Matching on the handle behind the separator.
        var byHandle = new FakePilot("Reaper", "Mobius", "Mobius11-1");
        r = Match("Enfield 1-1 | Reaper", byHandle, wardog);
        Check("the handle behind the separator matches the DCS player name",
            r.Match == byHandle && r.Rule == PilotNames.MatchRule.CanonicalHandle, $"{r.Rule}: {r.Explanation}");

        // Matching on the unit's callsign field rather than the player name.
        var byCallsign = new FakePilot("SomeoneElse", "Springfield 21", "Springfield21-1");
        r = Match("Springfield 2-1 | Bernhard", byCallsign, wardog);
        Check("the unit callsign matches when the player name doesn't",
            r.Match == byCallsign, $"{r.Rule}: {r.Explanation}");

        // The partial rule: helpful when unambiguous.
        var partial = new FakePilot("Bernhard_S", "Enfield", "Enfield11-1");
        r = Match("Enfield 1-1 | Bernhard", partial, wardog);
        Check("a handle that is only part of the DCS name still matches when it's unambiguous",
            r.Match == partial && r.Rule == PilotNames.MatchRule.UniqueSubstring, $"{r.Rule}: {r.Explanation}");

        // ...and refuses when it isn't.
        var twin1 = new FakePilot("Bernhard_S", "Enfield", "Enfield11-1");
        var twin2 = new FakePilot("Bernhard_M", "Enfield", "Enfield12-1");
        r = Match("Enfield 1-1 | Bernhard", twin1, twin2);
        Check("two candidates containing the name are refused, not guessed between",
            r.Match == null && r.Explanation.Contains("ambiguous"), $"{r.Rule}: {r.Explanation}");

        // A stricter rule must always win over a looser one, whatever the list order.
        var exact = new FakePilot("Bernhard", "Enfield", "Enfield11-1");
        var contains = new FakePilot("Bernhard_Other", "Wardog", "Wardog14-1");
        r = Match("Enfield 1-1 | Bernhard", contains, exact);
        Check("an exact-key match beats a partial one even when listed second",
            r.Match == exact && r.Rule == PilotNames.MatchRule.CanonicalHandle, $"{r.Rule}: {r.Explanation}");

        // Things that must NOT match.
        r = Match("Wardog 1-4 | Someone", mobius);
        Check("a pilot who isn't in the mission isn't matched to somebody else",
            r.Match == null, $"{r.Rule}: {r.Explanation}");

        // The one that must never happen: a wingman's call answered from the lead's aircraft. The DCS
        // Callsign field holds the FLIGHT callsign ("Mobius"), shared by everyone in it, so a loose
        // partial match there would hand Mobius 1-2's BRAA call to Mobius 1-1.
        r = Match("Mobius 1-2 | Someone", mobius);
        Check("a wingman is never matched to their lead's aircraft",
            r.Match == null, $"{r.Rule}: {r.Explanation}");

        var lead = new FakePilot("Mobius 1-1", "Mobius", "Mobius11-1");
        var wing = new FakePilot("Mobius 1-2", "Mobius", "Mobius11-2");
        r = Match("Mobius 1-2 | Someone", lead, wing);
        Check("with both in the flight, the right one is picked",
            r.Match == wing, $"{r.Rule}: {r.Explanation}");
        r = Match("[ISAF] Mobius 1-1 | Reaper", lead, wing);
        Check("and the lead is picked for the lead, tag and all",
            r.Match == lead, $"{r.Rule}: {r.Explanation}");

        r = Match("", mobius, wardog);
        Check("an empty sender name matches nothing", r.Match == null);

        r = Match("Mobius 1-1");
        Check("an empty mission matches nothing", r.Match == null);

        // Short keys must not be used for partial matching - "AB" would hit half the server.
        var shortName = new FakePilot("ABC", "AB", "AB-1");
        r = Match("AB | xy", shortName);
        Check("keys shorter than four characters aren't partially matched",
            r.Rule != PilotNames.MatchRule.UniqueSubstring, $"{r.Rule}: {r.Explanation}");

        Check("every result explains itself for the log",
            !string.IsNullOrWhiteSpace(Match("Mobius 1-1", mobius).Explanation) &&
            !string.IsNullOrWhiteSpace(Match("Nobody", mobius).Explanation));

        // --- consistency with the rest of the code --------------------------------------------------

        Console.WriteLine();
        Console.WriteLine("Wiring");


        var intel = File.ReadAllText(Root + "Darkstar.Core/DcsIntelService.cs");
        Check("the intel service uses the shared matcher", intel.Contains("PilotNames.FindMatch"));
        Check("and no longer compares names by hand", !intel.Contains("units.FirstOrDefault(u => Same("));
        Check("a failed match is logged instead of passing silently",
            intel.Contains("could not be matched to a unit"));

        var bot = File.ReadAllText(Root + "BotService.cs");
        Check("the display callsign comes from the shared helper", bot.Contains("PilotNames.DisplayCallsign"));
        Check("so does the speech form", bot.Contains("PilotNames.ForSpeech"));

        var circles = File.ReadAllText(Root + "Darkstar.Core/ThreatCircleService.cs");
        Check("threat circles are keyed canonically, so cancelling still finds them",
            circles.Contains("PilotNames.CanonicalKey(rawPlayerName)"));

        // ============================================================================================
        // Identifying the pilot from what they SAID - "active runway for Punch 1-1".
        // ============================================================================================

        Console.WriteLine();
        Console.WriteLine("NormalizeSpokenNumbers");

        Eq("spoken digits become figures", PilotNames.NormalizeSpokenNumbers("punch one one"), "punch 1 1");
        Eq("niner too", PilotNames.NormalizeSpokenNumbers("enfield niner"), "enfield 9");
        Eq("aviation forms are understood", PilotNames.NormalizeSpokenNumbers("spare tree fife"), "spare 3 5");
        Eq("zero and oh both work", PilotNames.NormalizeSpokenNumbers("chevy zero oh"), "chevy 0 0");
        Eq("figures already there are left alone", PilotNames.NormalizeSpokenNumbers("punch 1-1"), "punch 1-1");
        // Word boundaries matter here: "someone" contains "one" but is not a number.
        Eq("a number word inside a longer word is left alone",
            PilotNames.NormalizeSpokenNumbers("someone won"), "someone 1");
        Eq("and so is \"fortune\", which contains \"four\"... no, \"for\"",
            PilotNames.NormalizeSpokenNumbers("fortune favours"), "fortune favours");
        Eq("nothing in, nothing out", PilotNames.NormalizeSpokenNumbers(null), "");

        Console.WriteLine();
        Console.WriteLine("FindMatchInTranscript: the pilot names themselves");

        var punch = new FakePilot("Punch 1-1", "Punch", "Punch11-1");
        var enfield = new FakePilot("Enfield 2-1", "Enfield", "Enfield21-1");

        PilotNames.MatchResult<FakePilot> InTranscript(string transcript, params FakePilot[] units) =>
            PilotNames.FindMatchInTranscript(units, transcript, u => u.PlayerName, u => u.Callsign, u => u.Name);

        // The exact request from the report.
        var r2 = InTranscript("overlord active runway for punch 1-1", punch, enfield);
        Check("the written callsign in the transmission finds the pilot",
            r2.Match == punch && r2.Rule == PilotNames.MatchRule.SpokenInTranscript, $"{r2.Rule}: {r2.Explanation}");

        r2 = InTranscript("overlord active runway for punch one one", punch, enfield);
        Check("and so does the spoken form", r2.Match == punch, $"{r2.Rule}: {r2.Explanation}");

        r2 = InTranscript("OVERLORD, ACTIVE RUNWAY FOR PUNCH ONE ONE", punch, enfield);
        Check("capitalisation and punctuation don't matter", r2.Match == punch);

        r2 = InTranscript("overlord atis for enfield two one", punch, enfield);
        Check("the right pilot of two is picked", r2.Match == enfield, $"{r2.Rule}: {r2.Explanation}");

        // Must not guess.
        r2 = InTranscript("overlord active runway", punch, enfield);
        Check("nobody named means no match", r2.Match == null, r2.Explanation);

        r2 = InTranscript("overlord, punch one one and enfield two one are joining", punch, enfield);
        Check("two pilots named is refused rather than guessed",
            r2.Match == null && r2.Explanation.Contains("ambiguous"), r2.Explanation);

        var shortCallsign = new FakePilot("Ace", "Ace", "Ace-1");
        r2 = InTranscript("overlord the pace is fine", shortCallsign);
        Check("a callsign under five characters isn't matched inside other words", r2.Match == null, r2.Explanation);

        Check("an empty transcript matches nothing", InTranscript("", punch).Match == null);
        Check("no units matches nothing", InTranscript("punch one one").Match == null);

        // ============================================================================================
        // Log files - the bug where the log only seemed to fill up once the bot was stopped.
        // ============================================================================================

        Console.WriteLine();
        Console.WriteLine("LogFiles.PickNewest: by the name, not by the file system");

        Check("the timestamp is read out of the name",
            LogFiles.TryParseTimestamp("logs/darkstar_2026-09-26_14-32-05.log", out var stamp) &&
            stamp == new DateTime(2026, 9, 26, 14, 32, 5), stamp.ToString("O"));
        Check("a full Windows path works too",
            LogFiles.TryParseTimestamp(@"C:\Program Files\DARKSTAR\logs\darkstar_2026-01-02_03-04-05.log", out _));
        Check("an unrelated name has no timestamp",
            !LogFiles.TryParseTimestamp("logs/notes.log", out _));
        Check("a null path has no timestamp", !LogFiles.TryParseTimestamp(null, out _));

        var logDir = Path.Combine(Path.GetTempPath(), "logtest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(logDir);
        try
        {
            string MakeLog(string name, DateTime writeTime, string content = "x")
            {
                var path = Path.Combine(logDir, name);
                File.WriteAllText(path, content);
                File.SetLastWriteTimeUtc(path, writeTime);
                return path;
            }

            // THE ACTUAL BUG: the older file was touched more recently, because Windows does not update
            // the modification time of a file that is still open. Picking by write time chose the wrong one.
            var older = MakeLog("darkstar_2026-09-26_08-00-00.log", new DateTime(2026, 9, 26, 20, 0, 0, DateTimeKind.Utc));
            var newer = MakeLog("darkstar_2026-09-26_19-00-00.log", new DateTime(2026, 9, 26, 8, 0, 0, DateTimeKind.Utc));

            Check("the newest by NAME wins, even when the other was written more recently",
                LogFiles.PickNewest(new[] { older, newer }) == newer,
                Path.GetFileName(LogFiles.PickNewest(new[] { older, newer }) ?? ""));

            Check("order of the input doesn't matter",
                LogFiles.PickNewest(new[] { newer, older }) == newer);

            // A hand-kept copy must never outrank the live log, whatever its timestamp says.
            var renamed = MakeLog("keep-this-one.log", new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            Check("a renamed file never beats a properly named one",
                LogFiles.PickNewest(new[] { older, newer, renamed }) == newer,
                Path.GetFileName(LogFiles.PickNewest(new[] { older, newer, renamed }) ?? ""));

            Check("but it is still found when it's all there is",
                LogFiles.PickNewest(new[] { renamed }) == renamed);

            Check("no files gives null", LogFiles.PickNewest(Array.Empty<string>()) == null);
            Check("null gives null", LogFiles.PickNewest(null) == null);

            Console.WriteLine();
            Console.WriteLine("Reading a log while the bot still has it open");

            // The real test: write through Logger exactly as the bot does, and read it from "outside"
            // without closing anything. This is what used to appear empty until shutdown.
            var liveDir = Path.Combine(logDir, "live");
            Logger.Init(liveDir);

            Logger.Log("[STT] \"overlord bogey dope\"");
            Logger.Log("[Intel] 251.000 MHz: BogeyDope request");
            Logger.Flush();

            var liveFile = LogFiles.PickNewest(Directory.GetFiles(liveDir, "*.log"));
            Check("the live log file is found", liveFile != null, liveFile ?? "<null>");
            Check("Logger reports the same file it writes to",
                Logger.CurrentLogFile != null && Path.GetFullPath(Logger.CurrentLogFile) == Path.GetFullPath(liveFile!),
                Logger.CurrentLogFile ?? "<null>");

            var read = LogFiles.ReadAllLinesShared(liveFile!);
            Check("both lines are readable without the writer letting go",
                read.Any(l => l.Contains("[STT]")) && read.Any(l => l.Contains("[Intel]")),
                $"{read.Count} line(s)");

            // The point of forcing the data to disk: the size in the directory entry is right too, which
            // is what Explorer and anything sorting by time actually look at.
            Check("the reported file size is not zero while it is open",
                new FileInfo(liveFile!).Length > 0, $"{new FileInfo(liveFile!).Length} bytes");

            // A further line must show up on a later read, without any reopen dance.
            Logger.Log("[Reply] transmitted");
            Logger.Flush();
            Check("a line written afterwards shows up on the next read",
                LogFiles.ReadAllLinesShared(liveFile!).Any(l => l.Contains("[Reply]")));

            // And the sharing mode has to let a tail tool in - those ask for write sharing.
            var tailOpened = false;
            try
            {
                using var tail = new FileStream(liveFile!, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                tailOpened = true;
            }
            catch { }
            Check("a tail tool asking for write sharing can open it", tailOpened);

            Logger.Shutdown();
            Check("after shutdown the lines are still all there",
                LogFiles.ReadAllLinesShared(liveFile!).Count >= 4,
                $"{LogFiles.ReadAllLinesShared(liveFile!).Count} line(s)");
        }
        finally
        {
            try { Logger.Shutdown(); } catch { }
            try { Directory.Delete(logDir, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine("Verbose logging: kept out of the way, but not lost");

        var logDir2 = Path.Combine(Path.GetTempPath(), "logtest2-" + Guid.NewGuid().ToString("N"));
        try
        {
            Logger.Init(logDir2);
            Logger.DebugEnabled = false;

            Check("with debug off, the level check says so", !Logger.IsDebugEnabled);

            Logger.Debug("[SRS UDP] packet 1");
            Logger.Debug("[SRS UDP] packet 2");
            Logger.Log("Connected.");
            Logger.Flush();

            var file2 = LogFiles.PickNewest(Directory.GetFiles(logDir2, "*.log"))!;
            string Content() => string.Join("\n", LogFiles.ReadAllLinesShared(file2));

            Check("routine verbose lines stay out of the file", !Content().Contains("packet 1"));
            Check("ordinary messages are written as before", Content().Contains("Connected."));

            // ...but the moment something goes wrong, the run-up is handed over.
            Logger.Log("[Error] something broke");
            Logger.Flush();

            Check("an error brings the kept-back context with it",
                Content().Contains("packet 1") && Content().Contains("packet 2"));
            Check("and marks where it came from", Content().Contains("verbose line(s) before"));
            Check("the error itself is there too", Content().Contains("something broke"));

            // Handed over once, not repeated for every follow-up error.
            Logger.Log("[Error] second failure");
            Logger.Flush();
            Check("the same context is not dumped twice",
                Content().Split("verbose line(s) before").Length - 1 == 1,
                $"{Content().Split("verbose line(s) before").Length - 1} dump(s)");

            // With debug on, everything goes to the file as it always did.
            Logger.DebugEnabled = true;
            Check("with debug on, the level check says so too", Logger.IsDebugEnabled);
            Logger.Debug("[SRS UDP] packet 3");
            Logger.Flush();
            Check("verbose lines are written in full when asked for", Content().Contains("packet 3"));

            Logger.DebugEnabled = false;
            Logger.Shutdown();
        }
        finally
        {
            try { Logger.Shutdown(); } catch { }
            try { Directory.Delete(logDir2, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine("Wiring");

        const string DocRoot = "/tmp/claude-0/-home-claude/f56ec481-5bae-5544-bcb4-5bb5dd7c3a2c/scratchpad/Darkstar-Project/";

        var loggerSource = File.ReadAllText(DocRoot + "Darkstar.Core/Logger.cs");
        Check("the log is forced to disk, not just flushed to the OS cache",
            loggerSource.Contains("flushToDisk: true"));
        Check("and shared generously enough for tail tools",
            loggerSource.Contains("FileShare.ReadWrite | FileShare.Delete"));
        Check("the forced flush is throttled rather than per line",
            loggerSource.Contains("DiskFlushInterval"));

        var storeSource = File.ReadAllText(DocRoot + "Darkstar.Gui/ConfigStore.cs");
        Check("the config editor no longer picks the log by modification time",
            !storeSource.Contains("OrderByDescending(f => f.LastWriteTimeUtc)"));
        Check("it uses the shared helper instead", storeSource.Contains("LogFiles.PickNewest"));

        var panelSource = File.ReadAllText(DocRoot + "Darkstar.Gui/Pages/LoggingPanel.razor");
        Check("the log panel refreshes itself", panelSource.Contains("System.Timers.Timer") && panelSource.Contains("IDisposable"));
        Check("and names the file it is showing", panelSource.Contains("LOG FILE BEING SHOWN"));

        Console.WriteLine();
        Console.WriteLine("Performance and stability pass");

        var srsSource = File.ReadAllText(DocRoot + "SrsConnection.cs");
        Check("the per-packet logging checks the level before building the string",
            srsSource.Contains("if (Logger.IsDebugEnabled)"));
        Check("and the decoder is picked per sender",
            srsSource.Contains("OpusCodec.Decode(packet.OriginalClientGuid"));

        var opusSource = File.ReadAllText(DocRoot + "OpusCodec.cs");
        Check("there is no single shared decoder any more",
            !opusSource.Contains("static readonly IOpusDecoder Decoder"));
        Check("decoders are kept per client and capped",
            opusSource.Contains("ConcurrentDictionary<string, IOpusDecoder>") && opusSource.Contains("MaxDecoders"));

        var channelSource = File.ReadAllText(DocRoot + "Darkstar.Core/DcsGrpcChannels.cs");
        Check("gRPC channels are shared per address", channelSource.Contains("ConcurrentDictionary<string, GrpcChannel>"));
        foreach (var svc in new[] { "Darkstar.Core/DcsIntelService.cs", "Darkstar.Core/DcsAirfieldService.cs" })
            Check($"{Path.GetFileName(svc)} no longer opens a channel per request",
                !File.ReadAllText(DocRoot + svc).Contains("GrpcChannel.ForAddress"));

        var botSource2 = File.ReadAllText(DocRoot + "BotService.cs");
        Check("the async-void audio handler cannot take the process down",
            botSource2.Contains("audio handling failed"));
        Check("and the shared channels are closed on shutdown",
            botSource2.Contains("DcsGrpcChannels.DisposeAll()"));

        var filterSource = File.ReadAllText(DocRoot + "Darkstar.Core/AudioFrontEnd.cs");
        Check("the filter reuses its scratch buffer instead of allocating per frame",
            filterSource.Contains("private float[] _scratch"));

        Console.WriteLine();

        // Stands in for a DCS unit: the three name-bearing fields the real one has.
        // Declared last because top-level statements have to come first.
    }
}

sealed record FakePilot(string PlayerName, string Callsign, string Name);
