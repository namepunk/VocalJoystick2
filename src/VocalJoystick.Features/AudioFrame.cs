namespace VocalJoystick.Features;

/// <summary>Immutable framing parameters expressed in samples.</summary>
public sealed record AudioFrameSpec
{
    public int SampleRate { get; }
    public int FrameLengthSamples { get; }
    public int HopLengthSamples { get; }

    public AudioFrameSpec(int sampleRate, int frameLengthSamples, int hopLengthSamples)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (frameLengthSamples <= 0) throw new ArgumentOutOfRangeException(nameof(frameLengthSamples));
        if (hopLengthSamples <= 0 || hopLengthSamples > frameLengthSamples) throw new ArgumentOutOfRangeException(nameof(hopLengthSamples));
        SampleRate = sampleRate;
        FrameLengthSamples = frameLengthSamples;
        HopLengthSamples = hopLengthSamples;
    }
}

public readonly record struct AudioFrame(long StartSample, float[] Samples);
