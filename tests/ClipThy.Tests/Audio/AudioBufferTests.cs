using System.Buffers.Binary;
using System.Runtime.InteropServices;
using ClipThy.Audio;

namespace ClipThy.Tests.Audio;

public class AudioBufferTests
{
    private const long Frequency = 48_000_000; // 1 audio frame = 1000 ticks, so expectations are exact
    private const long TicksPerFrame = 1000;

    private static byte[] Packet(int frames, short value)
    {
        var pcm = new byte[frames * AudioBuffer.BytesPerFrame];
        for (int i = 0; i < frames * 2; i++) BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), value);
        return pcm;
    }

    private static void AssertFrames(byte[] pcm, int fromFrame, int count, short expected)
    {
        short[] samples = MemoryMarshal.Cast<byte, short>(pcm).ToArray();
        for (int frame = fromFrame; frame < fromFrame + count; frame++)
        {
            Assert.True(samples[2 * frame] == expected && samples[2 * frame + 1] == expected,
                $"frame {frame}: expected {expected}, got {samples[2 * frame]}/{samples[2 * frame + 1]}");
        }
    }

    [Fact]
    public void Back_to_back_packets_come_out_in_order()
    {
        var buffer = new AudioBuffer(Frequency);
        buffer.Append(Packet(480, 1), 0);
        buffer.Append(Packet(480, 2), 480 * TicksPerFrame);
        buffer.Append(Packet(480, 3), 960 * TicksPerFrame);

        byte[] pcm = buffer.Extract(0, 1440 * TicksPerFrame);

        Assert.Equal(1440 * AudioBuffer.BytesPerFrame, pcm.Length);
        AssertFrames(pcm, 0, 480, 1);
        AssertFrames(pcm, 480, 480, 2);
        AssertFrames(pcm, 960, 480, 3);
    }

    [Fact]
    public void Timestamp_jitter_under_15_ms_is_ignored()
    {
        var buffer = new AudioBuffer(Frequency);
        buffer.Append(Packet(480, 1), 0);
        buffer.Append(Packet(480, 2), (480 + 96) * TicksPerFrame); // 2 ms late

        byte[] pcm = buffer.Extract(0, 960 * TicksPerFrame);

        AssertFrames(pcm, 0, 480, 1);
        AssertFrames(pcm, 480, 480, 2);
    }

    [Fact]
    public void A_gap_becomes_silence()
    {
        var buffer = new AudioBuffer(Frequency);
        buffer.Append(Packet(480, 1), 0);
        buffer.Append(Packet(480, 2), 2400 * TicksPerFrame); // 50 ms later: nothing was playing in between

        byte[] pcm = buffer.Extract(0, 2880 * TicksPerFrame);

        AssertFrames(pcm, 0, 480, 1);
        AssertFrames(pcm, 480, 1920, 0);
        AssertFrames(pcm, 2400, 480, 2);
    }

    [Fact]
    public void Where_packets_overlap_the_later_one_wins()
    {
        var buffer = new AudioBuffer(Frequency);
        buffer.Append(Packet(4800, 1), 0);
        buffer.Append(Packet(4800, 2), 2400 * TicksPerFrame); // starts 50 ms before the first one ends

        byte[] pcm = buffer.Extract(0, 7200 * TicksPerFrame);

        AssertFrames(pcm, 0, 2400, 1);
        AssertFrames(pcm, 2400, 4800, 2);
    }

    [Fact]
    public void Time_before_and_after_the_data_is_silence()
    {
        var buffer = new AudioBuffer(Frequency);
        buffer.Append(Packet(480, 7), 1000 * TicksPerFrame);

        byte[] pcm = buffer.Extract(0, 2000 * TicksPerFrame);

        AssertFrames(pcm, 0, 1000, 0);
        AssertFrames(pcm, 1000, 480, 7);
        AssertFrames(pcm, 1480, 520, 0);
    }

    [Fact]
    public void Audio_older_than_the_retention_is_dropped()
    {
        var buffer = new AudioBuffer(Frequency) { Retention = TimeSpan.FromSeconds(1) };
        for (int i = 0; i < 300; i++) buffer.Append(Packet(480, (short)(i / 100 + 1)), i * 480 * TicksPerFrame); // 3 s

        AssertFrames(buffer.Extract(0, 48_000 * TicksPerFrame), 0, 48_000, 0);
        AssertFrames(buffer.Extract(120_000 * TicksPerFrame, 144_000 * TicksPerFrame), 0, 24_000, 3);
    }

    [Fact]
    public void Newest_end_tracks_the_last_packet()
    {
        var buffer = new AudioBuffer(Frequency);
        Assert.Null(buffer.NewestEndQpc);

        buffer.Append(Packet(480, 1), 5000);

        Assert.Equal(5000 + 480 * TicksPerFrame, buffer.NewestEndQpc);
    }

    [Fact]
    public void Partial_frames_are_rejected()
    {
        var buffer = new AudioBuffer(Frequency);
        Assert.Throws<ArgumentException>(() => buffer.Append(new byte[6], 0));
    }
}
