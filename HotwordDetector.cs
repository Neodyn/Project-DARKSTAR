namespace Darkstar;

public interface IHotwordDetector
{
    /// <summary>
    /// Receives a continuous stream of PCM16 audio chunks (48kHz mono) from the radio stream.
    /// Returns true as soon as the hotword has been detected.
    /// </summary>
    bool ProcessAudio(byte[] pcm16Mono48k);
}

/// <summary>
/// PLACEHOLDER implementation.
///
/// This project uses Vosk (see VoskHotwordDetector.cs) for real hotword detection: it
/// continuously transcribes locally and checks the text for the configured keyword.
///
/// This class only does simple energy-based voice activity detection (VAD) as a fallback so
/// the overall system stays runnable even without a Vosk model configured. It does NOT
/// actually "recognize" a word, only whether something is currently being spoken loudly enough.
/// </summary>
public sealed class EnergyThresholdPlaceholderDetector : IHotwordDetector
{
    private readonly short _threshold;
    private readonly int _consecutiveFramesNeeded;
    private int _loudFrameCount;

    // For debugging/calibration purposes only: periodically shows the currently measured volume
    // level compared to the threshold, so you can set HotwordEnergyThreshold in config.json sensibly.
    private int _framesSinceLastDebugLog;
    private const int DebugLogEveryNFrames = 25; // ~every 0.5s at 20ms frames

    public EnergyThresholdPlaceholderDetector(short threshold = 2000, int consecutiveFramesNeeded = 5)
    {
        _threshold = threshold;
        _consecutiveFramesNeeded = consecutiveFramesNeeded;
    }

    public bool ProcessAudio(byte[] pcm16Mono48k)
    {
        if (pcm16Mono48k.Length < 2) return false;

        long sumAbs = 0;
        int samples = pcm16Mono48k.Length / 2;
        for (int i = 0; i < samples; i++)
        {
            short s = BitConverter.ToInt16(pcm16Mono48k, i * 2);
            sumAbs += Math.Abs((int)s);
        }
        var avg = sumAbs / Math.Max(1, samples);

        _framesSinceLastDebugLog++;
        if (_framesSinceLastDebugLog >= DebugLogEveryNFrames)
        {
            _framesSinceLastDebugLog = 0;
            Logger.Debug($"[Hotword Debug] Current volume: {avg} (threshold: {_threshold})");
        }

        if (avg > _threshold)
        {
            _loudFrameCount++;
            if (_loudFrameCount >= _consecutiveFramesNeeded)
            {
                _loudFrameCount = 0;
                return true;
            }
        }
        else
        {
            _loudFrameCount = 0;
        }
        return false;
    }
}

