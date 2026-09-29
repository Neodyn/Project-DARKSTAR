using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Darkstar;

/// <summary>One TTS voice as DCS-SR-ExternalAudio.exe reports it.</summary>
/// <param name="Name">Exactly the string that goes into <c>--voice</c>. Copied verbatim, never tidied up.</param>
/// <param name="Culture">e.g. "en-GB". Empty when the line didn't carry one.</param>
/// <param name="Gender">"Male" / "Female" / "Neutral" as .NET reports it. Empty when absent.</param>
public sealed record TtsVoice(string Name, string Culture, string Gender)
{
    /// <summary>"Microsoft Hazel Desktop (en-GB, Female)" - for a picker, never for --voice.</summary>
    public string Describe()
    {
        var details = string.Join(", ", new[] { Culture, Gender }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return details.Length == 0 ? Name : $"{Name} ({details})";
    }
}

/// <summary>
/// Asks DCS-SR-ExternalAudio.exe which TTS voices this machine actually has, so a voice can be
/// picked from a list instead of typed from memory and hoped for.
///
/// WHY THIS IS NEEDED AT ALL: the voices Windows shows in its own settings are not the voices
/// ExternalAudio can use. It speaks through System.Speech, which only sees SAPI5 voices - the
/// modern "natural" Windows 11 voices are OneCore voices and are usually invisible to it. So the
/// only trustworthy list is the one the tool itself prints, and a name that isn't in it will fail
/// at transmit time with nothing but silence on the radio to show for it.
///
/// HOW THE LIST IS OBTAINED: ExternalAudio prints it from its *argument-error* handler, not from a
/// dedicated switch - so this deliberately calls it with `--help`, which CommandLineParser routes
/// there. That also means the tool exits with a non-zero code and prints its usage text first;
/// both are expected here and neither is an error.
/// </summary>
public static class TtsVoices
{
    /// <summary>
    /// Long enough for a cold start of a .NET tool that builds a SpeechSynthesizer, short enough
    /// that a GUI button can't appear to hang. The process is killed on expiry.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    /// <param name="Voices">What was found, in the order printed. Empty is a valid answer.</param>
    /// <param name="Error">Null on success. A sentence naming what to do otherwise.</param>
    public sealed record Result(List<TtsVoice> Voices, string? Error)
    {
        public bool Ok => Error == null;
    }

    /// <summary>
    /// The line ExternalAudio prints per voice:
    /// <c>Name: Microsoft Hazel Desktop, Culture: en-GB,  Gender: Female, Age: Adult, Desc: ...</c>
    /// </summary>
    /// <remarks>
    /// The name is taken non-greedily up to ", Culture:" rather than up to the first comma, because
    /// several shipped voices have one in their name ("Microsoft Server Speech Text to Speech Voice
    /// (en-US, ZiraPro)"). Whitespace is loose on purpose - the tool prints two spaces before
    /// "Gender:" today, and that is not worth depending on.
    /// </remarks>
    private static readonly Regex VoiceLine = new(
        @"^\s*Name:\s*(?<name>.+?)\s*,\s*Culture:\s*(?<culture>[^,]*?)\s*,\s*Gender:\s*(?<gender>[^,]*?)\s*(?:,|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Runs the tool and returns what it reported. Never throws: every failure comes back as
    /// <see cref="Result.Error"/>, because this is called from a GUI button and from startup
    /// validation, neither of which should be able to bring anything down.
    /// </summary>
    public static async Task<Result> ListAsync(string? exePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(exePath))
            return Fail("No path to DCS-SR-ExternalAudio.exe is configured (CH1 Connection).");

        if (!File.Exists(exePath))
            return Fail($"DCS-SR-ExternalAudio.exe was not found at \"{exePath}\" - use Detect SRS installation on CH1.");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "--help",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory
            };

            using var process = new Process { StartInfo = psi };
            process.Start();

            // Both streams: the voice lines go to stdout, while CommandLineParser writes its usage
            // text to stderr. Read them concurrently - filling one pipe while waiting on the other
            // is the classic way to deadlock a redirected child process.
            var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(DefaultTimeout);

            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                return Fail($"DCS-SR-ExternalAudio.exe did not answer within {DefaultTimeout.TotalSeconds:0} seconds.");
            }

            var combined = new StringBuilder()
                .AppendLine(await stdOutTask)
                .AppendLine(await stdErrTask)
                .ToString();

            var voices = Parse(combined);

            // Exit code is deliberately ignored: asking for --help IS an argument error as far as
            // this tool is concerned, so a non-zero code here is the normal case.
            if (voices.Count == 0)
                return Fail("DCS-SR-ExternalAudio.exe reported no usable TTS voices. " +
                            "Windows' own \"natural\" voices are not visible to it - a SAPI5 voice " +
                            "or language pack has to be installed, or use Azure/Google instead.");

            Logger.Debug($"[TtsVoices] {voices.Count} voice(s) reported by {exePath}.");
            return new Result(voices, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Fail($"Could not ask DCS-SR-ExternalAudio.exe for its voices: {ex.Message}");
        }
    }

    /// <summary>
    /// Picks the voice lines out of the tool's output. Pure, so the exact format shipped by SRS can
    /// be tested without Windows or a TTS engine present.
    /// </summary>
    internal static List<TtsVoice> Parse(string? output)
    {
        var voices = new List<TtsVoice>();
        if (string.IsNullOrWhiteSpace(output)) return voices;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in output.Split('\n'))
        {
            var match = VoiceLine.Match(line.TrimEnd('\r'));
            if (!match.Success) continue;

            var name = match.Groups["name"].Value.Trim();
            if (name.Length == 0) continue;

            // The tool prints its examples below the list, and one of them contains a voice name.
            // Those lines start with "--text=" or "Example:", so they never match the pattern above -
            // but a duplicate name is dropped regardless, since a picker with the same entry twice
            // is worse than a shorter one.
            if (!seen.Add(name)) continue;

            voices.Add(new TtsVoice(name,
                match.Groups["culture"].Value.Trim(),
                match.Groups["gender"].Value.Trim()));
        }

        return voices;
    }

    private static Result Fail(string message) => new(new List<TtsVoice>(), message);

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* Already gone, or not ours to kill - nothing useful to do either way. */ }
    }
}
