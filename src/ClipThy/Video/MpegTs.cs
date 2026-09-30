namespace ClipThy.Video;

/// <summary>Minimal MPEG-TS parsing: just what VideoBuffer needs.</summary>
public static class MpegTs
{
    public const int PacketSize = 188;
    public const byte SyncByte = 0x47;
    public const int ClockRate = 90_000;
    public const long PtsWrap = 1L << 33;

    public static int Pid(ReadOnlySpan<byte> packet) => ((packet[1] & 0x1F) << 8) | packet[2];

    public static bool PayloadUnitStart(ReadOnlySpan<byte> packet) => (packet[1] & 0x40) != 0;

    /// <summary>True when the adaptation field's random_access_indicator is set (ffmpeg sets it on keyframes).</summary>
    public static bool RandomAccess(ReadOnlySpan<byte> packet) =>
        (packet[3] & 0x20) != 0 && packet[4] > 0 && (packet[5] & 0x40) != 0;

    public static ReadOnlySpan<byte> Payload(ReadOnlySpan<byte> packet)
    {
        int adaptationControl = (packet[3] >> 4) & 0x3;
        if ((adaptationControl & 0x1) == 0) return ReadOnlySpan<byte>.Empty;
        int offset = (adaptationControl & 0x2) != 0 ? 5 + packet[4] : 4;
        return offset < PacketSize ? packet[offset..PacketSize] : ReadOnlySpan<byte>.Empty;
    }

    /// <summary>Reads the PTS of a PES header that starts at the beginning of <paramref name="payload"/>.</summary>
    public static bool TryReadPts(ReadOnlySpan<byte> payload, out long pts)
    {
        pts = 0;
        if (payload.Length < 14 || payload[0] != 0 || payload[1] != 0 || payload[2] != 1) return false;
        if ((payload[7] & 0x80) == 0) return false;
        ReadOnlySpan<byte> b = payload.Slice(9, 5);
        pts = ((long)((b[0] >> 1) & 0x07) << 30) | ((long)b[1] << 22) | ((long)(b[2] >> 1) << 15)
            | ((long)b[3] << 7) | ((long)b[4] >> 1);
        return true;
    }

    /// <summary>Reads a PAT and returns the PID of the first program's PMT.</summary>
    public static bool TryReadPmtPid(ReadOnlySpan<byte> payload, out int pmtPid)
    {
        pmtPid = 0;
        if (!TryGetSection(payload, tableId: 0x00, out ReadOnlySpan<byte> section)) return false;
        for (int i = 8; i + 4 <= section.Length - 4; i += 4)
        {
            int programNumber = (section[i] << 8) | section[i + 1];
            if (programNumber == 0) continue; // network information PID
            pmtPid = ((section[i + 2] & 0x1F) << 8) | section[i + 3];
            return true;
        }
        return false;
    }

    /// <summary>Reads a PMT and returns the PID of the first H.264 stream.</summary>
    public static bool TryReadH264Pid(ReadOnlySpan<byte> payload, out int videoPid)
    {
        videoPid = 0;
        if (!TryGetSection(payload, tableId: 0x02, out ReadOnlySpan<byte> section) || section.Length < 16) return false;
        int programInfoLength = ((section[10] & 0x0F) << 8) | section[11];
        for (int i = 12 + programInfoLength; i + 5 <= section.Length - 4;)
        {
            int streamType = section[i];
            int pid = ((section[i + 1] & 0x1F) << 8) | section[i + 2];
            int esInfoLength = ((section[i + 3] & 0x0F) << 8) | section[i + 4];
            if (streamType == 0x1B)
            {
                videoPid = pid;
                return true;
            }
            i += 5 + esInfoLength;
        }
        return false;
    }

    /// <summary>UTC time in the 90 kHz, 33-bit clock that ffmpeg writes as PTS with -use_wallclock_as_timestamps.</summary>
    public static long UtcTo90k(DateTime utc) => (utc.Ticks - DateTime.UnixEpoch.Ticks) * 9 / 1000 & (PtsWrap - 1);

    private static bool TryGetSection(ReadOnlySpan<byte> payload, byte tableId, out ReadOnlySpan<byte> section)
    {
        section = default;
        if (payload.IsEmpty) return false;
        int start = 1 + payload[0]; // skip pointer_field
        if (start + 3 > payload.Length || payload[start] != tableId) return false;
        int sectionLength = ((payload[start + 1] & 0x0F) << 8) | payload[start + 2];
        int end = start + 3 + sectionLength;
        if (sectionLength < 9 || end > payload.Length) return false;
        section = payload[start..end];
        return true;
    }
}
