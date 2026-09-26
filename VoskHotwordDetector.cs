using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Vosk;

namespace Darkstar;

/// <summary>
/// The Vosk model could not be loaded. Carries the reason plus concrete things the user can do,
/// so the caller can print something useful instead of a stack trace.
/// </summary>
public sealed class VoskModelLoadException : Exception
{
    public VoskModelLoadException(string message, IReadOnlyList<string> hints) : base(message)
    {
        Hints = hints;
    }

    public IReadOnlyList<string> Hints { get; }
}

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
    private const int VoskSampleRate = DecimatingLowPass.OutputSampleRate;

    private readonly Model _model;
    private readonly bool _ownsModel;
    private readonly VoskRecognizer _recognizer;
    private readonly Regex _keywordRegex;

    /// <summary>
    /// Anti-alias filter + decimation 48 kHz → 16 kHz. One per detector, i.e. one per radio,
    /// because it carries state across frames (see AudioFrontEnd for why that matters).
    /// Null when the old averaging path is selected for comparison.
    /// </summary>
    private readonly DecimatingLowPass? _lowPass;

    /// <summary>Optional level evening-out, off unless asked for. Also per radio.</summary>
    private readonly SpeechAutoGain? _autoGain;

    /// <summary>Loads its own model from disk. Fine for a single radio; for multiple radios
    /// prefer the constructor that takes an already-loaded Model, so the (often 40MB+) model
    /// file isn't loaded into memory once per radio.</summary>
    public VoskHotwordDetector(string modelPath, string keyword)
        : this(LoadModel(modelPath), keyword, ownsModel: true)
    {
    }

    /// <summary>Reuses an already-loaded Model (see VoskModelLoader) - use this when creating one
    /// detector per radio, so the model is only ever loaded from disk once.</summary>
    public VoskHotwordDetector(Model sharedModel, string keyword,
        HotwordAudioFilter filter = HotwordAudioFilter.LowPass, bool autoGain = false)
        : this(sharedModel, keyword, ownsModel: false, filter, autoGain)
    {
    }

    private VoskHotwordDetector(Model model, string keyword, bool ownsModel,
        HotwordAudioFilter filter = HotwordAudioFilter.LowPass, bool autoGain = false)
    {
        _model = model;
        _ownsModel = ownsModel;
        _recognizer = new VoskRecognizer(_model, VoskSampleRate);

        if (filter == HotwordAudioFilter.LowPass)
            _lowPass = new DecimatingLowPass();

        if (autoGain)
            _autoGain = new SpeechAutoGain();

        // Whole-word match with a word boundary on each side, instead of a plain substring
        // Contains() check - a substring match would (rarely, but it happens) fire on a keyword
        // that's merely part of a longer recognized word, and more importantly makes each
        // radio's detector strictly about ITS OWN configured word rather than any accidental
        // textual overlap with another radio's keyword. Case-insensitive so config.json casing
        // doesn't matter.
        _keywordRegex = new Regex($@"\b{Regex.Escape(keyword.Trim())}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    /// <summary>
    /// Loads a Vosk model from disk. Call once and share via the constructor that takes an
    /// already-loaded Model when running multiple radios, so the (often 40MB+) model file isn't
    /// loaded into memory once per radio.
    ///
    /// Everything that can go wrong here is checked up front, because it cannot be caught
    /// afterwards: Vosk's native loader returns a null pointer on failure, the C# wrapper stores
    /// it without complaint, and the process then dies inside <c>new_VoskRecognizer</c> with an
    /// AccessViolationException that no catch block can intercept. So a bad path has to be
    /// turned into an exception BEFORE the native call, and the loaded model is checked for a
    /// real handle before anything is built from it.
    /// </summary>
    /// <exception cref="VoskModelLoadException">
    /// The model folder is missing, isn't a model, or the native library refused to load it.
    /// </exception>
    public static Model LoadModel(string modelPath)
    {
        var check = VoskModelCheck.Check(modelPath);
        if (!check.IsUsable)
            throw new VoskModelLoadException(check.Message, check.Hints);

        // Usable, but worth saying something about - e.g. the model was found one folder deeper
        // than configured, which works but is better fixed in config.json.
        if (!string.IsNullOrEmpty(check.Message))
        {
            Logger.Log($"NOTE: {check.Message}");
            foreach (var hint in check.Hints)
                Logger.Log($"  - {hint}");
        }

        try
        {
            Vosk.Vosk.SetLogLevel(-1); // disable Vosk's own (fairly chatty) logging
        }
        catch (DllNotFoundException ex)
        {
            // libvosk.dll itself is missing, or its own dependencies are - almost always the
            // Visual C++ Redistributable, which the installer normally takes care of.
            throw new VoskModelLoadException(
                "The Vosk library (libvosk.dll) could not be loaded.",
                new[]
                {
                    "Install the Visual C++ Redistributable (x64): https://aka.ms/vs/17/release/vc_redist.x64.exe",
                    "Re-running the D.A.R.K.S.T.A.R. installer does this for you.",
                    $"Details: {ex.Message}",
                });
        }

        Model model;
        try
        {
            model = new Model(check.ResolvedPath!);
        }
        catch (Exception ex)
        {
            throw new VoskModelLoadException(
                $"Vosk could not load the model in '{check.ResolvedPath}'.",
                new[] { $"Details: {ex.Message}" });
        }

        if (!HasNativeHandle(model))
        {
            model.Dispose(); // safe: Dispose checks the handle before freeing it
            throw new VoskModelLoadException(
                $"Vosk refused the model in '{check.ResolvedPath}' - the folder looks like a model, but the library could not read it.",
                new[]
                {
                    "The files are probably incomplete or corrupt - unpack the archive again.",
                    "A model for a different Vosk version can cause this too; try vosk-model-small-en-us-0.15.",
                });
        }

        return model;
    }

    /// <summary>
    /// True if the loaded model actually holds a native pointer. Vosk's <c>Model</c> keeps it in
    /// a private <c>handle</c> field and exposes no way to ask, so this reads that field - the
    /// alternative being to find out by crashing.
    ///
    /// If a future Vosk version renames the field, this returns true rather than blocking a model
    /// that may well be fine: the folder checks above have already passed, so the remaining risk
    /// is a corrupt model, which was the situation before this check existed anyway.
    /// </summary>
    private static bool HasNativeHandle(Model model)
    {
        try
        {
            var field = typeof(Model).GetField("handle",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            if (field?.GetValue(model) is HandleRef handle)
                return handle.Handle != IntPtr.Zero;

            return true; // unknown layout - don't stand in the way
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// The transcript Vosk last produced, for diagnosing why a wake word did or didn't fire.
    /// </summary>
    public string LastText { get; private set; } = "";

    public bool ProcessAudio(byte[] pcm16Mono48k)
    {
        if (pcm16Mono48k.Length < 2) return false;

        if (_autoGain != null)
        {
            // Works on a copy: the caller's buffer also goes into the recording that is later
            // transcribed and, with SaveRecordings on, written to disk - amplifying that as a
            // side effect would change what the pilot hears back in a bug report.
            pcm16Mono48k = (byte[])pcm16Mono48k.Clone();
            _autoGain.Process(pcm16Mono48k);
        }

        var downsampled = _lowPass != null
            ? _lowPass.Process(pcm16Mono48k)
            : DecimatingLowPass.AverageDecimate(pcm16Mono48k);

        if (downsampled.Length == 0) return false;

        bool isFinal = _recognizer.AcceptWaveform(downsampled, downsampled.Length);
        string json = isFinal ? _recognizer.Result() : _recognizer.PartialResult();
        string text = ExtractText(json, isFinal);

        if (!string.IsNullOrWhiteSpace(text)) LastText = text;

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

    public void Dispose()
    {
        _recognizer.Dispose();
        if (_ownsModel) _model.Dispose();
    }
}
