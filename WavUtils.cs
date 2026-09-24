namespace Darkstar;

/// <summary>Wraps raw PCM16 samples into a minimal, valid WAV file (44-byte header).</summary>
public static class WavUtils
{
    public static byte[] WrapPcm16AsWav(byte[] pcm16, int sampleRate = 48000, short channels = 1)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        short bitsPerSample = 16;
        int byteRate = sampleRate * channels * bitsPerSample / 8;
        short blockAlign = (short)(channels * bitsPerSample / 8);

        // RIFF header
        w.Write("RIFF"u8.ToArray());
        w.Write(36 + pcm16.Length);
        w.Write("WAVE"u8.ToArray());

        // fmt chunk
        w.Write("fmt "u8.ToArray());
        w.Write(16); // chunk size for PCM
        w.Write((short)1); // AudioFormat = 1 (PCM)
        w.Write(channels);
        w.Write(sampleRate);
        w.Write(byteRate);
        w.Write(blockAlign);
        w.Write(bitsPerSample);

        // data chunk
        w.Write("data"u8.ToArray());
        w.Write(pcm16.Length);
        w.Write(pcm16);

        return ms.ToArray();
    }
}
