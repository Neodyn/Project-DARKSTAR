using Concentus;
using Concentus.Enums;

namespace Darkstar;

/// <summary>
/// SRS uses Opus at 48kHz mono. Concentus is a pure .NET port of Opus, so you don't need a
/// native opus.dll like the official SRS client does.
///
/// NOTE: The exact method signatures of OpusCodecFactory/IOpusEncoder/IOpusDecoder may vary
/// slightly depending on the Concentus package version. After the NuGet restore, please
/// double-check via IntelliSense/compiler errors against the version actually installed - this
/// is plain .NET code with no network protocol uncertainty, so it's easy to fix yourself if the
/// signature differs.
/// </summary>
public static class OpusCodec
{
    private const int SampleRate = 48000;
    private const int Channels = 1;

    private static readonly IOpusEncoder Encoder = OpusCodecFactory.CreateEncoder(SampleRate, Channels, OpusApplication.OPUS_APPLICATION_VOIP);
    private static readonly IOpusDecoder Decoder = OpusCodecFactory.CreateDecoder(SampleRate, Channels);

    /// <summary>Decodes a single Opus packet to PCM16 (little-endian, mono).</summary>
    public static byte[] Decode(byte[] opusData)
    {
        if (opusData.Length == 0) return Array.Empty<byte>();

        Span<short> pcm = stackalloc short[SampleRate / 10]; // buffer large enough for a ~100ms frame
        int samplesDecoded = Decoder.Decode(opusData, pcm, pcm.Length, false);

        var bytes = new byte[samplesDecoded * 2];
        for (int i = 0; i < samplesDecoded; i++)
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 2, 2), pcm[i]);
        }
        return bytes;
    }

    /// <summary>Encodes PCM16 audio data into 20ms Opus frames (SRS's usual frame cadence).</summary>
    public static IEnumerable<byte[]> EncodeInFrames(byte[] pcm16, int frameSamples)
    {
        int bytesPerFrame = frameSamples * 2;
        int frameCount = (int)Math.Ceiling(pcm16.Length / (double)bytesPerFrame);

        for (int f = 0; f < frameCount; f++)
        {
            int offset = f * bytesPerFrame;
            int available = Math.Min(bytesPerFrame, pcm16.Length - offset);

            var samples = new short[frameSamples];
            for (int i = 0; i < available / 2; i++)
                samples[i] = BitConverter.ToInt16(pcm16, offset + i * 2);
            // Remaining samples stay 0 (silence) if the last frame isn't full

            var outBuf = new byte[4000];
            int len = Encoder.Encode(samples, frameSamples, outBuf, outBuf.Length);
            yield return outBuf[..len];
        }
    }
}
