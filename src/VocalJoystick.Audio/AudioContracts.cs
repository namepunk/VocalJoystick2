namespace VocalJoystick.Audio;

/// <summary>PCM stream format expressed independently of a platform API.</summary>
public readonly record struct AudioFormat
{
    public int SampleRate { get; }
    public int Channels { get; }

    public AudioFormat(int sampleRate, int channels)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        SampleRate = sampleRate;
        Channels = channels;
    }
}

/// <summary>A contiguous interleaved floating-point PCM chunk.</summary>
public readonly record struct AudioChunk(long StartSample, float[] Samples);

/// <summary>Asynchronous PCM source. Implementations must not block audio callbacks.</summary>
public interface IAudioSource : IAsyncDisposable
{
    AudioFormat Format { get; }
    IAsyncEnumerable<AudioChunk> ReadAllAsync(CancellationToken cancellationToken = default);
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
