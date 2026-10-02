using System.Threading.Channels;
using System.Runtime.Versioning;
using NAudio.Wave;
using VocalJoystick.Features;

namespace VocalJoystick.Audio;

/// <summary>
/// Windows WaveIn microphone source. It explicitly requests the recovered
/// legacy processing format: mono 16-bit PCM at 16 kHz.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsMicrophoneSource : IAudioSource
{
    private readonly int _deviceNumber;
    private readonly Channel<AudioChunk> _chunks = Channel.CreateBounded<AudioChunk>(
        new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private WaveInEvent? _capture;
    private long _nextSample;
    private bool _started;

    public WindowsMicrophoneSource(int deviceNumber)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows microphone capture is only available on Windows.");
        if (deviceNumber < 0 || deviceNumber >= WaveInEvent.DeviceCount)
            throw new ArgumentOutOfRangeException(nameof(deviceNumber));
        _deviceNumber = deviceNumber;
    }

    public AudioFormat Format { get; } = new(LegacyFeatureStream.SampleRate, 1);

    public static IReadOnlyList<string> GetDeviceNames()
    {
        if (!OperatingSystem.IsWindows()) return [];
        return Enumerable.Range(0, WaveInEvent.DeviceCount)
            .Select(index => WaveInEvent.GetCapabilities(index).ProductName)
            .ToArray();
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_started) return Task.CompletedTask;
        _capture = new WaveInEvent
        {
            DeviceNumber = _deviceNumber,
            BufferMilliseconds = 20,
            NumberOfBuffers = 3,
            WaveFormat = new WaveFormat(Format.SampleRate, 16, Format.Channels)
        };
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
        _capture.StartRecording();
        _started = true;
        return Task.CompletedTask;
    }

    public IAsyncEnumerable<AudioChunk> ReadAllAsync(CancellationToken cancellationToken = default) =>
        _chunks.Reader.ReadAllAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_started) return Task.CompletedTask;
        _capture?.StopRecording();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _capture?.Dispose();
        _chunks.Writer.TryComplete();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs eventArgs)
    {
        var sampleCount = eventArgs.BytesRecorded / sizeof(short);
        var samples = new float[sampleCount];
        for (var index = 0; index < sampleCount; index++)
            samples[index] = BitConverter.ToInt16(eventArgs.Buffer, index * sizeof(short));
        var startSample = Interlocked.Add(ref _nextSample, sampleCount) - sampleCount;
        _chunks.Writer.TryWrite(new AudioChunk(startSample, samples));
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs eventArgs)
    {
        _started = false;
        _chunks.Writer.TryComplete(eventArgs.Exception);
    }
}
