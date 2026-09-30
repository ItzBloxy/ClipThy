using ClipThy.Timing;

namespace ClipThy.Video;

/// <summary>A self-contained piece of the stream: PAT + PMT + whole frames starting at a keyframe.</summary>
public sealed record VideoSnapshot(byte[] Data, long FirstFrameQpc, long LastFrameQpc, int FrameCount);

public interface IVideoSource
{
    /// <summary>Capture time of the newest frame that has fully arrived, or null if there is none.</summary>
    long? NewestCaptureQpc { get; }

    VideoSnapshot? Snapshot(long endQpc, TimeSpan length);
}

/// <summary>
/// Keeps the capture ffmpeg's recent MPEG-TS output in memory, indexed by each frame's capture time.
/// Every frame's PTS is its UTC capture time (-use_wallclock_as_timestamps), converted to QPC on arrival.
/// </summary>
public sealed class VideoBuffer : IVideoSource
{
    private const int PacketsPerBlock = 4096;
    private const int MaxSpareBlocks = 4;
    private const long MinDelay90k = -MpegTs.ClockRate; // ffmpeg's clock can run a few ms ahead of .NET's
    private const long MaxDelay90k = 5L * MpegTs.ClockRate;

    private readonly object _lock = new();
    private readonly long _frequency;
    private readonly List<byte[]> _blocks = [];
    private readonly Stack<byte[]> _spareBlocks = new();
    private readonly List<FrameEntry> _frames = [];
    private readonly byte[] _partial = new byte[MpegTs.PacketSize];
    private int _partialLength;
    private long _firstBlockNumber;
    private long _packetCount;
    private byte[]? _pat;
    private byte[]? _pmt;
    private int _pmtPid = -1;
    private int _videoPid = -1;
    private long _timestampWarnings;
    private TimeSpan _retention = TimeSpan.FromSeconds(32);

    private readonly record struct FrameEntry(long PacketIndex, long CaptureQpc, bool Keyframe);

    public VideoBuffer(long qpcFrequency) => _frequency = qpcFrequency;

    /// <summary>How much capture time to keep. Set to clip length + 2 s.</summary>
    public TimeSpan Retention
    {
        get { lock (_lock) return _retention; }
        set { lock (_lock) _retention = value; }
    }

    /// <summary>Frames whose PTS couldn't be mapped (delay outside −1…5 s); they use their arrival time instead.</summary>
    public long TimestampWarnings
    {
        get { lock (_lock) return _timestampWarnings; }
    }

    public long? NewestCaptureQpc
    {
        get { lock (_lock) return _frames.Count >= 2 ? _frames[^2].CaptureQpc : null; }
    }

    /// <summary>Adds bytes read from ffmpeg's stdout. <paramref name="data"/> doesn't need to be packet-aligned.</summary>
    public void Append(ReadOnlySpan<byte> data, long arrivalQpc, long arrivalUtc90k)
    {
        lock (_lock)
        {
            while (!data.IsEmpty)
            {
                if (_partialLength > 0 || data.Length < MpegTs.PacketSize)
                {
                    int take = Math.Min(MpegTs.PacketSize - _partialLength, data.Length);
                    data[..take].CopyTo(_partial.AsSpan(_partialLength));
                    _partialLength += take;
                    data = data[take..];
                    if (_partialLength == MpegTs.PacketSize)
                    {
                        AddPacket(_partial, arrivalQpc, arrivalUtc90k);
                        _partialLength = 0;
                    }
                }
                else
                {
                    AddPacket(data[..MpegTs.PacketSize], arrivalQpc, arrivalUtc90k);
                    data = data[MpegTs.PacketSize..];
                }
            }
        }
    }

    public VideoSnapshot? Snapshot(long endQpc, TimeSpan length)
    {
        lock (_lock)
        {
            if (_pat is null || _pmt is null) return null;

            long startTarget = endQpc - QpcTime.FromTimeSpan(length, _frequency);
            int start = -1;
            int oldestKeyframe = -1;
            for (int i = 0; i < _frames.Count; i++)
            {
                if (!_frames[i].Keyframe) continue;
                if (oldestKeyframe < 0) oldestKeyframe = i;
                if (_frames[i].CaptureQpc > startTarget) break;
                start = i;
            }
            if (start < 0) start = oldestKeyframe;
            if (start < 0) return null;

            // Stop before the first frame captured after endQpc. If there is none, leave out the newest
            // frame because its packets may still be arriving.
            int end = _frames.Count - 1;
            for (int i = start; i < _frames.Count; i++)
            {
                if (_frames[i].CaptureQpc > endQpc)
                {
                    end = i;
                    break;
                }
            }
            if (end <= start) return null;

            long fromPacket = _frames[start].PacketIndex;
            long toPacket = _frames[end].PacketIndex;
            var data = new byte[(2 + toPacket - fromPacket) * MpegTs.PacketSize];
            _pat.CopyTo(data, 0);
            _pmt.CopyTo(data, MpegTs.PacketSize);
            CopyPackets(fromPacket, toPacket, data.AsSpan(2 * MpegTs.PacketSize));
            return new VideoSnapshot(data, _frames[start].CaptureQpc, _frames[end - 1].CaptureQpc, end - start);
        }
    }

    /// <summary>Forgets everything; called whenever ffmpeg restarts (a new stream begins).</summary>
    public void Clear()
    {
        lock (_lock)
        {
            foreach (byte[] block in _blocks) ReturnBlock(block);
            _blocks.Clear();
            _frames.Clear();
            _partialLength = 0;
            _firstBlockNumber = 0;
            _packetCount = 0;
            _pat = null;
            _pmt = null;
            _pmtPid = -1;
            _videoPid = -1;
        }
    }

    private void AddPacket(ReadOnlySpan<byte> packet, long arrivalQpc, long arrivalUtc90k)
    {
        if (packet[0] != MpegTs.SyncByte) return;

        int pid = MpegTs.Pid(packet);
        if (pid == 0 && MpegTs.PayloadUnitStart(packet) && MpegTs.TryReadPmtPid(MpegTs.Payload(packet), out int pmtPid))
        {
            _pat = packet.ToArray();
            _pmtPid = pmtPid;
        }
        else if (pid == _pmtPid && MpegTs.PayloadUnitStart(packet) && MpegTs.TryReadH264Pid(MpegTs.Payload(packet), out int videoPid))
        {
            _pmt = packet.ToArray();
            _videoPid = videoPid;
        }

        StorePacket(packet);

        if (pid == _videoPid && MpegTs.PayloadUnitStart(packet) && MpegTs.TryReadPts(MpegTs.Payload(packet), out long pts))
        {
            bool keyframe = MpegTs.RandomAccess(packet);
            _frames.Add(new FrameEntry(_packetCount, ToCaptureQpc(pts, arrivalQpc, arrivalUtc90k), keyframe));
            if (keyframe) Trim();
        }
        _packetCount++;
    }

    private long ToCaptureQpc(long pts, long arrivalQpc, long arrivalUtc90k)
    {
        long delay90k = ((arrivalUtc90k - pts) % MpegTs.PtsWrap + MpegTs.PtsWrap) % MpegTs.PtsWrap;
        if (delay90k > MpegTs.PtsWrap / 2) delay90k -= MpegTs.PtsWrap; // make it signed
        if (delay90k < MinDelay90k || delay90k > MaxDelay90k)
        {
            _timestampWarnings++;
            return arrivalQpc;
        }
        return arrivalQpc - delay90k * _frequency / MpegTs.ClockRate;
    }

    private void StorePacket(ReadOnlySpan<byte> packet)
    {
        long blockNumber = _packetCount / PacketsPerBlock;
        int indexInBlock = (int)(_packetCount % PacketsPerBlock);
        if (indexInBlock == 0) _blocks.Add(RentBlock());
        packet.CopyTo(_blocks[(int)(blockNumber - _firstBlockNumber)].AsSpan(indexInBlock * MpegTs.PacketSize));
    }

    private void CopyPackets(long fromPacket, long toPacket, Span<byte> destination)
    {
        long packet = fromPacket;
        int written = 0;
        while (packet < toPacket)
        {
            long blockNumber = packet / PacketsPerBlock;
            int indexInBlock = (int)(packet % PacketsPerBlock);
            int count = (int)Math.Min(PacketsPerBlock - indexInBlock, toPacket - packet);
            _blocks[(int)(blockNumber - _firstBlockNumber)]
                .AsSpan(indexInBlock * MpegTs.PacketSize, count * MpegTs.PacketSize)
                .CopyTo(destination[(written * MpegTs.PacketSize)..]);
            written += count;
            packet += count;
        }
    }

    /// <summary>Drops frames older than the retention window, keeping the keyframe at or before its start.</summary>
    private void Trim()
    {
        long keepFrom = _frames[^1].CaptureQpc - QpcTime.FromTimeSpan(_retention, _frequency);
        int cut = -1;
        for (int i = 0; i < _frames.Count && _frames[i].CaptureQpc <= keepFrom; i++)
        {
            if (_frames[i].Keyframe) cut = i;
        }
        if (cut <= 0) return;

        _frames.RemoveRange(0, cut);
        long firstNeededBlock = _frames[0].PacketIndex / PacketsPerBlock;
        while (_firstBlockNumber < firstNeededBlock)
        {
            ReturnBlock(_blocks[0]);
            _blocks.RemoveAt(0);
            _firstBlockNumber++;
        }
    }

    private byte[] RentBlock() =>
        _spareBlocks.Count > 0 ? _spareBlocks.Pop() : new byte[PacketsPerBlock * MpegTs.PacketSize];

    private void ReturnBlock(byte[] block)
    {
        if (_spareBlocks.Count < MaxSpareBlocks) _spareBlocks.Push(block);
    }
}
