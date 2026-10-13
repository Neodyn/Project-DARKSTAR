using System.Text;

namespace Darkstar;

/// <summary>
/// UDP voice packet format - CONFIRMED using real, live-captured packets
/// (hex dump analysis of two consecutive packets, frequency and ASCII GUID locations
/// match this layout exactly):
///
///   [2 bytes] PacketLength (ushort) - total length of the packet
///   [2 bytes] AudioPart1Length (ushort)
///   [2 bytes] FrequencyPartLength (ushort) - length of the frequency block in bytes (= freqCount * 10)
///   [N bytes] AudioPart1Bytes (Opus audio data)
///   For each frequency (10 bytes each, interleaved):
///     [8 bytes] freq (double)
///     [1 byte]  modulation
///     [1 byte]  encryption
///   [4 bytes] UnitId (uint)
///   [8 bytes] PacketNumber (ulong)
///   [1 byte]  RetransmissionCount
///   [22 bytes ASCII] TransmissionGuid
///   [22 bytes ASCII] OriginalClientGuid
/// </summary>
public sealed class SrsAudioPacket
{
    private const int FrequencyEntrySize = 8 + 1 + 1; // double + modulation + encryption
    private const int GuidLength = 22;

    public byte[] OpusAudio { get; init; } = Array.Empty<byte>();
    public double[] Frequencies { get; init; } = Array.Empty<double>();
    public byte[] Modulations { get; init; } = Array.Empty<byte>();
    public byte[] Encryptions { get; init; } = Array.Empty<byte>();
    public uint UnitId { get; init; }
    public ulong PacketNumber { get; init; }
    public byte RetransmissionCount { get; init; }
    public string TransmissionGuid { get; init; } = "";
    public string OriginalClientGuid { get; init; } = "";

    public static SrsAudioPacket? TryParse(byte[] buffer)
    {
        try
        {
            int offset = 0;
            ushort packetLength = BitConverter.ToUInt16(buffer, offset); offset += 2;
            ushort audioLen = BitConverter.ToUInt16(buffer, offset); offset += 2;
            ushort freqPartLen = BitConverter.ToUInt16(buffer, offset); offset += 2;

            var audio = buffer.AsSpan(offset, audioLen).ToArray(); offset += audioLen;

            int freqCount = freqPartLen / FrequencyEntrySize;
            var freqs = new double[freqCount];
            var mods = new byte[freqCount];
            var encs = new byte[freqCount];
            for (int i = 0; i < freqCount; i++)
            {
                freqs[i] = BitConverter.ToDouble(buffer, offset); offset += 8;
                mods[i] = buffer[offset]; offset += 1;
                encs[i] = buffer[offset]; offset += 1;
            }

            uint unitId = BitConverter.ToUInt32(buffer, offset); offset += 4;
            ulong packetNumber = BitConverter.ToUInt64(buffer, offset); offset += 8;
            byte retransmit = buffer[offset]; offset += 1;

            var transmissionGuid = Encoding.ASCII.GetString(buffer, offset, GuidLength).TrimEnd('\0'); offset += GuidLength;
            var clientGuid = Encoding.ASCII.GetString(buffer, offset, GuidLength).TrimEnd('\0');

            return new SrsAudioPacket
            {
                OpusAudio = audio,
                Frequencies = freqs,
                Modulations = mods,
                Encryptions = encs,
                UnitId = unitId,
                PacketNumber = packetNumber,
                RetransmissionCount = retransmit,
                TransmissionGuid = transmissionGuid,
                OriginalClientGuid = clientGuid
            };
        }
        catch
        {
            return null;
        }
    }

    public static byte[] Build(byte[] opusAudio, double[] frequencies, string clientGuid, ulong packetId,
        byte modulation = 0, byte encryption = 0, uint unitId = 100001, byte retransmissionCount = 0)
    {
        ushort freqPartLen = (ushort)(frequencies.Length * FrequencyEntrySize);
        int totalLength = 2 + 2 + 2 + opusAudio.Length + freqPartLen + 4 + 8 + 1 + GuidLength + GuidLength;

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        w.Write((ushort)totalLength);
        w.Write((ushort)opusAudio.Length);
        w.Write(freqPartLen);
        w.Write(opusAudio);

        foreach (var f in frequencies)
        {
            w.Write(f);
            w.Write(modulation);
            w.Write(encryption);
        }

        w.Write(unitId);
        w.Write(packetId);
        w.Write(retransmissionCount);

        WriteFixedAscii(w, Guid.NewGuid().ToString("N")[..22]); // Transmission GUID (new for every sent packet)
        WriteFixedAscii(w, clientGuid); // Original client GUID (our bot)

        return ms.ToArray();
    }

    private static void WriteFixedAscii(BinaryWriter w, string s)
    {
        var bytes = Encoding.ASCII.GetBytes(s.PadRight(GuidLength, '\0')[..GuidLength]);
        w.Write(bytes);
    }
}
