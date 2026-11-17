using static Darkstar.Tests.Test;

namespace Darkstar.Tests;

/// <summary>
/// The setup checklist (<see cref="SetupChecklist"/>): each step judged from the real settings and
/// files, required steps telling the editor where to open, and every step pointing at the page that
/// fixes it.
/// </summary>
internal static class SetupChecklistTests
{
    private static SetupStep Step(IEnumerable<SetupStep> steps, string id) => steps.Single(s => s.Id == id);

    public static void Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "darkstar-checklist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        try
        {
            // A setup that works: ExternalAudio and a Vosk model on disk, a key, a radio with a voice
            // this PC has.
            var externalAudio = Path.Combine(folder, "DCS-SR-ExternalAudio.exe");
            File.WriteAllText(externalAudio, "");
            var model = Path.Combine(folder, "vosk-model");
            foreach (var marker in new[] { "am/final.mdl", "conf/model.conf", "conf/mfcc.conf" })
            {
                var path = Path.Combine(model, marker);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "x");
            }

            AppConfig Working() => new()
            {
                SrsHost = "127.0.0.1",
                SrsPort = 5002,
                ExternalAudioExePath = externalAudio,
                VoskModelPath = model,
                SpeechEngine = "Gemini",
                GeminiApiKey = "key",
                Radios = new List<RadioConfig> { new() { FrequencyHz = 251_000_000, Callsign = "Overlord", Voice = "Voice A" } },
            };

            var facts = new SetupFacts { ServiceInstalled = true, InstalledVoices = new[] { "Voice A" }, Backups = 1 };

            Section("A working setup is all done");

            var steps = SetupChecklist.Evaluate(Working(), folder, facts);
            Eq("the steps, in the order the work happens",
                string.Join(",", steps.Select(s => s.Id)),
                "srs,externalaudio,wakeword,transcription,radios,grpc,chatbridge,service,backup,discord");
            Check("the first five are required, the rest optional",
                steps.Take(5).All(s => s.Required) && steps.Skip(5).All(s => !s.Required));
            Check("every required step is done", steps.Where(s => s.Required).All(s => s.State == StepState.Done),
                string.Join("; ", steps.Where(s => s.Required && s.State != StepState.Done).Select(s => $"{s.Id}: {s.Detail}")));
            Eq("so nothing required is open", SetupChecklist.OpenRequired(steps).ToString(), "0");
            Check("functions switched off are off, not to-do",
                Step(steps, "grpc").State == StepState.Off && Step(steps, "chatbridge").State == StepState.Off &&
                Step(steps, "discord").State == StepState.Off);
            Check("and nothing asks for attention", steps.All(s => !s.NeedsAttention));
            Check("the radios step names the radio", Step(steps, "radios").Detail.Contains("Overlord 251.000", StringComparison.Ordinal),
                Step(steps, "radios").Detail);

            Section("Each problem is found, and says where it is fixed");

            var noKey = Working();
            noKey.GeminiApiKey = "";
            var keyStep = Step(SetupChecklist.Evaluate(noKey, folder, facts), "transcription");
            Check("no Gemini key is a step to do", keyStep.State == StepState.Todo && keyStep.Page == "speech");
            Eq("which keeps the checklist open", SetupChecklist.OpenRequired(SetupChecklist.Evaluate(noKey, folder, facts)).ToString(), "1");

            var whisper = Working();
            whisper.GeminiApiKey = "";
            whisper.SpeechEngine = "Whisper";
            whisper.WhisperModelPath = Path.Combine(folder, "missing.bin");
            Check("with Whisper the key does not matter, the model does",
                Step(SetupChecklist.Evaluate(whisper, folder, facts), "transcription").State == StepState.Problem);

            var wrongPath = Working();
            wrongPath.ExternalAudioExePath = Path.Combine(folder, "nowhere", "DCS-SR-ExternalAudio.exe");
            var audioStep = Step(SetupChecklist.Evaluate(wrongPath, folder, facts), "externalaudio");
            Check("an ExternalAudio path that leads nowhere is a problem", audioStep.State == StepState.Problem && audioStep.Page == "connection");

            var noModel = Working();
            noModel.VoskModelPath = "";
            Check("no speech model is a step to do", Step(SetupChecklist.Evaluate(noModel, folder, facts), "wakeword").State == StepState.Todo);

            var noRadios = Working();
            noRadios.Radios = new List<RadioConfig>();
            Check("no radio is a step to do", Step(SetupChecklist.Evaluate(noRadios, folder, facts), "radios").State == StepState.Todo);

            var voiceStep = Step(SetupChecklist.Evaluate(Working(), folder,
                new SetupFacts { InstalledVoices = new[] { "Voice B" } }), "radios");
            Check("a voice this PC does not have is a problem, by name",
                voiceStep.State == StepState.Problem && voiceStep.Detail.Contains("Voice A", StringComparison.Ordinal), voiceStep.Detail);
            Check("without the voice list it is not guessed at",
                Step(SetupChecklist.Evaluate(Working(), folder, new SetupFacts()), "radios").State == StepState.Done);

            var bridge = Working();
            bridge.ChatBridgeEnabled = true;
            var bridgeStep = Step(SetupChecklist.Evaluate(bridge, folder, facts), "chatbridge");
            Check("a chat bridge switched on without its hook is a problem",
                bridgeStep.State == StepState.Problem && bridgeStep.Page == "dcsgrpc" && bridgeStep.Section == "replies");

            var savedGames = Path.Combine(folder, "DCS.dcs_serverrelease");
            ChatBridgeSetup.Install(savedGames);
            bridge.ChatBridgeFolder = ChatBridgeSetup.BridgeFolderFor(savedGames);
            Check("and done once the hook is installed",
                Step(SetupChecklist.Evaluate(bridge, folder, facts), "chatbridge").State == StepState.Done);

            var grpc = Working();
            grpc.DcsGrpcEnabled = true;
            grpc.DcsGrpcAddress = "";
            Check("DCS-gRPC on without an address is a problem",
                Step(SetupChecklist.Evaluate(grpc, folder, facts), "grpc").State == StepState.Problem);

            Check("no service is optional, not a problem",
                Step(SetupChecklist.Evaluate(Working(), folder, new SetupFacts { ServiceInstalled = false }), "service").State == StepState.Optional);
            Check("no backup likewise",
                Step(SetupChecklist.Evaluate(Working(), folder, new SetupFacts { Backups = 0 }), "backup").State == StepState.Optional);

            Check("every step points at a page the editor has",
                steps.All(s => EditorSearch.PageName(s.Page, s.Section) != s.Page), string.Join(", ", steps.Select(s => s.Page)));

            Section("The startup warnings are the bot's own");

            var discord = Working();
            discord.DiscordEnabled = true;
            discord.DiscordWebhookUrl = "";
            var warnings = SetupChecklist.StartupWarnings(discord);
            Check("a switched-on Discord without a webhook is warned about",
                warnings.Any(w => w.Contains("DiscordWebhookUrl", StringComparison.Ordinal)), string.Join(" | ", warnings));
            Check("and the step says the same", Step(SetupChecklist.Evaluate(discord, folder, facts), "discord").State == StepState.Problem);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch { /* best effort */ }
        }

        Section("The editor opens on the checklist while it is needed");

        var main = ReadSource("src/Darkstar.Gui/Pages/Main.razor");
        Check("it counts the open required steps", main.Contains("SetupChecklist.OpenRequired(SetupChecklist.Evaluate(Store.Config, Store.ConfigFolder))", StringComparison.Ordinal));
        Check("and starts there when one is open", main.Contains("if (Store.IsLoaded && _openRequired > 0) _active = \"checklist\";", StringComparison.Ordinal));
        Check("the checklist page is wired in", main.Contains("<ChecklistPanel OnGo=\"GoToStep\" />", StringComparison.Ordinal));
    }
}
