using static Darkstar.Tests.Test;

namespace Darkstar.Tests;

/// <summary>
/// Checking a Whisper model before the native library gets it.
///
/// Every case here is driven through the injected file system, so the suite needs no model on
/// disk - which matters, because the smallest real one is 75 MB and the interesting ones are
/// half a gigabyte. The failures being checked are the ones that actually happen: a path that is
/// a folder, a download that stopped halfway, and an error page saved under a .bin name.
/// </summary>
internal static class WhisperModelTests
{
    public static void Run()
    {
        const long ok = 466L * 1024 * 1024;

        // A fake file system: whatever the test declares exists, with the size and header it says.
        static WhisperModelCheckResult CheckWith(
            string? configured,
            IReadOnlyDictionary<string, (long Size, uint? Magic)>? files = null,
            IReadOnlyDictionary<string, string[]>? folders = null)
        {
            files ??= new Dictionary<string, (long, uint?)>();
            folders ??= new Dictionary<string, string[]>();

            return WhisperModelCheck.Check(
                configured,
                fileExists: p => files.ContainsKey(p),
                directoryExists: p => folders.ContainsKey(p),
                lengthOf: p => files.TryGetValue(p, out var f) ? f.Size : 0,
                magicOf: p => files.TryGetValue(p, out var f) ? f.Magic : null,
                modelsIn: p => folders.TryGetValue(p, out var inside) ? inside : Array.Empty<string>());
        }

        Section("Nothing configured is a state, not a failure");

        var none = CheckWith(null);
        Check("an empty path reports itself as not configured",
            none.Status == WhisperModelStatus.NotConfigured && !none.IsUsable);
        Check("and says where a model comes from",
            none.Hints.Any(h => h.Contains("huggingface.co", StringComparison.OrdinalIgnoreCase)));
        Check("and names the script that fetches one",
            none.Hints.Any(h => h.Contains("download-ggml-model", StringComparison.OrdinalIgnoreCase)));

        Check("whitespace counts as nothing configured",
            CheckWith("   ").Status == WhisperModelStatus.NotConfigured);

        Section("A good model is accepted, quotes and all");

        var good = new Dictionary<string, (long, uint?)>
        {
            ["models/ggml-small.en.bin"] = (ok, WhisperModelCheck.GgmlMagic),
        };

        var fine = CheckWith("models/ggml-small.en.bin", good);
        Check("a real model is usable", fine.IsUsable && fine.Status == WhisperModelStatus.Ok);
        Check("and its path is handed on", fine.ResolvedPath == "models/ggml-small.en.bin");
        Check("and its size is reported", fine.SizeBytes == ok);
        Check("the summary names the size", WhisperModelCheck.Describe(fine).Contains("466"),
            WhisperModelCheck.Describe(fine));

        // A path pasted from Explorer arrives wrapped in quotes often enough to be worth handling.
        Check("a quoted path still works", CheckWith("\"models/ggml-small.en.bin\"", good).IsUsable);

        Section("The failure that actually happens: a download that stopped");

        var halfDone = new Dictionary<string, (long, uint?)>
        {
            // The first bytes arrived, so the magic is right and only the size gives it away.
            ["models/ggml-small.en.bin"] = (3L * 1024 * 1024, WhisperModelCheck.GgmlMagic),
        };

        var cut = CheckWith("models/ggml-small.en.bin", halfDone);
        Check("a 3 MB model is reported as truncated",
            cut.Status == WhisperModelStatus.Truncated && !cut.IsUsable);
        Check("the message states the actual size rather than 'bad magic'",
            cut.Message.Contains("3 MB"), cut.Message);
        Check("and the hint gives the real sizes to compare against",
            cut.Hints.Any(h => h.Contains("466", StringComparison.Ordinal)));

        // An HTML error page saved under a .bin name is large enough to pass the size test and is
        // exactly what a redirect or a rate limit leaves behind.
        var notAModel = new Dictionary<string, (long, uint?)>
        {
            ["models/ggml-small.en.bin"] = (ok, 0x21444f43),
        };

        var wrong = CheckWith("models/ggml-small.en.bin", notAModel);
        Check("a big file with the wrong header is refused",
            wrong.Status == WhisperModelStatus.NotAModel && !wrong.IsUsable);
        Check("and the hint names the likely cause",
            wrong.Hints.Any(h => h.Contains("HTML", StringComparison.OrdinalIgnoreCase)));

        // A file that cannot be read at all yields no magic, and must not be treated as fine.
        var unreadable = new Dictionary<string, (long, uint?)>
        {
            ["models/ggml-small.en.bin"] = (ok, null),
        };
        Check("an unreadable file is not usable",
            !CheckWith("models/ggml-small.en.bin", unreadable).IsUsable);

        Section("A missing file says what to check");

        var gone = CheckWith("models/ggml-small.en.bin");
        Check("a missing model is reported", gone.Status == WhisperModelStatus.FileMissing);
        Check("and the message names the path", gone.Message.Contains("models/ggml-small.en.bin"));

        Section("A folder instead of a file is resolved when it can be");

        var oneInside = new Dictionary<string, string[]>
        {
            ["models"] = new[] { "models/ggml-base.en.bin" },
        };
        var insideFiles = new Dictionary<string, (long, uint?)>
        {
            ["models/ggml-base.en.bin"] = (142L * 1024 * 1024, WhisperModelCheck.GgmlMagic),
        };

        var resolved = CheckWith("models", insideFiles, oneInside);
        Check("one model in a folder is used", resolved.IsUsable);
        Check("and the result says which", resolved.ResolvedPath == "models/ggml-base.en.bin");
        Check("and says so out loud, since it is not what was configured",
            resolved.Message.Contains("only model", StringComparison.OrdinalIgnoreCase), resolved.Message);

        // Two is a choice, and guessing which one somebody meant is how a server ends up running
        // tiny when the admin downloaded medium.
        var twoInside = new Dictionary<string, string[]>
        {
            ["models"] = new[] { "models/ggml-base.en.bin", "models/ggml-small.en.bin" },
        };
        var ambiguous = CheckWith("models", insideFiles, twoInside);
        Check("two models in a folder is refused rather than guessed",
            ambiguous.Status == WhisperModelStatus.FolderWithoutModel && !ambiguous.IsUsable);
        Check("and both are offered by name",
            ambiguous.Hints.Count(h => h.Contains("ggml-", StringComparison.Ordinal)) == 2);

        var emptyFolder = CheckWith("models", new Dictionary<string, (long, uint?)>(),
            new Dictionary<string, string[]> { ["models"] = Array.Empty<string>() });
        Check("an empty folder says so",
            emptyFolder.Status == WhisperModelStatus.FolderWithoutModel &&
            emptyFolder.Message.Contains("no ggml-*.bin", StringComparison.OrdinalIgnoreCase),
            emptyFolder.Message);

        Section("The magic constant matches what whisper.cpp actually reads");

        // Pinned deliberately. whisper.cpp reads these four bytes as a native-endian uint32 and
        // compares with GGML_FILE_MAGIC; the converter writes them with struct.pack("i", ...). On
        // disk that is 6c 6d 67 67, which reads "lmgg" in a hex dump - the sort of detail that
        // gets "corrected" to "ggml" by somebody being helpful, after which every model is refused.
        Check("the constant is 0x67676d6c", WhisperModelCheck.GgmlMagic == 0x67676d6c);

        var onDisk = new byte[] { 0x6c, 0x6d, 0x67, 0x67 };
        var asRead = (uint)(onDisk[0] | (onDisk[1] << 8) | (onDisk[2] << 16) | (onDisk[3] << 24));
        Check("and the bytes 6c 6d 67 67 on disk are what produce it", asRead == WhisperModelCheck.GgmlMagic,
            $"0x{asRead:x8}");

        Check("the threshold sits well below the smallest published model",
            WhisperModelCheck.MinimumPlausibleBytes < 75L * 1024 * 1024,
            $"{WhisperModelCheck.MinimumPlausibleBytes / (1024 * 1024)} MB");

        Section("Gemini stays the default");

        // The whole point of the option: nobody who does not ask for Whisper is affected by it.
        var untouched = new AppConfig();
        Check("a fresh config still uses Gemini", untouched.SpeechEngine == "Gemini", untouched.SpeechEngine);
        Check("and configures no Whisper model", untouched.WhisperModelPath == "");
        Check("the language default is English, which is what radio phraseology is",
            untouched.WhisperLanguage == "en");

        Section("Two variants: one online with Gemini, one offline with Whisper");

        // The point of the setting. Checked as a property of the configuration rather than by
        // reading the code, because "we run this offline" is a promise somebody makes to their
        // squadron, and it is kept or broken by four unrelated settings.
        var online = new AppConfig { GeminiApiKey = "key" };
        Check("the default configuration is the online one",
            !NetworkUse.IsFullyOffline(online) &&
            NetworkUse.Describe(online).Any(r => r.Component.Contains("Gemini", StringComparison.Ordinal)));

        var offline = new AppConfig { SpeechEngine = "Whisper", WhisperModelPath = "models/ggml-small.en.bin" };
        Check("switching the engine alone makes it offline",
            NetworkUse.IsFullyOffline(offline), NetworkUse.Summarise(offline));

        Check("and the summary says so as a statement rather than a reassurance",
            NetworkUse.Summarise(offline).Contains("Nothing in this configuration reaches the internet",
                StringComparison.Ordinal));

        // The three ways an "offline" installation is not one. Each is a separate setting, and
        // none of them is undone by switching the engine - which is exactly why the list exists.
        var withDiscord = new AppConfig
        {
            SpeechEngine = "Whisper",
            DiscordEnabled = true,
            DiscordWebhookUrl = "https://discord.com/api/webhooks/x",
        };
        Check("Discord notifications are reported as reaching out",
            !NetworkUse.IsFullyOffline(withDiscord) &&
            NetworkUse.Describe(withDiscord).Any(r => r.Component.Contains("Discord", StringComparison.Ordinal)));

        Check("but Discord on its own is named as sending no audio",
            NetworkUse.Describe(withDiscord).Single(r => r.Component.Contains("Discord", StringComparison.Ordinal))
                .Detail.Contains("No pilot audio", StringComparison.Ordinal));

        // The one that hides: cloud TTS credentials live inside a free-form argument string, so
        // they survive every other offline switch and nothing about the field's name suggests the
        // replies are being spoken by somebody else's server.
        foreach (var (flag, service) in new[] { ("--azureCredentials=\"k;westeurope\"", "Azure"),
                                                ("--googleCredentials=\"c.json\"", "Google Cloud") })
        {
            var cloudVoice = new AppConfig { SpeechEngine = "Whisper", ExternalAudioExtraArgs = flag };
            Check($"cloud speech output via {service} is not mistaken for offline",
                !NetworkUse.IsFullyOffline(cloudVoice), NetworkUse.Summarise(cloudVoice));
        }

        // Discord off but a URL left behind is the common half-state, and it sends nothing.
        var discordOff = new AppConfig
        {
            SpeechEngine = "Whisper",
            DiscordEnabled = false,
            DiscordWebhookUrl = "https://discord.com/api/webhooks/x",
        };
        Check("a webhook left behind with the switch off reaches nobody",
            NetworkUse.IsFullyOffline(discordOff));

        // SRS and DCS-gRPC are network sockets too, but to services the operator runs. Listing
        // them would bury the ones that actually go to a third party.
        var withGrpc = new AppConfig
        {
            SpeechEngine = "Whisper",
            DcsGrpcEnabled = true,
            DcsIntelEnabled = true,
        };
        Check("the operator's own services are not counted as reaching out",
            NetworkUse.IsFullyOffline(withGrpc), NetworkUse.Summarise(withGrpc));

        Section("The config check asks about the engine in use, not the other one");

        // Warning about a missing Gemini key on a deliberately offline installation tells somebody
        // who set it up correctly that it is broken - and the next thing they do is get a key they
        // do not need.
        static List<string> Warnings(AppConfig config)
        {
            var found = new List<string>();
            AppConfig.ValidateValues(config, w => found.Add(w));
            return found;
        }

        var offlineNoKey = new AppConfig { SpeechEngine = "Whisper", WhisperModelPath = "models/ggml-small.en.bin" };
        Check("no Gemini key is demanded when Whisper transcribes",
            !Warnings(offlineNoKey).Any(w => w.Contains("GeminiApiKey", StringComparison.Ordinal)),
            string.Join(" | ", Warnings(offlineNoKey).Where(w => w.Contains("Gemini", StringComparison.Ordinal))));

        var onlineNoKey = new AppConfig();
        Check("but it is still demanded when Gemini does",
            Warnings(onlineNoKey).Any(w => w.Contains("GeminiApiKey", StringComparison.Ordinal)));

        var offlineNoModel = new AppConfig { SpeechEngine = "Whisper" };
        Check("and a missing Whisper model is warned about instead",
            Warnings(offlineNoModel).Any(w => w.Contains("Whisper", StringComparison.Ordinal)),
            string.Join(" | ", Warnings(offlineNoModel)));

        var nonsense = new AppConfig { SpeechEngine = "Wisper" };
        Check("a misspelt engine is reported rather than silently treated as Gemini",
            Warnings(nonsense).Any(w => w.Contains("SpeechEngine", StringComparison.Ordinal)),
            string.Join(" | ", Warnings(nonsense)));

        Section("The documentation does not still present Gemini as the only way");

        // The engine is a choice now, and the places that describe the bot from outside were all
        // written when it was not. A reader who takes the front page at its word and concludes an
        // API key is mandatory is the failure here, and it is invisible from inside the code.
        foreach (var document in new[] { "README.md", "README.de.md", "docs/manual-en.md", "docs/manual-de.md" })
        {
            var text = ReadSource(document);

            Check($"{document} mentions the engine setting",
                text.Contains("SpeechEngine", StringComparison.Ordinal));

            Check($"{document} says a Whisper model replaces the key",
                text.Contains("Whisper", StringComparison.OrdinalIgnoreCase) &&
                text.Contains("huggingface.co", StringComparison.OrdinalIgnoreCase));
        }

        // The diagram is the first thing anybody reads, and it used to fold transcription into the
        // last branch - which was already slightly wrong and became properly misleading once the
        // engine became a choice.
        foreach (var manual in new[] { "docs/manual-en.md", "docs/manual-de.md" })
            Check($"{System.IO.Path.GetFileName(manual)}'s flow diagram shows transcription as its own step",
                ReadSource(manual).Contains("(SpeechEngine)", StringComparison.Ordinal));

        // The RID itself is already guarded in HousekeepingTests ("publishing is pinned to win-x64").
        // What was missing is the reason being written down: Whisper.net.Runtime is the first
        // package that ships binaries for other platforms, so that flag went from tidy to load
        // bearing, and the next person to see it has no way to tell.
        Check("the installer documentation explains what the win-x64 pin now keeps out",
            ReadSource("docs/building-the-installer.md").Contains("dylib", StringComparison.OrdinalIgnoreCase));

        Section("The engine is wired so that only the last rung of the reply chain changes");

        var bot = ReadSource("src/Darkstar.Bot/BotService.cs");

        // Whisper transcribes and nothing more, so the free reply disappears - but ONLY that. If
        // the branch were placed higher, selecting Whisper would silently switch off the mission
        // data calls too, and the symptom ("the bot only says the fallback now") looks identical.
        var transcribeAt = bot.IndexOf("whisper.TranscribeAsync(audio", StringComparison.Ordinal);
        var intelAt = bot.IndexOf("intel.Classify(text", StringComparison.Ordinal);
        var noFreeReplyAt = bot.IndexOf("else if (geminiReply == null)", StringComparison.Ordinal);

        Check("the transcript is produced before anything classifies it",
            transcribeAt > 0 && intelAt > 0 && transcribeAt < intelAt,
            $"transcribe at {transcribeAt}, classify at {intelAt}");

        Check("and the missing free reply is handled after the mission data, not instead of it",
            noFreeReplyAt > 0 && intelAt < noFreeReplyAt,
            $"classify at {intelAt}, fallback at {noFreeReplyAt}");

        Check("the two reasons for a fallback reply are logged apart",
            bot.Contains("this engine writes no free replies", StringComparison.Ordinal) &&
            bot.Contains("No known trigger detected, using FallbackResponse", StringComparison.Ordinal));

        // The refusal has to survive: falling back to Gemini would send audio to Google for
        // somebody who picked Whisper precisely so that would not happen.
        Check("an unusable model stops the bot rather than reverting to Gemini",
            bot.Contains("Fix WhisperModelPath, or set SpeechEngine back to \\\"Gemini\\\"", StringComparison.Ordinal));

        Section("The local engine does not leak one transmission into the next");

        var client = ReadSource("src/Darkstar.Bot/WhisperClient.cs");

        // The resampler carries its delay line across calls by design - right for a continuous
        // stream, wrong for separate recordings. Without the reset the tail of the previous
        // transmission starts the next one, which reads as a recognition fault and is not.
        Check("the resampler is reset per transmission",
            client.Contains("_resampler.Reset()", StringComparison.Ordinal));

        Check("and the anti-alias filter is used rather than plain decimation",
            client.Contains("DecimatingLowPass", StringComparison.Ordinal) &&
            !client.Contains("AverageDecimate", StringComparison.Ordinal));

        Check("the audio handed over is 16 kHz, which is what Whisper wants",
            client.Contains("DecimatingLowPass.OutputSampleRate", StringComparison.Ordinal));
        Check("and that constant really is 16000", DecimatingLowPass.OutputSampleRate == 16000);

        // Two radios can finish recording in the same second, and one decoder is not safe for two.
        Check("only one transmission is transcribed at a time",
            client.Contains("SemaphoreSlim", StringComparison.Ordinal) &&
            client.Contains("_oneAtATime.WaitAsync", StringComparison.Ordinal));

        Check("the model is loaded once rather than per transmission",
            client.Contains("if (_processor != null) return _processor;", StringComparison.Ordinal));

        Section("Whisper's own annotations never reach the trigger matcher");

        // Whisper emits [BLANK_AUDIO], (wind), and similar for anything it cannot turn into words.
        // Those are not things a pilot said; left in, they are one unlucky word from matching a
        // trigger. Calling the shipped method rather than a copy of its expression: a test that
        // reimplements what it checks passes happily while the real thing is broken - which is
        // exactly what happened when this test was first written.
        static string Strip(string raw) => WhisperTranscript.Clean(raw);

        Check("a blank-audio marker is removed", Strip(" [BLANK_AUDIO]") == "", $"\"{Strip(" [BLANK_AUDIO]")}\"");
        Check("a noise annotation is removed", Strip("(wind blowing)") == "");
        Check("a marker around real speech leaves the speech",
            Strip(" [BLANK_AUDIO] Overlord, bogey dope") == "Overlord, bogey dope",
            Strip(" [BLANK_AUDIO] Overlord, bogey dope"));
        Check("Whisper's leading space is trimmed",
            Strip(" Overlord, radio check") == "Overlord, radio check");
        Check("and the gap left behind does not become a double space",
            Strip("Overlord, (static) bogey dope") == "Overlord, bogey dope",
            Strip("Overlord, (static) bogey dope"));
    }
}
