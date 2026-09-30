using System.Buffers.Binary;

namespace ClipThy.Tests.TestSupport;

/// <summary>Builds small, valid MPEG-TS streams (PAT, PMT and H.264 PES packets) for tests.</summary>
public sealed class TsBuilder
{
    public const int PmtPid = 0x1000;
    public const int VideoPid = 0x100;
    public const int PacketsPerFrame = 3;

    private int _patCounter;
    private int _pmtCounter;
    private int _videoCounter;

    public byte[] Pat()
    {
        byte[] section =
        [
            0x00, 0xB0, 0x0D, 0x00, 0x01, 0xC1, 0x00, 0x00, // table header, section_length 13
            0x00, 0x01, 0xE0 | (PmtPid >> 8), PmtPid & 0xFF, // program 1 -> PMT PID
        ];
        return SectionPacket(0x0000, ref _patCounter, section);
    }

    public byte[] Pmt()
    {
        byte[] section =
        [
            0x02, 0xB0, 0x12, 0x00, 0x01, 0xC1, 0x00, 0x00, // table header, section_length 18
            0xE0 | (VideoPid >> 8), VideoPid & 0xFF,        // PCR PID
            0xF0, 0x00,                                     // program_info_length 0
            0x1B, 0xE0 | (VideoPid >> 8), VideoPid & 0xFF, 0xF0, 0x00, // H.264 stream
        ];
        return SectionPacket(PmtPid, ref _pmtCounter, section);
    }

    /// <summary>One video frame; the first packet carries the PES header (and the keyframe flag).</summary>
    public byte[] Frame(long pts, bool keyframe, int payloadPackets = PacketsPerFrame)
    {
        var packets = new byte[payloadPackets * 188];
        for (int i = 0; i < payloadPackets; i++)
        {
            Span<byte> p = packets.AsSpan(i * 188, 188);
            p[0] = 0x47;
            p[1] = (byte)((i == 0 ? 0x40 : 0x00) | (VideoPid >> 8));
            p[2] = VideoPid & 0xFF;
            int offset = 4;
            if (i == 0 && keyframe)
            {
                p[3] = (byte)(0x30 | (_videoCounter++ & 0x0F)); // adaptation field + payload
                p[4] = 1;                                       // adaptation_field_length
                p[5] = 0x40;                                    // random_access_indicator
                offset = 6;
            }
            else
            {
                p[3] = (byte)(0x10 | (_videoCounter++ & 0x0F)); // payload only
            }
            if (i == 0)
            {
                byte[] pes = PesHeader(pts);
                pes.CopyTo(p[offset..]);
                offset += pes.Length;
            }
            p[offset..].Fill(0xAB); // stand-in for H.264 data
        }
        return packets;
    }

    private static byte[] PesHeader(long pts) =>
    [
        0x00, 0x00, 0x01, 0xE0, 0x00, 0x00, // start code, stream id, unbounded length
        0x80, 0x80, 0x05,                   // flags: PTS present, header length 5
        (byte)(0x21 | ((pts >> 29) & 0x0E)),
        (byte)(pts >> 22),
        (byte)(0x01 | ((pts >> 14) & 0xFE)),
        (byte)(pts >> 7),
        (byte)(0x01 | ((pts << 1) & 0xFE)),
    ];

    private static byte[] SectionPacket(int pid, ref int counter, byte[] section)
    {
        var packet = new byte[188];
        packet.AsSpan().Fill(0xFF);
        packet[0] = 0x47;
        packet[1] = (byte)(0x40 | (pid >> 8));
        packet[2] = (byte)(pid & 0xFF);
        packet[3] = (byte)(0x10 | (counter++ & 0x0F));
        packet[4] = 0x00; // pointer_field
        section.CopyTo(packet, 5);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(5 + section.Length), Crc32Mpeg2(section));
        return packet;
    }

    private static uint Crc32Mpeg2(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= (uint)b << 24;
            for (int bit = 0; bit < 8; bit++) crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C11DB7 : crc << 1;
        }
        return crc;
    }
}
