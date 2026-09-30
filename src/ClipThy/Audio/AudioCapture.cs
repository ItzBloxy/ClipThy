using System.Diagnostics;
using System.Runtime.InteropServices;
using ClipThy.Timing;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace ClipThy.Audio;

public enum AudioSourceKind
{
    SystemLoopback,
    Microphone,
}

/// <summary>
/// Captures the default render device (loopback) or default microphone with WASAPI in shared mode, stamps
/// every packet with WASAPI's own capture time, and appends it to an <see cref="AudioBuffer"/>.
/// Follows default-device changes and survives unplugging.
/// </summary>
public sealed class AudioCapture : IDisposable
{
    private static readonly Guid SubtypeIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00aa00389b71");
    private const int ENotFound = unchecked((int)0x80070490);
    private const int EAccessDenied = unchecked((int)0x80070005);
    private const long BufferDuration100ns = 1_000_000; // 100 ms

    private readonly AudioSourceKind _kind;
    private readonly AudioBuffer _buffer;
    private readonly Action<string> _log;
    private readonly AutoResetEvent _deviceChanged = new(false);
    private readonly ManualResetEvent _stop = new(false);
    private Thread? _thread;
    private string? _lastProblem;

    public AudioCapture(AudioSourceKind kind, AudioBuffer buffer, Action<string> log)
    {
        _kind = kind;
        _buffer = buffer;
        _log = log;
    }

    /// <summary>Raised once per distinct problem (no device, access blocked), on the capture thread.</summary>
    public event Action<string>? Problem;

    private DataFlow Flow => _kind == AudioSourceKind.SystemLoopback ? DataFlow.Render : DataFlow.Capture;

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = $"ClipThy {_kind} capture" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    /// <summary>Reopens the default device (for example after resume from sleep).</summary>
    public void Restart() => _deviceChanged.Set();

    public void Dispose()
    {
        _stop.Set();
        _thread?.Join(TimeSpan.FromSeconds(3));
    }

    private void Run()
    {
        using var enumerator = new MMDeviceEnumerator();
        var notifications = new DefaultDeviceNotifications(this);
        enumerator.RegisterEndpointNotificationCallback(notifications);
        try
        {
            while (!_stop.WaitOne(0))
            {
                try
                {
                    CaptureUntilDeviceChanges(enumerator);
                }
                catch (COMException ex) when (ex.HResult == ENotFound)
                {
                    ReportProblem(_kind == AudioSourceKind.Microphone
                        ? "No microphone found; the mic track will be silent."
                        : "No audio output device found; the system audio track will be silent.");
                    WaitHandle.WaitAny([_deviceChanged, _stop], TimeSpan.FromSeconds(5));
                }
                catch (Exception ex) when (ex.HResult == EAccessDenied || ex is UnauthorizedAccessException)
                {
                    ReportProblem("Windows is blocking microphone access (Settings > Privacy > Microphone); the mic track will be silent.");
                    WaitHandle.WaitAny([_deviceChanged, _stop], TimeSpan.FromSeconds(5));
                }
                catch (Exception ex)
                {
                    // Device unplugged (AUDCLNT_E_DEVICE_INVALIDATED) or anything unexpected: log and reopen.
                    _log($"{_kind} capture error: {ex.Message}. Reopening.");
                    _stop.WaitOne(TimeSpan.FromSeconds(1));
                }
            }
        }
        finally
        {
            enumerator.UnregisterEndpointNotificationCallback(notifications);
        }
    }

    private void CaptureUntilDeviceChanges(MMDeviceEnumerator enumerator)
    {
        using MMDevice device = enumerator.GetDefaultAudioEndpoint(Flow, Role.Console);
        AudioClient client = device.AudioClient;
        try
        {
            WaveFormat mixFormat = client.MixFormat;
            InputFormat format = ToInputFormat(mixFormat);
            var converter = new PcmConverter(format);
            using var packetReady = new AutoResetEvent(false);

            AudioClientStreamFlags flags = AudioClientStreamFlags.EventCallback;
            if (_kind == AudioSourceKind.SystemLoopback) flags |= AudioClientStreamFlags.Loopback;
            client.Initialize(AudioClientShareMode.Shared, flags, BufferDuration100ns, 0, mixFormat, Guid.Empty);
            client.SetEventHandle(packetReady.SafeWaitHandle.DangerousGetHandle());
            AudioCaptureClient capture = client.AudioCaptureClient;

            byte[] raw = new byte[format.BytesPerFrame * 4800];
            client.Start();
            _log($"{_kind} capture started on '{device.FriendlyName}' ({mixFormat}).");
            _lastProblem = null;

            WaitHandle[] handles = [packetReady, _deviceChanged, _stop];
            while (true)
            {
                // The 10 ms timeout also covers systems where loopback never signals the event.
                int signaled = WaitHandle.WaitAny(handles, 10);
                if (signaled is 1 or 2)
                {
                    _log($"{_kind} capture: default device changed or stopping.");
                    return;
                }

                while (capture.GetNextPacketSize() > 0)
                {
                    IntPtr data = capture.GetBuffer(out int frames, out AudioClientBufferFlags bufferFlags, out _, out long qpcPosition);
                    int bytes = frames * format.BytesPerFrame;
                    if (raw.Length < bytes) raw = new byte[bytes];
                    if ((bufferFlags & AudioClientBufferFlags.Silent) != 0) Array.Clear(raw, 0, bytes);
                    else Marshal.Copy(data, raw, 0, bytes);
                    capture.ReleaseBuffer(frames);

                    long startQpc = (bufferFlags & AudioClientBufferFlags.TimestampError) != 0
                        ? Stopwatch.GetTimestamp() - QpcTime.FromFrames(frames, format.SampleRate, Stopwatch.Frequency)
                        : QpcTime.From100ns(qpcPosition, Stopwatch.Frequency);
                    _buffer.Append(converter.Convert(raw.AsSpan(0, bytes)), startQpc);
                }
            }
        }
        finally
        {
            try { client.Stop(); } catch (COMException) { }
            client.Dispose();
        }
    }

    private static InputFormat ToInputFormat(WaveFormat format)
    {
        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
            || (format is WaveFormatExtensible ext && ext.SubFormat == SubtypeIeeeFloat);
        bool isPcm = format.Encoding == WaveFormatEncoding.Pcm
            || (format is WaveFormatExtensible pcm && pcm.SubFormat == SubtypePcm);

        if (isFloat && format.BitsPerSample == 32) return new InputFormat(format.SampleRate, format.Channels, SampleFormat.Float32);
        if (isPcm && format.BitsPerSample == 16) return new InputFormat(format.SampleRate, format.Channels, SampleFormat.Pcm16);
        throw new NotSupportedException($"Unsupported mix format: {format}");
    }

    private void ReportProblem(string message)
    {
        if (message == _lastProblem) return;
        _lastProblem = message;
        _log(message);
        Problem?.Invoke(message);
    }

    private sealed class DefaultDeviceNotifications(AudioCapture owner) : IMMNotificationClient
    {
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow == owner.Flow && role == Role.Console) owner._deviceChanged.Set();
        }

        public void OnDeviceAdded(string pwstrDeviceId) { }

        public void OnDeviceRemoved(string deviceId) { }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }

        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}
