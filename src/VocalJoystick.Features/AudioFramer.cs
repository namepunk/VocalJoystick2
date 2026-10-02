namespace VocalJoystick.Features;

/// <summary>Converts consecutive PCM samples to complete, fixed-length frames.</summary>
public sealed class AudioFramer
{
    private readonly AudioFrameSpec _spec;
    private readonly List<float> _buffer = [];
    private long _bufferStartSample;

    public AudioFramer(AudioFrameSpec spec) => _spec = spec;

    public IReadOnlyList<AudioFrame> Push(ReadOnlySpan<float> samples)
    {
        _buffer.AddRange(samples);
        var frames = new List<AudioFrame>();
        while (_buffer.Count >= _spec.FrameLengthSamples)
        {
            frames.Add(new AudioFrame(_bufferStartSample, _buffer.Take(_spec.FrameLengthSamples).ToArray()));
            _buffer.RemoveRange(0, _spec.HopLengthSamples);
            _bufferStartSample += _spec.HopLengthSamples;
        }
        return frames;
    }

    public void Reset()
    {
        _buffer.Clear();
        _bufferStartSample = 0;
    }
}
