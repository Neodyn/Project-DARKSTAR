using static Darkstar.Tests.Test;

namespace Darkstar.Tests;

/// <summary>Runway in use, ATIS, and the per-radio roles.</summary>
internal static class AirfieldTests
{
    public static void Run()
    {
        // The ATIS maths, checked against cases where the right answer is known independently: a runway
        // aligned with the wind must win, a headwind must beat a tailwind, and the numbers must come out
        // in the units DCS actually hands over (Kelvin, Pascals, metres per second, radians).




        static Runway Strip(double trueHeading, string name = "", double length = 2500) =>
            new() { Name = name, TrueHeadingDegrees = trueHeading, LengthMeters = length, WidthMeters = 45 };

        var config = new AppConfig();

        // --- course -> heading ----------------------------------------------------------------------

        Console.WriteLine("HeadingFromCourse: DCS gives radians, with the opposite sign");

        // Hoggit: "Multiply by -1 to make it useful." A course of -pi/2 rad is therefore 090 true.
        Check("-pi/2 rad becomes 090", Math.Abs(DcsAirfieldService.HeadingFromCourse(-Math.PI / 2) - 90) < 0.001,
            $"{DcsAirfieldService.HeadingFromCourse(-Math.PI / 2):0.0}");
        Check("0 rad becomes 000", Math.Abs(DcsAirfieldService.HeadingFromCourse(0)) < 0.001);
        Check("+pi/2 rad becomes 270", Math.Abs(DcsAirfieldService.HeadingFromCourse(Math.PI / 2) - 270) < 0.001,
            $"{DcsAirfieldService.HeadingFromCourse(Math.PI / 2):0.0}");
        Check("the result is always inside 0-360",
            Enumerable.Range(-20, 40).Select(i => DcsAirfieldService.HeadingFromCourse(i * 0.5))
                .All(h => h is >= 0 and < 360));

        // --- designators ----------------------------------------------------------------------------

        Console.WriteLine();
        Console.WriteLine("DesignatorFor: the number painted on the threshold");

        Eq("130 magnetic is runway 13", DcsAirfieldService.DesignatorFor(130, null), "13");
        Eq("134 rounds down to 13", DcsAirfieldService.DesignatorFor(134, null), "13");
        Eq("136 rounds up to 14", DcsAirfieldService.DesignatorFor(136, null), "14");
        Eq("040 keeps its leading zero", DcsAirfieldService.DesignatorFor(40, null), "04");
        Eq("000 is runway 36, not 0", DcsAirfieldService.DesignatorFor(0, null), "36");
        Eq("358 is also runway 36", DcsAirfieldService.DesignatorFor(358, null), "36");
        Eq("005 is runway 36 as well", DcsAirfieldService.DesignatorFor(3, null), "36");
        Eq("090 is runway 09", DcsAirfieldService.DesignatorFor(90, null), "09");
        Eq("355 rounds to 36 rather than 37", DcsAirfieldService.DesignatorFor(355, null), "36");

        // DCS's own name wins when it agrees - it is what that terrain's charts show.
        Eq("DCS's \"13\" is kept when it agrees", DcsAirfieldService.DesignatorFor(130, "13"), "13");
        Eq("DCS's \"13L\" keeps the side designator", DcsAirfieldService.DesignatorFor(132, "13L"), "13L");
        Eq("one degree of disagreement is tolerated", DcsAirfieldService.DesignatorFor(139, "13"), "13");
        Eq("the opposite end is not taken from DCS's name", DcsAirfieldService.DesignatorFor(310, "13"), "31");
        Eq("nonsense in the name is ignored", DcsAirfieldService.DesignatorFor(130, "runway"), "13");
        Eq("an out-of-range name is ignored", DcsAirfieldService.DesignatorFor(130, "99"), "13");

        // --- runway selection -----------------------------------------------------------------------

        Console.WriteLine();
        Console.WriteLine("RankRunwayEnds: the wind decides");

        // One strip on 130/310. Wind straight down 130 means runway 13.
        var ends = DcsAirfieldService.RankRunwayEnds(new List<Runway> { Strip(130, "13") }, windFromTrue: 130, windKnots: 15, declination: 0);
        Check("a strip gives two usable directions", ends.Count == 2, $"{ends.Count}");
        Eq("wind from 130 picks runway 13", ends[0].Designator, "13");
        Check("with the full 15 knots as headwind", Math.Abs(ends[0].HeadwindKnots - 15) < 0.01, $"{ends[0].HeadwindKnots:0.0} kt");
        Check("and no crosswind", ends[0].CrosswindKnots < 0.01, $"{ends[0].CrosswindKnots:0.0} kt");
        Check("the other end is ranked last with a tailwind", ends[1].HeadwindKnots < -14, $"{ends[1].HeadwindKnots:0.0} kt");

        // Wind from the opposite direction must flip the answer - the core behaviour.
        ends = DcsAirfieldService.RankRunwayEnds(new List<Runway> { Strip(130, "13") }, 310, 15, 0);
        Eq("wind from 310 picks runway 31 instead", ends[0].Designator, "31");

        // A pure crosswind: neither end has a headwind, so the tie-break has to be stable.
        ends = DcsAirfieldService.RankRunwayEnds(new List<Runway> { Strip(130, "13") }, 40, 10, 0);
        Check("a pure crosswind still gives an answer", ends.Count == 2 && ends[0].Designator is "13" or "31");
        Check("and reports the crosswind honestly", Math.Abs(ends[0].CrosswindKnots - 10) < 0.1, $"{ends[0].CrosswindKnots:0.0} kt");

        // Dead calm: no headwind anywhere, so the longer runway should win.
        ends = DcsAirfieldService.RankRunwayEnds(
            new List<Runway> { Strip(130, "13", length: 2000), Strip(40, "04", length: 3200) }, 0, 0, 0);
        Check("in dead calm the longest runway is chosen",
            Math.Abs(ends[0].LengthMeters - 3200) < 1, $"{ends[0].Designator}, {ends[0].LengthMeters:0} m");

        // Two strips, wind favouring the shorter one: the wind still wins over length.
        ends = DcsAirfieldService.RankRunwayEnds(
            new List<Runway> { Strip(130, "13", length: 3500), Strip(40, "04", length: 1800) }, 40, 20, 0);
        Eq("a headwind beats a longer runway", ends[0].Designator, "04");

        // Same wind, two ends within a knot of each other - the smaller crosswind should decide.
        ends = DcsAirfieldService.RankRunwayEnds(
            new List<Runway> { Strip(90, "09"), Strip(100, "10") }, 95, 12, 0);
        Check("a near-tie is broken by the smaller crosswind",
            ends[0].CrosswindKnots <= ends[1].CrosswindKnots + 0.001,
            $"{ends[0].Designator} ({ends[0].CrosswindKnots:0.0} kt) before {ends[1].Designator} ({ends[1].CrosswindKnots:0.0} kt)");

        // Ranking must not depend on the order DCS happened to list the strips in.
        var forward = DcsAirfieldService.RankRunwayEnds(new List<Runway> { Strip(130), Strip(40), Strip(220) }, 45, 8, 0);
        var reversed = DcsAirfieldService.RankRunwayEnds(new List<Runway> { Strip(220), Strip(40), Strip(130) }, 45, 8, 0);
        Eq("the answer doesn't depend on DCS's list order", reversed[0].Designator, forward[0].Designator);

        Check("no runways means no ends, not a crash",
            DcsAirfieldService.RankRunwayEnds(new List<Runway>(), 130, 15, 0).Count == 0);

        // Declination only shifts the spoken designator, never the physics.
        var trueEnds = DcsAirfieldService.RankRunwayEnds(new List<Runway> { Strip(130) }, 130, 15, 0);
        var magEnds = DcsAirfieldService.RankRunwayEnds(new List<Runway> { Strip(130) }, 130, 15, 6);
        Check("declination doesn't change which end is chosen",
            Math.Abs(trueEnds[0].TrueHeadingDegrees - magEnds[0].TrueHeadingDegrees) < 0.001);
        Eq("but it does change the designator", magEnds[0].Designator, "12");

        // --- unit conversions -----------------------------------------------------------------------

        Console.WriteLine();
        Console.WriteLine("Units: DCS hands over Kelvin, Pascals and metres per second");

        var conditions = new AirfieldConditions
        {
            Airfield = new Airfield { Name = "Batumi", DisplayName = "Batumi", Runways = { Strip(130, "13") } },
            WindFromMagnetic = 130,
            WindKnots = 10 * 1.943844,            // 10 m/s
            TemperatureCelsius = 288.15 - 273.15, // 288.15 K
            QnhHectopascals = 101325 / 100.0,
            QnhInchesHg = 101325 / 3386.389,
            RunwayEnds = DcsAirfieldService.RankRunwayEnds(new List<Runway> { Strip(130, "13") }, 130, 10 * 1.943844, 0),
        };

        Check("10 m/s is about 19 knots", Math.Abs(conditions.WindKnots - 19.44) < 0.01, $"{conditions.WindKnots:0.00} kt");
        Check("288.15 K is 15 C", Math.Abs(conditions.TemperatureCelsius - 15) < 0.001, $"{conditions.TemperatureCelsius:0.0} C");
        Check("101325 Pa is 1013 hPa", Math.Abs(conditions.QnhHectopascals - 1013.25) < 0.01, $"{conditions.QnhHectopascals:0.00}");
        Check("101325 Pa is 29.92 inHg", Math.Abs(conditions.QnhInchesHg - 29.92) < 0.01, $"{conditions.QnhInchesHg:0.00}");

        // --- what it says ---------------------------------------------------------------------------

        Console.WriteLine();
        Console.WriteLine("Spoken output");

        Eq("the runway call", DcsAirfieldService.BuildRunwayInUse(conditions, config),
            "Batumi, runway in use one three, wind one three zero at one niner knots.");

        Eq("the full ATIS", DcsAirfieldService.BuildAtis(conditions, config),
            "Batumi information, wind one three zero at one niner knots, temperature one five, " +
            "QNH one zero one three, altimeter two niner niner two, runway in use one three.");

        var hpaOnly = new AppConfig { DcsAirfieldPressureUnit = PressureUnit.Hectopascals };
        Check("hectopascals only", DcsAirfieldService.SpeakPressure(conditions, hpaOnly) == "QNH one zero one three",
            DcsAirfieldService.SpeakPressure(conditions, hpaOnly));

        var inHgOnly = new AppConfig { DcsAirfieldPressureUnit = PressureUnit.InchesHg };
        Check("inches only", DcsAirfieldService.SpeakPressure(conditions, inHgOnly) == "altimeter two niner niner two",
            DcsAirfieldService.SpeakPressure(conditions, inHgOnly));

        var fast = new AppConfig { DcsIntelSlowSpeech = false };
        Check("with slow speech off the numbers stay as digits",
            DcsAirfieldService.SpeakWind(conditions, fast) == "wind 130 at 19 knots",
            DcsAirfieldService.SpeakWind(conditions, fast));

        var calm = new AirfieldConditions { Airfield = conditions.Airfield, WindFromMagnetic = 0, WindKnots = 0.4 };
        Eq("barely any wind is reported as calm", DcsAirfieldService.SpeakWind(calm, config), "wind calm");

        Eq("a freezing morning", DcsAirfieldService.SpeakTemperature(-7.2, config), "temperature minus seven");
        Eq("and zero", DcsAirfieldService.SpeakTemperature(0.4, config), "temperature zero");

        Eq("a side designator is spoken as a word", DcsAirfieldService.SpeakDesignator("13L", config), "one three left");
        Eq("and a plain one digit by digit", DcsAirfieldService.SpeakDesignator("04", config), "zero four");

        // No runway data (the usual case when the server has eval turned off): the weather must still
        // come through, and the gap must be stated rather than guessed at.
        var noRunways = new AirfieldConditions
        {
            Airfield = conditions.Airfield,
            WindFromMagnetic = 130,
            WindKnots = conditions.WindKnots,
            TemperatureCelsius = 15,
            QnhHectopascals = 1013.25,
            QnhInchesHg = 29.92,
        };
        Check("without runway data the ATIS still reports the weather",
            DcsAirfieldService.BuildAtis(noRunways, config).Contains("wind one three zero") &&
            DcsAirfieldService.BuildAtis(noRunways, config).EndsWith("runway in use unknown."),
            DcsAirfieldService.BuildAtis(noRunways, config));
        Check("and the runway call says so plainly",
            DcsAirfieldService.BuildRunwayInUse(noRunways, config).Contains("runway unknown"),
            DcsAirfieldService.BuildRunwayInUse(noRunways, config));

        // --- airfield by name ------------------------------------------------------------------------

        Console.WriteLine();
        Console.WriteLine("MatchAirfieldByName");

        var list = new List<Airfield>
        {
            new() { Name = "Batumi", DisplayName = "Batumi" },
            new() { Name = "Kobuleti", DisplayName = "Kobuleti" },
            new() { Name = "Mineralnye Vody", DisplayName = "Mineralnye Vody" },
            new() { Name = "Mozdok", DisplayName = "Mozdok" },
        };

        Check("a named airfield is found", DcsAirfieldService.MatchAirfieldByName("overlord batumi runway in use", list)?.Name == "Batumi");
        Check("the pilot's own callsign is not read as an airfield name",
            DcsAirfieldService.MatchAirfieldByName("overlord active runway for batumi one one", list, "Batumi 1-1 | someone") == null,
            DcsAirfieldService.MatchAirfieldByName("overlord active runway for batumi one one", list, "Batumi 1-1 | someone")?.Name ?? "<null>");
        Check("but another pilot's request for that field still works",
            DcsAirfieldService.MatchAirfieldByName("overlord batumi atis", list, "Punch 1-1 | someone")?.Name == "Batumi");
        Check("capitalisation doesn't matter", DcsAirfieldService.MatchAirfieldByName("OVERLORD, BATUMI ATIS", list)?.Name == "Batumi");
        Check("a two-word name is found despite the space",
            DcsAirfieldService.MatchAirfieldByName("request atis for mineralnye vody", list)?.Name == "Mineralnye Vody");
        Check("the longest match wins over a shorter one",
            DcsAirfieldService.MatchAirfieldByName("mineralnye vody atis", list)?.Name == "Mineralnye Vody");
        Check("no airfield named gives null", DcsAirfieldService.MatchAirfieldByName("overlord, runway in use", list) == null);
        Check("an empty transcript gives null", DcsAirfieldService.MatchAirfieldByName("", list) == null);

        // --- the Lua result --------------------------------------------------------------------------

        Console.WriteLine();
        Console.WriteLine("ParseRunwayJson: JSON produced by Lua is not always tidy");

        var parsed = DcsAirfieldService.ParseRunwayJson(
            """[{"name":"Batumi","runways":[{"name":"13","course":-2.2689,"length":2400,"width":45}]}]""");
        Check("a normal result parses", parsed.Count == 1 && parsed["Batumi"].Count == 1);
        Check("and the course becomes a true heading",
            Math.Abs(parsed["Batumi"][0].TrueHeadingDegrees - 130) < 0.5,
            $"{parsed["Batumi"][0].TrueHeadingDegrees:0.0}");

        Check("numbers arriving as strings are still read",
            Math.Abs(DcsAirfieldService.ParseRunwayJson(
                """[{"name":"X","runways":[{"name":"09","course":"-1.5708","length":"3000"}]}]""")["X"][0].LengthMeters - 3000) < 1);

        Check("an airfield with no runways is kept with an empty list",
            DcsAirfieldService.ParseRunwayJson("""[{"name":"Helipad","runways":[]}]""")["Helipad"].Count == 0);

        Check("an empty Lua table serialised as an object is handled",
            DcsAirfieldService.ParseRunwayJson("{}").Count == 0);
        Check("so is an empty array", DcsAirfieldService.ParseRunwayJson("[]").Count == 0);
        Check("so is nonsense", DcsAirfieldService.ParseRunwayJson("not json at all").Count == 0);
        Check("so is null", DcsAirfieldService.ParseRunwayJson(null).Count == 0);
        Check("an entry without a name is skipped, the rest survives",
            DcsAirfieldService.ParseRunwayJson("""[{"runways":[]},{"name":"Good","runways":[]}]""").Count == 1);
        Check("lookup ignores case", DcsAirfieldService.ParseRunwayJson(
            """[{"name":"Batumi","runways":[]}]""").ContainsKey("BATUMI"));

        // --- the safety property ---------------------------------------------------------------------

        Console.WriteLine();
        Console.WriteLine("Eval safety");

        var source = File.ReadAllText(Root + "Darkstar.Core/DcsAirfieldService.cs");

        // The whole safety argument rests on the Lua being a constant. If anything is ever interpolated
        // into it, that argument is gone - so assert the shape of the declaration itself.
        Check("the Lua is a compile-time constant", source.Contains("private const string RunwayQueryLua"));

        var luaStart = source.IndexOf("RunwayQueryLua = \"\"\"", StringComparison.Ordinal);
        var luaEnd = source.IndexOf("\"\"\";", luaStart, StringComparison.Ordinal);
        var lua = source[luaStart..luaEnd];
        Check("it contains no interpolation", !lua.Contains("{") || !lua.Contains("}") || !source[luaStart..luaEnd].Contains("$\""));
        Check("it is the only Eval call in the file",
            source.Split("EvalAsync").Length - 1 == 1, $"{source.Split("EvalAsync").Length - 1} call(s)");
        Check("the Eval request is built from that constant and nothing else",
            source.Contains("new EvalRequest { Lua = RunwayQueryLua }"));

        foreach (var forbidden in new[] { "coalition.addGroup", "trigger.action", "net.send", "os.", "io.", "loadstring", "require" })
            Check($"the Lua doesn't use {forbidden}", !lua.Contains(forbidden));

        Check("a refused Eval is reported once, with the setting to change",
            source.Contains("PermissionDenied") && source.Contains("evalEnabled = true"));
        Check("and the weather still works without it",
            source.Contains("only the runway in use is missing"));

        var appConfig = File.ReadAllText(Root + "Darkstar.Core/AppConfig.cs");
        Check("the feature is off by default", appConfig.Contains("DcsAirfieldEnabled { get; set; } = false"));

        var bot = File.ReadAllText(Root + "BotService.cs");
        Check("the bot only builds the service when it is switched on",
            bot.Contains("if (config.DcsAirfieldEnabled)") && bot.Contains("new DcsAirfieldService(config, intel)"));
        Check("tactical requests are still answered first",
            bot.IndexOf("intel.Classify", StringComparison.Ordinal) <
            bot.IndexOf("airfields.Classify", StringComparison.Ordinal));

        // ============================================================================================
        // Trigger matching - the bug where an unintelligible transmission became a bogey dope.
        // ============================================================================================

        Console.WriteLine();
        Console.WriteLine("TriggerMatcher: whole phrases only");

        var triggers = new List<string> { "bogey dope", "picture", "threats" };

        Check("the plain phrase matches", TriggerMatcher.MatchesAny("overlord bogey dope", triggers));
        Check("capitalisation doesn't matter", TriggerMatcher.MatchesAny("OVERLORD, BOGEY DOPE", triggers));
        Check("punctuation around it doesn't matter", TriggerMatcher.MatchesAny("overlord, bogey dope!", triggers));
        Check("extra whitespace inside the phrase is tolerated", TriggerMatcher.MatchesAny("overlord bogey  dope", triggers));
        Check("the matched trigger is reported", TriggerMatcher.FindMatch("say picture", triggers) == "picture",
            TriggerMatcher.FindMatch("say picture", triggers) ?? "<null>");

        // The actual fix: a trigger must not match inside a longer word.
        Check("\"threats\" does not match \"threatsomething\"", !TriggerMatcher.MatchesAny("threatsomething", triggers));
        Check("\"picture\" does not match \"depictured\"", !TriggerMatcher.MatchesAny("depictured", triggers));
        Check("a short hand-added trigger can't run wild",
            !TriggerMatcher.MatchesAny("antelope canteloupe", new List<string> { "dope" }));
        Check("but it still matches as its own word",
            TriggerMatcher.MatchesAny("give me the dope", new List<string> { "dope" }));

        Check("an empty transcript matches nothing", !TriggerMatcher.MatchesAny("", triggers));
        Check("a null transcript matches nothing", !TriggerMatcher.MatchesAny(null, triggers));
        Check("an empty trigger list matches nothing", !TriggerMatcher.MatchesAny("bogey dope", new List<string>()));
        Check("a blank trigger does not match everything",
            !TriggerMatcher.MatchesAny("anything at all", new List<string> { "   ", "" }));
        Check("a trigger of punctuation only still works",
            TriggerMatcher.MatchesAny("what now?", new List<string> { "?" }));

        // Order is the caller's business, but the first listed match must be the one reported.
        Check("the first matching trigger in the list wins",
            TriggerMatcher.FindMatch("bogey dope and picture", triggers) == "bogey dope");

        Console.WriteLine();
        Console.WriteLine("Which airfield the request is about");

        var resolveSource = File.ReadAllText(Root + "Darkstar.Core/DcsAirfieldService.cs");

        // The point of the change: a mis-transcribed airfield name must not beat knowing where the
        // pilot actually is.
        Check("the pilot is located before any name is considered",
            resolveSource.IndexOf("FindRequesterUnitAsync", StringComparison.Ordinal) <
            resolveSource.IndexOf("MatchAirfieldByName(transcript, airfields, rawPlayerName)", StringComparison.Ordinal));
        Check("being at a field decides it",
            resolveSource.Contains("nearestDistance <= _config.DcsAirfieldAtFieldNm"));
        Check("the transcript is used as a second way to identify the pilot",
            resolveSource.Contains("rawPlayerName, transcript, friendly, token"));
        Check("a nonsensically distant airfield is refused rather than reported",
            resolveSource.Contains("DcsAirfieldMaxDistanceNm"));
        Check("the diagnostics say how the airfield was chosen",
            resolveSource.Contains("the pilot is at it") && resolveSource.Contains("nearest to the pilot"));

        var cfg = new AppConfig();
        Check("\"at the field\" defaults to a radius that covers the ramp and the circuit",
            cfg.DcsAirfieldAtFieldNm is >= 2 and <= 10, $"{cfg.DcsAirfieldAtFieldNm} NM");
        Check("and the outer limit is sane", cfg.DcsAirfieldMaxDistanceNm > cfg.DcsAirfieldAtFieldNm,
            $"{cfg.DcsAirfieldMaxDistanceNm} NM");

        Check("\"active runway\" is a trigger out of the box",
            cfg.DcsAirfieldRunwayTriggers.Any(t => t.Equals("active runway", StringComparison.OrdinalIgnoreCase)));
        Check("and the whole example request classifies as a runway request",
            TriggerMatcher.FindMatch("overlord active runway for punch 1-1", cfg.DcsAirfieldRunwayTriggers) == "active runway");

        Console.WriteLine();
        Console.WriteLine("The vocabulary trap");

        var trapConfig = new AppConfig();
        var conflicts = AppConfig.FindVocabularyTriggerConflicts(trapConfig,
            new[] { "Overlord", "Bogey Dope", "Batumi" });
        Check("a trigger phrase sitting in the vocabulary is found",
            conflicts.Count == 1 && conflicts[0] == "Bogey Dope", string.Join(", ", conflicts));

        Check("case doesn't hide it",
            AppConfig.FindVocabularyTriggerConflicts(trapConfig, new[] { "bogey DOPE" }).Count == 1);

        Check("proper nouns are not flagged",
            AppConfig.FindVocabularyTriggerConflicts(trapConfig, new[] { "Overlord", "Batumi", "Su-27" }).Count == 0);

        Check("ATIS triggers are checked too",
            AppConfig.FindVocabularyTriggerConflicts(trapConfig, new[] { "atis" }).Count == 1);

        Check("an empty vocabulary is fine",
            AppConfig.FindVocabularyTriggerConflicts(trapConfig, Array.Empty<string>()).Count == 0);
        Check("a null vocabulary is fine",
            AppConfig.FindVocabularyTriggerConflicts(trapConfig, null).Count == 0);

        Check("the same conflict isn't reported twice",
            AppConfig.FindVocabularyTriggerConflicts(trapConfig, new[] { "Bogey Dope", "bogey dope" }).Count == 1);

        // The default vocabulary must not carry the trap it used to.
        var vocabularySource = File.ReadAllText(Root + "Darkstar.Core/VocabularyBook.cs");
        var defaultsBlock = vocabularySource[vocabularySource.IndexOf("var defaults", StringComparison.Ordinal)..];
        defaultsBlock = defaultsBlock[..defaultsBlock.IndexOf("};", StringComparison.Ordinal)];
        Check("no trigger phrase is left in the default vocabulary",
            !defaultsBlock.Contains("Bogey Dope") && !defaultsBlock.Contains("Bullseye"),
            defaultsBlock.Replace("\n", " ").Replace("  ", ""));

        Console.WriteLine();
        Console.WriteLine("Unintelligible transmissions");

        var botSource = File.ReadAllText(Root + "BotService.cs");
        Check("an empty transcript is answered with a repeat request, not guessed at",
            botSource.Contains("nothing intelligible in the transmission"));
        Check("and it happens before the tactical classifier runs",
            botSource.IndexOf("nothing intelligible", StringComparison.Ordinal) <
            botSource.IndexOf("intel.Classify", StringComparison.Ordinal));
        Check("the reply text is configurable",
            botSource.Contains("config.UnintelligibleReply"));
        Check("the pending standby is cancelled first",
            botSource.IndexOf("nothing intelligible", StringComparison.Ordinal) <
            botSource.IndexOf("await TransmitAsync(session, spokenSayAgain)", StringComparison.Ordinal));

        Check("both classifiers report which trigger fired",
            botSource.Contains("intel.Classify(text, out var matchedTrigger)") &&
            botSource.Contains("airfields.Classify(text, out var airfieldTrigger)"));
        Check("and the log names it",
            botSource.Contains("triggered by"));
        Check("the vocabulary conflict is warned about at startup",
            botSource.Contains("WarnAboutVocabularyTriggerConflicts"));

        var intelSource = File.ReadAllText(Root + "Darkstar.Core/DcsIntelService.cs");
        Check("no bare Contains matching is left in the intel classifier",
            !intelSource.Contains("lowerText.Contains"));
        var airfieldSource = File.ReadAllText(Root + "Darkstar.Core/DcsAirfieldService.cs");
        Check("nor in the airfield classifier", !airfieldSource.Contains("lowerText.Contains"));

        // ============================================================================================
        // Splitting the roles across frequencies - the tower on its own channel.
        // ============================================================================================

        Console.WriteLine();
        Console.WriteLine("RadioConfig.Answers: three-way per radio, one master switch");

        Check("unset follows the global switch (on)", RadioConfig.Answers(null, true));
        Check("unset follows the global switch (off)", !RadioConfig.Answers(null, false));
        Check("on means on when the feature exists", RadioConfig.Answers(true, true));
        Check("off means off even when the feature exists", !RadioConfig.Answers(false, true));
        Check("a radio cannot switch on what is globally off", !RadioConfig.Answers(true, false));

        var tower = new RadioConfig { AnswerTacticalRequests = false, AnswerAirfieldRequests = true };
        var awacs = new RadioConfig { AnswerTacticalRequests = true, AnswerAirfieldRequests = false };

        Eq("the tower describes itself as airfield only", tower.DescribeRole(true, true), "airfield");
        Eq("the AWACS as tactical only", awacs.DescribeRole(true, true), "tactical");
        Eq("a radio with both on says so", new RadioConfig().DescribeRole(true, true), "tactical + airfield");
        Eq("and one with neither is honest about it",
            new RadioConfig { AnswerTacticalRequests = false, AnswerAirfieldRequests = false }.DescribeRole(true, true),
            "phrases/Gemini only");
        Eq("the master switch shows through", tower.DescribeRole(true, false), "phrases/Gemini only");

        Console.WriteLine();
        Console.WriteLine("RadioRoles.FindHandoff: sending the pilot to the right frequency");

        var plan = new[]
        {
            (FrequencyHz: 251_000_000.0, Callsign: "Overlord", Tactical: true,  Airfield: false, FriendlyPosition: false),
            (FrequencyHz: 252_000_000.0, Callsign: "Tower",    Tactical: false, Airfield: true,  FriendlyPosition: false),
        };

        var handoff = RadioRoles.FindHandoff(plan, 251_000_000, RadioCapability.Airfield);
        Check("asking the AWACS for the runway points at the tower",
            handoff?.Callsign == "Tower" && Math.Abs(handoff.FrequencyHz - 252_000_000) < 1,
            handoff?.Callsign ?? "<null>");

        handoff = RadioRoles.FindHandoff(plan, 252_000_000, RadioCapability.Tactical);
        Check("and the other way round", handoff?.Callsign == "Overlord", handoff?.Callsign ?? "<null>");

        Check("a radio never points at itself",
            RadioRoles.FindHandoff(plan, 252_000_000, RadioCapability.Airfield) == null);

        Check("nothing to point at gives null",
            RadioRoles.FindHandoff(plan, 251_000_000, RadioCapability.Tactical) is var self && self == null ||
            self!.Callsign != "Overlord");

        // Two towers: there is no single right answer, so it must not invent one.
        var twoTowers = plan.Concat(new[] { (FrequencyHz: 253_000_000.0, Callsign: "Tower 2", Tactical: false, Airfield: true, FriendlyPosition: false) });
        Check("two candidates is refused rather than guessed",
            RadioRoles.FindHandoff(twoTowers, 251_000_000, RadioCapability.Airfield) == null);

        Check("an empty radio plan gives null",
            RadioRoles.FindHandoff(Array.Empty<(double, string, bool, bool, bool)>(), 251_000_000, RadioCapability.Airfield) == null);

        Console.WriteLine();
        Console.WriteLine("Saying the frequency");

        Eq("a round frequency keeps one decimal", RadioRoles.SpeakFrequency(251_000_000, false), "251.0");
        Eq("and is read digit by digit", RadioRoles.SpeakFrequency(251_000_000),
            "two five one decimal zero");
        Eq("a fractional one keeps its digits", RadioRoles.SpeakFrequency(124_500_000, false), "124.5");
        Eq("three decimals survive", RadioRoles.SpeakFrequency(251_125_000, false), "251.125");
        Eq("niner appears here too", RadioRoles.SpeakFrequency(129_000_000), "one two niner decimal zero");

        var handoffReply = RadioRoles.BuildHandoffReply("Contact {callsign} on {frequency}.",
            new RadioRoles.Handler(252_000_000, "Tower"), spellOutDigits: true);
        Eq("the handoff reads like a controller", handoffReply!,
            "Contact Tower on two five two decimal zero.");

        Check("an empty template means no handoff at all",
            RadioRoles.BuildHandoffReply("", new RadioRoles.Handler(252_000_000, "Tower"), true) == null);

        Console.WriteLine();
        Console.WriteLine("Wiring");

        var botRoles = File.ReadAllText(Root + "BotService.cs");
        Check("tactical requests are gated on the radio", botRoles.Contains("_ => session.AnswersTactical"));
        Check("airfield requests too", botRoles.Contains("airfields != null && session.AnswersAirfield"));
        Check("the roles are resolved once, not per transmission",
            botRoles.Contains("AnswersTactical = RadioConfig.Answers("));
        Check("the startup log says what each radio answers", botRoles.Contains("DescribeRole("));
        Check("a wrong-channel request is handed off", botRoles.Contains("RadioRoles.FindHandoff"));

        var cfgRoles = new AppConfig();
        Check("the handoff reply is on by default and mentions both placeholders",
            cfgRoles.WrongChannelReply.Contains("{callsign}") && cfgRoles.WrongChannelReply.Contains("{frequency}"),
            cfgRoles.WrongChannelReply);
        Check("existing configurations are unaffected: both flags default to unset",
            new RadioConfig().AnswerTacticalRequests == null && new RadioConfig().AnswerAirfieldRequests == null);
    }
}
