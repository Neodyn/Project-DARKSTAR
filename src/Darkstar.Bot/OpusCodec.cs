using Concentus;
using Concentus.Enums;

using System.Collections.Concurrent;

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

    /// <summary>
    /// One decoder per talking client, because an Opus decoder is not a stateless function.
    ///
    /// It carries the previous frame's state for prediction and for concealing lost packets. Push
    /// two pilots' packets through the same decoder and each one is decoded against the other's
    /// history - which sounds like intermittent roughness on both, exactly the kind of thing that
    /// then gets blamed on the recognizer. It is also not safe to call concurrently, which a
    /// single shared instance would eventually have to be.
    ///
    /// Keyed by the sending client's GUID, which is what SRS puts in every packet.
    /// </summary>
    private static readonly ConcurrentDictionary<string, IOpusDecoder> Decoders = new();

    /// <summary>
    /// A busy server's client list turns over; without a cap the decoders would accumulate for
    /// everyone who ever transmitted. Well above any realistic number of simultaneous talkers.
    /// </summary>
    private const int MaxDecoders = 64;

    /// <summary>
    /// Decodes a single Opus packet to PCM16 (little-endian, mono), using the decoder belonging
    /// to the client that sent it.
    /// </summary>
    /// <param name="senderClientGuid">
    /// The transmitting client's GUID from the SRS packet. An empty value shares one fallback
    /// decoder, which is no worse than the single shared decoder this replaced.
    /// </param>
    public static byte[] Decode(string? senderClientGuid, byte[] opusData)
    {
        if (opusData.Length == 0) return Array.Empty<byte>();

        var decoder = DecoderFor(senderClientGuid);

        Span<short> pcm = stackalloc short[SampleRate / 10]; // buffer large enough for a ~100ms frame

        int samplesDecoded;
        lock (decoder)
        {
            // One client's packets arrive in order on one socket, so this is almost never
            // contended - it is here so that a future caller on another thread cannot corrupt
            // the decoder's state, which would show up as a native crash rather than an exception.
            samplesDecoded = decoder.Decode(opusData, pcm, pcm.Length, false);
        }

        var bytes = new byte[samplesDecoded * 2];
        for (int i = 0; i < samplesDecoded; i++)
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 2, 2), pcm[i]);
        }
        return bytes;
    }

    private static IOpusDecoder DecoderFor(string? senderClientGuid)
    {
        var key = string.IsNullOrEmpty(senderClientGuid) ? "" : senderClientGuid;

        if (Decoders.TryGetValue(key, out var existing)) return existing;

        // Far more clients than could ever be talking at once: start over rather than grow
        // without bound. A brief moment of decoding against a fresh state is inaudible next to
        // the alternative of leaking a native object per client for the life of the process.
        if (Decoders.Count >= MaxDecoders)
        {
            Logger.Debug($"[Opus] Decoder cache full ({MaxDecoders}) - clearing it.");
            Decoders.Clear();
        }

        return Decoders.GetOrAdd(key, _ => OpusCodecFactory.CreateDecoder(SampleRate, Channels));
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
