namespace Darkstar;

// The two abstractions the reply pipeline is written against. GeminiClient implements both, which
// is what keeps BotService independent of which service actually does the transcribing and the
// answering - swapping in another backend means writing one class, not touching the pipeline.
//
// There is deliberately no ITextToSpeech here: reply audio doesn't go through a .NET interface at
// all, it is handed to the official DCS-SR-ExternalAudio.exe (see ExternalAudioSender), which
// takes care of TTS and the whole SRS protocol in one step.

public interface ISpeechToText
{
    /// <summary>PCM16 mono 48kHz audio -> recognized text.</summary>
    Task<string> TranscribeAsync(byte[] pcm16Mono48k, CancellationToken token = default);
}

public interface IResponseGenerator
{
    Task<string> GenerateReplyAsync(string transcribedText, CancellationToken token = default);
}
