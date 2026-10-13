namespace Darkstar;

/// <summary>
/// What, if anything, leaves this machine for the internet with the current configuration.
///
/// WHY THIS EXISTS AS CODE rather than as a sentence in the manual: "run it offline" is a promise
/// somebody makes to their squadron, and the configuration that keeps it is spread across four
/// unrelated settings. Switching <see cref="AppConfig.SpeechEngine"/> to Whisper is the obvious
/// one and the easiest to mistake for the whole job - a server with Discord notifications on, or
/// with Azure credentials left in <see cref="AppConfig.ExternalAudioExtraArgs"/>, is still sending
/// pilots' words and the bot's replies to somebody else's server while its owner believes
/// otherwise.
///
/// So the bot states this at startup, as a list rather than a verdict: naming what reaches the
/// network is checkable, "you are offline" is a claim somebody has to trust.
///
/// Deliberately a pure function of the configuration. It reports what the settings MEAN, not what
/// the sockets did - a probe would be a different and much weaker thing, since the absence of
/// traffic during one minute proves nothing about the next.
/// </summary>
public static class NetworkUse
{
    /// <summary>One thing that talks to a service outside this machine.</summary>
    /// <param name="Component">What is doing it, in the words the settings use.</param>
    /// <param name="Detail">What it sends, and which setting turns it off.</param>
    public sealed record Reach(string Component, string Detail);

    /// <summary>
    /// Everything that currently leaves the machine. Empty means nothing does - which is the state
    /// an offline installation is aiming for.
    ///
    /// SRS and DCS-gRPC are deliberately absent. Both are reached over a network socket, but both
    /// are services the operator runs themselves, usually on the same host or the same LAN; listing
    /// them would bury the three that actually go to a third party.
    /// </summary>
    public static List<Reach> Describe(AppConfig config)
    {
        var reaches = new List<Reach>();

        var usesWhisper = string.Equals(config.SpeechEngine?.Trim(), "Whisper", StringComparison.OrdinalIgnoreCase);

        if (!usesWhisper)
        {
            reaches.Add(new Reach(
                "Transcription (Google Gemini)",
                "Every transmission is sent to Google as audio, and the reply comes back from there. " +
                "Set SpeechEngine to \"Whisper\" to transcribe on this machine instead."));
        }

        if (config.DiscordEnabled && !string.IsNullOrWhiteSpace(config.DiscordWebhookUrl))
        {
            reaches.Add(new Reach(
                "Status notifications (Discord)",
                "Start, stop and SRS connection changes are posted to a Discord webhook. " +
                "No pilot audio or transcripts. Set DiscordEnabled to false to stop it."));
        }

        // The cloud TTS credentials are the one that hides: they are not a setting of their own but
        // a flag inside a free-form argument string, so they survive every other offline switch and
        // nothing about the field's name suggests the replies are being spoken by somebody else.
        var extraArgs = config.ExternalAudioExtraArgs ?? "";

        if (extraArgs.Contains("--azureCredentials", StringComparison.OrdinalIgnoreCase))
            reaches.Add(new Reach(
                "Speech output (Azure AI Speech)",
                "Reply text is sent to Azure to be spoken. Remove --azureCredentials from " +
                "ExternalAudioExtraArgs to use a local voice."));

        if (extraArgs.Contains("--googleCredentials", StringComparison.OrdinalIgnoreCase))
            reaches.Add(new Reach(
                "Speech output (Google Cloud TTS)",
                "Reply text is sent to Google to be spoken. Remove --googleCredentials from " +
                "ExternalAudioExtraArgs to use a local voice."));

        return reaches;
    }

    /// <summary>True when nothing in this configuration talks to a third party.</summary>
    public static bool IsFullyOffline(AppConfig config) => Describe(config).Count == 0;

    /// <summary>
    /// The one-line summary for the startup log. Written so that both outcomes are a statement of
    /// fact rather than reassurance - somebody grepping the log for this line should learn what is
    /// true, not be told everything is fine.
    /// </summary>
    public static string Summarise(AppConfig config)
    {
        var reaches = Describe(config);

        return reaches.Count == 0
            ? "Nothing in this configuration reaches the internet: transcription, the wake word and the " +
              "replies all run on this machine."
            : $"{reaches.Count} part(s) of this configuration reach the internet: " +
              string.Join(", ", reaches.Select(r => r.Component)) + ".";
    }
}
