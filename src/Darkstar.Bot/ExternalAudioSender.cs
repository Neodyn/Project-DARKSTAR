using System.Diagnostics;
using System.Globalization;

namespace Darkstar;

/// <summary>
/// Uses the official, bundled DCS-SR-ExternalAudio.exe to send text (via local Windows TTS,
/// Azure, or Google) or an audio file to an SRS frequency. This takes care of the full
/// TCP/UDP protocol handling for us - no need for our own Opus/packet handling, and it's
/// guaranteed to be protocol-compatible since it's part of the official SRS codebase.
///
/// Frequency and modulation are passed per call (not fixed at construction) so a single sender
/// can reply on whichever radio the incoming request came in on, when multiple radios are
/// monitored simultaneously.
/// </summary>
public sealed class ExternalAudioSender
{
    private readonly string _exePath;
    private readonly string _host;
    private readonly int _port;
    private readonly int _coalition;
    private readonly string _name;
    private readonly string _voiceName;
    private readonly string _extraArgs;

    public ExternalAudioSender(string exePath, string host, int port,
        int coalition = 2, string name = "DARKSTAR", string voiceName = "", string extraArgs = "")
    {
        _exePath = exePath;
        _host = host;
        _port = port;
        _coalition = coalition;
        _name = name;
        _voiceName = voiceName;
        _extraArgs = extraArgs ?? "";
    }

    /// <summary>
    /// User-supplied extra arguments (config: ExternalAudioExtraArgs), appended to every call -
    /// for options that depend on the installed SRS version, such as a speaking-rate flag.
    /// </summary>
    private string ExtraArgs => string.IsNullOrWhiteSpace(_extraArgs) ? "" : " " + _extraArgs.Trim();

    /// <summary>
    /// Sends text via TTS on the given frequency/modulation.
    /// </summary>
    /// <param name="voice">
    /// Voice for this one transmission, overriding the sender's default. This is how several
    /// radios end up sounding like several people: the voice belongs to the radio that is
    /// answering, not to the bot. Empty or null falls back to the global voice, and that in turn
    /// to whatever ExternalAudio picks by itself.
    /// </param>
    public Task SendTextAsync(string text, double frequencyHz, string modulation,
        string? voice = null, CancellationToken token = default)
    {
        var freqMhz = (frequencyHz / 1_000_000.0).ToString(CultureInfo.InvariantCulture);

        // Escape quotation marks in the text, since everything is passed as a single command-line argument.
        var escapedText = text.Replace("\"", "\\\"");

        var effectiveVoice = string.IsNullOrWhiteSpace(voice) ? _voiceName : voice.Trim();

        var args = $"--text=\"{escapedText}\" " +
                   $"--freqs={freqMhz} " +
                   $"--modulations={modulation} " +
                   $"--coalition={_coalition} " +
                   $"--port={_port} " +
                   $"--name=\"{_name}\"" +
                   (string.IsNullOrWhiteSpace(effectiveVoice) ? "" : $" --voice=\"{effectiveVoice}\"") +
                   ExtraArgs;

        return RunAsync(args, token);
    }

    /// <summary>Sends an existing MP3/OGG file on the given frequency/modulation.</summary>
    public Task SendFileAsync(string filePath, double frequencyHz, string modulation, CancellationToken token = default)
    {
        var freqMhz = (frequencyHz / 1_000_000.0).ToString(CultureInfo.InvariantCulture);

        var args = $"--file=\"{filePath}\" " +
                   $"--freqs={freqMhz} " +
                   $"--modulations={modulation} " +
                   $"--coalition={_coalition} " +
                   $"--port={_port} " +
                   $"--name=\"{_name}\"" +
                   ExtraArgs;

        return RunAsync(args, token);
    }

    private async Task RunAsync(string arguments, CancellationToken token)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _exePath,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(_exePath) ?? Environment.CurrentDirectory
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        // Log the output so configuration errors (wrong port, TTS voice not found, etc.) are
        // immediately visible instead of just "audio simply isn't arriving".
        var stdOutTask = process.StandardOutput.ReadToEndAsync(token);
        var stdErrTask = process.StandardError.ReadToEndAsync(token);

        await process.WaitForExitAsync(token);

        var stdOut = await stdOutTask;
        var stdErr = await stdErrTask;

        if (!string.IsNullOrWhiteSpace(stdOut))
            Logger.Debug($"[ExternalAudio] {stdOut.Trim()}");
        if (!string.IsNullOrWhiteSpace(stdErr))
            Logger.Log($"[ExternalAudio:ERR] {stdErr.Trim()}");

        if (process.ExitCode != 0)
            Logger.Log($"[ExternalAudio] Process exited with exit code {process.ExitCode}.");
    }
}
