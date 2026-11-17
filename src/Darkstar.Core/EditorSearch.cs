using System.Globalization;
using System.Text;

namespace Darkstar;

/// <summary>What a search result points at: a whole page, a card on it, a switch or one field.</summary>
public enum SearchKind
{
    Page,
    Card,
    Switch,
    Field,
}

/// <summary>
/// One thing the config editor's search can find.
/// </summary>
/// <param name="Page">The panel: the editor's own key for it ("connection", "dcsgrpc", ...).</param>
/// <param name="Section">For CH8, which of its three parts; null everywhere else.</param>
/// <param name="Label">The text exactly as the panel shows it - how the editor finds it on screen.</param>
/// <param name="Title">The same, readable in a result list ("Chat bridge folder", not "CHAT BRIDGE FOLDER").</param>
/// <param name="Card">The card the field sits in, as shown - for telling apart two fields of one name.</param>
/// <param name="Keywords">The settings behind it, by their config.json names, and other words that should find it.</param>
public sealed record SearchTarget(
    string Page,
    string? Section,
    SearchKind Kind,
    string Label,
    string Title,
    string? Card,
    string Keywords)
{
    /// <summary>Where it is, the way the sidebar and the manual name it: "CH8 Replies › Threat circle".</summary>
    public string Where => EditorSearch.PageName(Page, Section) + (Card != null ? " › " + Card : "");
}

/// <summary>
/// The config editor's search: type "threat", "Funkrad" or "ChatBridgeFolder" and get the cards
/// and fields that match, each with the page it is on.
///
/// The index is a table rather than something read from the panels at run time, because the
/// panels are Razor markup that only exists once rendered, and only the open one is. It was
/// generated from the panels and is kept honest by EditorSearchTests: every label in it has to be
/// on its page, and every setting a panel uses has to be findable by its config.json name and
/// land on that panel. A field added without an entry fails the suite instead of quietly being
/// unfindable.
///
/// Lives in Darkstar.Core, not in the editor, so that the suite - which cannot load a WPF project -
/// can run it.
/// </summary>
public static class EditorSearch
{
    /// <summary>The most results the sidebar shows; more than that is a query worth refining.</summary>
    public const int MaxResults = 10;

    /// <summary>The page names as the sidebar shows them, for the "where" line of a result.</summary>
    public static string PageName(string page, string? section) => (page, section) switch
    {
        ("checklist", _) => "Setup checklist",
        ("connection", _) => "CH1 Connection",
        ("service", _) => "CH2 Service",
        ("profiles", _) => "CH3 Profiles & Backup",
        ("radios", _) => "CH4 Radios",
        ("speech", _) => "CH5 Speech",
        ("phrases", _) => "CH6 Phrases",
        ("vocabulary", _) => "CH7 Vocabulary",
        ("dcsgrpc", "server") => "CH8 Server & test",
        ("dcsgrpc", "explorer") => "CH8 Mission explorer",
        ("dcsgrpc", _) => "CH8 Replies",
        ("discord", _) => "CH9 Discord",
        ("logging", _) => "CH10 Logging",
        _ => page,
    };

    /// <summary>
    /// Generated from the panels: every heading, card title, switch and field label, with the
    /// settings each one edits. Regenerate - or add the line by hand - when a panel changes;
    /// EditorSearchTests names what is missing.
    /// </summary>
    internal static readonly SearchTarget[] Generated =
    {
        new("connection", null, SearchKind.Page, "CONNECTION", "Connection", null, ""),
        new("connection", null, SearchKind.Field, "SRS HOST", "SRS host", null, "SrsHost"),
        new("connection", null, SearchKind.Field, "PORT", "Port", null, "SrsPort"),
        new("connection", null, SearchKind.Field, "CLIENT NAME", "Client name", null, "ClientName"),
        new("connection", null, SearchKind.Field, "EXTERNAL AWACS PASSWORD", "External AWACS password", null, "ExternalAwacsPassword"),
        new("connection", null, SearchKind.Field, "EXTERNALAUDIO.EXE PATH", "ExternalAudio.exe path", null, "ExternalAudioExePath"),
        new("connection", null, SearchKind.Field, "CALLSIGN SEPARATOR IN PLAYER NAMES", "Callsign separator in player names", null, "PlayerNameCallsignSeparator"),
        new("connection", null, SearchKind.Field, "OWN COALITION", "Own coalition", null, ""),
        new("connection", null, SearchKind.Switch, "Restrict to own coalition (ignore opposing coalition entirely)", "Restrict to own coalition (ignore opposing coalition entirely)", null, "RestrictToOwnCoalition"),
        new("service", null, SearchKind.Page, "WINDOWS SERVICE", "Windows service", null, ""),
        new("service", null, SearchKind.Field, "SERVICE NAME", "Service name", null, ""),
        new("service", null, SearchKind.Field, "BOT EXECUTABLE", "Bot executable", null, ""),
        new("service", null, SearchKind.Field, "DISPLAY NAME (shown in services.msc)", "Display name (shown in services.msc)", null, ""),
        new("service", null, SearchKind.Switch, "Start automatically after a reboot", "Start automatically after a reboot", null, ""),
        new("service", null, SearchKind.Switch, "Start right after installing", "Start right after installing", null, ""),
        new("profiles", null, SearchKind.Page, "PROFILES", "Profiles", null, ""),
        new("profiles", null, SearchKind.Field, "CONFIG FOLDER", "Config folder", null, ""),
        new("profiles", null, SearchKind.Field, "PROFILES FOLDER", "Profiles folder", null, ""),
        new("profiles", null, SearchKind.Field, "NAME", "Name", null, ""),
        new("profiles", null, SearchKind.Page, "BACKUP & RESTORE", "Backup & restore", null, ""),
        new("profiles", null, SearchKind.Card, "MAKE A BACKUP", "Make a backup", null, ""),
        new("profiles", null, SearchKind.Card, "RESTORE", "Restore", null, ""),
        new("radios", null, SearchKind.Page, "RADIOS — PER-CHANNEL IDENTITY", "Radios — per-channel identity", null, ""),
        new("radios", null, SearchKind.Field, "WAKE WORD (empty = global default)", "Wake word (empty = global default)", null, "Keyword"),
        new("radios", null, SearchKind.Field, "CALLSIGN (empty = global default)", "Callsign (empty = global default)", null, "Callsign"),
        new("radios", null, SearchKind.Field, "ALSO ACCEPT AS THE WAKE WORD (comma separated)", "Also accept as the wake word (comma separated)", null, "KeywordVariants"),
        new("radios", null, SearchKind.Field, "VOICE (empty = global default)", "Voice (empty = global default)", null, "Voice"),
        new("radios", null, SearchKind.Card, "Voices on this machine", "Voices on this machine", null, ""),
        new("radios", null, SearchKind.Field, "REPLY THAT POINTS AT THE RIGHT FREQUENCY", "Reply that points at the right frequency", null, "WrongChannelReply"),
        new("radios", null, SearchKind.Card, "One tower per airfield", "One tower per airfield", null, ""),
        new("radios", null, SearchKind.Field, "FIRST FREQUENCY (MHZ)", "First frequency (MHz)", "One tower per airfield", "TowerPlanBaseMHz"),
        new("radios", null, SearchKind.Field, "STEP (MHZ)", "Step (MHz)", "One tower per airfield", "TowerPlanStepMHz"),
        new("radios", null, SearchKind.Field, "CALLSIGN SUFFIX", "Callsign suffix", "One tower per airfield", "TowerPlanCallsignSuffix"),
        new("radios", null, SearchKind.Field, "MODULATION", "Modulation", "One tower per airfield", "TowerPlanModulation"),
        new("radios", null, SearchKind.Card, "Announce the frequencies in game", "Announce the frequencies in game", null, "AnnounceFrequenciesEnabled"),
        new("radios", null, SearchKind.Switch, "One F10 map marker per airfield", "One F10 map marker per airfield", "Announce the frequencies in game", "AnnounceFrequenciesMarkers"),
        new("radios", null, SearchKind.Switch, "One on-screen message at startup", "One on-screen message at startup", "Announce the frequencies in game", "AnnounceFrequenciesMessage"),
        new("radios", null, SearchKind.Field, "MESSAGE STAYS ON SCREEN (SECONDS)", "Message stays on screen (seconds)", "Announce the frequencies in game", "AnnounceFrequenciesMessageSeconds"),
        new("radios", null, SearchKind.Card, "Greet a pilot who tunes in", "Greet a pilot who tunes in", null, "TuneInGreetingEnabled"),
        new("radios", null, SearchKind.Field, "WHAT TO SAY", "What to say", "Greet a pilot who tunes in", "TuneInGreetingText"),
        new("radios", null, SearchKind.Field, "MINIMUM SECONDS BETWEEN GREETINGS ON ONE FREQUENCY", "Minimum seconds between greetings on one frequency", "Greet a pilot who tunes in", "TuneInGreetingGapSeconds"),
        new("radios", null, SearchKind.Card, "Legacy single-radio fields", "Legacy single-radio fields", null, ""),
        new("radios", null, SearchKind.Field, "FREQUENCY (MHZ)", "Frequency (MHz)", "Legacy single-radio fields", "FrequencyHz"),
        new("radios", null, SearchKind.Field, "MODULATION", "Modulation", "Legacy single-radio fields", "Modulation"),
        new("speech", null, SearchKind.Page, "SPEECH — TRANSCRIPTION & REPLY", "Speech — transcription & reply", null, ""),
        new("speech", null, SearchKind.Card, "Which engine turns a transmission into text", "Which engine turns a transmission into text", null, ""),
        new("speech", null, SearchKind.Field, "ENGINE", "Engine", "Which engine turns a transmission into text", "SpeechEngine"),
        new("speech", null, SearchKind.Field, "WHISPER MODEL FILE (ggml-*.bin)", "Whisper model file (ggml-*.bin)", "Which engine turns a transmission into text", "WhisperModelPath"),
        new("speech", null, SearchKind.Field, "LANGUAGE", "Language", "Which engine turns a transmission into text", "WhisperLanguage"),
        new("speech", null, SearchKind.Field, "API KEY", "API key", null, "GeminiApiKey"),
        new("speech", null, SearchKind.Field, "MODEL", "Model", null, "GeminiModel"),
        new("speech", null, SearchKind.Field, "FALLBACK MODEL (optional)", "Fallback model (optional)", null, "GeminiFallbackModel"),
        new("speech", null, SearchKind.Field, "MAX RETRIES", "Max retries", null, "GeminiMaxRetries"),
        new("speech", null, SearchKind.Field, "RETRY DELAY (MS)", "Retry delay (ms)", null, "GeminiRetryDelayMs"),
        new("speech", null, SearchKind.Card, "Rate limit per pilot", "Rate limit per pilot", null, ""),
        new("speech", null, SearchKind.Field, "REQUESTS PER PILOT (0 = NO LIMIT)", "Requests per pilot (0 = no limit)", "Rate limit per pilot", "RateLimitMaxRequests"),
        new("speech", null, SearchKind.Field, "WITHIN (SECONDS)", "Within (seconds)", "Rate limit per pilot", "RateLimitWindowSeconds"),
        new("speech", null, SearchKind.Field, "WHAT TO SAY WHEN A PILOT IS OVER THE LIMIT", "What to say when a pilot is over the limit", "Rate limit per pilot", "RateLimitReply"),
        new("speech", null, SearchKind.Field, "TTS VOICE (used by any radio that doesn't set its own)", "TTS voice (used by any radio that doesn't set its own)", null, "VoiceName"),
        new("speech", null, SearchKind.Field, "PRE-ROLL (SECONDS)", "Pre-roll (seconds)", null, "PreRollSeconds"),
        new("speech", null, SearchKind.Field, "EXTRA ARGUMENTS FOR DCS-SR-ExternalAudio.exe", "Extra arguments for DCS-SR-ExternalAudio.exe", null, "ExternalAudioExtraArgs"),
        new("speech", null, SearchKind.Field, "VOSK MODEL FOLDER", "Vosk model folder", null, "VoskModelPath"),
        new("speech", null, SearchKind.Field, "GLOBAL WAKE WORD", "Global wake word", null, "VoskKeyword"),
        new("speech", null, SearchKind.Field, "ALSO ACCEPT AS THE WAKE WORD (comma separated)", "Also accept as the wake word (comma separated)", null, "VoskKeywordVariants"),
        new("speech", null, SearchKind.Field, "SILENCE FRAMES TO STOP RECORDING", "Silence frames to stop recording", null, "SilenceFramesToStopRecording"),
        new("speech", null, SearchKind.Switch, "\"Message received, standby\" while the reply is being worked on", "\"Message received, standby\" while the reply is being worked on", null, "AckEnabled"),
        new("speech", null, SearchKind.Field, "SEND AFTER (SECONDS)", "Send after (seconds)", null, "AckAfterSeconds"),
        new("speech", null, SearchKind.Field, "ACKNOWLEDGEMENT TEXT", "Acknowledgement text", null, "AckMessage"),
        new("speech", null, SearchKind.Card, "Wake word accuracy", "Wake word accuracy", null, ""),
        new("speech", null, SearchKind.Field, "AUDIO PREPARATION", "Audio preparation", "Wake word accuracy", ""),
        new("speech", null, SearchKind.Switch, "Even out quiet pilots (automatic gain)", "Even out quiet pilots (automatic gain)", "Wake word accuracy", "HotwordAutoGain SaveRecordings"),
        new("speech", null, SearchKind.Field, "DELETE RECORDINGS OLDER THAN (DAYS, 0 = NEVER)", "Delete recordings older than (days, 0 = never)", "Wake word accuracy", "RecordingRetentionDays RecordingRetentionMaxMb"),
        new("speech", null, SearchKind.Card, "Fallback hotword (used only if no Vosk model is set)", "Fallback hotword (used only if no Vosk model is set)", null, ""),
        new("speech", null, SearchKind.Field, "ENERGY THRESHOLD (0-32767)", "Energy threshold (0-32767)", "Fallback hotword (used only if no Vosk model is set)", "HotwordEnergyThreshold"),
        new("speech", null, SearchKind.Field, "CONSECUTIVE LOUD FRAMES NEEDED", "Consecutive loud frames needed", "Fallback hotword (used only if no Vosk model is set)", "HotwordConsecutiveFramesNeeded"),
        new("phrases", null, SearchKind.Page, "PHRASES", "Phrases", null, ""),
        new("phrases", null, SearchKind.Field, "FALLBACK RESPONSE", "Fallback response", null, "FallbackResponse"),
        new("phrases", null, SearchKind.Field, "REPLY WHEN NOTHING WAS UNDERSTOOD", "Reply when nothing was understood", null, "UnintelligibleReply"),
        new("phrases", null, SearchKind.Card, "Radio check", "Radio check", null, "RadioCheckEnabled"),
        new("phrases", null, SearchKind.Field, "TRIGGERS (comma separated)", "Triggers (comma separated)", "Radio check", "RadioCheckTriggers"),
        new("phrases", null, SearchKind.Field, "REPLY WITHOUT MISSION DATA", "Reply without mission data", "Radio check", "RadioCheckReply"),
        new("phrases", null, SearchKind.Field, "REPLY WHEN THE CALLER IS ON SCOPE", "Reply when the caller is on scope", "Radio check", "RadioCheckReplyWithContact"),
        new("phrases", null, SearchKind.Field, "REPLY WHEN THE CALLER IS NOT ON SCOPE", "Reply when the caller is not on scope", "Radio check", "RadioCheckReplyNoContact"),
        new("vocabulary", null, SearchKind.Page, "VOCABULARY", "Vocabulary", null, ""),
        new("vocabulary", null, SearchKind.Field, "NEW TERM", "New term", null, ""),
        new("dcsgrpc", "server", SearchKind.Page, "DCS-gRPC — SERVER & TEST", "DCS-gRPC — server & test", null, ""),
        new("dcsgrpc", "server", SearchKind.Switch, "Enable DCS-gRPC", "Enable DCS-gRPC", null, "DcsGrpcEnabled"),
        new("dcsgrpc", "server", SearchKind.Field, "SERVER ADDRESS", "Server address", null, "DcsGrpcAddress"),
        new("dcsgrpc", "server", SearchKind.Field, "API KEY (only if auth is enabled on the server)", "API key (only if auth is enabled on the server)", null, "DcsGrpcApiKey"),
        new("dcsgrpc", "replies", SearchKind.Page, "REPLIES FROM MISSION DATA", "Replies from mission data", null, ""),
        new("dcsgrpc", "replies", SearchKind.Switch, "Answer tactical requests from mission data", "Answer tactical requests from mission data", null, "DcsIntelEnabled"),
        new("dcsgrpc", "replies", SearchKind.Field, "CONTACT SOURCE", "Contact source", null, "DcsIntelContactSource"),
        new("dcsgrpc", "replies", SearchKind.Field, "AWACS UNIT NAME (mission editor name)", "AWACS unit name (mission editor name)", null, "DcsIntelAwacsUnitName"),
        new("dcsgrpc", "replies", SearchKind.Field, "MAX RANGE (NM, 0 = NO LIMIT)", "Max range (NM, 0 = no limit)", null, "DcsIntelMaxRangeNm"),
        new("dcsgrpc", "replies", SearchKind.Field, "GROUPS PER PICTURE", "Groups per picture", null, "DcsIntelMaxGroups"),
        new("dcsgrpc", "replies", SearchKind.Field, "QUERY TIMEOUT (SECONDS)", "Query timeout (seconds)", null, "DcsIntelIncludeHelicopters DcsIntelMagneticBearings DcsIntelSayContactType DcsIntelTimeoutSeconds"),
        new("dcsgrpc", "replies", SearchKind.Switch, "Slow, clearly spoken numbers", "Slow, clearly spoken numbers", null, "DcsIntelSlowSpeech"),
        new("dcsgrpc", "replies", SearchKind.Field, "BOGEY DOPE TRIGGERS (comma separated)", "Bogey dope triggers (comma separated)", null, "DcsIntelBogeyDopeTriggers"),
        new("dcsgrpc", "replies", SearchKind.Field, "BULLSEYE TRIGGERS", "Bullseye triggers", null, "DcsIntelBullseyeTriggers"),
        new("dcsgrpc", "replies", SearchKind.Field, "PICTURE TRIGGERS", "Picture triggers", null, "DcsIntelPictureTriggers"),
        new("dcsgrpc", "replies", SearchKind.Field, "THREAT TRIGGERS", "Threat triggers", null, "DcsIntelThreatTriggers"),
        new("dcsgrpc", "replies", SearchKind.Field, "DECLARE TRIGGERS", "Declare triggers", null, "DcsIntelDeclareTriggers"),
        new("dcsgrpc", "replies", SearchKind.Field, "SPIKE TRIGGERS", "Spike triggers", null, "DcsIntelSpikeTriggers"),
        new("dcsgrpc", "replies", SearchKind.Field, "REPLY WHEN A DECLARE HAS NO POINT", "Reply when a declare has no point", null, "DcsIntelDeclareNoPointReply"),
        new("dcsgrpc", "replies", SearchKind.Field, "REPLY WHEN A SPIKE HAS NO BEARING", "Reply when a spike has no bearing", null, "DcsIntelSpikeNoBearingReply"),
        new("dcsgrpc", "replies", SearchKind.Field, "CHECK IN TRIGGERS", "Check in triggers", null, "DcsIntelCheckInTriggers"),
        new("dcsgrpc", "replies", SearchKind.Field, "CHECK OUT TRIGGERS", "Check out triggers", null, "DcsIntelCheckOutTriggers"),
        new("dcsgrpc", "replies", SearchKind.Field, "CHECK IN REPLY", "Check in reply", null, "DcsIntelCheckInReply"),
        new("dcsgrpc", "replies", SearchKind.Field, "CHECK OUT REPLY", "Check out reply", null, "DcsIntelCheckInGatesCalls DcsIntelCheckOutReply"),
        new("dcsgrpc", "replies", SearchKind.Field, "SAY AGAIN TRIGGERS", "Say again triggers", null, "DcsIntelSayAgainTriggers"),
        new("dcsgrpc", "replies", SearchKind.Field, "REPLY WHEN THERE IS NOTHING TO REPEAT", "Reply when there is nothing to repeat", null, "DcsIntelNothingToRepeatReply"),
        new("dcsgrpc", "replies", SearchKind.Field, "ALPHA CHECK TRIGGERS", "Alpha check triggers", null, "DcsIntelAlphaCheckTriggers"),
        new("dcsgrpc", "replies", SearchKind.Field, "REPLY WHEN THE CALLER CANNOT BE FOUND", "Reply when the caller cannot be found", null, "DcsIntelNoPositionReply"),
        new("dcsgrpc", "replies", SearchKind.Field, "REPLY WHEN NOTHING IS FOUND", "Reply when nothing is found", null, "DcsIntelNoContactsReply"),
        new("dcsgrpc", "replies", SearchKind.Field, "REPLY WHEN MISSION DATA IS UNAVAILABLE", "Reply when mission data is unavailable", null, "DcsIntelUnavailableReply"),
        new("dcsgrpc", "replies", SearchKind.Card, "Friendly positions (\"where is Springfield 2-1?\")", "Friendly positions (\"where is Springfield 2-1?\")", null, "DcsIntelFriendlyPositionEnabled"),
        new("dcsgrpc", "replies", SearchKind.Field, "TRIGGERS (comma separated)", "Triggers (comma separated)", "Friendly positions (\"where is Springfield 2-1?\")", "DcsIntelFriendlyPositionTriggers"),
        new("dcsgrpc", "replies", SearchKind.Field, "REPLY WHEN THE NAMED PILOT ISN'T FOUND", "Reply when the named pilot isn't found", "Friendly positions (\"where is Springfield 2-1?\")", "DcsIntelFriendlyNotFoundReply"),
        new("dcsgrpc", "replies", SearchKind.Field, "REPLY WHEN NOBODY WAS NAMED", "Reply when nobody was named", "Friendly positions (\"where is Springfield 2-1?\")", "DcsIntelFriendlyNoNameReply"),
        new("dcsgrpc", "replies", SearchKind.Field, "REPLY WHEN THE CALLER'S COALITION IS UNKNOWN", "Reply when the caller's coalition is unknown", "Friendly positions (\"where is Springfield 2-1?\")", "DcsIntelFriendlyNoCoalitionReply"),
        new("dcsgrpc", "replies", SearchKind.Switch, "Also say the friendly's heading", "Also say the friendly's heading", "Friendly positions (\"where is Springfield 2-1?\")", "DcsIntelFriendlySayHeading"),
        new("dcsgrpc", "replies", SearchKind.Card, "Threat circle (standing watch)", "Threat circle (standing watch)", null, "DcsIntelThreatCircleEnabled"),
        new("dcsgrpc", "replies", SearchKind.Field, "DEFAULT RADIUS (NM)", "Default radius (NM)", "Threat circle (standing watch)", "DcsIntelThreatCircleDefaultRadiusNm"),
        new("dcsgrpc", "replies", SearchKind.Field, "MAX RADIUS (NM)", "Max radius (NM)", "Threat circle (standing watch)", "DcsIntelThreatCircleMaxRadiusNm"),
        new("dcsgrpc", "replies", SearchKind.Field, "SWEEP INTERVAL (SECONDS)", "Sweep interval (seconds)", "Threat circle (standing watch)", "DcsIntelThreatCirclePollSeconds"),
        new("dcsgrpc", "replies", SearchKind.Field, "EXPIRES AFTER (MINUTES)", "Expires after (minutes)", "Threat circle (standing watch)", "DcsIntelThreatCircleDurationMinutes"),
        new("dcsgrpc", "replies", SearchKind.Field, "MAX CIRCLES AT ONCE", "Max circles at once", "Threat circle (standing watch)", "DcsIntelThreatCircleMaxActive"),
        new("dcsgrpc", "replies", SearchKind.Field, "MAX WARNINGS PER SWEEP", "Max warnings per sweep", "Threat circle (standing watch)", "DcsIntelThreatCircleMaxAlertsPerSweep"),
        new("dcsgrpc", "replies", SearchKind.Field, "START TRIGGERS", "Start triggers", "Threat circle (standing watch)", "DcsIntelThreatCircleTriggers"),
        new("dcsgrpc", "replies", SearchKind.Field, "CANCEL TRIGGERS", "Cancel triggers", "Threat circle (standing watch)", "DcsIntelThreatCircleCancelTriggers"),
        new("dcsgrpc", "replies", SearchKind.Field, "REPLY WHEN THE CIRCLE WAS CANCELLED", "Reply when the circle was cancelled", "Threat circle (standing watch)", "DcsIntelThreatCircleCancelledReply"),
        new("dcsgrpc", "replies", SearchKind.Field, "REPLY WHEN NOTHING WAS RUNNING", "Reply when nothing was running", "Threat circle (standing watch)", "DcsIntelThreatCircleNoneActiveReply"),
        new("dcsgrpc", "replies", SearchKind.Field, "REPLY WHEN THE CALLER'S AIRCRAFT WASN'T FOUND", "Reply when the caller's aircraft wasn't found", "Threat circle (standing watch)", "DcsIntelThreatCircleNoPilotReply"),
        new("dcsgrpc", "replies", SearchKind.Field, "REPLY WHEN TOO MANY CIRCLES ARE ACTIVE", "Reply when too many circles are active", "Threat circle (standing watch)", "DcsIntelThreatCircleBusyReply"),
        new("dcsgrpc", "replies", SearchKind.Card, "Try it without flying", "Try it without flying", null, ""),
        new("dcsgrpc", "replies", SearchKind.Field, "REQUEST", "Request", "Try it without flying", ""),
        new("dcsgrpc", "replies", SearchKind.Field, "PILOT'S SRS NAME (for BRAA)", "Pilot's SRS name (for BRAA)", "Try it without flying", ""),
        new("dcsgrpc", "replies", SearchKind.Switch, "Answer \"runway in use\" and ATIS calls", "Answer \"runway in use\" and ATIS calls", null, "DcsAirfieldEnabled"),
        new("dcsgrpc", "replies", SearchKind.Field, "RUNWAY TRIGGERS", "Runway triggers", null, "DcsAirfieldRunwayTriggers"),
        new("dcsgrpc", "replies", SearchKind.Field, "ATIS TRIGGERS", "ATIS triggers", null, "DcsAirfieldAtisTriggers"),
        new("dcsgrpc", "replies", SearchKind.Field, "DIVERT TRIGGERS", "Divert triggers", null, "DcsAirfieldVectorTriggers"),
        new("dcsgrpc", "replies", SearchKind.Field, "HOW MANY AIRFIELDS TO OFFER", "How many airfields to offer", null, "DcsAirfieldVectorCount"),
        new("dcsgrpc", "replies", SearchKind.Field, "REPLY WHEN NO AIRFIELD IS IN RANGE", "Reply when no airfield is in range", null, "DcsAirfieldNoVectorReply"),
        new("dcsgrpc", "replies", SearchKind.Field, "ALTIMETER SETTING", "Altimeter setting", null, ""),
        new("dcsgrpc", "replies", SearchKind.Field, "\"AT THE FIELD\" RADIUS (NM)", "\"at the field\" radius (NM)", null, "DcsAirfieldAtFieldNm"),
        new("dcsgrpc", "replies", SearchKind.Field, "MAXIMUM DISTANCE (NM)", "Maximum distance (NM)", null, "DcsAirfieldMaxDistanceNm"),
        new("dcsgrpc", "replies", SearchKind.Field, "REPLY WHEN NO AIRFIELD COULD BE DETERMINED", "Reply when no airfield could be determined", null, "DcsAirfieldUnknownReply"),
        new("dcsgrpc", "replies", SearchKind.Field, "REPLY WHEN NO DATA IS AVAILABLE", "Reply when no data is available", null, "DcsAirfieldUnavailableReply"),
        new("dcsgrpc", "replies", SearchKind.Field, "REQUEST", "Request", null, ""),
        new("dcsgrpc", "replies", SearchKind.Field, "AIRFIELD (OR PILOT'S SRS NAME)", "Airfield (or pilot's srs name)", null, ""),
        new("dcsgrpc", "replies", SearchKind.Card, "The F10 menu — calls without being understood", "The F10 menu — calls without being understood", null, "F10MenuEnabled"),
        new("dcsgrpc", "replies", SearchKind.Switch, "Offer the calls under F10", "Offer the calls under F10", "The F10 menu — calls without being understood", "F10MenuEnabled"),
        new("dcsgrpc", "replies", SearchKind.Field, "MENU ENTRY NAME", "Menu entry name", "The F10 menu — calls without being understood", "F10MenuRootName"),
        new("dcsgrpc", "replies", SearchKind.Field, "REPLY GOES OUT AS", "Reply goes out as", "The F10 menu — calls without being understood", "F10ReplyMode"),
        new("dcsgrpc", "replies", SearchKind.Field, "ON-SCREEN TEXT (SECONDS)", "On-screen text (seconds)", "The F10 menu — calls without being understood", "F10EchoEnabled F10TextSeconds"),
        new("dcsgrpc", "replies", SearchKind.Field, "PILOT VOICE", "Pilot voice", "The F10 menu — calls without being understood", "F10EchoVoice"),
        new("dcsgrpc", "replies", SearchKind.Field, "MOST FLIGHTS WITH A MENU", "Most flights with a menu", "The F10 menu — calls without being understood", "F10MenuMaxGroups"),
        new("dcsgrpc", "replies", SearchKind.Field, "REFRESH (SECONDS)", "Refresh (seconds)", "The F10 menu — calls without being understood", "F10MenuRefreshSeconds"),
        new("dcsgrpc", "replies", SearchKind.Card, "Requests typed into the chat — and the radio wheel", "Requests typed into the chat — and the radio wheel", null, "ChatBridgeEnabled"),
        new("dcsgrpc", "replies", SearchKind.Switch, "Answer requests from the chat", "Answer requests from the chat", "Requests typed into the chat — and the radio wheel", "ChatBridgeEnabled"),
        new("dcsgrpc", "replies", SearchKind.Field, "SET UP THE SERVER HOOK", "Set up the server hook", "Requests typed into the chat — and the radio wheel", ""),
        new("dcsgrpc", "replies", SearchKind.Field, "CHAT BRIDGE FOLDER", "Chat bridge folder", "Requests typed into the chat — and the radio wheel", "ChatBridgeFolder"),
        new("dcsgrpc", "replies", SearchKind.Field, "DROP A REQUEST OLDER THAN (SECONDS)", "Drop a request older than (seconds)", "Requests typed into the chat — and the radio wheel", "ChatBridgeMaxAgeSeconds"),
        new("dcsgrpc", "replies", SearchKind.Field, "WHAT A PILOT CAN TYPE", "What a pilot can type", "Requests typed into the chat — and the radio wheel", ""),
        new("dcsgrpc", "explorer", SearchKind.Page, "MISSION DATA EXPLORER", "Mission data explorer", null, ""),
        new("dcsgrpc", "explorer", SearchKind.Card, "Mission snapshot", "Mission snapshot", null, ""),
        new("dcsgrpc", "explorer", SearchKind.Field, "SINGLE QUERY", "Single query", null, ""),
        new("dcsgrpc", "explorer", SearchKind.Field, "COALITION", "Coalition", null, ""),
        new("dcsgrpc", "explorer", SearchKind.Field, "CATEGORY", "Category", null, ""),
        new("dcsgrpc", "explorer", SearchKind.Field, "GROUP NAME (as in the mission editor)", "Group name (as in the mission editor)", null, ""),
        new("dcsgrpc", "explorer", SearchKind.Field, "UNIT NAME (as in the mission editor, not the player name)", "Unit name (as in the mission editor, not the player name)", null, ""),
        new("dcsgrpc", "explorer", SearchKind.Field, "LATITUDE", "Latitude", null, ""),
        new("dcsgrpc", "explorer", SearchKind.Field, "LONGITUDE", "Longitude", null, ""),
        new("dcsgrpc", "explorer", SearchKind.Field, "ALTITUDE (m MSL)", "Altitude (m MSL)", null, ""),
        new("dcsgrpc", "explorer", SearchKind.Field, "FLAG NAME / NUMBER", "Flag name / number", null, ""),
        new("dcsgrpc", "explorer", SearchKind.Field, "POLL RATE (seconds)", "Poll rate (seconds)", null, ""),
        new("discord", null, SearchKind.Page, "DISCORD NOTIFICATIONS", "Discord notifications", null, ""),
        new("discord", null, SearchKind.Switch, "Enable Discord notifications", "Enable Discord notifications", null, "DiscordEnabled"),
        new("discord", null, SearchKind.Field, "WEBHOOK URL", "Webhook URL", null, "DiscordWebhookUrl"),
        new("logging", null, SearchKind.Page, "LOGGING", "Logging", null, ""),
        new("logging", null, SearchKind.Switch, "Logging enabled", "Logging enabled", null, "LoggingEnabled"),
        new("logging", null, SearchKind.Switch, "Debug (verbose) logging", "Debug (verbose) logging", null, "DebugLogging"),
        new("logging", null, SearchKind.Field, "LOG FOLDER", "Log folder", null, ""),
        new("logging", null, SearchKind.Field, "LOG FILE BEING SHOWN", "Log file being shown", null, ""),
        new("logging", null, SearchKind.Field, "DELETE LOGS OLDER THAN (DAYS, 0 = NEVER)", "Delete logs older than (days, 0 = never)", null, "LogRetentionDays LogRetentionMaxMb"),
    };

    /// <summary>
    /// Every setting each panel touches, whether or not one field can be named for it - a setting
    /// edited through a select or a list editor is still found by its name, and lands on the right
    /// page. Attached to the page's own entry.
    /// </summary>
    internal static readonly Dictionary<string, string> PageSettings = new()
    {
        ["connection"] = "ClientName Coalition ExternalAudioExePath ExternalAwacsPassword PlayerNameCallsignSeparator RestrictToOwnCoalition SrsHost SrsPort",
        ["service"] = "",
        ["profiles"] = "",
        ["radios"] = "AnnounceFrequenciesEnabled AnnounceFrequenciesMarkers AnnounceFrequenciesMessage AnnounceFrequenciesMessageSeconds AnswerAirfieldRequests AnswerFriendlyPositionRequests AnswerTacticalRequests BotCallsign Callsign DcsAirfieldEnabled DcsGrpcEnabled DcsIntelEnabled DcsIntelFriendlyPositionEnabled DescribeRole FrequencyHz Keyword KeywordVariants Modulation Radios TowerPlanBaseMHz TowerPlanCallsignSuffix TowerPlanModulation TowerPlanStepMHz TuneInGreetingEnabled TuneInGreetingGapSeconds TuneInGreetingText Voice VoiceName VoskKeyword VoskKeywordVariants WrongChannelReply",
        ["speech"] = "AckAfterSeconds AckEnabled AckMessage ExternalAudioExtraArgs GeminiApiKey GeminiFallbackModel GeminiMaxRetries GeminiModel GeminiRetryDelayMs HotwordAudioFilter HotwordAutoGain HotwordConsecutiveFramesNeeded HotwordEnergyThreshold PreRollSeconds RateLimitMaxRequests RateLimitReply RateLimitWindowSeconds RecordingRetentionDays RecordingRetentionMaxMb SaveRecordings SilenceFramesToStopRecording SpeechEngine VoiceName VoskKeyword VoskKeywordVariants VoskModelPath WhisperLanguage WhisperModelPath",
        ["phrases"] = "FallbackResponse RadioCheckEnabled RadioCheckReply RadioCheckReplyNoContact RadioCheckReplyWithContact RadioCheckTriggers RestrictToKnownPhrases UnintelligibleReply",
        ["vocabulary"] = "",
        ["dcsgrpc"] = "ChatBridgeEnabled ChatBridgeFolder ChatBridgeMaxAgeSeconds DcsAirfieldAtFieldNm DcsAirfieldAtisTriggers DcsAirfieldEnabled DcsAirfieldMaxDistanceNm DcsAirfieldNoVectorReply DcsAirfieldPressureUnit DcsAirfieldRunwayTriggers DcsAirfieldUnavailableReply DcsAirfieldUnknownReply DcsAirfieldVectorCount DcsAirfieldVectorTriggers DcsGrpcAddress DcsGrpcApiKey DcsGrpcEnabled DcsIntelAlphaCheckTriggers DcsIntelAwacsUnitName DcsIntelBogeyDopeTriggers DcsIntelBullseyeTriggers DcsIntelCheckInGatesCalls DcsIntelCheckInReply DcsIntelCheckInTriggers DcsIntelCheckOutReply DcsIntelCheckOutTriggers DcsIntelContactSource DcsIntelDeclareNoPointReply DcsIntelDeclareTriggers DcsIntelEnabled DcsIntelFriendlyNoCoalitionReply DcsIntelFriendlyNoNameReply DcsIntelFriendlyNotFoundReply DcsIntelFriendlyPositionEnabled DcsIntelFriendlyPositionTriggers DcsIntelFriendlySayHeading DcsIntelIncludeHelicopters DcsIntelMagneticBearings DcsIntelMaxGroups DcsIntelMaxRangeNm DcsIntelNoContactsReply DcsIntelNoPositionReply DcsIntelNothingToRepeatReply DcsIntelPictureTriggers DcsIntelSayAgainTriggers DcsIntelSayContactType DcsIntelSlowSpeech DcsIntelSpikeNoBearingReply DcsIntelSpikeTriggers DcsIntelThreatCircleBusyReply DcsIntelThreatCircleCancelTriggers DcsIntelThreatCircleCancelledReply DcsIntelThreatCircleDefaultRadiusNm DcsIntelThreatCircleDurationMinutes DcsIntelThreatCircleEnabled DcsIntelThreatCircleMaxActive DcsIntelThreatCircleMaxAlertsPerSweep DcsIntelThreatCircleMaxRadiusNm DcsIntelThreatCircleNoPilotReply DcsIntelThreatCircleNoneActiveReply DcsIntelThreatCirclePollSeconds DcsIntelThreatCircleTriggers DcsIntelThreatTriggers DcsIntelTimeoutSeconds DcsIntelUnavailableReply F10EchoEnabled F10EchoVoice F10MenuEnabled F10MenuMaxGroups F10MenuRefreshSeconds F10MenuRootName F10ReplyMode F10TextSeconds",
        ["discord"] = "DiscordEnabled DiscordWebhookUrl",
        ["logging"] = "DebugLogging LogRetentionDays LogRetentionMaxMb LoggingEnabled",
    };

    /// <summary>
    /// Things people search for by another name than the one on screen - including the German
    /// ones, since the editor's labels are English and its users often are not.
    /// </summary>
    internal static readonly SearchTarget[] Extra =
    {
        new("checklist", null, SearchKind.Page, "SETUP CHECKLIST", "Setup checklist", null,
            "setup checklist start first steps getting started einrichtung erste schritte status"),
        new("dcsgrpc", "replies", SearchKind.Card, "Requests typed into the chat — and the radio wheel",
            "Radio wheel (pilots' PCs)", null, "radio wheel radial menu wheel.json funkrad radialmenü chat bridge"),
        new("dcsgrpc", "replies", SearchKind.Field, "SET UP THE SERVER HOOK", "Install the chat bridge hook",
            "Requests typed into the chat — and the radio wheel", "hook lua install saved games scripts hooks dcs server darkstar-chatbridge.lua installieren"),
        new("profiles", null, SearchKind.Page, "BACKUP & RESTORE", "Backup and restore", null,
            "backup restore zip copy undo sicherung wiederherstellen"),
        new("service", null, SearchKind.Page, "WINDOWS SERVICE", "Run as a Windows service", null,
            "service autostart install start stop dienst"),
    };

    private static readonly IReadOnlyList<SearchTarget> All = BuildAll();

    private static IReadOnlyList<SearchTarget> BuildAll()
    {
        var all = new List<SearchTarget>();
        var catchAllUsed = new HashSet<string>();

        foreach (var target in Generated)
        {
            // The page's own entry carries every setting of the page, so nothing a panel edits is
            // unfindable. On CH8 that is the Replies heading, where nearly all of them are.
            var isCatchAll = target.Kind == SearchKind.Page &&
                             (target.Page != "dcsgrpc" || target.Section == "replies") &&
                             PageSettings.TryGetValue(target.Page, out _) &&
                             catchAllUsed.Add(target.Page);

            all.Add(isCatchAll
                ? target with { Keywords = (target.Keywords + " " + PageSettings[target.Page]).Trim() }
                : target);
        }

        all.AddRange(Extra);
        return all;
    }

    /// <summary>Everything the search knows, generated entries first.</summary>
    public static IReadOnlyList<SearchTarget> Targets => All;

    /// <summary>
    /// German and other words for what the labels call it. A query word is matched as itself or
    /// as any of these, so "Stimme" finds "Voice" without the index holding both everywhere.
    /// </summary>
    internal static readonly Dictionary<string, string[]> Synonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sicherung"] = new[] { "backup" },
        ["wiederherstellen"] = new[] { "restore" },
        ["stimme"] = new[] { "voice" },
        ["stimmen"] = new[] { "voice" },
        ["funkgerät"] = new[] { "radio" },
        ["funkgeräte"] = new[] { "radio" },
        ["funk"] = new[] { "radio" },
        ["frequenz"] = new[] { "frequency" },
        ["frequenzen"] = new[] { "frequency" },
        ["rufzeichen"] = new[] { "callsign" },
        ["funkrad"] = new[] { "wheel" },
        ["radialmenü"] = new[] { "wheel" },
        ["schlüssel"] = new[] { "key" },
        ["passwort"] = new[] { "password" },
        ["kennwort"] = new[] { "password" },
        ["dienst"] = new[] { "service" },
        ["protokoll"] = new[] { "log" },
        ["logdatei"] = new[] { "log" },
        ["sprache"] = new[] { "speech", "language" },
        ["weckwort"] = new[] { "wake" },
        ["hotword"] = new[] { "wake" },
        ["aktivierungswort"] = new[] { "wake" },
        ["antwort"] = new[] { "reply" },
        ["antworten"] = new[] { "reply" },
        ["auslöser"] = new[] { "trigger" },
        ["flugplatz"] = new[] { "airfield" },
        ["platz"] = new[] { "airfield" },
        ["landebahn"] = new[] { "runway" },
        ["piste"] = new[] { "runway" },
        ["ausweich"] = new[] { "divert" },
        ["ausweichen"] = new[] { "divert" },
        ["bedrohung"] = new[] { "threat" },
        ["kreis"] = new[] { "circle" },
        ["ordner"] = new[] { "folder" },
        ["pfad"] = new[] { "path" },
        ["profil"] = new[] { "profile" },
        ["verbindung"] = new[] { "connection" },
        ["koalition"] = new[] { "coalition" },
        ["begrüßung"] = new[] { "greet" },
        ["ansage"] = new[] { "announce" },
        ["modell"] = new[] { "model" },
        ["wortschatz"] = new[] { "vocabulary" },
        ["vokabular"] = new[] { "vocabulary" },
        ["phrase"] = new[] { "phrase" },
        ["luftdruck"] = new[] { "altimeter" },
        ["höhenmesser"] = new[] { "altimeter" },
        ["wetter"] = new[] { "atis" },
        ["einrichtung"] = new[] { "setup" },
        ["dunkel"] = new[] { "dark" },
        ["hell"] = new[] { "light" },
    };

    /// <summary>
    /// The best matches for what was typed, best first, at most <see cref="MaxResults"/>. Every
    /// word typed has to match somewhere - in the label, the card, the page or a setting name -
    /// so adding a word narrows the list instead of widening it.
    /// </summary>
    public static IReadOnlyList<SearchTarget> Find(string? query)
    {
        var words = Words(query);
        if (words.Count == 0) return Array.Empty<SearchTarget>();

        var phrase = string.Join(' ', words);
        var scored = new List<(SearchTarget Target, int Score, int Order)>();

        for (var order = 0; order < All.Count; order++)
        {
            var target = All[order];
            var title = Normalise(target.Title);
            var keywords = Normalise(target.Keywords + " " + SplitCamel(target.Keywords));
            var haystack = title + " " + Normalise(target.Label) + " " + Normalise(target.Card ?? "") + " " +
                           Normalise(target.Where) + " " + keywords;

            var score = 0;
            var allFound = true;
            foreach (var word in words)
            {
                var forms = Forms(word);
                if (!forms.Any(f => haystack.Contains(f, StringComparison.Ordinal)))
                {
                    allFound = false;
                    break;
                }

                if (forms.Any(f => title.Contains(f, StringComparison.Ordinal))) score += 20;
            }

            if (!allFound) continue;

            if (title.StartsWith(phrase, StringComparison.Ordinal)) score += 100;
            else if (title.Contains(phrase, StringComparison.Ordinal)) score += 60;

            // Somebody typing a config.json name wants that field, not every page mentioning it.
            if (target.Keywords.Split(' ').Any(k => string.Equals(k, query?.Trim(), StringComparison.OrdinalIgnoreCase)))
                score += target.Kind == SearchKind.Page ? 30 : 80;

            score += target.Kind switch
            {
                SearchKind.Card => 8,
                SearchKind.Page => 6,
                SearchKind.Switch => 6,
                _ => 4,
            };

            scored.Add((target, score, order));
        }

        return scored
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Order)
            .Select(s => s.Target)
            .DistinctBy(t => (t.Page, t.Section, t.Label, t.Card))
            .Take(MaxResults)
            .ToList();
    }

    private static IReadOnlyList<string> Forms(string word) =>
        Synonyms.TryGetValue(word, out var other) ? other.Prepend(word).ToList() : new[] { word };

    private static List<string> Words(string? query) =>
        Normalise(query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

    /// <summary>Lower case, letters and digits only, single spaces.</summary>
    internal static string Normalise(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.ToLower(CultureInfo.InvariantCulture))
            builder.Append(char.IsLetterOrDigit(c) || c == '.' ? c : ' ');

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>"ChatBridgeFolder" also as "chat bridge folder", so the words find it too.</summary>
    private static string SplitCamel(string text)
    {
        var builder = new StringBuilder(text.Length + 16);
        for (var i = 0; i < text.Length; i++)
        {
            if (i > 0 && char.IsUpper(text[i]) && char.IsLower(text[i - 1])) builder.Append(' ');
            builder.Append(text[i]);
        }
        return builder.ToString();
    }
}
