using System.Runtime.InteropServices;

namespace ClipThy.Audio;

public enum SampleFormat
{
    Float32,
    Pcm16,
}

/// <summary>A WASAPI mix format that ClipThy can convert.</summary>
public sealed record InputFormat(int SampleRate, int Channels, SampleFormat Format)
{
    public int BytesPerFrame => Channels * (Format == SampleFormat.Float32 ? 4 : 2);
}

/// <summary>
/// Converts captured audio to 48 kHz 16-bit stereo. Streaming: resampler state carries over between calls,
/// so one converter must be used per continuous capture stream.
/// </summary>
public sealed class PcmConverter
{
    private const float Minus3dB = 0.70710677f;

    private readonly InputFormat _input;
    private readonly double _step; // input frames per output frame
    private double _position;      // next output position in input frames; -1 = last frame of the previous call
    private float _previousLeft;
    private float _previousRight;
    private float[] _interleaved = new float[4096];
    private float[] _stereo = new float[4096];
    private byte[] _output = new byte[8192];

    public PcmConverter(InputFormat input)
    {
        if (input.Channels < 1 || input.SampleRate < 8000)
        {
            throw new ArgumentOutOfRangeException(nameof(input), input, "Unsupported audio format.");
        }
        _input = input;
        _step = (double)input.SampleRate / AudioBuffer.SampleRate;
    }

    /// <summary>Returns 48 kHz stereo PCM16 for <paramref name="input"/>; valid until the next call.</summary>
    public ReadOnlySpan<byte> Convert(ReadOnlySpan<byte> input)
    {
        int frames = input.Length / _input.BytesPerFrame;
        if (frames == 0) return ReadOnlySpan<byte>.Empty;

        Grow(ref _interleaved, frames * _input.Channels);
        Grow(ref _stereo, frames * 2);
        Decode(input[..(frames * _input.BytesPerFrame)]);
        Downmix(frames);
        return _input.SampleRate == AudioBuffer.SampleRate ? Quantize(frames) : Resample(frames);
    }

    private void Decode(ReadOnlySpan<byte> input)
    {
        if (_input.Format == SampleFormat.Float32)
        {
            MemoryMarshal.Cast<byte, float>(input).CopyTo(_interleaved);
            return;
        }
        ReadOnlySpan<short> samples = MemoryMarshal.Cast<byte, short>(input);
        for (int i = 0; i < samples.Length; i++) _interleaved[i] = samples[i] / 32768f;
    }

    /// <summary>Mono is duplicated; 3+ channels use the standard order FL FR FC LFE then surround pairs.</summary>
    private void Downmix(int frames)
    {
        int channels = _input.Channels;
        for (int i = 0; i < frames; i++)
        {
            int f = i * channels;
            float left, right;
            if (channels == 1)
            {
                left = right = _interleaved[f];
            }
            else
            {
                left = _interleaved[f];
                right = _interleaved[f + 1];
                if (channels >= 3)
                {
                    left += Minus3dB * _interleaved[f + 2];
                    right += Minus3dB * _interleaved[f + 2];
                }
                for (int c = 4; c < channels; c++) // channel 3 is LFE and is dropped
                {
                    if (c % 2 == 0) left += Minus3dB * _interleaved[f + c];
                    else right += Minus3dB * _interleaved[f + c];
                }
            }
            _stereo[2 * i] = left;
            _stereo[2 * i + 1] = right;
        }
    }

    private ReadOnlySpan<byte> Quantize(int frames)
    {
        Grow(ref _output, frames * AudioBuffer.BytesPerFrame);
        Span<short> output = MemoryMarshal.Cast<byte, short>(_output.AsSpan());
        for (int i = 0; i < frames * 2; i++) output[i] = ToPcm16(_stereo[i]);
        return _output.AsSpan(0, frames * AudioBuffer.BytesPerFrame);
    }

    /// <summary>Linear interpolation; frame -1 is the last frame of the previous call.</summary>
    private ReadOnlySpan<byte> Resample(int frames)
    {
        int maxOutput = (int)Math.Ceiling((frames + 1) / _step) + 2;
        Grow(ref _output, maxOutput * AudioBuffer.BytesPerFrame);
        Span<short> output = MemoryMarshal.Cast<byte, short>(_output.AsSpan());

        int produced = 0;
        while (_position < frames - 1)
        {
            int i = (int)Math.Floor(_position);
            float t = (float)(_position - i);
            float left0 = i < 0 ? _previousLeft : _stereo[2 * i];
            float right0 = i < 0 ? _previousRight : _stereo[2 * i + 1];
            float left1 = _stereo[2 * (i + 1)];
            float right1 = _stereo[2 * (i + 1) + 1];
            output[2 * produced] = ToPcm16(left0 + (left1 - left0) * t);
            output[2 * produced + 1] = ToPcm16(right0 + (right1 - right0) * t);
            produced++;
            _position += _step;
        }

        _position -= frames;
        _previousLeft = _stereo[2 * (frames - 1)];
        _previousRight = _stereo[2 * (frames - 1) + 1];
        return _output.AsSpan(0, produced * AudioBuffer.BytesPerFrame);
    }

    private static short ToPcm16(float value) => (short)MathF.Round(Math.Clamp(value, -1f, 1f) * 32767f);

    private static void Grow<T>(ref T[] buffer, int length)
    {
        if (buffer.Length < length) buffer = new T[Math.Max(length, buffer.Length * 2)];
    }
}
