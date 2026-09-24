using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Darkstar;

/// <summary>One monitored radio: a frequency, its modulation, and optionally its own wake word/callsign.</summary>
public sealed class RadioConfig
{
    /// <summary>Frequency in Hz, e.g. 251000000 for 251.000 MHz.</summary>
    public double FrequencyHz { get; set; } = 251_000_000;

    /// <summary>"AM" or "FM".</summary>
    public string Modulation { get; set; } = "AM";

    /// <summary>
    /// Wake word for this specific radio (Vosk keyword). Leave empty to use the global
    /// VoskKeyword for this radio instead. Lets different radios respond to different callsigns,
    /// e.g. "Overlord" on the AWACS frequency and "Texaco" on the tanker frequency.
    /// </summary>
    public string Keyword { get; set; } = "";

    /// <summary>
    /// Callsign this radio identifies itself with in replies. Leave empty to use the global
    /// BotCallsign for this radio instead. Usually matches Keyword above (the wake word and the
    /// callsign are typically the same word).
    /// </summary>
    public string Callsign { get; set; } = "";
}

public sealed class AppConfig
{
    /// <summary>
    /// Global logging kill switch. When false, NOTHING is logged anymore (neither console nor
    /// file) - saves I/O and some CPU time. Should stay on for normal operation, relevant e.g.
    /// for performance-critical continuous operation where every saved I/O counts.
    /// </summary>
    public bool LoggingEnabled { get; set; } = true;

    /// <summary>
    /// When true: also shows verbose/raw log messages on the console (every UDP packet,
    /// raw TCP JSON lines, volume readings, ...). When false (default), the console stays
    /// compact and only shows the essential events - the full details are still ALWAYS
    /// written to the log file under logs/, regardless of this setting.
    /// </summary>
    public bool DebugLogging { get; set; } = false;

    /// <summary>
    /// Master switch for Discord status notifications (bot started, SRS connection lost/
    /// restored, shutdown). Default false - notifications are entirely opt-in. When true,
    /// DiscordWebhookUrl must also be set to a valid webhook URL.
    /// </summary>
    public bool DiscordEnabled { get; set; } = false;

    /// <summary>
    /// Discord webhook URL for status notifications. Only used when DiscordEnabled is true.
    /// Create one in Discord under a channel's Settings -> Integrations -> Webhooks -> New Webhook.
    /// </summary>
    public string DiscordWebhookUrl { get; set; } = "";

    public string SrsHost { get; set; } = "127.0.0.1";
    public int SrsPort { get; set; } = 5002;

    /// <summary>
    /// Legacy single-frequency setting. Still supported for backward compatibility - if
    /// "Radios" below is left empty, the bot builds a single-radio list from FrequencyHz +
    /// Modulation automatically. Once you add entries to "Radios", these two fields are ignored.
    /// </summary>
    public double FrequencyHz { get; set; } = 251_000_000;

    /// <summary>See FrequencyHz - legacy single-radio fallback, ignored once "Radios" has entries.</summary>
    public string Modulation { get; set; } = "AM";

    /// <summary>
    /// The radios (frequency + modulation pairs) the bot monitors simultaneously. Each one gets
    /// its own independent hotword detector, recording state, and reply channel - a hotword said
    /// on one frequency doesn't affect the others. Leave empty to fall back to the legacy single
    /// FrequencyHz/Modulation fields above. Keyword/Callsign are optional per radio (fall back to
    /// VoskKeyword/BotCallsign below if left empty). Example with two radios, each with its own identity:
    /// "Radios": [ { "FrequencyHz": 251000000, "Modulation": "AM", "Keyword": "Overlord", "Callsign": "Overlord" },
    ///             { "FrequencyHz": 127500000, "Modulation": "AM", "Keyword": "Texaco", "Callsign": "Texaco" } ]
    /// </summary>
    public List<RadioConfig> Radios { get; set; } = new();

    public string ClientName { get; set; } = "DARKSTAR";

    /// <summary>Callsign the bot uses to identify itself in its replies, e.g. "Overlord".</summary>
    public string BotCallsign { get; set; } = "Overlord";

    /// <summary>
    /// Separator used to parse the pilot's callsign out of their SRS player name, e.g. with the
    /// default "|": a player named "Enfield 1-1 | neodym" is addressed as "Enfield 1-1" in
    /// replies - the part after the separator (typically the player's real name/handle) is
    /// ignored entirely. If the separator isn't found in a given player's name, the full name is
    /// used as-is, so players not following this naming convention still get addressed normally.
    /// </summary>
    public string PlayerNameCallsignSeparator { get; set; } = "|";

    /// <summary>
    /// Master switch for the DCS-gRPC connection (real mission data - bogey dope, picture,
    /// bullseye calls, etc.). Default false - not used yet by the reply pipeline itself, this is
    /// currently only wired up for the GUI's connection test. Enable once you actually start
    /// building features on top of it.
    /// </summary>
    public bool DcsGrpcEnabled { get; set; } = false;

    /// <summary>Address of the DCS-gRPC server, e.g. "http://127.0.0.1:50051". Must include the http:// scheme.</summary>
    public string DcsGrpcAddress { get; set; } = "http://127.0.0.1:50051";

    /// <summary>Only needed if DCS-gRPC has authentication enabled on the server (GRPC.authorization_required). Leave empty otherwise.</summary>
    public string DcsGrpcApiKey { get; set; } = "";

    /// <summary>
    /// Lets the bot answer tactical requests ("bogey dope", "picture", "threat") from live
    /// mission data instead of from phrases.json/Gemini. Requires DcsGrpcEnabled and a running
    /// DCS-gRPC server. Default false.
    /// </summary>
    public bool DcsIntelEnabled { get; set; } = false;

    /// <summary>
    /// Name (as in the mission editor) of the unit whose sensors decide what the bot may report -
    /// typically an AI AWACS. Only contacts that unit's group actually detects are reported.
    /// Leave empty to always use plain mission data instead ("god's eye": every hostile aircraft,
    /// detected or not). NOTE: DCS only fills the detection table for AI-controlled units, so a
    /// player-flown AWACS reports nothing - the bot then falls back to mission data as well.
    /// </summary>
    public string DcsIntelAwacsUnitName { get; set; } = "";

    /// <summary>
    /// Where hostile contacts may come from. One of:
    ///
    ///   "AwacsThenMissionData" (default) - use the AWACS unit's sensors, but fall back to plain
    ///       mission data when no unit is configured, the unit doesn't exist, the call fails, or
    ///       its detection table is empty. Never silently reports a clean picture, at the price of
    ///       quietly giving god's-eye information while the AWACS sees nothing.
    ///
    ///   "AwacsOnly" - report strictly what the configured unit detects; "detects nothing" is
    ///       answered as a clean picture. God's eye is never used. Needs DcsIntelAwacsUnitName to
    ///       be set, and note that DCS only fills the detection table for AI-controlled units - a
    ///       player-flown AWACS will always look blind in this mode.
    ///
    ///   "MissionDataOnly" - always use plain mission data and ignore DcsIntelAwacsUnitName.
    ///
    /// Anything else is treated as the default, with a warning in the log.
    /// </summary>
    public string DcsIntelContactSource { get; set; } = "AwacsThenMissionData";

    /// <summary>Contacts further away than this (in nautical miles) are not reported. 0 disables the limit.</summary>
    public double DcsIntelMaxRangeNm { get; set; } = 120;

    /// <summary>How many hostile groups a "picture" call reports at most before summarizing the rest.</summary>
    public int DcsIntelMaxGroups { get; set; } = 3;

    /// <summary>
    /// Report magnetic bearings (what the pilot reads on their instruments) instead of true
    /// bearings. Uses the map's magnetic declination from DCS-gRPC.
    /// </summary>
    public bool DcsIntelMagneticBearings { get; set; } = true;

    /// <summary>Include hostile helicopters, not just fixed-wing aircraft.</summary>
    public bool DcsIntelIncludeHelicopters { get; set; } = true;

    /// <summary>
    /// Name the contact's aircraft/helicopter type in replies ("type MiG-29"). DCS type ids carry
    /// variant suffixes that are meaningless on the radio ("F-16C_50"), so everything from the
    /// first underscore on is dropped before speaking. In a picture call, a group flying more
    /// than one type is announced as "mixed, lead &lt;type&gt;".
    /// </summary>
    public bool DcsIntelSayContactType { get; set; } = true;

    /// <summary>
    /// Paces the spoken tactical replies so the numbers stay intelligible: bearings get a comma
    /// between their digits (every TTS engine pauses briefly at a comma, instead of running
    /// "zeroninerzero" together), and ranges, altitudes and counts are spelled out as words
    /// ("thirty five miles" rather than a rattled-off "35"). Turn it off for the terser,
    /// faster-sounding phrasing.
    /// </summary>
    public bool DcsIntelSlowSpeech { get; set; } = true;

    /// <summary>Per-call timeout in seconds for the DCS-gRPC queries behind a tactical reply.</summary>
    public int DcsIntelTimeoutSeconds { get; set; } = 5;

    /// <summary>Phrases (case-insensitive, matched anywhere in the transmission) that request the nearest hostile contact with full BRAA.</summary>
    public List<string> DcsIntelBogeyDopeTriggers { get; set; } = new()
    {
        "bogey dope", "bogie dope", "bogey dobe", "boogie dope", "nearest bandit", "closest contact"
    };

    /// <summary>Phrases that request an overview of the hostile air picture.</summary>
    public List<string> DcsIntelPictureTriggers { get; set; } = new()
    {
        "picture", "request picture", "say picture"
    };

    /// <summary>Phrases that request a short "anything near me?" answer.</summary>
    public List<string> DcsIntelThreatTriggers { get; set; } = new()
    {
        "threat check", "any threats", "threats"
    };

    /// <summary>
    /// Phrases that force positions to be given from the bullseye even when the bot could give
    /// BRAA from the pilot's own aircraft (e.g. "bogey dope bullseye").
    /// </summary>
    public List<string> DcsIntelBullseyeTriggers { get; set; } = new() { "bullseye" };

    /// <summary>
    /// Lets a pilot request a standing threat watch ("Overlord, threat circle forty miles"): the
    /// bot then keeps checking a circle of that radius around the pilot's own aircraft - it moves
    /// with them - and warns them on the same frequency as soon as a hostile aircraft or
    /// helicopter enters it. Each contact is reported at most once per circle.
    /// </summary>
    public bool DcsIntelThreatCircleEnabled { get; set; } = true;

    /// <summary>Radius used when the pilot doesn't name one in the request, in nautical miles.</summary>
    public double DcsIntelThreatCircleDefaultRadiusNm { get; set; } = 40;

    /// <summary>Upper limit for a requested radius, in nautical miles.</summary>
    public double DcsIntelThreatCircleMaxRadiusNm { get; set; } = 150;

    /// <summary>
    /// Seconds between two sweeps of all active circles. Every sweep costs one set of DCS-gRPC
    /// queries per circle, so don't set this too low on a busy server.
    /// </summary>
    public int DcsIntelThreatCirclePollSeconds { get; set; } = 15;

    /// <summary>A circle ends automatically after this many minutes, so a forgotten one can't run forever.</summary>
    public int DcsIntelThreatCircleDurationMinutes { get; set; } = 30;

    /// <summary>How many pilots can have a circle running at the same time.</summary>
    public int DcsIntelThreatCircleMaxActive { get; set; } = 8;

    /// <summary>
    /// How many warnings one sweep may transmit per circle. A whole package entering at once
    /// would otherwise occupy the frequency; the remaining contacts are reported on the
    /// following sweeps instead of being dropped.
    /// </summary>
    public int DcsIntelThreatCircleMaxAlertsPerSweep { get; set; } = 2;

    /// <summary>Phrases that start a threat circle. A number in the same transmission sets the radius.</summary>
    public List<string> DcsIntelThreatCircleTriggers { get; set; } = new()
    {
        "threat circle", "threat ring", "set threat circle"
    };

    /// <summary>Phrases that cancel the pilot's own threat circle. Checked before the start triggers.</summary>
    public List<string> DcsIntelThreatCircleCancelTriggers { get; set; } = new()
    {
        "cancel threat circle", "stop threat circle", "threat circle off", "cancel threat ring"
    };

    /// <summary>Spoken when a circle could not be set up because the pilot's aircraft wasn't found.</summary>
    public string DcsIntelThreatCircleNoPilotReply { get; set; } =
        "Unable to set the threat circle, cannot locate your aircraft.";

    /// <summary>Spoken when the maximum number of simultaneous circles is already in use.</summary>
    public string DcsIntelThreatCircleBusyReply { get; set; } =
        "Unable, too many threat circles active at the moment.";

    /// <summary>Spoken when a circle was cancelled.</summary>
    public string DcsIntelThreatCircleCancelledReply { get; set; } = "Threat circle cancelled.";

    /// <summary>Spoken when a cancel request comes in but nothing is running for that pilot.</summary>
    public string DcsIntelThreatCircleNoneActiveReply { get; set; } = "No threat circle active for you.";

    /// <summary>Reply when no hostile aircraft match the filters.</summary>
    public string DcsIntelNoContactsReply { get; set; } = "Picture clean.";

    /// <summary>Reply when the mission data could not be read (DCS-gRPC down, mission not running, call failed).</summary>
    public string DcsIntelUnavailableReply { get; set; } = "Negative, no tactical data available at this time.";

    /// <summary>
    /// When true: the bot ONLY responds to phrases defined as triggers in phrases.json.
    /// Everything else gets "FallbackResponse" instead of a freely generated Gemini reply.
    /// When false: if no phrase matches, the Gemini-generated reply is used instead (free conversation).
    /// </summary>
    public bool RestrictToKnownPhrases { get; set; } = true;

    /// <summary>Fixed reply used when RestrictToKnownPhrases is active and none of the configured phrases match.</summary>
    public string FallbackResponse { get; set; } = "Sorry, cannot answer that for you.";

    /// <summary>
    /// Sends a short "message received, standby" acknowledgement when transcription + reply
    /// generation take longer than AckAfterSeconds, so the pilot knows their call was picked up
    /// instead of wondering whether the bot heard them at all. Default false (opt-in): it costs
    /// one extra transmission per slow request, which also blocks the frequency for a moment.
    /// </summary>
    public bool AckEnabled { get; set; } = false;

    /// <summary>
    /// How long (in seconds, measured from the moment the wake word was detected) the bot may
    /// stay silent before it sends the acknowledgement. Only ever sent while a reply is still
    /// being worked on - if the real reply is ready sooner, no acknowledgement is sent at all.
    /// The bot never talks over the pilot: if the pilot is still transmitting when this time is
    /// up, the acknowledgement goes out right after their transmission ends.
    /// </summary>
    public double AckAfterSeconds { get; set; } = 4.0;

    /// <summary>
    /// Text of the acknowledgement. Placeholders: {pilot} = the requesting pilot's callsign
    /// (parsed out of their SRS player name using PlayerNameCallsignSeparator, so "Enfield 1-1 |
    /// neodym" becomes "Enfield 1-1"), {callsign} = the replying radio's own callsign. If the
    /// pilot's name is unknown, "{pilot}, " is dropped from the text automatically.
    /// </summary>
    public string AckMessage { get; set; } = "{pilot}, this is {callsign}, message received, standby.";

    /// <summary>0 = Spectator, 1 = Red, 2 = Blue. The bot's own coalition.</summary>
    public int Coalition { get; set; } = 2;

    /// <summary>
    /// When true: the bot checks the requester's coalition (learned from the SRS client list)
    /// against its own Coalition setting. Requests from the opposing coalition are ignored
    /// entirely (no reply sent at all - realistic radio security behavior) instead of being
    /// answered like any other transmission. Requests from an unknown/spectator coalition are
    /// still answered normally. Default false - coalition is otherwise ignored.
    /// </summary>
    public bool RestrictToOwnCoalition { get; set; } = false;

    /// <summary>Only needed if the server requires EXTERNAL_AWACS_MODE with a password. Leave empty if not needed.</summary>
    public string ExternalAwacsPassword { get; set; } = "";

    /// <summary>Path to DCS-SR-ExternalAudio.exe, usually located in the SRS server install folder.</summary>
    public string ExternalAudioExePath { get; set; } = @"C:\Program Files\DCS-SimpleRadio-Standalone\Server\DCS-SR-ExternalAudio.exe";

    /// <summary>
    /// Extra command-line arguments appended to every DCS-SR-ExternalAudio.exe call, for options
    /// this bot doesn't set itself. Whether your SRS build offers a speaking-rate option (and
    /// what it is called) depends on its version - run "DCS-SR-ExternalAudio.exe --help" to see
    /// the list, then put the flag here, e.g. "--speed=-1". Left empty by default; a wrong flag
    /// makes the tool refuse the call, which shows up in the log as an [ExternalAudio] error.
    /// </summary>
    public string ExternalAudioExtraArgs { get; set; } = "";

    /// <summary>Volume threshold for the placeholder hotword detector (0-32767). Only relevant as a fallback if VoskModelPath is left empty.</summary>
    public short HotwordEnergyThreshold { get; set; } = 2000;

    /// <summary>How many consecutive "loud" 20ms frames are needed before the hotword counts as detected. Only relevant for the volume fallback.</summary>
    public int HotwordConsecutiveFramesNeeded { get; set; } = 5;

    /// <summary>Path to the unpacked Vosk model folder (offline wake word detection, no account needed). Download: https://alphacephei.com/vosk/models</summary>
    public string VoskModelPath { get; set; } = "";

    /// <summary>Keyword the continuously transcribed text is checked against, e.g. "computer".</summary>
    public string VoskKeyword { get; set; } = "computer";

    /// <summary>How many consecutive "silent" 20ms frames end the recording (silence detection after the hotword).</summary>
    public int SilenceFramesToStopRecording { get; set; } = 50;

    /// <summary>API key for the Google Gemini API (speech-to-text + reply generation). https://aistudio.google.com/apikey</summary>
    public string GeminiApiKey { get; set; } = "";

    /// <summary>
    /// Gemini model used for transcription and reply generation. Flash-Lite variants usually
    /// have a significantly higher daily limit in the free tier than the large Flash models -
    /// if you still hit a 429/RESOURCE_EXHAUSTED quota error, check Google AI Studio under your
    /// project to see what currently applies to your account.
    /// </summary>
    public string GeminiModel { get; set; } = "gemini-3.5-flash-lite";

    /// <summary>
    /// Optional second Gemini model to try if GeminiModel keeps failing after all retries
    /// (e.g. its daily quota is exhausted). Leave empty to disable the fallback. Useful to set
    /// this to a different model than GeminiModel, e.g. a larger one with its own separate quota.
    /// </summary>
    public string GeminiFallbackModel { get; set; } = "";

    /// <summary>How many times to retry a Gemini call after a transient error (429/500/502/503/504) before giving up on that model.</summary>
    public int GeminiMaxRetries { get; set; } = 2;

    /// <summary>Delay in milliseconds between Gemini retry attempts.</summary>
    public int GeminiRetryDelayMs { get; set; } = 1000;

    /// <summary>
    /// Name of the TTS voice used for the reply output (Windows TTS voice name, e.g. "Microsoft David Desktop").
    /// Leave empty for the server's default voice. List available voices with
    /// "DCS-SR-ExternalAudio.exe --help".
    /// </summary>
    public string VoiceName { get; set; } = "";

    /// <summary>
    /// How many seconds of audio are always kept before a hotword detection and prepended to
    /// the recording on a match (pre-roll). Prevents the start of the actual message from being
    /// cut off because the hotword detection takes a bit of time before it triggers.
    /// </summary>
    public double PreRollSeconds { get; set; } = 2.0;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    /// <summary>
    /// Returns the effective list of radios to monitor: "Radios" if it has entries, otherwise a
    /// single-item list built from the legacy FrequencyHz/Modulation fields. Always returns at
    /// least one entry.
    /// </summary>
    public List<RadioConfig> GetEffectiveRadios()
    {
        if (Radios.Count > 0) return Radios;
        return new List<RadioConfig> { new() { FrequencyHz = FrequencyHz, Modulation = Modulation } };
    }

    /// <summary>
    /// Checks whether configPath exists. If not: creates a default configuration there and
    /// returns null (signal to Program.cs to exit). If the file exists: loads, validates, and
    /// returns it. Any problem found (broken JSON, unknown leftover fields, out-of-range values)
    /// is written to the log (console + logs/ file) so it's never silently swallowed.
    /// </summary>
    /// <summary>
    /// Writes this config back to configPath, backing up the previous version first (see
    /// BackupUtils). Used by the GUI editor when the user saves changes - the bot service itself
    /// never calls this directly (it only ever reads config.json, except for the automatic
    /// missing-field merge in MergeMissingFields above).
    /// </summary>
    public void Save(string configPath)
    {
        BackupUtils.BackupBeforeWrite(configPath);
        var json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(configPath, json);
    }

    public static AppConfig? LoadOrCreateDefault(string configPath)
    {
        if (!File.Exists(configPath))
        {
            var defaultConfig = new AppConfig();
            var json = JsonSerializer.Serialize(defaultConfig, JsonOptions);
            File.WriteAllText(configPath, json);

            Logger.Log($"No configuration file was found.");
            Logger.Log($"A new file with default values was created: {Path.GetFullPath(configPath)}");
            Logger.Log("Please adjust the values in it to your environment (SRS server, frequency, path to DCS-SR-ExternalAudio.exe, ...) and restart the bot.");
            return null;
        }

        var text = File.ReadAllText(configPath);

        AppConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<AppConfig>(text, JsonOptions);
        }
        catch (JsonException ex)
        {
            // Without this try/catch, a syntax error in config.json (missing comma, stray
            // bracket, ...) would crash the whole process with an unhandled exception instead
            // of failing gracefully.
            Logger.Log($"ERROR: config.json has a JSON syntax error and could not be parsed: {ex.Message}");
            Logger.Log($"Please fix the syntax in {Path.GetFullPath(configPath)}, or delete the file so it gets recreated with default values.");
            return null;
        }

        if (config == null)
        {
            Logger.Log($"Could not read {configPath} (empty or 'null' content). Please check the file or delete it so it gets recreated with default values.");
            return null;
        }

        MergeMissingFields(configPath, text, config);
        ValidateValues(config);

        return config;
    }

    /// <summary>
    /// Compares the top-level fields present in the file against the current schema (the
    /// properties of this class). Missing fields (because the code now knows new options) are
    /// added back into the file with their default values - existing values remain unchanged,
    /// since "config" is already populated with the loaded values and we're just writing back
    /// this already-merged state. Fields present in the file but no longer used by the code
    /// (e.g. leftovers from a removed feature) are reported as a warning, not deleted - they're
    /// harmless, but worth knowing about so you can clean them up.
    /// </summary>
    private static void MergeMissingFields(string configPath, string originalText, AppConfig config)
    {
        using var doc = JsonDocument.Parse(originalText);
        var existingFieldNames = doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var allFieldNames = typeof(AppConfig)
            .GetProperties()
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var newFieldNames = allFieldNames.Where(name => !existingFieldNames.Contains(name)).ToList();
        var obsoleteFieldNames = existingFieldNames.Where(name => !allFieldNames.Contains(name)).ToList();

        if (obsoleteFieldNames.Count > 0)
        {
            Logger.Log($"WARNING: config.json contains fields that are no longer used by the bot (safe to remove): {string.Join(", ", obsoleteFieldNames)}");
        }

        if (newFieldNames.Count == 0) return;

        BackupUtils.BackupBeforeWrite(configPath);

        var mergedJson = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(configPath, mergedJson);

        Logger.Log($"config.json was extended with new settings (using default values): {string.Join(", ", newFieldNames)}");
        Logger.Log("Your existing values were not changed. Adjust the new fields in config.json if needed.");
    }

    /// <summary>
    /// Sanity-checks the loaded values and logs a warning for anything that looks wrong (out of
    /// range, missing required paths, etc.). This never changes the config or stops the bot -
    /// it just makes misconfiguration visible in the log instead of failing silently later
    /// (e.g. a bad SrsPort just leading to "connection refused" with no obvious cause).
    /// </summary>
    private static void ValidateValues(AppConfig config)
    {
        void Warn(string message) => Logger.Log($"WARNING: config.json - {message}");

        // Only check the Discord settings at all if the feature is actually turned on - an
        // empty/invalid DiscordWebhookUrl is irrelevant noise while DiscordEnabled is false.
        if (config.DiscordEnabled)
        {
            if (string.IsNullOrWhiteSpace(config.DiscordWebhookUrl))
            {
                Warn("DiscordEnabled is true, but DiscordWebhookUrl is empty - notifications will not work. " +
                     "Either set a webhook URL or set DiscordEnabled back to false.");
            }
            else if (!config.DiscordWebhookUrl.StartsWith("https://discord.com/api/webhooks/", StringComparison.OrdinalIgnoreCase) &&
                     !config.DiscordWebhookUrl.StartsWith("https://discordapp.com/api/webhooks/", StringComparison.OrdinalIgnoreCase))
            {
                Warn($"DiscordWebhookUrl ('{config.DiscordWebhookUrl}') doesn't look like a Discord webhook URL " +
                     "(expected to start with https://discord.com/api/webhooks/) - notifications will likely fail.");
            }
        }

        if (string.IsNullOrWhiteSpace(config.SrsHost))
            Warn("SrsHost is empty.");

        if (config.SrsPort is < 1 or > 65535)
            Warn($"SrsPort ({config.SrsPort}) is not a valid port number (1-65535).");

        var effectiveRadios = config.GetEffectiveRadios();
        if (effectiveRadios.Count == 0)
        {
            Warn("No radios configured at all (neither FrequencyHz/Modulation nor Radios) - the bot has nothing to listen to.");
        }
        else
        {
            for (int i = 0; i < effectiveRadios.Count; i++)
            {
                var radio = effectiveRadios[i];
                var label = config.Radios.Count > 0 ? $"Radios[{i}]" : "FrequencyHz/Modulation";

                if (radio.FrequencyHz <= 0)
                    Warn($"{label}: FrequencyHz ({radio.FrequencyHz}) should be a positive number of Hz, e.g. 251000000 for 251.000 MHz.");

                if (!string.Equals(radio.Modulation, "AM", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(radio.Modulation, "FM", StringComparison.OrdinalIgnoreCase))
                    Warn($"{label}: Modulation ('{radio.Modulation}') should be \"AM\" or \"FM\".");
            }

            var duplicateFrequencies = effectiveRadios
                .GroupBy(r => r.FrequencyHz)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key);
            foreach (var freq in duplicateFrequencies)
                Warn($"Radios contains the frequency {freq} Hz more than once - only one entry per frequency is needed.");
        }

        if (config.Coalition is < 0 or > 2)
            Warn($"Coalition ({config.Coalition}) should be 0 (Spectator), 1 (Red), or 2 (Blue).");

        if (config.RestrictToOwnCoalition && config.Coalition == 0)
            Warn("RestrictToOwnCoalition is true, but Coalition is 0 (Spectator) - this would reject every requester, since nobody is on the Spectator coalition.");

        if (string.IsNullOrWhiteSpace(config.ClientName))
            Warn("ClientName is empty.");

        if (string.IsNullOrWhiteSpace(config.BotCallsign))
            Warn("BotCallsign is empty.");

        if (string.IsNullOrEmpty(config.PlayerNameCallsignSeparator))
            Warn("PlayerNameCallsignSeparator is empty - pilot names will never be shortened, the bot will always use the full raw SRS player name in replies.");

        if (config.DcsGrpcEnabled)
        {
            if (string.IsNullOrWhiteSpace(config.DcsGrpcAddress))
                Warn("DcsGrpcEnabled is true, but DcsGrpcAddress is empty.");
            else if (!config.DcsGrpcAddress.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                     !config.DcsGrpcAddress.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                Warn($"DcsGrpcAddress ('{config.DcsGrpcAddress}') should include the scheme, e.g. \"http://127.0.0.1:50051\".");
        }

        if (string.IsNullOrWhiteSpace(config.ExternalAudioExePath))
            Warn("ExternalAudioExePath is empty - the bot won't be able to send any replies.");
        else if (!File.Exists(config.ExternalAudioExePath))
            Warn($"ExternalAudioExePath ('{config.ExternalAudioExePath}') does not exist on disk.");

        if (!string.IsNullOrWhiteSpace(config.VoskModelPath) && !Directory.Exists(config.VoskModelPath))
            Warn($"VoskModelPath ('{config.VoskModelPath}') does not exist on disk.");

        if (config.HotwordEnergyThreshold is < 0 or > 32767)
            Warn($"HotwordEnergyThreshold ({config.HotwordEnergyThreshold}) should be between 0 and 32767.");

        if (config.HotwordConsecutiveFramesNeeded <= 0)
            Warn($"HotwordConsecutiveFramesNeeded ({config.HotwordConsecutiveFramesNeeded}) should be greater than 0.");

        if (config.SilenceFramesToStopRecording <= 0)
            Warn($"SilenceFramesToStopRecording ({config.SilenceFramesToStopRecording}) should be greater than 0.");

        if (config.PreRollSeconds < 0)
            Warn($"PreRollSeconds ({config.PreRollSeconds}) should not be negative.");

        if (string.IsNullOrWhiteSpace(config.GeminiApiKey))
            Warn("GeminiApiKey is empty - transcription/reply generation will not work.");

        if (string.IsNullOrWhiteSpace(config.GeminiModel))
            Warn("GeminiModel is empty.");

        if (config.GeminiMaxRetries < 0)
            Warn($"GeminiMaxRetries ({config.GeminiMaxRetries}) should not be negative.");

        if (config.GeminiRetryDelayMs < 0)
            Warn($"GeminiRetryDelayMs ({config.GeminiRetryDelayMs}) should not be negative.");

        if (string.IsNullOrWhiteSpace(config.FallbackResponse))
            Warn("FallbackResponse is empty.");

        if (config.DcsIntelEnabled)
        {
            if (!config.DcsGrpcEnabled)
                Warn("DcsIntelEnabled is true but DcsGrpcEnabled is false - tactical requests (bogey dope/picture/threat) cannot be answered without DCS-gRPC.");

            var sourceMode = (config.DcsIntelContactSource ?? "").Trim();
            if (!string.Equals(sourceMode, "AwacsThenMissionData", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(sourceMode, "AwacsOnly", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(sourceMode, "MissionDataOnly", StringComparison.OrdinalIgnoreCase))
            {
                Warn($"DcsIntelContactSource (\"{config.DcsIntelContactSource}\") is not one of AwacsThenMissionData, AwacsOnly, MissionDataOnly - " +
                     "falling back to AwacsThenMissionData.");
            }
            else if (string.Equals(sourceMode, "AwacsOnly", StringComparison.OrdinalIgnoreCase)
                     && string.IsNullOrWhiteSpace(config.DcsIntelAwacsUnitName))
            {
                Warn("DcsIntelContactSource is AwacsOnly but DcsIntelAwacsUnitName is empty - tactical requests will be answered " +
                     "with \"no tactical data\" until a unit is named.");
            }

            if (config.DcsIntelMaxRangeNm < 0)
                Warn($"DcsIntelMaxRangeNm ({config.DcsIntelMaxRangeNm}) should not be negative (use 0 to disable the range limit).");

            if (config.DcsIntelMaxGroups <= 0)
                Warn($"DcsIntelMaxGroups ({config.DcsIntelMaxGroups}) should be at least 1.");

            if (config.DcsIntelTimeoutSeconds <= 0)
                Warn($"DcsIntelTimeoutSeconds ({config.DcsIntelTimeoutSeconds}) should be greater than 0.");

            var triggerCount = (config.DcsIntelBogeyDopeTriggers?.Count ?? 0)
                             + (config.DcsIntelPictureTriggers?.Count ?? 0)
                             + (config.DcsIntelThreatTriggers?.Count ?? 0);
            if (triggerCount == 0)
                Warn("DcsIntelEnabled is true but no trigger phrases are configured - no transmission will ever be recognized as a tactical request.");

            if (config.DcsIntelThreatCircleEnabled)
            {
                if (config.DcsIntelThreatCircleDefaultRadiusNm <= 0)
                    Warn($"DcsIntelThreatCircleDefaultRadiusNm ({config.DcsIntelThreatCircleDefaultRadiusNm}) must be greater than 0.");

                if (config.DcsIntelThreatCircleMaxRadiusNm < config.DcsIntelThreatCircleDefaultRadiusNm)
                    Warn($"DcsIntelThreatCircleMaxRadiusNm ({config.DcsIntelThreatCircleMaxRadiusNm}) is smaller than the default radius " +
                         $"({config.DcsIntelThreatCircleDefaultRadiusNm}) - every circle will be clamped down to the maximum.");

                if (config.DcsIntelThreatCirclePollSeconds < 5)
                    Warn($"DcsIntelThreatCirclePollSeconds ({config.DcsIntelThreatCirclePollSeconds}) is below the 5 second minimum and will be raised to it.");

                if (config.DcsIntelThreatCircleDurationMinutes <= 0)
                    Warn($"DcsIntelThreatCircleDurationMinutes ({config.DcsIntelThreatCircleDurationMinutes}) must be greater than 0.");

                if ((config.DcsIntelThreatCircleTriggers?.Count ?? 0) == 0)
                    Warn("DcsIntelThreatCircleEnabled is true but no trigger phrases are configured - a threat circle can never be requested.");
            }
        }

        if (config.AckEnabled)
        {
            if (config.AckAfterSeconds <= 0)
                Warn($"AckAfterSeconds ({config.AckAfterSeconds}) must be greater than 0 - the acknowledgement would otherwise be sent before the pilot has even finished speaking.");

            if (string.IsNullOrWhiteSpace(config.AckMessage))
                Warn("AckEnabled is true but AckMessage is empty - no acknowledgement will be sent.");
        }
    }
}
