using ClipThy.Timing;

namespace ClipThy.Audio;

public interface IAudioSource
{
    /// <summary>Exactly [fromQpc, toQpc) as 48 kHz 16-bit stereo PCM, with silence wherever nothing was captured.</summary>
    byte[] Extract(long fromQpc, long toQpc);
}

/// <summary>
/// Recent audio in memory, as "runs" of back-to-back packets. Each run knows the capture time of its
/// first frame; a packet that doesn't line up with the end of the current run (±15 ms) starts a new run.
/// </summary>
public sealed class AudioBuffer : IAudioSource
{
    public const int SampleRate = 48_000;
    public const int BytesPerFrame = 4; // 16-bit stereo
    private const int ChunkFrames = SampleRate; // 1 s per pooled chunk
    private const int ChunkBytes = ChunkFrames * BytesPerFrame;
    private const int MaxSpareChunks = 4;

    private readonly object _lock = new();
    private readonly long _frequency;
    private readonly long _toleranceTicks;
    private readonly List<Run> _runs = [];
    private readonly Stack<byte[]> _spareChunks = new();
    private TimeSpan _retention = TimeSpan.FromSeconds(33);

    public AudioBuffer(long qpcFrequency)
    {
        _frequency = qpcFrequency;
        _toleranceTicks = QpcTime.FromSeconds(0.015, qpcFrequency);
    }

    /// <summary>How much audio to keep. Set to clip length + 3 s.</summary>
    public TimeSpan Retention
    {
        get { lock (_lock) return _retention; }
        set { lock (_lock) _retention = value; }
    }

    public long? NewestEndQpc
    {
        get { lock (_lock) return _runs.Count == 0 ? null : _runs[^1].EndQpc(_frequency); }
    }

    public void Append(ReadOnlySpan<byte> pcm, long startQpc)
    {
        if (pcm.Length % BytesPerFrame != 0)
        {
            throw new ArgumentException("PCM must be whole 16-bit stereo frames.", nameof(pcm));
        }
        if (pcm.IsEmpty) return;

        lock (_lock)
        {
            Run? run = _runs.Count > 0 ? _runs[^1] : null;
            if (run is null || Math.Abs(startQpc - run.EndQpc(_frequency)) > _toleranceTicks)
            {
                run = new Run(startQpc);
                _runs.Add(run);
            }
            run.Write(pcm, RentChunk);
            Trim();
        }
    }

    public byte[] Extract(long fromQpc, long toQpc)
    {
        long frames = QpcTime.ToFrames(toQpc - fromQpc, SampleRate, _frequency);
        if (frames <= 0) return [];
        var result = new byte[frames * BytesPerFrame]; // zeros = silence

        lock (_lock)
        {
            // Runs are in capture order, so where two runs overlap the later one wins.
            foreach (Run run in _runs)
            {
                long from = Math.Max(fromQpc, run.TimeOf(run.FirstRetainedFrame, _frequency));
                long to = Math.Min(toQpc, run.EndQpc(_frequency));
                if (from >= to) continue;

                long sourceFrame = Math.Max(run.FirstRetainedFrame, QpcTime.ToFrames(from - run.StartQpc, SampleRate, _frequency));
                long targetFrame = QpcTime.ToFrames(from - fromQpc, SampleRate, _frequency);
                long count = Math.Min(run.FrameCount - sourceFrame, frames - targetFrame);
                if (count <= 0) continue;

                run.CopyFrames(sourceFrame, count, result.AsSpan((int)(targetFrame * BytesPerFrame), (int)(count * BytesPerFrame)));
            }
        }
        return result;
    }

    private void Trim()
    {
        long keepFrom = _runs[^1].EndQpc(_frequency) - QpcTime.FromTimeSpan(_retention, _frequency);
        while (_runs.Count > 0)
        {
            Run oldest = _runs[0];
            if (oldest.EndQpc(_frequency) <= keepFrom)
            {
                foreach (byte[] chunk in oldest.Chunks) ReturnChunk(chunk);
                _runs.RemoveAt(0);
                continue;
            }
            while (oldest.Chunks.Count > 1 && oldest.TimeOf((oldest.FirstChunk + 1) * ChunkFrames, _frequency) <= keepFrom)
            {
                ReturnChunk(oldest.DropFirstChunk());
            }
            break;
        }
    }

    private byte[] RentChunk() => _spareChunks.Count > 0 ? _spareChunks.Pop() : new byte[ChunkBytes];

    private void ReturnChunk(byte[] chunk)
    {
        if (_spareChunks.Count < MaxSpareChunks) _spareChunks.Push(chunk);
    }

    private sealed class Run(long startQpc)
    {
        public long StartQpc { get; } = startQpc;

        /// <summary>Frames written since the run started (frame indices 0..FrameCount-1).</summary>
        public long FrameCount { get; private set; }

        /// <summary>Chunk index of Chunks[0]; earlier chunks have been trimmed.</summary>
        public long FirstChunk { get; private set; }

        public List<byte[]> Chunks { get; } = [];

        public long FirstRetainedFrame => FirstChunk * ChunkFrames;

        public long TimeOf(long frame, long frequency) => StartQpc + QpcTime.FromFrames(frame, SampleRate, frequency);

        public long EndQpc(long frequency) => TimeOf(FrameCount, frequency);

        public void Write(ReadOnlySpan<byte> pcm, Func<byte[]> rentChunk)
        {
            while (!pcm.IsEmpty)
            {
                long chunk = FrameCount / ChunkFrames;
                int frameInChunk = (int)(FrameCount % ChunkFrames);
                if (chunk - FirstChunk == Chunks.Count) Chunks.Add(rentChunk());
                int frames = Math.Min(ChunkFrames - frameInChunk, pcm.Length / BytesPerFrame);
                pcm[..(frames * BytesPerFrame)].CopyTo(Chunks[(int)(chunk - FirstChunk)].AsSpan(frameInChunk * BytesPerFrame));
                pcm = pcm[(frames * BytesPerFrame)..];
                FrameCount += frames;
            }
        }

        public void CopyFrames(long fromFrame, long count, Span<byte> destination)
        {
            int written = 0;
            while (count > 0)
            {
                long chunk = fromFrame / ChunkFrames;
                int frameInChunk = (int)(fromFrame % ChunkFrames);
                int frames = (int)Math.Min(ChunkFrames - frameInChunk, count);
                Chunks[(int)(chunk - FirstChunk)]
                    .AsSpan(frameInChunk * BytesPerFrame, frames * BytesPerFrame)
                    .CopyTo(destination[(written * BytesPerFrame)..]);
                written += frames;
                fromFrame += frames;
                count -= frames;
            }
        }

        public byte[] DropFirstChunk()
        {
            byte[] chunk = Chunks[0];
            Chunks.RemoveAt(0);
            FirstChunk++;
            return chunk;
        }
    }
}
