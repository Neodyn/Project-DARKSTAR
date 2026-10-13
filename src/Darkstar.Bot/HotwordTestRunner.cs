using Vosk;

namespace Darkstar;

/// <summary>
/// Runs recorded audio through the real wake word detector and reports what happened, so a change
/// to the audio path or the model can be judged on numbers instead of on impressions.
///
/// Invoked as: Darkstar.exe --test-hotword &lt;file-or-folder&gt; [options]
///
/// Files saved by SaveRecordings are named "..._hit_..." and "..._missed_...", which is where the
/// expected outcome comes from when it isn't given explicitly: a "hit" file should trigger, a
/// "missed" file is one that didn't trigger in the air and usually should have. That makes
/// "did this change help?" a question the tool can answer by itself.
/// </summary>
public static class HotwordTestRunner
{
    private const int FrameSamples = 960; // 20 ms at 48 kHz, the same size SRS delivers

    private sealed class Options
    {
        public string Path = "";
        public string? Keyword;
        public string? ModelPath;
        public bool Compare;
        public bool AutoGain;
        public HotwordAudioFilter Filter = HotwordAudioFilter.LowPass;
        public bool Verbose;
        public bool SuggestVariants;

        /// <summary>Accepted spellings besides the wake word, from config.json unless overridden.</summary>
        public List<string>? Variants;
    }

    private sealed record FileResult(string Name, bool? Expected, bool Detected, double DetectedAtSeconds, string Transcript);

    /// <summary>True if these command-line arguments ask for the test runner.</summary>
    public static bool IsRequested(string[] args) =>
        args.Any(a => a.Equals("--test-hotword", StringComparison.OrdinalIgnoreCase));

    /// <summary>Runs the test and returns a process exit code (0 = every expectation met).</summary>
    public static int Run(string[] args)
    {
        Options options;
        try
        {
            options = ParseArgs(args);
        }
        catch (ArgumentException ex)
        {
            Console.WriteLine($"ERROR: {ex.Message}");
            Console.WriteLine();
            PrintUsage();
            return 1;
        }

        // The config next to the executable supplies whatever wasn't given on the command line,
        // so in the normal case "--test-hotword recordings" is all that's needed.
        var configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        AppConfig? config = null;
        if (File.Exists(configPath))
        {
            try { config = AppConfig.LoadReadOnly(configPath); }
            catch (Exception ex) { Console.WriteLine($"NOTE: config.json could not be read ({ex.Message}) - command-line values only."); }
        }

        var keyword = options.Keyword ?? config?.VoskKeyword;
        var modelPath = options.ModelPath ?? config?.VoskModelPath;
        var variants = options.Variants ?? config?.VoskKeywordVariants;

        if (string.IsNullOrWhiteSpace(keyword))
        {
            Console.WriteLine("ERROR: no wake word. Pass --keyword, or put one in config.json as VoskKeyword.");
            return 1;
        }

        var check = VoskModelCheck.Check(modelPath);
        if (!check.IsUsable)
        {
            Console.WriteLine($"ERROR: {check.Message}");
            foreach (var hint in check.Hints)
                Console.WriteLine($"  - {hint}");
            return 1;
        }

        var files = CollectFiles(options.Path);
        if (files.Count == 0)
        {
            Console.WriteLine($"ERROR: no .wav files found at '{options.Path}'.");
            return 1;
        }

        var accepted = HotwordVariants.Resolve(keyword, variants);

        Console.WriteLine($"Wake word : \"{keyword}\"");
        if (accepted.Count > 1)
            Console.WriteLine($"Variants  : {string.Join(", ", accepted.Skip(1).Select(p => $"\"{p}\""))}");
        Console.WriteLine($"Model     : {check.ResolvedPath}");
        Console.WriteLine($"Files     : {files.Count}");
        Console.WriteLine();

        Model model;
        try
        {
            model = VoskHotwordDetector.LoadModel(modelPath!);
        }
        catch (VoskModelLoadException ex)
        {
            Console.WriteLine($"ERROR: {ex.Message}");
            foreach (var hint in ex.Hints) Console.WriteLine($"  - {hint}");
            return 1;
        }

        try
        {
            if (options.Compare)
                return RunComparison(model, keyword!, files, options);

            var results = files
                .Select(f => RunOneFile(model, keyword!, f, options.Filter, options.AutoGain, variants))
                .ToList();

            PrintTable(results, options.Verbose);
            var allAsExpected = Summarise(results, options.Filter.ToString());

            if (options.SuggestVariants)
            {
                PrintSuggestions(results, keyword!, accepted);

                // Always 0: this mode exists to look at the misses, so having some is the normal
                // case and not a failure of the run.
                return 0;
            }

            return allAsExpected ? 0 : 2;
        }
        finally
        {
            model.Dispose();
        }
    }

    /// <summary>
    /// Runs every file through both audio paths and prints them side by side. This is the mode
    /// that answers whether the filter actually helps on this machine's own traffic.
    /// </summary>
    private static int RunComparison(Model model, string keyword, List<string> files, Options options)
    {
        var lowPass = files.Select(f => RunOneFile(model, keyword, f, HotwordAudioFilter.LowPass, options.AutoGain, options.Variants)).ToList();
        var average = files.Select(f => RunOneFile(model, keyword, f, HotwordAudioFilter.Average, options.AutoGain, options.Variants)).ToList();

        Console.WriteLine($"{"file",-46} {"expected",-9} {"Average",-9} {"LowPass",-9}");
        Console.WriteLine(new string('-', 78));

        for (int i = 0; i < files.Count; i++)
        {
            var expected = lowPass[i].Expected switch { true => "trigger", false => "silence", _ => "-" };
            Console.WriteLine($"{Shorten(lowPass[i].Name, 46),-46} {expected,-9} " +
                              $"{Outcome(average[i]),-9} {Outcome(lowPass[i]),-9}");

            if (options.Verbose)
            {
                Console.WriteLine($"{"",-46} Average transcript: {average[i].Transcript}");
                Console.WriteLine($"{"",-46} LowPass transcript: {lowPass[i].Transcript}");
            }
        }

        Console.WriteLine();
        var averageOk = Summarise(average, "Average");
        var lowPassOk = Summarise(lowPass, "LowPass");

        int averageCorrect = average.Count(r => r.Expected == null || r.Expected == r.Detected);
        int lowPassCorrect = lowPass.Count(r => r.Expected == null || r.Expected == r.Detected);

        Console.WriteLine();
        if (lowPassCorrect > averageCorrect)
            Console.WriteLine($"LowPass is better on this material: {lowPassCorrect} vs {averageCorrect} of {files.Count} as expected.");
        else if (lowPassCorrect < averageCorrect)
            Console.WriteLine($"Average is better on this material: {averageCorrect} vs {lowPassCorrect} of {files.Count}. " +
                              "Worth reporting - that is not what the filter measurements predict.");
        else
            Console.WriteLine($"Both paths agree on this material ({lowPassCorrect} of {files.Count} as expected). " +
                              "A larger or harder set of recordings would be needed to tell them apart.");

        return lowPassOk || averageOk ? 0 : 2;
    }

    private static FileResult RunOneFile(Model model, string keyword, string file,
        HotwordAudioFilter filter, bool autoGain, IEnumerable<string>? variants = null)
    {
        var name = Path.GetFileName(file);

        WavUtils.WavAudio wav;
        try
        {
            wav = WavUtils.ReadPcm16(file);
        }
        catch (Exception ex)
        {
            return new FileResult(name, null, false, 0, $"(could not be read: {ex.Message})");
        }

        var pcm = WavUtils.ToMono(wav.Pcm16, wav.Channels);

        if (wav.SampleRate != DecimatingLowPass.InputSampleRate)
            return new FileResult(name, ExpectationFromName(name), false, 0,
                $"(needs {DecimatingLowPass.InputSampleRate} Hz, file is {wav.SampleRate} Hz)");

        using var detector = new VoskHotwordDetector(model, keyword, filter, autoGain, variants);

        double detectedAt = 0;
        bool detected = false;

        // Feed it exactly as the live path does: 20 ms frames, in order, one detector per stream.
        for (int offset = 0; offset + 2 <= pcm.Length; offset += FrameSamples * 2)
        {
            int length = Math.Min(FrameSamples * 2, pcm.Length - offset);
            var frame = pcm.AsSpan(offset, length).ToArray();

            if (detector.ProcessAudio(frame))
            {
                detected = true;
                detectedAt = offset / 2.0 / DecimatingLowPass.InputSampleRate;
                break;
            }
        }

        return new FileResult(name, ExpectationFromName(name), detected, detectedAt, detector.LastText);
    }

    /// <summary>
    /// What a file is expected to do, taken from the tag SaveRecordings puts in its name.
    /// Null for anything else, which is then reported but not counted as right or wrong.
    /// </summary>
    private static bool? ExpectationFromName(string name)
    {
        if (name.Contains("_hit_", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Contains("_missed_", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Contains("_silence_", StringComparison.OrdinalIgnoreCase)) return false;
        return null;
    }

    private static void PrintTable(List<FileResult> results, bool verbose)
    {
        Console.WriteLine($"{"file",-46} {"expected",-9} {"result",-9} {"at",-7}");
        Console.WriteLine(new string('-', 74));

        foreach (var r in results)
        {
            var expected = r.Expected switch { true => "trigger", false => "silence", _ => "-" };
            var at = r.Detected ? $"{r.DetectedAtSeconds,5:F2}s" : "";
            Console.WriteLine($"{Shorten(r.Name, 46),-46} {expected,-9} {Outcome(r),-9} {at,-7}");

            if (verbose && !string.IsNullOrWhiteSpace(r.Transcript))
                Console.WriteLine($"{"",-46} heard: {r.Transcript}");
        }
    }

    /// <summary>Prints the counts and returns whether every stated expectation was met.</summary>
    private static bool Summarise(List<FileResult> results, string label)
    {
        int withExpectation = results.Count(r => r.Expected != null);
        int correct = results.Count(r => r.Expected != null && r.Expected == r.Detected);
        int falseNegatives = results.Count(r => r.Expected == true && !r.Detected);
        int falsePositives = results.Count(r => r.Expected == false && r.Detected);

        if (withExpectation == 0)
        {
            Console.WriteLine($"{label}: {results.Count(r => r.Detected)} of {results.Count} files triggered " +
                              "(no expectations in the file names, so nothing to compare against).");
            return true;
        }

        var rate = 100.0 * correct / withExpectation;
        Console.WriteLine($"{label}: {correct} of {withExpectation} as expected ({rate:F0}%), " +
                          $"{falseNegatives} missed, {falsePositives} fired when they shouldn't.");

        return correct == withExpectation;
    }

    private static string Outcome(FileResult r)
    {
        if (r.Transcript.StartsWith('(')) return "skipped";
        if (r.Expected == null) return r.Detected ? "triggered" : "silent";
        return r.Expected == r.Detected ? (r.Detected ? "ok" : "ok (quiet)") : (r.Detected ? "FALSE FIRE" : "MISSED");
    }

    private static string Shorten(string s, int max) =>
        s.Length <= max ? s : "..." + s[^(max - 3)..];

    private static List<string> CollectFiles(string path)
    {
        if (File.Exists(path))
            return new List<string> { path };

        if (Directory.Exists(path))
            return Directory.GetFiles(path, "*.wav", SearchOption.TopDirectoryOnly).OrderBy(f => f).ToList();

        return new List<string>();
    }

    private static Options ParseArgs(string[] args)
    {
        var options = new Options();

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            switch (arg.ToLowerInvariant())
            {
                case "--test-hotword":
                    // The value may be attached to the switch or follow it.
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                        options.Path = args[++i];
                    break;

                case "--keyword":
                    options.Keyword = Next(args, ref i, "--keyword");
                    break;

                case "--model":
                    options.ModelPath = Next(args, ref i, "--model");
                    break;

                case "--filter":
                    var value = Next(args, ref i, "--filter");
                    if (!Enum.TryParse<HotwordAudioFilter>(value, ignoreCase: true, out var filter))
                        throw new ArgumentException($"--filter must be LowPass or Average, not '{value}'.");
                    options.Filter = filter;
                    break;

                case "--compare":
                    options.Compare = true;
                    break;

                case "--autogain":
                    options.AutoGain = true;
                    break;

                case "--verbose":
                    options.Verbose = true;
                    break;

                case "--suggest-variants":
                    options.SuggestVariants = true;
                    options.Verbose = true; // The transcripts are the evidence - always show them here.
                    break;

                case "--variants":
                    options.Variants = Next(args, ref i, "--variants")
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .ToList();
                    break;

                default:
                    if (arg.StartsWith("--"))
                        throw new ArgumentException($"Unknown option '{arg}'.");
                    if (options.Path == "")
                        options.Path = arg;
                    break;
            }
        }

        if (options.Path == "")
            options.Path = Path.Combine(AppContext.BaseDirectory, "recordings");

        return options;
    }

    private static string Next(string[] args, ref int i, string option)
    {
        if (i + 1 >= args.Length)
            throw new ArgumentException($"{option} needs a value.");
        return args[++i];
    }

    /// <summary>
    /// Reads the variant list off the recordings the detector missed: what the model produced on
    /// transmissions where the pilot did say the wake word is exactly the set of spellings worth
    /// accepting. Candidates that also turn up on recordings where nobody called are listed
    /// separately rather than recommended, because accepting those buys hits with false triggers.
    /// </summary>
    private static void PrintSuggestions(List<FileResult> results, string keyword, List<string> accepted)
    {
        // Only files that were supposed to trigger and didn't. A file that already triggers needs
        // no new spelling, and one with no expectation in its name says nothing either way.
        var missed = results
            .Where(r => r.Expected == true && !r.Detected && !r.Transcript.StartsWith('('))
            .Select(r => r.Transcript)
            .ToList();

        var unwanted = results
            .Where(r => r.Expected == false && !r.Transcript.StartsWith('('))
            .Select(r => r.Transcript)
            .ToList();

        Console.WriteLine();
        Console.WriteLine($"Variant suggestions from {missed.Count} missed recording(s)" +
                          (unwanted.Count > 0 ? $", checked against {unwanted.Count} recording(s) where nobody called" : "") + ":");
        Console.WriteLine();

        if (missed.Count == 0)
        {
            Console.WriteLine("  Nothing was missed, so there is nothing to add. Every variant accepted also");
            Console.WriteLine("  raises the false-trigger rate, so leave the list as it is.");
            return;
        }

        var suggestions = HotwordVariants.Suggest(keyword, missed, unwanted, accepted);

        if (suggestions.Count == 0)
        {
            Console.WriteLine($"  Nothing in those recordings resembles \"{keyword}\" closely enough to propose.");
            Console.WriteLine("  The transcripts above show what the model did hear - if the wake word isn't in");
            Console.WriteLine("  them at all, a bigger model is the fix, not a variant (see the manual's chapter");
            Console.WriteLine("  on wake word accuracy).");
            return;
        }

        Console.WriteLine($"  {"phrase",-28} {"missed files",-13} {"edits",-6} note");
        Console.WriteLine("  " + new string('-', 74));

        foreach (var suggestion in suggestions)
        {
            var note = suggestion.AlsoWhenNobodyCalled
                ? "ALSO heard when nobody called - would cause false triggers"
                : "";
            Console.WriteLine($"  {Shorten(suggestion.Phrase, 28),-28} {suggestion.Count,-13} {suggestion.Distance,-6} {note}");
        }

        var recommended = suggestions.Where(s => s.Recommended).Select(s => s.Phrase).ToList();
        if (recommended.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("  Every candidate also appeared when nobody called, so none is recommended.");
            return;
        }

        Console.WriteLine();
        Console.WriteLine("  For config.json (add to the radio's KeywordVariants, or VoskKeywordVariants globally):");
        Console.WriteLine();
        Console.WriteLine($"    \"VoskKeywordVariants\": [{string.Join(", ", recommended.Select(p => $"\"{p}\""))}]");
        Console.WriteLine();
        Console.WriteLine("  Then run this again with the new list: the same recordings should now be hits, and");
        Console.WriteLine("  the _silence_ files should still be quiet. That second number is the one that");
        Console.WriteLine("  decides whether a variant was worth it.");
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: Darkstar.exe --test-hotword [file-or-folder] [options]");
        Console.WriteLine();
        Console.WriteLine("Runs recorded 48 kHz mono WAV files through the real wake word detector.");
        Console.WriteLine("Without a path, the \"recordings\" folder next to the executable is used");
        Console.WriteLine("(set SaveRecordings in config.json to have the bot fill it from live traffic).");
        Console.WriteLine();
        Console.WriteLine("  --keyword <word>      wake word to look for (default: VoskKeyword from config.json)");
        Console.WriteLine("  --model <folder>      Vosk model to use (default: VoskModelPath from config.json)");
        Console.WriteLine("  --filter <name>       LowPass (default) or Average (the old audio path)");
        Console.WriteLine("  --compare             run both audio paths and print them side by side");
        Console.WriteLine("  --autogain            also apply the optional automatic gain");
        Console.WriteLine("  --verbose             print what Vosk actually transcribed");
        Console.WriteLine("  --variants <a,b,c>    extra spellings to accept (default: VoskKeywordVariants)");
        Console.WriteLine("  --suggest-variants    propose extra spellings from what the model heard on the");
        Console.WriteLine("                        recordings it missed - for pilots whose accent the model");
        Console.WriteLine("                        mangles. Implies --verbose and always exits 0.");
        Console.WriteLine();
        Console.WriteLine("Files whose names contain _hit_ or _missed_ are expected to trigger,");
        Console.WriteLine("_silence_ is expected not to. Exit code 0 means every expectation was met.");
    }
}
