using Microsoft.Extensions.Hosting;
using System.Linq;
using Vosk;

namespace Darkstar;

/// <summary>
/// The actual bot logic, wrapped as a BackgroundService so it can run either as a normal
/// console app (via `dotnet run` / the .exe directly) or as a real Windows Service, using the
/// exact same code either way (Program.cs decides which based on how the process was started).
/// </summary>
public sealed class BotService : BackgroundService
{
    private readonly IHostApplicationLifetime _lifetime;
    private SrsConnection? _srs;
    private AppConfig? _config;
    private Model? _voskModel;

    public BotService(IHostApplicationLifetime lifetime)
    {
        _lifetime = lifetime;
    }

    /// <summary>
    /// Per-radio runtime state: every monitored frequency gets its own hotword detector,
    /// recording buffer, pre-roll buffer, and self-mute flag, so activity on one radio never
    /// interferes with another (e.g. two pilots talking on different frequencies at once).
    /// </summary>
    private sealed class RadioSession
    {
        public double FrequencyHz;
        public string Modulation = "AM";
        public string Callsign = "Overlord";
        public IHotwordDetector Hotword = null!;

        public readonly object Lock = new();
        public readonly List<byte> RecordingBuffer = new();
        public readonly List<byte> PreRollBuffer = new();
        public bool IsRecording;
        public int SilenceFrames;
        public DateTime LastAudioReceivedAt = DateTime.UtcNow;
        public bool IsTransmittingReply;
        public string CurrentSenderName = "";

        /// <summary>
        /// The sender's unmodified SRS name (before the callsign is parsed out of it). Needed to
        /// match the pilot against their DCS unit for tactical replies, since DCS knows them by
        /// their player name, not by the shortened callsign used on the radio.
        /// </summary>
        public string CurrentSenderRawName = "";

        public int CurrentSenderCoalition;

        /// <summary>
        /// Who was last heard on this radio, set for every packet rather than only on detection -
        /// so a saved "missed" recording is labelled with whoever actually spoke, not with the
        /// last pilot the wake word happened to work for.
        /// </summary>
        public string LastHeardSenderName = "";

        /// <summary>
        /// Serializes everything this radio transmits. Without it, a slow-request acknowledgement
        /// and the real reply could end up starting DCS-SR-ExternalAudio.exe twice at the same
        /// time on the same frequency, talking over each other.
        /// </summary>
        public readonly SemaphoreSlim TransmitLock = new(1, 1);

        /// <summary>
        /// Cancels the pending "message received, standby" acknowledgement for the request
        /// currently being processed on this radio - used as soon as the real reply is ready (or
        /// the request is dropped), so the acknowledgement is only ever sent while the pilot
        /// would actually still be waiting.
        /// </summary>
        public CancellationTokenSource? PendingAckCts;
    }

    private static string CoalitionLabel(int coalition) => coalition switch
    {
        1 => "Red",
        2 => "Blue",
        _ => "Spectator/Unknown"
    };

    /// <summary>
    /// Parses the pilot's callsign out of their raw SRS player name using the configured
    /// separator, e.g. "Enfield 1-1 | neodym" -> "Enfield 1-1" (the part after the separator -
    /// typically the player's real name/handle - is discarded entirely). Falls back to the full
    /// (trimmed) name if the separator isn't present, so players not following the convention
    /// still get addressed normally instead of breaking.
    /// </summary>
    /// <summary>
    /// The callsign the bot addresses a pilot by, taken from their SRS name. See PilotNames for
    /// what gets stripped and why - squadron tags in particular, since "[ISAF] Mobius 1" read out
    /// by a speech engine is not a radio call.
    /// </summary>
    private static string ParseCallsign(string rawName, string separator) =>
        PilotNames.DisplayCallsign(rawName, separator);

    /// <summary>
    /// Makes a callsign sound natural when spoken via TTS: flight numbers digit by digit ("Spare
    /// 15" as "Spare one five", the way every air force says it), hyphens and underscores as
    /// pauses rather than as the word "dash", and squadron tags dropped. Only used right before
    /// building the text that actually goes to the speech engine - logs and everywhere else keep
    /// showing the original, readable "Titan 1-1" form. See PilotNames.ForSpeech.
    /// </summary>
    private static string ForSpeech(string text) => PilotNames.ForSpeech(text);

    /// <summary>
    /// Fills the {pilot}/{callsign} placeholders of the acknowledgement text (AckMessage). If the
    /// pilot's name is unknown (no SRS client entry for the sender), "{pilot}, " is dropped
    /// entirely instead of leaving a dangling comma or an empty form of address.
    /// </summary>
    private static string BuildAckText(string template, string pilotCallsign, string botCallsign)
    {
        if (string.IsNullOrWhiteSpace(template)) return "";

        string text;
        if (string.IsNullOrWhiteSpace(pilotCallsign))
        {
            text = template.Replace("{pilot}, ", "").Replace("{pilot} ", "").Replace("{pilot}", "").Trim();

            // Dropping a leading "{pilot}, " would otherwise leave the sentence starting
            // lower-case ("this is Overlord, ...") in the log output.
            if (text.Length > 0)
                text = char.ToUpperInvariant(text[0]) + text[1..];
        }
        else
        {
            text = template.Replace("{pilot}", pilotCallsign.Trim());
        }

        return text.Replace("{callsign}", botCallsign).Trim();
    }

    /// <summary>Exit code for "the bot is fine, the configuration isn't" - a wrong path, a missing model.</summary>
    public const int ExitCodeConfigurationError = 2;

    /// <summary>Exit code for anything unexpected that stopped the bot from starting.</summary>
    public const int ExitCodeStartupFailure = 3;

    /// <summary>
    /// Wraps the actual startup so that nothing reaches the runtime as an unhandled exception.
    /// An unhandled exception in a BackgroundService takes the whole process down and prints a
    /// .NET stack trace - which tells the person running a voice bot nothing at all. Every
    /// failure that gets here is logged as a sentence, followed by a clean stop.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown (service stop / Ctrl+C).
        }
        catch (VoskModelLoadException ex)
        {
            FailStartup(ex.Message, ex.Hints, ExitCodeConfigurationError);
        }
        catch (Exception ex)
        {
            Logger.Log("");
            Logger.Log($"ERROR: the bot stopped unexpectedly - {ex.GetType().Name}: {ex.Message}");

            // The full trace goes to the log file only; the console gets the readable version.
            Logger.Debug(ex.ToString());

            Logger.Log("The full details are in the log file under logs\\.");
            DiscordNotifier.Notify($"❌ **D.A.R.K.S.T.A.R.** stopped unexpectedly: {ex.Message}");

            Environment.ExitCode = ExitCodeStartupFailure;
            _lifetime.StopApplication();
        }
    }

    /// <summary>Logs a startup failure the way a person can act on it, then stops the host cleanly.</summary>
    private void FailStartup(string message, IReadOnlyList<string> hints, int exitCode)
    {
        Logger.Log("");
        Logger.Log($"ERROR: {message}");
        foreach (var hint in hints)
            Logger.Log($"  - {hint}");
        Logger.Log("");

        DiscordNotifier.Notify($"❌ **D.A.R.K.S.T.A.R.** could not start: {message}");

        Environment.ExitCode = exitCode;
        _lifetime.StopApplication();
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        // IMPORTANT: a Windows Service's working directory defaults to C:\Windows\System32,
        // not the folder the .exe lives in - so config.json/phrases.json must be resolved
        // relative to the executable's own directory, not the current working directory.
        var baseDir = AppContext.BaseDirectory;
        var configPath = Path.Combine(baseDir, "config.json");
        var phraseBookPath = Path.Combine(baseDir, "phrases.json");
        var vocabularyPath = Path.Combine(baseDir, "vocabulary.json");

        var config = AppConfig.LoadOrCreateDefault(configPath);
        if (config == null)
        {
            // Either a new default config was just created, or parsing failed.
            // Either way: the message was already logged in AppConfig - stop the whole host
            // instead of leaving a Windows Service stuck in "starting" with nothing to do.
            _lifetime.StopApplication();
            return;
        }
        _config = config;

        DiscordNotifier.Init(config.DiscordEnabled, config.DiscordWebhookUrl);

        var phrases = PhraseBook.LoadOrCreateDefault(phraseBookPath);
        Logger.Log($"{phrases.Count} fixed reply phrase(s) loaded from phrases.json.");

        var vocabulary = VocabularyBook.LoadOrCreateDefault(vocabularyPath);
        Logger.Log($"{vocabulary.Count} vocabulary hint(s) loaded from vocabulary.json.");

        // A trigger phrase in the vocabulary makes the transcriber turn unclear audio into that
        // very command, which the bot then answers instead of asking for a repeat. Worth a loud
        // warning, because nothing about the symptom points at vocabulary.json.
        AppConfig.WarnAboutVocabularyTriggerConflicts(config, vocabulary);

        Logger.DebugEnabled = config.DebugLogging;
        if (config.DebugLogging)
            Logger.Log("Verbose debug logging is active (DebugLogging in config.json).");

        if (!config.LoggingEnabled)
        {
            // Still print this message while logging is active - after this, it's truly silent.
            Logger.Log("Logging is now being fully disabled (LoggingEnabled=false in config.json) - no further console/file output from here on.");
            Logger.Enabled = false;
        }

        string? eamPassword = string.IsNullOrWhiteSpace(config.ExternalAwacsPassword) ? null : config.ExternalAwacsPassword;

        var radioConfigs = config.GetEffectiveRadios();

        // Load the Vosk model once (it's often 40MB+) and share it across one lightweight
        // VoskRecognizer per radio, instead of loading the whole model from disk per radio.
        if (!string.IsNullOrWhiteSpace(config.VoskModelPath))
        {
            try
            {
                _voskModel = VoskHotwordDetector.LoadModel(config.VoskModelPath);
                Logger.Log($"Wake word detection active (Vosk, offline), {radioConfigs.Count} radio(s):");

                if (config.HotwordAudioFilter == HotwordAudioFilter.Average)
                    Logger.Log("NOTE: HotwordAudioFilter is \"Average\" - the old audio path, kept for comparison. " +
                               "\"LowPass\" recognizes noticeably better; see the manual's chapter on wake word accuracy.");

                if (config.HotwordAutoGain)
                    Logger.Log("[Hotword] Automatic gain is on - quiet pilots are amplified for detection. " +
                               "Watch for wake words firing on noise.");
            }
            catch (VoskModelLoadException ex)
            {
                // A configured-but-unusable model is a stop condition, not something to work
                // around: falling back to the volume detector would leave a bot that looks like
                // it started normally and then answers every loud noise on the frequency.
                var hints = ex.Hints.ToList();
                hints.Add("To start without wake word detection anyway, clear VoskModelPath in config.json - " +
                          "the bot then reacts to loud audio instead of a spoken word (for testing only).");

                FailStartup($"the wake word model could not be loaded. {ex.Message}", hints, ExitCodeConfigurationError);
                return;
            }
        }
        else
        {
            Logger.Log("WARNING: VoskModelPath is not set in config.json - falling back to the volume placeholder (no real wake word) on every radio.");
            Logger.Log("Recommendation: set VoskModelPath (free, offline, no account needed - see README).");
        }

        // Tactical replies from live mission data - only useful when DCS-gRPC is actually enabled.
        DcsIntelService? intel = null;
        ThreatCircleService? threatCircles = null;
        DcsAirfieldService? airfields = null;
        if (config.DcsIntelEnabled && config.DcsGrpcEnabled)
        {
            intel = new DcsIntelService(config);
            if (config.DcsIntelThreatCircleEnabled)
                threatCircles = new ThreatCircleService(config, intel);
            var source = string.IsNullOrWhiteSpace(config.DcsIntelAwacsUnitName)
                ? "mission data (god's eye)"
                : $"sensors of '{config.DcsIntelAwacsUnitName}' (falls back to mission data)";
            Logger.Log($"[Intel] Enabled - tactical requests are answered from {source} via DCS-gRPC at {config.DcsGrpcAddress}.");

            if (threatCircles != null)
                Logger.Log($"[ThreatCircle] Enabled - pilots can request a standing watch (default {config.DcsIntelThreatCircleDefaultRadiusNm:0} NM, " +
                           $"swept every {config.DcsIntelThreatCirclePollSeconds}s, expires after {config.DcsIntelThreatCircleDurationMinutes} min).");

            if (config.DcsAirfieldEnabled)
            {
                airfields = new DcsAirfieldService(config, intel);
                Logger.Log("[Airfield] Enabled - \"runway in use\" and ATIS answered from live weather. " +
                           "The runway headings need \"evalEnabled = true\" on the DCS-gRPC server; " +
                           "without it the weather still works and the runway is reported as unknown.");
            }
        }
        else if (config.DcsIntelEnabled)
        {
            Logger.Log("[Intel] DcsIntelEnabled is true, but DcsGrpcEnabled is false - tactical requests will be handled by phrases/Gemini as before.");
        }

        if (config.AckEnabled)
            Logger.Log($"[Ack] Enabled - if a reply takes longer than {config.AckAfterSeconds:0.#}s (measured from the wake word), " +
                       $"the bot sends: \"{config.AckMessage}\"");

        if (config.RestrictToOwnCoalition)
            Logger.Log($"[Coalition Check] Enabled - the bot will ignore requests from the opposing coalition (bot coalition: {CoalitionLabel(config.Coalition)}).");

        // One RadioSession per configured radio, keyed by the frequency rounded to the nearest
        // Hz (to avoid floating-point equality issues when matching against frequencies parsed
        // back off the wire). Keyword and Callsign are per-radio if set, otherwise fall back to
        // the global VoskKeyword/BotCallsign - so different radios can respond to different
        // wake words (e.g. "Overlord" on AWACS, "Texaco" on the tanker frequency).
        var sessions = new Dictionary<long, RadioSession>();
        foreach (var radioConfig in radioConfigs)
        {
            var effectiveKeyword = string.IsNullOrWhiteSpace(radioConfig.Keyword) ? config.VoskKeyword : radioConfig.Keyword;
            var effectiveCallsign = string.IsNullOrWhiteSpace(radioConfig.Callsign) ? config.BotCallsign : radioConfig.Callsign;

            IHotwordDetector hotword = _voskModel != null
                ? new VoskHotwordDetector(_voskModel, effectiveKeyword, config.HotwordAudioFilter, config.HotwordAutoGain)
                : new EnergyThresholdPlaceholderDetector(config.HotwordEnergyThreshold, config.HotwordConsecutiveFramesNeeded);

            if (_voskModel != null)
                Logger.Log($"  {radioConfig.FrequencyHz / 1_000_000:0.000} MHz ({radioConfig.Modulation}): wake word \"{effectiveKeyword}\", callsign \"{effectiveCallsign}\"");

            var key = (long)Math.Round(radioConfig.FrequencyHz);
            sessions[key] = new RadioSession
            {
                FrequencyHz = radioConfig.FrequencyHz,
                Modulation = radioConfig.Modulation,
                Callsign = effectiveCallsign,
                Hotword = hotword
            };
        }

        if (string.IsNullOrWhiteSpace(config.GeminiApiKey))
        {
            Logger.Log("WARNING: GeminiApiKey is empty in config.json - transcription/reply will not work.");
            Logger.Log("Create a key at https://aistudio.google.com/apikey and enter it in config.json.");
        }

        var gemini = new GeminiClient(
            config.GeminiApiKey,
            config.GeminiModel,
            fallbackModel: config.GeminiFallbackModel,
            maxRetries: config.GeminiMaxRetries,
            retryDelayMs: config.GeminiRetryDelayMs);

        var audioSender = new ExternalAudioSender(
            config.ExternalAudioExePath,
            config.SrsHost,
            config.SrsPort,
            coalition: config.Coalition,
            name: config.ClientName,
            voiceName: config.VoiceName,
            extraArgs: config.ExternalAudioExtraArgs);

        _srs = new SrsConnection(
            config.SrsHost,
            config.SrsPort,
            config.ClientName,
            radioConfigs,
            eamPassword);
        var srs = _srs;

        // Shared across all radios: how many seconds of audio to always keep before a hotword
        // detection, and how long to wait for more audio before ending a recording via timeout.
        var preRollMaxBytes = (int)(48000 * 2 * config.PreRollSeconds); // 48kHz mono 16-bit
        var noAudioTimeout = TimeSpan.FromMilliseconds(config.SilenceFramesToStopRecording * 20);

        // Transmits one piece of text on a radio, serialized per radio and with the bot's own
        // self-mute active for the whole transmission (see the comment in FinalizeRecordingAsync
        // about the feedback loop). Used for both the real reply and the standby acknowledgement.
        async Task TransmitAsync(RadioSession session, string spokenText, CancellationToken token = default)
        {
            await session.TransmitLock.WaitAsync(token);
            session.IsTransmittingReply = true;
            try
            {
                // Runs entirely through the official DCS-SR-ExternalAudio.exe - no need for our
                // own Opus encoding/UDP sending, which makes it protocol-safe.
                await audioSender.SendTextAsync(spokenText, session.FrequencyHz, session.Modulation);

                // Small safety margin in case the transmission trails off slightly before it
                // arrives back at the bot itself as incoming audio.
                await Task.Delay(500, CancellationToken.None);
            }
            finally
            {
                session.IsTransmittingReply = false;

                // Whatever was collected into the pre-roll during the self-mute period might
                // contain remnants of our own transmission - discard it to be safe, so the next
                // recording cycle starts cleanly.
                lock (session.Lock) { session.PreRollBuffer.Clear(); }
                session.TransmitLock.Release();
            }
        }

        // Cancels this radio's pending acknowledgement (reply is ready, or the request was
        // dropped). When "expected" is given, only that exact acknowledgement is cancelled: if
        // the pilot already fired off a SECOND request while the first one was still being
        // processed, finishing the first request must not silently cancel the second one's
        // acknowledgement.
        void CancelPendingAck(RadioSession session, CancellationTokenSource? expected = null)
        {
            CancellationTokenSource? cts;
            if (expected == null)
            {
                cts = Interlocked.Exchange(ref session.PendingAckCts, null);
            }
            else
            {
                cts = Interlocked.CompareExchange(ref session.PendingAckCts, null, expected) == expected ? expected : null;
            }

            if (cts == null) return;
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
            cts.Dispose();
        }

        // Starts the "message received, standby" timer for a freshly detected request. Fires only
        // if the request is still being worked on when AckAfterSeconds (counted from the wake word
        // detection) have passed, and never while the pilot is still transmitting.
        void ScheduleAck(RadioSession session, string pilotCallsign)
        {
            CancelPendingAck(session);

            var cts = new CancellationTokenSource();
            session.PendingAckCts = cts;
            var token = cts.Token;
            var freqLabel = $"{session.FrequencyHz / 1_000_000:0.000} MHz";

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(0.1, config.AckAfterSeconds)), token);

                    // Don't step on the pilot's own transmission: if they're still talking when
                    // the timer expires, wait until their recording has actually been finalized.
                    while (session.IsRecording)
                        await Task.Delay(100, token);

                    token.ThrowIfCancellationRequested();

                    var ackText = BuildAckText(config.AckMessage, pilotCallsign, session.Callsign);
                    if (string.IsNullOrWhiteSpace(ackText))
                        return;

                    Logger.Log($"[Ack] {freqLabel}: reply still being worked on - sending \"{ackText}\"");
                    await TransmitAsync(session, ForSpeech(ackText), token);
                }
                catch (OperationCanceledException)
                {
                    // Normal case: the real reply was ready before the acknowledgement was due.
                }
                catch (Exception ex)
                {
                    // An acknowledgement is a nice-to-have - never let it take down the request
                    // it belongs to, the real reply is still on its way.
                    Logger.Log($"[Ack] {freqLabel}: could not send the acknowledgement: {ex.Message}");
                }
            }, stoppingToken);
        }

        async Task FinalizeRecordingAsync(RadioSession session)
        {
            byte[] audio;
            string senderName;
            string senderRawName;
            int senderCoalition;

            // The acknowledgement belonging to THIS request - captured up front so a later
            // request's acknowledgement can never be cancelled by this one finishing.
            var ackCts = session.PendingAckCts;

            lock (session.Lock)
            {
                if (!session.IsRecording) return;
                session.IsRecording = false;
                audio = session.RecordingBuffer.ToArray();
                session.RecordingBuffer.Clear();
                senderName = session.CurrentSenderName;
                senderRawName = session.CurrentSenderRawName;
                senderCoalition = session.CurrentSenderCoalition;
            }

            var freqLabel = $"{session.FrequencyHz / 1_000_000:0.000} MHz";

            // The counterpart to the "missed" recordings written by the watchdog: together the
            // two make up a corpus of real traffic that --test-hotword can be measured against.
            if (config.SaveRecordings)
                SaveRecording(audio, "hit", session.FrequencyHz, senderRawName);

            // Coalition security check happens BEFORE transcribing (not just before replying) -
            // this both matches realistic radio discipline (no engagement with the opposing
            // coalition at all) and avoids spending a Gemini call on a transmission we're going
            // to ignore anyway. Coalition 0 (Spectator/unknown) is never restricted.
            if (config.RestrictToOwnCoalition && senderCoalition != 0 && senderCoalition != config.Coalition)
            {
                // No reply at all means no acknowledgement either - otherwise the bot would
                // answer the opposing coalition with "standby" and then stay silent forever.
                CancelPendingAck(session, ackCts);
                Logger.Log($"[Coalition Check] {freqLabel}: ignoring transmission from {CoalitionLabel(senderCoalition)} " +
                           $"coalition (bot is {CoalitionLabel(config.Coalition)}) - no reply sent.");
                return;
            }

            try
            {
                Logger.Log($"[Recording finished] {freqLabel}: {audio.Length} bytes of PCM, transcribing...");

                var coalitionLabel = senderCoalition is 1 or 2 ? CoalitionLabel(senderCoalition) : null;
                var (text, geminiReply) = await gemini.TranscribeAndReplyAsync(audio, vocabulary, coalitionLabel);
                Logger.Log($"[STT] \"{text}\"");

                // Tactical requests (bogey dope / picture / threat) are answered from live mission
                // data and take priority over everything else: a fixed phrase or a Gemini reply
                // would invent an answer, while this one is actually true for the running mission.
                // Nothing intelligible came back. Everything below this point would be guessing:
                // the tactical classifier would match whatever the transcriber invented, and a
                // fixed phrase or a Gemini reply would answer a request nobody made. Ask for a
                // repeat instead, which is what a real controller does.
                if (string.IsNullOrWhiteSpace(text))
                {
                    var sayAgain = string.IsNullOrWhiteSpace(geminiReply) ? config.UnintelligibleReply : geminiReply;
                    Logger.Log($"[STT] {freqLabel}: nothing intelligible in the transmission - asking for a repeat.");

                    var spokenSayAgain = string.IsNullOrWhiteSpace(senderName)
                        ? $"This is {ForSpeech(session.Callsign)}... {sayAgain}"
                        : $"{ForSpeech(senderName)}, this is {ForSpeech(session.Callsign)}... {sayAgain}";

                    CancelPendingAck(session, ackCts);
                    await TransmitAsync(session, spokenSayAgain);
                    return;
                }

                string? intelReply = null;
                if (intel != null)
                {
                    var kind = intel.Classify(text, out var matchedTrigger);

                    if (kind is IntelRequestKind.ThreatCircleStart or IntelRequestKind.ThreatCircleCancel && threatCircles != null)
                    {
                        // Standing watch rather than a one-off answer: the reply here is just the
                        // confirmation, the actual warnings arrive later from the sweep loop.
                        intelReply = kind == IntelRequestKind.ThreatCircleStart
                            ? await threatCircles.StartAsync(senderRawName, senderName, session.FrequencyHz, senderCoalition, text)
                            : threatCircles.Cancel(senderRawName, session.FrequencyHz);

                        Logger.Log($"[Intel] {freqLabel}: {kind} handled, triggered by \"{matchedTrigger}\" " +
                                   $"({threatCircles.ActiveCount} threat circle(s) active).");
                    }
                    else if (kind != IntelRequestKind.None)
                    {
                        var intelResult = await intel.AnswerAsync(kind, text, senderRawName, senderCoalition);
                        if (intelResult.Handled)
                        {
                            intelReply = intelResult.Reply;
                            // The trigger that fired is logged on purpose: when the bot answers
                            // the wrong request, this line plus the [STT] line above it are the
                            // whole diagnosis.
                            Logger.Log($"[Intel] {freqLabel}: {kind} request, triggered by \"{matchedTrigger}\", " +
                                       $"answered from mission data ({intelResult.Detail}).");
                        }
                    }
                }

                // Airfield conditions, same principle: real weather and the runway the wind
                // actually favours, rather than something a language model made up. Checked after
                // the tactical requests, because a call naming both is more likely about threats.
                if (intelReply == null && airfields != null)
                {
                    var airfieldKind = airfields.Classify(text, out var airfieldTrigger);
                    if (airfieldKind != AirfieldRequestKind.None)
                    {
                        var airfieldResult = await airfields.AnswerAsync(airfieldKind, text, senderRawName, senderCoalition);
                        if (airfieldResult.Handled)
                        {
                            intelReply = airfieldResult.Reply;
                            Logger.Log($"[Airfield] {freqLabel}: {airfieldKind}, triggered by \"{airfieldTrigger}\", " +
                                       $"answered ({airfieldResult.Diagnostics}).");
                        }
                    }
                }

                // Fixed phrases (phrases.json) take priority over the Gemini-generated reply -
                // e.g. for a consistent radio-check text instead of a freshly worded reply every time.
                var phraseMatch = intelReply == null ? PhraseBook.TryMatch(text, phrases) : null;
                string reply;
                if (intelReply != null)
                {
                    reply = intelReply;
                }
                else if (phraseMatch != null)
                {
                    reply = phraseMatch;
                    Logger.Log("[Phrase Match] Known trigger detected, using the configured reply instead of the Gemini reply.");
                }
                else if (config.RestrictToKnownPhrases)
                {
                    // No known trigger -> don't use a freely generated Gemini reply, use the
                    // fixed fallback reply instead. This makes the bot respond only to configured phrases.
                    reply = config.FallbackResponse;
                    Logger.Log("[Phrase Match] No known trigger detected, using FallbackResponse.");
                }
                else
                {
                    reply = geminiReply;
                }

                // Fixed reply format: "<SenderName>, this is <Callsign>... <Reply>" - the ellipsis
                // acts as a short speaking pause before the actual reply, like real radio traffic.
                // Uses this radio's own callsign (falls back to the global BotCallsign if the
                // radio didn't set its own - see session.Callsign resolution above).
                var callsignPrefix = string.IsNullOrWhiteSpace(senderName)
                    ? $"This is {session.Callsign}..."
                    : $"{senderName}, this is {session.Callsign}...";
                var addressedReply = $"{callsignPrefix} {reply}";
                Logger.Log($"[Reply] {freqLabel}: \"{addressedReply}\"");

                // Separate, TTS-only version: hyphens in callsigns ("1-1") get read out loud or
                // misheard as "dash"/"minus" by most TTS voices - replacing them with a comma
                // (a natural short pause, not vocalized) makes "Titan 1-1" sound like "Titan
                // one, one" instead. Logging above still shows the original, readable form.
                var spokenSenderName = ForSpeech(senderName);
                var spokenCallsign = ForSpeech(session.Callsign);
                var spokenPrefix = string.IsNullOrWhiteSpace(senderName)
                    ? $"This is {spokenCallsign}..."
                    : $"{spokenSenderName}, this is {spokenCallsign}...";
                var spokenReply = $"{spokenPrefix} {reply}";

                // The reply is ready, so the pilot doesn't need a "standby" anymore. If the
                // acknowledgement is already being transmitted, cancelling doesn't interrupt it -
                // TransmitAsync serializes per radio, so the reply simply follows right after it.
                CancelPendingAck(session, ackCts);

                // IMPORTANT: while the bot is sending its own reply, it must mute ITSELF ON
                // THIS RADIO - otherwise it hears its own reply (relayed back over the same
                // frequency), might mistakenly detect the hotword in it again, and generates a
                // new reply -> infinite loop (audio feedback). Other radios stay fully active.
                // TransmitAsync handles the self-mute, the pre-roll cleanup, and serializing
                // against a possible standby acknowledgement on the same radio.
                await TransmitAsync(session, spokenReply);
            }
            catch (Exception ex)
            {
                // IMPORTANT: an error here (Gemini API, ExternalAudio.exe, etc.) must never let
                // the calling task (watchdog loop or event handler) die unobserved - IsRecording
                // was already reset above, so this radio stays responsive for the next hotword regardless.
                Logger.Log($"[Error] {freqLabel}: failed to process the recording: {ex}");
            }
            finally
            {
                // Covers every remaining path (Gemini/TTS error, empty reply, ...): a pending
                // acknowledgement must never outlive the request it belongs to.
                CancelPendingAck(session, ackCts);
            }
        }

        srs.OnAudioReceived += async (pcm, freq, senderName, senderCoalition) =>
        {
            var key = (long)Math.Round(freq);
            if (!sessions.TryGetValue(key, out var session)) return; // audio on an unmonitored frequency - ignore

            // Completely ignore while the bot itself is replying on THIS radio (see
            // FinalizeRecordingAsync) - otherwise a feedback loop threatens from our own reply
            // being relayed back on the same frequency. Other radios are unaffected.
            if (session.IsTransmittingReply) return;

            session.LastAudioReceivedAt = DateTime.UtcNow;

            // Normalise once: the event's sender name can be absent, and it is used in several
            // places below (callsign parsing, the saved recording's file name).
            senderName ??= "";
            session.LastHeardSenderName = senderName;

            bool justStarted = false;
            lock (session.Lock)
            {
                if (!session.IsRecording)
                {
                    // Keep the pre-roll up to date at all times while not recording.
                    session.PreRollBuffer.AddRange(pcm);
                    int excess = session.PreRollBuffer.Count - preRollMaxBytes;
                    if (excess > 0) session.PreRollBuffer.RemoveRange(0, excess);

                    if (session.Hotword.ProcessAudio(pcm))
                    {
                        session.IsRecording = true;
                        justStarted = true;
                        session.CurrentSenderName = ParseCallsign(senderName, config.PlayerNameCallsignSeparator);
                        session.CurrentSenderRawName = senderName ?? "";
                        session.CurrentSenderCoalition = senderCoalition;
                        session.RecordingBuffer.Clear();
                        session.RecordingBuffer.AddRange(session.PreRollBuffer); // everything said shortly before/during the detection
                        session.PreRollBuffer.Clear();
                        session.SilenceFrames = 0;
                    }
                }
                else
                {
                    session.RecordingBuffer.AddRange(pcm);
                    session.SilenceFrames = IsSilent(pcm) ? session.SilenceFrames + 1 : 0;
                }
            }

            if (justStarted)
            {
                Logger.Log($"[Hotword Detected] {session.FrequencyHz / 1_000_000:0.000} MHz: starting recording " +
                           $"(sender: \"{senderName}\" -> callsign \"{session.CurrentSenderName}\", coalition: {CoalitionLabel(senderCoalition)})...");

                // Start the standby-acknowledgement timer from here, i.e. from the moment the
                // wake word was heard - that's when the pilot starts waiting for an answer.
                if (config.AckEnabled)
                    ScheduleAck(session, session.CurrentSenderName);

                return;
            }

            if (session.IsRecording && session.SilenceFrames >= config.SilenceFramesToStopRecording)
            {
                await FinalizeRecordingAsync(session);
            }
        };

        // Watchdog: ends an active recording even when no further UDP packets arrive at all on
        // that radio (e.g. because the other side ended the transmission completely, without
        // trailing off into silence). Checks every configured radio independently.
        _ = Task.Run(async () =>
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(250, stoppingToken);
                    foreach (var session in sessions.Values)
                    {
                        if (session.IsRecording && DateTime.UtcNow - session.LastAudioReceivedAt > noAudioTimeout)
                        {
                            Logger.Log($"[Watchdog] {session.FrequencyHz / 1_000_000:0.000} MHz: no further audio packets received - ending the recording.");
                            await FinalizeRecordingAsync(session);
                        }
                        else if (config.SaveRecordings && !session.IsRecording)
                        {
                            // Someone transmitted on this frequency and the wake word did NOT
                            // fire. Those are the recordings worth having: a hit can be replayed
                            // from the reply log, a miss leaves no trace anywhere else. The
                            // pre-roll buffer already holds the audio, so this costs nothing
                            // beyond writing the file.
                            SaveMissedTransmission(session, noAudioTimeout);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Even an unexpected error here must not permanently stop the watchdog loop -
                    // otherwise timeout detection would never work again from this point on.
                    Logger.Log($"[Watchdog] Unexpected error: {ex}");
                }
            }
        }, stoppingToken);

        // Threat circles: sweeps run independently of any transmission, and a warning is the one
        // case where the bot starts talking on its own initiative.
        if (threatCircles != null)
        {
            _ = Task.Run(() => threatCircles.RunAsync(async alert =>
            {
                var key = (long)Math.Round(alert.Circle.FrequencyHz);
                if (!sessions.TryGetValue(key, out var session))
                    return; // the radio the circle belongs to is no longer configured

                // Don't cut into a pilot's transmission. Bounded wait, so a recording that never
                // finalizes can't hold the warning back indefinitely - a threat call is
                // time-critical and late is worse than slightly overlapping.
                var waitUntil = DateTime.UtcNow.AddSeconds(10);
                while (session.IsRecording && DateTime.UtcNow < waitUntil)
                    await Task.Delay(200, stoppingToken);

                var spokenPilot = ForSpeech(alert.Circle.PilotCallsign);
                var spokenCallsign = ForSpeech(session.Callsign);
                var prefix = string.IsNullOrWhiteSpace(spokenPilot)
                    ? $"This is {spokenCallsign}..."
                    : $"{spokenPilot}, this is {spokenCallsign}...";

                Logger.Log($"[ThreatCircle] {session.FrequencyHz / 1_000_000:0.000} MHz -> " +
                           $"\"{alert.Circle.PilotCallsign}\": {alert.SpokenText}");

                await TransmitAsync(session, $"{prefix} {ForSpeech(alert.SpokenText)}", stoppingToken);
            }, stoppingToken), stoppingToken);
        }

        var radioSummary = string.Join(", ", radioConfigs.Select(r => $"{r.FrequencyHz / 1_000_000:0.000} MHz ({r.Modulation})"));
        Logger.Log($"Connecting to SRS server {config.SrsHost}:{config.SrsPort}, monitoring: {radioSummary} ...");
        await srs.ConnectAsync(stoppingToken);
        Logger.Log("Connected. Waiting for hotword...");

        DiscordNotifier.Notify(
            $"✅ **{config.BotCallsign}** bot started and connected to SRS `{config.SrsHost}:{config.SrsPort}`, monitoring: {radioSummary}.");

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path (service stop / Ctrl+C) - nothing to do here, cleanup happens in StopAsync.
        }
    }

    /// <summary>
    /// Where recordings go when SaveRecordings is on: next to the executable, so the service and
    /// the console app agree on one place regardless of working directory.
    /// </summary>
    private static string RecordingsDirectory => Path.Combine(AppContext.BaseDirectory, "recordings");

    /// <summary>
    /// Writes one transmission's audio to recordings\ as a WAV file. Best-effort by design: a
    /// full disk or a locked folder must never interfere with the radio work, so failures are
    /// logged at debug level and otherwise ignored.
    /// </summary>
    private static void SaveRecording(byte[] pcm48k, string tag, double frequencyHz, string senderName)
    {
        if (pcm48k.Length < 2) return;

        try
        {
            Directory.CreateDirectory(RecordingsDirectory);

            var safeSender = string.Join("_", (senderName ?? "unknown")
                .Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
            if (safeSender.Length == 0) safeSender = "unknown";
            if (safeSender.Length > 40) safeSender = safeSender[..40];

            var name = $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss.fff}_{frequencyHz / 1_000_000:0.000}MHz_{tag}_{safeSender}.wav";
            File.WriteAllBytes(Path.Combine(RecordingsDirectory, name), WavUtils.WrapPcm16AsWav(pcm48k));
            Logger.Debug($"[Recording] Saved {name} ({pcm48k.Length / 2 / 48000.0:0.0}s).");
        }
        catch (Exception ex)
        {
            Logger.Debug($"[Recording] Could not save a recording: {ex.Message}");
        }
    }

    /// <summary>
    /// A transmission that has gone quiet without the wake word firing: saves the pre-roll so the
    /// miss can be replayed through --test-hotword. Only fires once per transmission, because the
    /// buffer is cleared afterwards.
    /// </summary>
    private static void SaveMissedTransmission(RadioSession session, TimeSpan noAudioTimeout)
    {
        byte[] audio;
        string sender;

        lock (session.Lock)
        {
            // Wait for the same quiet period the recording watchdog uses, so this doesn't fire
            // mid-sentence, and require enough audio to be worth listening to (0.4s).
            if (session.PreRollBuffer.Count < 48000 * 2 * 0.4) return;
            if (DateTime.UtcNow - session.LastAudioReceivedAt <= noAudioTimeout) return;

            audio = session.PreRollBuffer.ToArray();
            session.PreRollBuffer.Clear();
            sender = session.LastHeardSenderName;
        }

        SaveRecording(audio, "missed", session.FrequencyHz, sender);
    }

    private static bool IsSilent(byte[] pcm16)
    {
        if (pcm16.Length < 2) return true;
        long sum = 0;
        int samples = pcm16.Length / 2;
        for (int i = 0; i < samples; i++)
            sum += Math.Abs((int)BitConverter.ToInt16(pcm16, i * 2));
        return (sum / Math.Max(1, samples)) < 500;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Logger.Log("Shutting down...");

        var callsign = _config?.BotCallsign ?? "Bot";
        DiscordNotifier.Notify($"🛑 **{callsign}** bot is shutting down.");

        if (_srs != null)
            await _srs.DisposeAsync();

        _voskModel?.Dispose();

        await base.StopAsync(cancellationToken);

        // Last thing: make sure the shutdown lines themselves are on disk, not just in the OS
        // cache - a service stop can be followed immediately by the process going away.
        Logger.Flush();
    }
}
