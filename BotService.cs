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

        /// <summary>
        /// TTS voice for this radio's replies, resolved once at startup (radio's own, else the
        /// global one, else empty for whatever ExternalAudio picks). Empty is passed through
        /// rather than substituted, so the decision stays in one place.
        /// </summary>
        public string Voice = "";

        public IHotwordDetector Hotword = null!;

        /// <summary>
        /// What this radio is for. Resolved once at startup from the radio's own three-way
        /// setting and the global master switch, so the hot path is a plain bool.
        /// </summary>
        public bool AnswersTactical = true;
        public bool AnswersAirfield = true;

        /// <summary>
        /// Whether this radio tells pilots where other players are. Its own role rather than part
        /// of the tactical one, because it is a different decision - the tactical replies are about
        /// the enemy, this is about your own side.
        /// </summary>
        public bool AnswersFriendlyPosition;

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

    /// <summary>
    /// Pairs each airfield with the tower frequency that serves it: the nearest radio that answers
    /// airfield requests. With one tower per airfield this is the obvious one-to-one mapping; with a
    /// single tower for the whole map every airfield maps to that one, which is also correct, because
    /// the airfield is resolved from the pilot's own position rather than from the frequency.
    /// </summary>
    /// <remarks>
    /// Distance is measured against the frequency's own callsign rather than a configured position,
    /// because a radio has no position: a generated tower carries its airfield's name in its
    /// callsign, so matching on that is exact where it exists and simply falls back to "all airfields
    /// share the one tower" where it does not.
    /// </remarks>
    private static Dictionary<string, double> TowerFrequencyByAirfield(
        IReadOnlyList<Airfield> airfields, IEnumerable<RadioSession> radios)
    {
        var towers = radios.Where(r => r.AnswersAirfield).ToList();
        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        if (towers.Count == 0) return map;

        foreach (var airfield in airfields)
        {
            if (string.IsNullOrWhiteSpace(airfield.Name)) continue;

            // A generated tower is called "<airfield> Tower", so its callsign names the airfield it
            // belongs to. PilotNames.CanonicalKey folds away spaces, case and punctuation, which is
            // what makes "Senaki-Kolkhi" match a callsign shortened to "Senaki Tower".
            var key = PilotNames.CanonicalKey(airfield.Name);

            var own = towers.FirstOrDefault(t =>
            {
                var callsign = PilotNames.CanonicalKey(t.Callsign);
                return callsign.Length >= 4 && (key.StartsWith(callsign, StringComparison.Ordinal) ||
                                                callsign.StartsWith(key, StringComparison.Ordinal));
            });

            // No tower names this airfield, so whatever single tower exists serves it. With several
            // unnamed towers there is no right answer, and naming one would be a guess.
            map[airfield.Name] = own?.FrequencyHz ?? (towers.Count == 1 ? towers[0].FrequencyHz : 0);
        }

        return map;
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

        // Tidy up before doing anything else, so a folder that filled up during the last session
        // is dealt with rather than added to.
        PruneOwnFiles(config);

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

        // Which features exist at all this run. Each radio can then narrow that further - one
        // frequency as the AWACS, another as the tower - but nothing can switch on what the
        // global settings turned off.
        var tacticalGloballyOn = config.DcsIntelEnabled && config.DcsGrpcEnabled;
        var airfieldGloballyOn = tacticalGloballyOn && config.DcsAirfieldEnabled;
        var friendlyGloballyOn = tacticalGloballyOn && config.DcsIntelFriendlyPositionEnabled;

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
            var effectiveVoice = string.IsNullOrWhiteSpace(radioConfig.Voice) ? config.VoiceName : radioConfig.Voice.Trim();

            // Variants follow the wake word, not the radio: a radio with its own Keyword starts
            // from an empty variant list rather than inheriting another word's spellings.
            var acceptedPhrases = HotwordVariants.Resolve(
                radioConfig.Keyword, radioConfig.KeywordVariants,
                config.VoskKeyword, config.VoskKeywordVariants);

            IHotwordDetector hotword = _voskModel != null
                ? new VoskHotwordDetector(_voskModel, effectiveKeyword, config.HotwordAudioFilter,
                    config.HotwordAutoGain, acceptedPhrases.Skip(1))
                : new EnergyThresholdPlaceholderDetector(config.HotwordEnergyThreshold, config.HotwordConsecutiveFramesNeeded);

            if (_voskModel != null)
                Logger.Log($"  {radioConfig.FrequencyHz / 1_000_000:0.000} MHz ({radioConfig.Modulation}): wake word \"{effectiveKeyword}\"" +
                           // Spelled out because an accepted variant is the one thing that can make
                           // a radio react to something that isn't its wake word.
                           (acceptedPhrases.Count > 1
                               ? $" (also: {string.Join(", ", acceptedPhrases.Skip(1).Select(p => $"\"{p}\""))})"
                               : "") + ", " +
                           $"callsign \"{effectiveCallsign}\", answers: {radioConfig.DescribeRole(tacticalGloballyOn, airfieldGloballyOn, friendlyGloballyOn)}" +
                           // Named here because a wrong voice name is otherwise invisible: the
                           // transmission simply doesn't arrive, and nothing says why.
                           (string.IsNullOrWhiteSpace(effectiveVoice) ? "" : $", voice \"{effectiveVoice}\""));

            var key = (long)Math.Round(radioConfig.FrequencyHz);
            sessions[key] = new RadioSession
            {
                FrequencyHz = radioConfig.FrequencyHz,
                Modulation = radioConfig.Modulation,
                Callsign = effectiveCallsign,
                Voice = effectiveVoice,
                Hotword = hotword,
                AnswersTactical = RadioConfig.Answers(radioConfig.AnswerTacticalRequests, tacticalGloballyOn),
                AnswersAirfield = RadioConfig.Answers(radioConfig.AnswerAirfieldRequests, airfieldGloballyOn),
                AnswersFriendlyPosition = RadioConfig.Answers(radioConfig.AnswerFriendlyPositionRequests, friendlyGloballyOn)
            };
        }

        // Who can serve what, for handing a pilot off to the right frequency when they call the
        // wrong one. Built once: the radio set doesn't change while the bot runs.
        var radioRoles = sessions.Values
            .Select(r => (r.FrequencyHz, r.Callsign, Tactical: r.AnswersTactical, Airfield: r.AnswersAirfield,
                          FriendlyPosition: r.AnswersFriendlyPosition))
            .ToList();

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

        // One pilot must not be able to spend the whole Gemini quota and hold the frequency by
        // themselves - see RateLimiter for what that failure looks like from the outside.
        var rateLimiter = new RateLimiter(config.RateLimitMaxRequests, config.RateLimitWindowSeconds);
        if (rateLimiter.Enabled)
            Logger.Log($"Rate limit active: {config.RateLimitMaxRequests} request(s) per pilot " +
                       $"every {config.RateLimitWindowSeconds:0} seconds.");
        else
            Logger.Log("Rate limit disabled (RateLimitMaxRequests is 0) - one pilot can use the whole Gemini quota.");

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
                // The voice travels with the radio, not with the bot: on a mission with an AWACS,
                // a tanker and a tower configured, each answers in its own voice.
                await audioSender.SendTextAsync(spokenText, session.FrequencyHz, session.Modulation, session.Voice);

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

            // Checked here for the same reason the coalition check is: before the Gemini call, which
            // is what costs money, and before the reply, which is what occupies the frequency. The
            // pilot's name is known by now, which is what the limit is per.
            var verdict = rateLimiter.Check(senderRawName, DateTime.UtcNow);
            if (verdict != RateLimiter.Verdict.Allow)
            {
                CancelPendingAck(session, ackCts);

                var retryAfter = rateLimiter.RetryAfter(senderRawName, DateTime.UtcNow);

                if (verdict == RateLimiter.Verdict.RejectAndSay)
                {
                    var limitReply = RateLimiter.BuildReply(config.RateLimitReply, senderName, retryAfter);

                    Logger.Log($"[Rate limit] {freqLabel}: \"{senderRawName}\" is over " +
                               $"{config.RateLimitMaxRequests} request(s) per {config.RateLimitWindowSeconds:0}s - " +
                               $"told to stand by for {retryAfter.TotalSeconds:0}s. " +
                               $"({rateLimiter.TrackedPilots} pilot(s) tracked.)");

                    if (!string.IsNullOrWhiteSpace(limitReply))
                        await TransmitAsync(session, limitReply);
                }
                else
                {
                    // Already told. Saying it again would occupy exactly the frequency this is
                    // protecting, so the transmission is dropped - but it is still logged, because
                    // otherwise a pilot complaining about being ignored leaves no trace.
                    Logger.Log($"[Rate limit] {freqLabel}: \"{senderRawName}\" still over the limit " +
                               $"({retryAfter.TotalSeconds:0}s remaining) - transmission ignored, already advised.");
                }

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

                // "Overlord, radio check." Answered on every radio regardless of its role - a
                // tower, an AWACS and a tanker all answer a radio check, and refusing one because
                // this frequency is "only for airfield requests" would be absurd. So this sits
                // above the role gating rather than inside it.
                //
                // An entry in phrases.json wins, though: somebody who wrote their own radio-check
                // text there meant it, and an upgrade must not silently start answering something
                // else. New installations no longer get that entry by default.
                if (RadioCheck.Matches(config, text) && PhraseBook.TryMatch(text, phrases) == null)
                {
                    var scope = intel != null
                        ? await intel.LookUpScopeStateAsync(senderRawName, text, senderCoalition)
                        : RadioCheck.ScopeState.Unknown;

                    intelReply = RadioCheck.Reply(config, scope);
                    Logger.Log($"[RadioCheck] {freqLabel}: triggered by \"{RadioCheck.MatchedTrigger(config, text)}\", " +
                               $"answered (scope: {scope}).");
                }

                if (intelReply == null && intel != null)
                {
                    var kind = intel.Classify(text, out var matchedTrigger);

                    // Which role this radio needs depends on what was asked. "Where is Springfield
                    // 2-1" is answered by the friendly-position role, everything else here by the
                    // tactical one - a squadron frequency may well have the first and not the second.
                    var radioServesIt = kind switch
                    {
                        IntelRequestKind.None => false,
                        IntelRequestKind.FriendlyPosition => session.AnswersFriendlyPosition,
                        _ => session.AnswersTactical
                    };

                    if (!radioServesIt) kind = IntelRequestKind.None;

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
                if (intelReply == null && airfields != null && session.AnswersAirfield)
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

                // Called the wrong frequency? A real controller doesn't refuse, they hand you off.
                // Only when exactly one other radio serves it - with two towers configured there
                // is no single right answer, and naming one would be a guess dressed as an
                // instruction.
                if (intelReply == null && !string.IsNullOrWhiteSpace(config.WrongChannelReply))
                {
                    RadioCapability? wanted = null;

                    var wrongChannelKind = intel?.Classify(text) ?? IntelRequestKind.None;

                    if (wrongChannelKind == IntelRequestKind.FriendlyPosition && !session.AnswersFriendlyPosition)
                        wanted = RadioCapability.FriendlyPosition;
                    else if (wrongChannelKind != IntelRequestKind.None && !session.AnswersTactical)
                        wanted = RadioCapability.Tactical;
                    else if (airfields != null && !session.AnswersAirfield &&
                             airfields.Classify(text) != AirfieldRequestKind.None)
                        wanted = RadioCapability.Airfield;

                    if (wanted != null)
                    {
                        var handoff = RadioRoles.FindHandoff(radioRoles, session.FrequencyHz, wanted.Value);
                        if (handoff != null)
                        {
                            intelReply = RadioRoles.BuildHandoffReply(config.WrongChannelReply, handoff, config.DcsIntelSlowSpeech);
                            Logger.Log($"[Radio] {freqLabel}: {wanted} request on a radio that doesn't serve it - " +
                                       $"handing off to \"{handoff.Callsign}\" on {handoff.FrequencyHz / 1_000_000:0.000} MHz.");
                        }
                        else
                        {
                            Logger.Log($"[Radio] {freqLabel}: {wanted} request on a radio that doesn't serve it, " +
                                       "and no single other radio does - falling through to phrases/Gemini.");
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

        // NOTE ON THE SHAPE OF THIS HANDLER: the event is an Action, so an async lambda on it is
        // async void. That is deliberate - the UDP read loop must not wait for a reply that takes
        // a Gemini round trip, or packets would pile up in the socket buffer and audio would be
        // lost. The price is that an exception escaping here has nowhere to go and takes the
        // process down with it, so the whole body is wrapped. Anything that throws costs one
        // transmission and a log line, never the bot.
        srs.OnAudioReceived += async (pcm, freq, senderName, senderCoalition) =>
        {
          try
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
          }
          catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
          {
              // Shutting down mid-transmission.
          }
          catch (Exception ex)
          {
              // Unreachable in normal operation - FinalizeRecordingAsync has its own handler -
              // but an async void handler is the one place where "unreachable" means "kills the
              // process", so it is caught here rather than assumed away.
              Logger.Log($"[Error] {freq / 1_000_000:0.000} MHz: audio handling failed - {ex.Message}");
              Logger.Debug(ex.ToString());
          }
        };

        // Watchdog: ends an active recording even when no further UDP packets arrive at all on
        // that radio (e.g. because the other side ended the transmission completely, without
        // trailing off into silence). Checks every configured radio independently.
        var nextPrune = DateTime.UtcNow.AddHours(1);

        _ = Task.Run(async () =>
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(250, stoppingToken);

                    // Piggy-backing on the watchdog rather than starting another timer: it
                    // already ticks, and this needs to happen about once an hour, not exactly.
                    if (DateTime.UtcNow >= nextPrune)
                    {
                        nextPrune = DateTime.UtcNow.AddHours(1);
                        PruneOwnFiles(config);

                        // Otherwise the limiter keeps a record for every name that ever connected.
                        var forgotten = rateLimiter.Prune(DateTime.UtcNow);
                        if (forgotten > 0)
                            Logger.Debug($"[Rate limit] Forgot {forgotten} pilot(s) who stopped transmitting.");
                    }
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
        // Greeting a pilot who tunes in. Subscribed before connecting, so the first SYNC - which
        // carries everybody already on the server - is not missed.
        var greetings = new TuneInGreeting.State();
        if (config.TuneInGreetingEnabled)
        {
            var greetingGap = TimeSpan.FromSeconds(Math.Max(5, config.TuneInGreetingGapSeconds));

            srs.OnClientDisconnected += guid => greetings.Forget(guid);

            srs.OnClientRadiosChanged += client =>
            {
                // Fire and forget: this runs on the TCP receive loop, and blocking it to speak would
                // stall the client list for everybody.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        foreach (var frequency in client.Frequencies)
                        {
                            var key = (long)Math.Round(frequency);
                            if (!sessions.TryGetValue(key, out var session)) continue;

                            // Same rule as everywhere else: the opposing coalition is not served.
                            if (config.RestrictToOwnCoalition && client.Coalition != 0 &&
                                client.Coalition != config.Coalition) continue;

                            if (!greetings.ShouldGreet(client.ClientGuid, frequency, DateTime.UtcNow, greetingGap))
                                continue;

                            var text = TuneInGreeting.Build(
                                PilotNames.DisplayCallsign(client.Name, config.PlayerNameCallsignSeparator),
                                session.Callsign,
                                radioRoles.Where(r => r.Tactical).Select(r => (r.FrequencyHz, r.Callsign)),
                                config);

                            if (string.IsNullOrWhiteSpace(text)) continue;

                            Logger.Log($"[Greeting] {frequency / 1_000_000:0.000} MHz: \"{client.Name}\" tuned in - " +
                                       $"greeting sent ({greetings.Sent} so far this session).");

                            // Goes through the same transmit path as a reply, so it queues behind
                            // whatever that radio is already saying rather than talking over it.
                            await TransmitAsync(session, text, stoppingToken);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Shutting down mid-greeting is not worth a line.
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[Greeting] Failed: {ex.Message}");
                    }
                }, stoppingToken);
            };

            Logger.Log($"Tune-in greeting active: once per pilot per frequency, " +
                       $"at most one every {config.TuneInGreetingGapSeconds:0} seconds per frequency.");
        }

        await srs.ConnectAsync(stoppingToken);
        Logger.Log("Connected. Waiting for hotword...");

        // Tell the pilots which frequencies exist. Deliberately after the SRS connection rather than
        // before: announcing frequencies the bot then fails to monitor would be worse than silence.
        //
        // Not awaited into the startup path either - an F10 marker is not worth delaying the first
        // transmission for, and a mission that is still loading would make this the slowest step in
        // the whole start. It logs its own outcome.
        if (config.AnnounceFrequenciesEnabled && airfields != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var found = await airfields.ListAirfieldsAsync(stoppingToken);

                    // Which tower serves which airfield: the radio closest to it, among those that
                    // answer airfield requests. With one tower per airfield that is the obvious
                    // pairing; with a single tower for everything, every airfield maps to it.
                    var towerByAirfield = TowerFrequencyByAirfield(found, sessions.Values);

                    var announced = sessions.Values
                        .Select(r => (r.FrequencyHz, r.Callsign, Airfield: r.AnswersAirfield))
                        .OrderBy(r => r.FrequencyHz)
                        .ToList();

                    var result = await FrequencyAnnouncer.AnnounceAsync(
                        config, found, towerByAirfield, announced, stoppingToken);

                    if (result.Ok)
                        Logger.Log($"[Announce] {result.MarkersPlaced} F10 marker(s) placed" +
                                   $"{(result.MessageSent ? ", on-screen message sent" : "")}" +
                                   $" ({found.Count} airfield(s) in the mission).");
                    else
                        Logger.Log($"[Announce] {result.Error}");
                }
                catch (OperationCanceledException)
                {
                    // Shutting down during the announcement is not worth a line.
                }
                catch (Exception ex)
                {
                    Logger.Log($"[Announce] Failed: {ex.Message}");
                }
            }, stoppingToken);
        }

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
    /// Keeps the bot's own folders from growing until somebody notices. Run at startup and then
    /// hourly - both are things that only accumulate, so there is no hurry, and doing it rarely
    /// keeps it off the audio path entirely.
    /// </summary>
    private static void PruneOwnFiles(AppConfig config)
    {
        // Recordings only exist when they were asked for, but a folder left behind from an
        // earlier session should still be tidied.
        FileRetention.Prune(RecordingsDirectory, "*.wav",
            config.RecordingRetentionDays, config.RecordingRetentionMaxMb,
            alwaysKeepNewest: 0, label: "recording");

        // Never the newest few logs: the one being written to lives in this folder.
        FileRetention.Prune(Path.Combine(AppContext.BaseDirectory, "logs"), "*.log",
            config.LogRetentionDays, config.LogRetentionMaxMb,
            alwaysKeepNewest: 3, label: "log file");
    }

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

        // Close the shared gRPC connections before the process goes away, so the server sees a
        // clean disconnect rather than a dropped socket.
        DcsGrpcChannels.DisposeAll();

        // Last thing: make sure the shutdown lines themselves are on disk, not just in the OS
        // cache - a service stop can be followed immediately by the process going away.
        Logger.Flush();
    }
}
