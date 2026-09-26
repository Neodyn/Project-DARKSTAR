using System.Text;

namespace Darkstar;

/// <summary>Reads and writes uncompressed PCM16 WAV files.</summary>
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

    /// <summary>What came out of a WAV file: the samples plus what they are.</summary>
    public sealed record WavAudio(byte[] Pcm16, int SampleRate, int Channels)
    {
        public double DurationSeconds => Pcm16.Length / 2.0 / Math.Max(1, Channels) / Math.Max(1, SampleRate);
    }

    /// <summary>
    /// Reads an uncompressed PCM16 WAV file. Walks the chunk list rather than assuming the 44-byte
    /// layout this class writes, because anything recorded elsewhere (Audacity, SRS's own
    /// recorder) tends to carry extra chunks such as LIST/INFO before the audio.
    /// </summary>
    /// <exception cref="InvalidDataException">Not a WAV file, or not uncompressed PCM16.</exception>
    public static WavAudio ReadPcm16(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 12)
            throw new InvalidDataException("File is too short to be a WAV file.");

        if (Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" || Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE")
            throw new InvalidDataException("Not a RIFF/WAVE file.");

        int sampleRate = 0, channels = 0, bitsPerSample = 0, audioFormat = 0;
        byte[]? data = null;

        int position = 12;
        while (position + 8 <= bytes.Length)
        {
            var chunkId = Encoding.ASCII.GetString(bytes, position, 4);
            int chunkSize = BitConverter.ToInt32(bytes, position + 4);
            int body = position + 8;

            if (chunkSize < 0 || body + chunkSize > bytes.Length)
                chunkSize = bytes.Length - body; // truncated file: take what is there

            if (chunkId == "fmt " && chunkSize >= 16)
            {
                audioFormat = BitConverter.ToInt16(bytes, body);
                channels = BitConverter.ToInt16(bytes, body + 2);
                sampleRate = BitConverter.ToInt32(bytes, body + 4);
                bitsPerSample = BitConverter.ToInt16(bytes, body + 14);
            }
            else if (chunkId == "data")
            {
                data = new byte[chunkSize];
                Array.Copy(bytes, body, data, 0, chunkSize);
            }

            position = body + chunkSize + (chunkSize % 2); // chunks are word-aligned
        }

        if (data == null)
            throw new InvalidDataException("No data chunk found.");

        // 1 = PCM, 0xFFFE = WAVE_FORMAT_EXTENSIBLE, which is still plain PCM for our purposes.
        if (audioFormat != 1 && audioFormat != 0xFFFE)
            throw new InvalidDataException($"Only uncompressed PCM is supported (this file's format tag is {audioFormat}).");

        if (bitsPerSample != 16)
            throw new InvalidDataException($"Only 16-bit samples are supported (this file has {bitsPerSample}).");

        if (channels < 1)
            throw new InvalidDataException("The file reports no channels.");

        return new WavAudio(data, sampleRate, channels);
    }

    /// <summary>Averages a multi-channel PCM16 buffer down to one channel. A mono buffer is returned as is.</summary>
    public static byte[] ToMono(byte[] pcm16, int channels)
    {
        if (channels <= 1) return pcm16;

        int frames = pcm16.Length / 2 / channels;
        var mono = new byte[frames * 2];

        for (int f = 0; f < frames; f++)
        {
            int sum = 0;
            for (int c = 0; c < channels; c++)
                sum += BitConverter.ToInt16(pcm16, (f * channels + c) * 2);

            BitConverter.TryWriteBytes(mono.AsSpan(f * 2, 2), (short)(sum / channels));
        }

        return mono;
    }
}
