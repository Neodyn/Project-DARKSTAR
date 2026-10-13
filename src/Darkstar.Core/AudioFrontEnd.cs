using System.Text.Json.Serialization;

namespace Darkstar;

/// <summary>Which audio path feeds the wake word detector (config: HotwordAudioFilter).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HotwordAudioFilter
{
    /// <summary>Anti-alias filter before decimation. The default, and the one to use.</summary>
    LowPass,

    /// <summary>The old three-sample average. Kept only so the two can be compared.</summary>
    Average,
}

/// <summary>
/// Turns the 48 kHz audio arriving from SRS into the 16 kHz stream Vosk wants.
///
/// WHY THIS IS MORE THAN A LOOP: dropping two of every three samples folds everything above
/// 8 kHz back down into the audible range as a mirror image - aliasing. At 48 kHz, content at
/// 10 kHz lands on 6 kHz, 11 kHz lands on 5 kHz, and so on: exactly the band where consonants
/// live and where one word is told from another. The previous implementation averaged three
/// neighbouring samples before dropping two, which is a three-tap boxcar filter and does
/// attenuate the offending band - but only by 6 to 9 dB, so most of it still folded down.
///
/// This class filters properly first: a windowed-sinc low-pass that keeps speech up to about
/// 7 kHz and pushes everything from 8 kHz upwards more than 50 dB down, so what folds over is
/// far below the noise a radio carries anyway.
///
/// The filter has memory, which is the second thing the old code got wrong by not having any:
/// audio arrives in 20 ms frames, and a filter restarted at every frame boundary produces a
/// small step 50 times a second. Those steps are broadband clicks - the very thing a
/// recognizer misreads. So the delay line is carried across calls, and one detector instance
/// belongs to exactly one radio (which is how BotService already uses them).
/// </summary>
public sealed class DecimatingLowPass
{
    /// <summary>Input rate from SRS.</summary>
    public const int InputSampleRate = 48000;

    /// <summary>Rate Vosk models are trained for.</summary>
    public const int OutputSampleRate = 16000;

    /// <summary>48000 / 16000.</summary>
    public const int DecimationFactor = InputSampleRate / OutputSampleRate;

    /// <summary>
    /// Filter length. 161 taps put the transition band at roughly 7.0-8.0 kHz, which fits
    /// between "keeps the speech" and "below the 8 kHz fold-over point". The cost is 161
    /// multiply-adds per output sample, and only every third input sample produces one: about
    /// 2.6 million per second per radio, which is nothing on any machine that runs DCS.
    /// </summary>
    public const int DefaultTapCount = 161;

    /// <summary>
    /// −6 dB point of the filter. Deliberately below the 8 kHz Nyquist limit of the output rate
    /// so the transition band has somewhere to go before the fold-over point.
    /// </summary>
    public const double DefaultCutoffHz = 7200.0;

    private readonly float[] _taps;
    private readonly float[] _history;

    /// <summary>
    /// Index (into the history + new-samples stream) of the next sample that produces an output.
    /// Kept across calls so the decimation phase doesn't drift when frames arrive with a length
    /// that isn't a multiple of three.
    /// </summary>
    private int _nextOutputIndex;

    public DecimatingLowPass()
        : this(DesignLowPass(DefaultTapCount, DefaultCutoffHz, InputSampleRate))
    {
    }

    public DecimatingLowPass(float[] taps)
    {
        if (taps.Length < DecimationFactor)
            throw new ArgumentException($"A decimating filter needs at least {DecimationFactor} taps.", nameof(taps));

        _taps = taps;
        _history = new float[taps.Length - 1];
        _nextOutputIndex = _history.Length; // first full window ends at the first new sample
    }

    /// <summary>The filter coefficients in use - exposed so its response can be measured in tests.</summary>
    public IReadOnlyList<float> Taps => _taps;

    /// <summary>
    /// A windowed-sinc low-pass: the ideal brick wall (a sinc) multiplied by a Hamming window to
    /// stop it ringing, then normalised to unity gain at DC so the filter changes the spectrum
    /// but not the volume.
    ///
    /// Hamming rather than a fancier window because its ~53 dB of stopband rejection is already
    /// far below anything a radio transmission carries, and it gets there with fewer taps than
    /// Blackman would need.
    /// </summary>
    public static float[] DesignLowPass(int tapCount, double cutoffHz, double sampleRateHz)
    {
        if (tapCount < 3) throw new ArgumentOutOfRangeException(nameof(tapCount));
        if (tapCount % 2 == 0) tapCount++; // odd length keeps the filter symmetric around one centre tap
        if (cutoffHz <= 0 || cutoffHz >= sampleRateHz / 2)
            throw new ArgumentOutOfRangeException(nameof(cutoffHz));

        var taps = new double[tapCount];
        int middle = tapCount / 2;
        double normalisedCutoff = cutoffHz / sampleRateHz; // cycles per sample
        double sum = 0;

        for (int i = 0; i < tapCount; i++)
        {
            int n = i - middle;

            // Ideal low-pass impulse response: 2*fc * sinc(2*fc*n).
            double sinc = n == 0
                ? 2.0 * normalisedCutoff
                : Math.Sin(2.0 * Math.PI * normalisedCutoff * n) / (Math.PI * n);

            // Hamming window.
            double window = 0.54 - 0.46 * Math.Cos(2.0 * Math.PI * i / (tapCount - 1));

            taps[i] = sinc * window;
            sum += taps[i];
        }

        // Unity DC gain: without this, the filter would also change the level, and the
        // placeholder detector's volume threshold would suddenly mean something different.
        var result = new float[tapCount];
        for (int i = 0; i < tapCount; i++)
            result[i] = (float)(taps[i] / sum);

        return result;
    }

    /// <summary>
    /// Filters and decimates one chunk of PCM16 (48 kHz mono) to PCM16 (16 kHz mono).
    /// Call repeatedly with consecutive chunks; the filter state carries over.
    /// </summary>
    /// <summary>
    /// Scratch space for one call, kept between calls. Frames arrive at fifty a second per radio
    /// and are almost always the same size, so allocating this each time produced about 15 MB of
    /// garbage per minute per radio - measured, and the only reason this instance holds buffers
    /// at all. Safe because one instance belongs to exactly one radio and one thread at a time.
    /// </summary>
    private float[] _scratch = Array.Empty<float>();

    public byte[] Process(byte[] pcm16Mono48k)
    {
        int inputCount = pcm16Mono48k.Length / 2;
        if (inputCount == 0)
            return Array.Empty<byte>();

        // The filter window reaches back over the tail of the previous chunk, so work on
        // history + new samples as one contiguous stream.
        var needed = _history.Length + inputCount;
        if (_scratch.Length < needed) _scratch = new float[needed];
        var stream = _scratch;

        Array.Copy(_history, stream, _history.Length);
        for (int i = 0; i < inputCount; i++)
            stream[_history.Length + i] = BitConverter.ToInt16(pcm16Mono48k, i * 2);

        // How many outputs fall inside this chunk. Counted against the data length, not the
        // buffer length - the scratch buffer can be larger than this chunk needs.
        int outputCount = 0;
        for (int k = _nextOutputIndex; k < needed; k += DecimationFactor)
            outputCount++;

        var output = new byte[outputCount * 2];
        int written = 0;
        int index = _nextOutputIndex;

        for (; index < needed; index += DecimationFactor)
        {
            // The window ends at 'index' and reaches _taps.Length-1 samples back into the past.
            float accumulator = 0f;
            int start = index - (_taps.Length - 1);
            for (int t = 0; t < _taps.Length; t++)
                accumulator += _taps[t] * stream[start + t];

            // PCM16 range. The filter has unity DC gain but its passband can overshoot slightly
            // on transients, so clamp rather than let it wrap around into a loud crackle.
            int sample = (int)MathF.Round(accumulator);
            if (sample > short.MaxValue) sample = short.MaxValue;
            else if (sample < short.MinValue) sample = short.MinValue;

            BitConverter.TryWriteBytes(output.AsSpan(written, 2), (short)sample);
            written += 2;
        }

        // Carry the tail over: the next call's stream starts with these samples, so translate
        // the pending output position into that coordinate system.
        Array.Copy(stream, needed - _history.Length, _history, 0, _history.Length);
        _nextOutputIndex = index - inputCount;

        return output;
    }

    /// <summary>Clears the delay line - for reusing an instance on unrelated audio, e.g. between test files.</summary>
    public void Reset()
    {
        Array.Clear(_history);
        _nextOutputIndex = _history.Length;
    }

    /// <summary>
    /// The previous implementation, kept so the two can be compared on the same recording
    /// (config: HotwordAudioFilter = "Average"). Averages three neighbouring samples and drops
    /// two - stateless, which is part of why it was worse.
    /// </summary>
    public static byte[] AverageDecimate(byte[] pcm16at48k)
    {
        int inSamples = pcm16at48k.Length / 2;
        int outSamples = inSamples / DecimationFactor;
        var result = new byte[outSamples * 2];

        for (int i = 0; i < outSamples; i++)
        {
            int sum = 0;
            for (int j = 0; j < DecimationFactor; j++)
                sum += BitConverter.ToInt16(pcm16at48k, (i * DecimationFactor + j) * 2);

            BitConverter.TryWriteBytes(result.AsSpan(i * 2, 2), (short)(sum / DecimationFactor));
        }

        return result;
    }
}

/// <summary>
/// Evens out how loud different pilots arrive. SRS carries whatever level a pilot's microphone
/// and gain settings produce, and a transmission that arrives quiet is transcribed worse.
///
/// Deliberately timid, because the failure mode of an eager automatic gain control is worse
/// than the problem it solves: amplifying near-silence turns room noise into words, and words
/// into false wake words. So this only ever amplifies, never beyond a fixed ceiling, and only
/// adapts while something is actually being said - during quiet passages it holds the gain it
/// had rather than winding up.
///
/// Off by default (config: HotwordAutoGain).
/// </summary>
public sealed class SpeechAutoGain
{
    /// <summary>RMS level aimed for, in PCM16 units - about −18 dBFS, a normal speech level.</summary>
    public const float TargetRms = 4000f;

    /// <summary>Below this RMS a frame counts as silence: the gain is applied but not adapted.</summary>
    public const float SilenceRms = 300f;

    /// <summary>Never amplify more than this (~+16 dB), so noise can't be lifted into speech range.</summary>
    public const float MaxGain = 6.0f;

    /// <summary>Never attenuate: a loud pilot is not a problem worth solving here.</summary>
    public const float MinGain = 1.0f;

    /// <summary>How much of the newly measured gain to adopt per frame - slow, to avoid pumping.</summary>
    public const float AdaptionRate = 0.05f;

    private float _gain = 1.0f;

    public float CurrentGain => _gain;

    /// <summary>Applies the current gain to the frame in place and adapts it if the frame carries speech.</summary>
    public void Process(byte[] pcm16)
    {
        int samples = pcm16.Length / 2;
        if (samples == 0)
            return;

        double sumSquares = 0;
        for (int i = 0; i < samples; i++)
        {
            double s = BitConverter.ToInt16(pcm16, i * 2);
            sumSquares += s * s;
        }

        var rms = (float)Math.Sqrt(sumSquares / samples);

        // Apply first, adapt afterwards: the gain that applies to this frame is the one measured
        // from previous frames, so a sudden loud onset is never boosted by its own measurement.
        if (_gain != 1.0f)
        {
            for (int i = 0; i < samples; i++)
            {
                int amplified = (int)MathF.Round(BitConverter.ToInt16(pcm16, i * 2) * _gain);
                if (amplified > short.MaxValue) amplified = short.MaxValue;
                else if (amplified < short.MinValue) amplified = short.MinValue;
                BitConverter.TryWriteBytes(pcm16.AsSpan(i * 2, 2), (short)amplified);
            }
        }

        if (rms < SilenceRms)
            return; // hold the gain; don't wind up on room noise

        float wanted = Math.Clamp(TargetRms / rms, MinGain, MaxGain);
        _gain += (wanted - _gain) * AdaptionRate;
        _gain = Math.Clamp(_gain, MinGain, MaxGain);
    }

    public void Reset() => _gain = 1.0f;
}
