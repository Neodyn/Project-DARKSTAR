using Whisper.net;

namespace Darkstar;

/// <summary>
/// Transcription on this machine, through whisper.cpp via the Whisper.net bindings.
///
/// WHAT IT IS NOT: a replacement for <see cref="GeminiClient"/>. Gemini returns a transcript AND a
/// freely worded reply from one call; Whisper returns the transcript. Everything above the last
/// rung of the reply chain - the radio check, the mission-data calls, the airfield calls, the
/// handoff, phrases.json - needs only the transcript and behaves identically. The freely generated
/// answer is the one thing that needs a language model, and with this engine it becomes
/// FallbackResponse. That is a deliberate trade, not a gap: see <see cref="ProducesFreeReplies"/>.
///
/// WHY LOCAL IS WORTH THE CPU: no API key, no per-transmission cost, no daily quota, and no
/// recording of other players' voices leaving the machine. Whisper is also markedly better at
/// accented English than the small Vosk model, which is the failure this project keeps running
/// into.
///
/// THE MODEL IS LOADED ONCE. WhisperFactory maps a file of up to 1.5 GB and a processor holds the
/// decoding state; building either per transmission would dominate the time it takes to answer.
/// Both are created on first use and reused, and the processor is used under a lock because one
/// decoder is not safe for two radios transcribing at once.
/// </summary>
public sealed class WhisperClient : IDisposable
{
    private readonly string _modelPath;
    private readonly string _language;

    /// <summary>
    /// One transcription at a time. Several radios can finish recording in the same second, and
    /// whisper.cpp's decoder state is not shared safely - the symptom would be two transmissions
    /// transcribed into one another, which reads like a recognition problem and is not.
    /// </summary>
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    private readonly DecimatingLowPass _resampler = new();

    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private bool _disposed;

    public WhisperClient(string modelPath, string language)
    {
        _modelPath = modelPath;
        _language = string.IsNullOrWhiteSpace(language) ? "en" : language.Trim();
    }

    /// <summary>
    /// False: this engine transcribes and nothing more. The reply pipeline reads this rather than
    /// checking which engine is configured, so adding a third one later does not mean finding
    /// every place that asked "is this Gemini?".
    /// </summary>
    public bool ProducesFreeReplies => false;

    /// <summary>
    /// Turns one recorded transmission into text.
    /// </summary>
    /// <param name="pcm16Mono48k">
    /// The recording as the radio path produces it: PCM16, mono, 48 kHz. Whisper wants 16 kHz, and
    /// the reduction goes through the same <see cref="DecimatingLowPass"/> the wake word uses -
    /// a 161-tap windowed-sinc filter that puts fold-over products 60-79 dB down. Decimating
    /// without it would alias 10 kHz onto 6 kHz, right where consonants are told apart, which is
    /// exactly the mistake this project already made once.
    /// </param>
    public async Task<string> TranscribeAsync(byte[] pcm16Mono48k, CancellationToken token = default)
    {
        if (pcm16Mono48k.Length == 0) return "";

        await _oneAtATime.WaitAsync(token);

        try
        {
            var processor = GetProcessor();

            // The filter carries its delay line between calls, which is right for a continuous
            // stream and wrong here: each transmission is its own recording, and the tail of the
            // last one has no business bleeding into the start of this one.
            _resampler.Reset();

            var pcm16k = _resampler.Process(pcm16Mono48k);
            var wav = WavUtils.WrapPcm16AsWav(pcm16k, sampleRate: DecimatingLowPass.OutputSampleRate, channels: 1);

            using var stream = new MemoryStream(wav, writable: false);

            // Whisper returns one segment per utterance it hears; a radio call is usually one, but
            // a pilot who pauses mid-sentence produces two and both belong to the same request.
            var text = new System.Text.StringBuilder();
            await foreach (var segment in processor.ProcessAsync(stream, token))
                text.Append(segment.Text);

            return WhisperTranscript.Clean(text.ToString());
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    /// <summary>
    /// The model on first use rather than at construction, so a bad path is reported by the
    /// startup check with a sentence somebody can act on, instead of by a native loader throwing
    /// out of a constructor.
    /// </summary>
    private WhisperProcessor GetProcessor()
    {
        if (_processor != null) return _processor;

        Logger.Log($"[Speech] Loading the Whisper model, which takes a moment: {_modelPath}");

        _factory = WhisperFactory.FromPath(_modelPath);
        _processor = _factory.CreateBuilder()
            .WithLanguage(_language)
            .Build();

        Logger.Log($"[Speech] Whisper ready, transcribing in \"{_language}\".");
        return _processor;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _processor?.Dispose();
        _factory?.Dispose();
        _oneAtATime.Dispose();
    }
}
