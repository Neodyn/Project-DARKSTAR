namespace Darkstar;

/// <summary>How far one step of the setup is.</summary>
public enum StepState
{
    /// <summary>Set up and, as far as the editor can tell, working.</summary>
    Done,

    /// <summary>A required step not done yet: the bot does not work without it.</summary>
    Todo,

    /// <summary>Set up, but something about it is wrong - a path that does not exist, a voice that is missing.</summary>
    Problem,

    /// <summary>An optional step not done. Nothing is broken; it would add something.</summary>
    Optional,

    /// <summary>An optional function that is switched off on purpose. Nothing to do.</summary>
    Off,
}

/// <summary>One step of the setup checklist.</summary>
/// <param name="Id">Stable name, for tests and for the editor to remember things by.</param>
/// <param name="Detail">One line on what was found - or what is missing and how to fix it.</param>
/// <param name="Page">The editor page that fixes it, as the editor's own key ("connection", ...).</param>
/// <param name="Section">For CH8, which of its parts.</param>
public sealed record SetupStep(
    string Id,
    string Title,
    StepState State,
    string Detail,
    string Page,
    string? Section,
    bool Required)
{
    /// <summary>Whether it still asks for something: a required step not done, or anything with a problem.</summary>
    public bool NeedsAttention => State is StepState.Todo or StepState.Problem;
}

/// <summary>
/// What the checklist needs to know that is not in config.json. Gathered by the editor, because
/// finding them out is Windows-only or slow; each is null when it could not be found out, and the
/// step then says so rather than guessing.
/// </summary>
public sealed class SetupFacts
{
    /// <summary>Whether the bot is registered as a Windows service.</summary>
    public bool? ServiceInstalled { get; init; }

    /// <summary>The text-to-speech voices installed on this PC.</summary>
    public IReadOnlyCollection<string>? InstalledVoices { get; init; }

    /// <summary>How many backups the Backup folder holds.</summary>
    public int? Backups { get; init; }
}

/// <summary>
/// The setup checklist: everything between a fresh install and a bot that answers, in the order it
/// has to happen, each step checked against the actual settings rather than ticked off by hand.
///
/// The required steps are the ones without which the bot does not answer at all; the optional ones
/// add something. The editor opens on the checklist while a required step is open, so a new
/// installation starts where the work is - and once everything is done, it opens where it always did.
///
/// Every check here is one the bot also makes at startup, in the same words where possible; the
/// point is to see them before starting the bot, not to have a second opinion.
/// </summary>
public static class SetupChecklist
{
    public static IReadOnlyList<SetupStep> Evaluate(AppConfig config, string configFolder, SetupFacts? facts = null)
    {
        facts ??= new SetupFacts();

        return new List<SetupStep>
        {
            Srs(config),
            ExternalAudio(config),
            WakeWord(config),
            Transcription(config),
            Radios(config, facts),
            MissionData(config),
            ChatBridgeStep(config),
            Service(facts),
            Backup(configFolder, facts),
            Discord(config),
        };
    }

    /// <summary>How many required steps are not done - what decides whether the editor opens on the checklist.</summary>
    public static int OpenRequired(IEnumerable<SetupStep> steps) =>
        steps.Count(s => s.Required && s.State != StepState.Done);

    /// <summary>Everything the bot would warn about at startup with these settings, in its own words.</summary>
    public static IReadOnlyList<string> StartupWarnings(AppConfig config)
    {
        var warnings = new List<string>();
        try
        {
            AppConfig.ValidateValues(config, warnings.Add);
        }
        catch (Exception ex)
        {
            warnings.Add("The settings could not be checked: " + ex.Message);
        }
        return warnings;
    }

    private static SetupStep Srs(AppConfig config)
    {
        var ok = !string.IsNullOrWhiteSpace(config.SrsHost) && config.SrsPort is > 0 and < 65536;
        return new SetupStep("srs", "Connect to the SRS server",
            ok ? StepState.Done : StepState.Todo,
            ok
                ? $"{config.SrsHost}:{config.SrsPort}, as \"{config.ClientName}\". Whether the server answers shows in the bot's log."
                : "No SRS server address or port. Enter the server the bot should join.",
            "connection", null, Required: true);
    }

    private static SetupStep ExternalAudio(AppConfig config)
    {
        var path = (config.ExternalAudioExePath ?? "").Trim().Trim('"');
        if (path.Length == 0)
            return new SetupStep("externalaudio", "Find DCS-SR-ExternalAudio.exe", StepState.Todo,
                "Not set. Press Detect SRS installation - every reply is spoken through it.",
                "connection", null, Required: true);

        return File.Exists(path)
            ? new SetupStep("externalaudio", "Find DCS-SR-ExternalAudio.exe", StepState.Done, path, "connection", null, Required: true)
            : new SetupStep("externalaudio", "Find DCS-SR-ExternalAudio.exe", StepState.Problem,
                $"Not found at {path}. Press Detect SRS installation, or pick the file.",
                "connection", null, Required: true);
    }

    private static SetupStep WakeWord(AppConfig config)
    {
        var model = VoskModelCheck.Check(config.VoskModelPath);
        var keyword = string.IsNullOrWhiteSpace(config.VoskKeyword) ? "" : $", wake word \"{config.VoskKeyword}\"";

        return new SetupStep("wakeword", "Wake word: the speech model",
            model.IsUsable ? StepState.Done : (model.Status == VoskModelStatus.NotConfigured ? StepState.Todo : StepState.Problem),
            model.IsUsable ? $"Vosk model in {model.ResolvedPath}{keyword}" : model.Message,
            "speech", null, Required: true);
    }

    private static SetupStep Transcription(AppConfig config)
    {
        if (string.Equals(config.SpeechEngine, "Whisper", StringComparison.OrdinalIgnoreCase))
        {
            var model = WhisperModelCheck.Check(config.WhisperModelPath);
            return new SetupStep("transcription", "Transcription",
                model.IsUsable ? StepState.Done : (model.Status == WhisperModelStatus.NotConfigured ? StepState.Todo : StepState.Problem),
                model.IsUsable ? $"Whisper, locally: {config.WhisperModelPath}" : model.Message,
                "speech", null, Required: true);
        }

        var hasKey = !string.IsNullOrWhiteSpace(config.GeminiApiKey);
        return new SetupStep("transcription", "Transcription",
            hasKey ? StepState.Done : StepState.Todo,
            hasKey
                ? $"Google Gemini ({config.GeminiModel}), key set"
                : "No Gemini API key. Get one at Google AI Studio - or switch the engine to Whisper, which needs no account.",
            "speech", null, Required: true);
    }

    private static SetupStep Radios(AppConfig config, SetupFacts facts)
    {
        var radios = config.Radios ?? new List<RadioConfig>();
        if (radios.Count == 0)
            return new SetupStep("radios", "Radios", StepState.Todo,
                "No radio yet. Add one - its frequency is where the bot listens and answers.",
                "radios", null, Required: true);

        var noFrequency = radios.Where(r => r.FrequencyHz <= 0).ToList();
        if (noFrequency.Count > 0)
            return new SetupStep("radios", "Radios", StepState.Problem,
                $"{noFrequency.Count} radio(s) without a frequency.", "radios", null, Required: true);

        if (facts.InstalledVoices != null)
        {
            var installed = new HashSet<string>(facts.InstalledVoices, StringComparer.OrdinalIgnoreCase);
            var missing = radios
                .Select(r => (Radio: r, Voice: string.IsNullOrWhiteSpace(r.Voice) ? config.VoiceName : r.Voice))
                .Where(x => !string.IsNullOrWhiteSpace(x.Voice) && !installed.Contains(x.Voice.Trim()))
                .Select(x => $"{(string.IsNullOrWhiteSpace(x.Radio.Callsign) ? FrequencyText.ToMhz(x.Radio.FrequencyHz) : x.Radio.Callsign)} uses \"{x.Voice}\"")
                .ToList();

            if (missing.Count > 0)
                return new SetupStep("radios", "Radios", StepState.Problem,
                    $"A voice that is not installed on this PC: {string.Join("; ", missing)}. That radio stays silent.",
                    "radios", null, Required: true);
        }

        var list = string.Join(" · ", radios.Take(4).Select(r =>
            (string.IsNullOrWhiteSpace(r.Callsign) ? "" : r.Callsign + " ") + FrequencyText.ToMhz(r.FrequencyHz)));
        return new SetupStep("radios", "Radios", StepState.Done,
            $"{radios.Count} radio(s): {list}{(radios.Count > 4 ? " · …" : "")}", "radios", null, Required: true);
    }

    private static SetupStep MissionData(AppConfig config)
    {
        if (!config.DcsGrpcEnabled)
            return new SetupStep("grpc", "Mission data (DCS-gRPC)", StepState.Off,
                "Off. Tactical calls, the threat circle, airfield calls and the F10 menu need it.",
                "dcsgrpc", "server", Required: false);

        return string.IsNullOrWhiteSpace(config.DcsGrpcAddress)
            ? new SetupStep("grpc", "Mission data (DCS-gRPC)", StepState.Problem,
                "On, but without a server address.", "dcsgrpc", "server", Required: false)
            : new SetupStep("grpc", "Mission data (DCS-gRPC)", StepState.Done,
                $"On, {config.DcsGrpcAddress}. Test connection on that page checks that a mission is running.",
                "dcsgrpc", "server", Required: false);
    }

    private static SetupStep ChatBridgeStep(AppConfig config)
    {
        const string title = "Chat bridge and radio wheel";

        if (!config.ChatBridgeEnabled)
            return new SetupStep("chatbridge", title, StepState.Off,
                "Off. With it, pilots can type requests into the chat or use the radio wheel.",
                "dcsgrpc", "replies", Required: false);

        if (string.IsNullOrWhiteSpace(config.ChatBridgeFolder))
            return new SetupStep("chatbridge", title, StepState.Problem,
                "On, but the server hook is not set up. Set up the server hook does both halves.",
                "dcsgrpc", "replies", Required: false);

        string? problem;
        try { problem = ChatBridge.FolderProblem(config.ChatBridgeFolder); }
        catch (Exception ex) { problem = ex.Message; }

        return problem == null
            ? new SetupStep("chatbridge", title, StepState.Done,
                $"Hook in place for {ChatBridge.ResolveFolder(config.ChatBridgeFolder)}", "dcsgrpc", "replies", Required: false)
            : new SetupStep("chatbridge", title, StepState.Problem,
                problem + " (Checked from this PC - on another one the bot checks again at startup.)",
                "dcsgrpc", "replies", Required: false);
    }

    private static SetupStep Service(SetupFacts facts) => facts.ServiceInstalled switch
    {
        true => new SetupStep("service", "Run as a Windows service", StepState.Done,
            "Installed - the bot starts with Windows.", "service", null, Required: false),
        false => new SetupStep("service", "Run as a Windows service", StepState.Optional,
            "Not installed. The bot only runs while it is started by hand.", "service", null, Required: false),
        null => new SetupStep("service", "Run as a Windows service", StepState.Optional,
            "Could not be checked from here.", "service", null, Required: false),
    };

    private static SetupStep Backup(string configFolder, SetupFacts facts)
    {
        var count = facts.Backups;
        if (count == null)
        {
            try { count = ConfigBackup.List(configFolder).Count(b => !b.BeforeRestore); }
            catch (Exception) { count = 0; }
        }

        return count > 0
            ? new SetupStep("backup", "A backup of the setup", StepState.Done,
                $"{count} backup(s) in the Backup folder.", "profiles", null, Required: false)
            : new SetupStep("backup", "A backup of the setup", StepState.Optional,
                "None yet. One click, and a working setup is one restore away.", "profiles", null, Required: false);
    }

    private static SetupStep Discord(AppConfig config)
    {
        if (!config.DiscordEnabled)
            return new SetupStep("discord", "Discord notifications", StepState.Off,
                "Off. With it, start, stop and lost connections are posted to a channel.", "discord", null, Required: false);

        return string.IsNullOrWhiteSpace(config.DiscordWebhookUrl)
            ? new SetupStep("discord", "Discord notifications", StepState.Problem,
                "On, but without a webhook URL.", "discord", null, Required: false)
            : new SetupStep("discord", "Discord notifications", StepState.Done,
                "On, webhook set.", "discord", null, Required: false);
    }
}
