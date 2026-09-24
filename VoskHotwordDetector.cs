using System.Text.Json;
using System.Text.RegularExpressions;
using Vosk;

namespace Darkstar;

/// <summary>
/// Wake word detection with no account/cloud needed at all: Vosk continuously transcribes the
/// audio locally (offline, free) and we check the recognized text for the configured keyword.
///
/// This isn't a specialized wake word detector but a full (albeit small/fast) offline STT
/// engine - so a bit more CPU load than a dedicated wake word engine would need, but no
/// account, no API key, no internet connection required, and you can set the keyword freely as
/// text instead of having to pick from a fixed list of built-in words.
///
/// Model download (one-time, not part of NuGet):
///   https://alphacephei.com/vosk/models -> e.g. "vosk-model-small-en-us-0.15" (small, ~40MB)
///   download, unpack, enter the unpacked folder path in config.json under VoskModelPath.
/// </summary>
public sealed class VoskHotwordDetector : IHotwordDetector, IDisposable
{
    private const int InputSampleRate = 48000;
    private const int VoskSampleRate = 16000;
    private const int DownsampleRatio = InputSampleRate / VoskSampleRate; // = 3

    private readonly Model _model;
    private readonly bool _ownsModel;
    private readonly VoskRecognizer _recognizer;
    private readonly Regex _keywordRegex;

    /// <summary>Loads its own model from disk. Fine for a single radio; for multiple radios
    /// prefer the constructor that takes an already-loaded Model, so the (often 40MB+) model
    /// file isn't loaded into memory once per radio.</summary>
    public VoskHotwordDetector(string modelPath, string keyword)
        : this(LoadModel(modelPath), keyword, ownsModel: true)
    {
    }

    /// <summary>Reuses an already-loaded Model (see VoskModelLoader) - use this when creating one
    /// detector per radio, so the model is only ever loaded from disk once.</summary>
    public VoskHotwordDetector(Model sharedModel, string keyword)
        : this(sharedModel, keyword, ownsModel: false)
    {
    }

    private VoskHotwordDetector(Model model, string keyword, bool ownsModel)
    {
        _model = model;
        _ownsModel = ownsModel;
        _recognizer = new VoskRecognizer(_model, VoskSampleRate);

        // Whole-word match with a word boundary on each side, instead of a plain substring
        // Contains() check - a substring match would (rarely, but it happens) fire on a keyword
        // that's merely part of a longer recognized word, and more importantly makes each
        // radio's detector strictly about ITS OWN configured word rather than any accidental
        // textual overlap with another radio's keyword. Case-insensitive so config.json casing
        // doesn't matter.
        _keywordRegex = new Regex($@"\b{Regex.Escape(keyword.Trim())}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    /// <summary>Loads a Vosk model from disk. Call once and share via the constructor that takes
    /// an already-loaded Model when running multiple radios, so the (often 40MB+) model file
    /// isn't loaded into memory once per radio.</summary>
    public static Model LoadModel(string modelPath)
    {
        Vosk.Vosk.SetLogLevel(-1); // disable Vosk's own (fairly chatty) logging
        return new Model(modelPath);
    }

    public bool ProcessAudio(byte[] pcm16Mono48k)
    {
        if (pcm16Mono48k.Length < 2) return false;

        var downsampled = Downsample48kTo16k(pcm16Mono48k);

        bool isFinal = _recognizer.AcceptWaveform(downsampled, downsampled.Length);
        string json = isFinal ? _recognizer.Result() : _recognizer.PartialResult();
        string text = ExtractText(json, isFinal);

        if (string.IsNullOrWhiteSpace(text)) return false;
        if (!_keywordRegex.IsMatch(text)) return false;

        // Match: reset the recognizer so the same word doesn't keep re-triggering and the next
        // recording cycle starts from a "clean" state again.
        _recognizer.Reset();
        return true;
    }

    private static string ExtractText(string json, bool isFinal)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var propertyName = isFinal ? "text" : "partial";
            return doc.RootElement.TryGetProperty(propertyName, out var value) ? value.GetString() ?? "" : "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>Average-based decimation 48kHz -> 16kHz (factor 3), result as PCM16 bytes for Vosk.</summary>
    private static byte[] Downsample48kTo16k(byte[] pcm16at48k)
    {
        int inSamples = pcm16at48k.Length / 2;
        int outSamples = inSamples / DownsampleRatio;
        var result = new byte[outSamples * 2];

        for (int i = 0; i < outSamples; i++)
        {
            int sum = 0;
            for (int j = 0; j < DownsampleRatio; j++)
            {
                int byteOffset = (i * DownsampleRatio + j) * 2;
                sum += BitConverter.ToInt16(pcm16at48k, byteOffset);
            }
            short avg = (short)(sum / DownsampleRatio);
            BitConverter.TryWriteBytes(result.AsSpan(i * 2, 2), avg);
        }

        return result;
    }

    public void Dispose()
    {
        _recognizer.Dispose();
        if (_ownsModel) _model.Dispose();
    }
}
