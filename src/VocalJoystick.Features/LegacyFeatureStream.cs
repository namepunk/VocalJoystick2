namespace VocalJoystick.Features;

/// <summary>One completed legacy static-plus-delta feature frame.</summary>
public readonly record struct LegacyFeatureFrame(long StartSample, float[] Features);

/// <summary>
/// Streaming implementation of the recovered 16 kHz MFCC path. It emits a
/// 26-value feature for the centre of each five-frame delta window, so output
/// is delayed by two 10 ms hops rather than using an invented boundary rule.
/// </summary>
public sealed class LegacyFeatureStream
{
    public const int SampleRate = 16_000;
    public const int HopLengthSamples = 160;

    private readonly AudioFramer _framer = new(new AudioFrameSpec(
        SampleRate, LegacyMfccExtractor.FrameLength, HopLengthSamples));
    private readonly LegacyMfccExtractor _mfcc = new();
    private readonly Queue<(long StartSample, float[] Static)> _pending = [];

    /// <summary>
    /// Supplies already-resampled mono PCM samples. The returned frames have
    /// 13 static coefficients followed by their centred 13-value deltas.
    /// </summary>
    public IReadOnlyList<LegacyFeatureFrame> Push(ReadOnlySpan<float> samples)
    {
        var result = new List<LegacyFeatureFrame>();
        foreach (var audioFrame in _framer.Push(samples))
        {
            _pending.Enqueue((audioFrame.StartSample, _mfcc.Extract(audioFrame.Samples)));
            if (_pending.Count < DeltaFeatures.RequiredContextRadius * 2 + 1) continue;

            var frames = _pending.ToArray();
            var center = DeltaFeatures.RequiredContextRadius;
            var features = new float[LegacyMfccExtractor.StaticFeatureCount * 2];
            frames[center].Static.CopyTo(features, 0);
            DeltaFeatures.ComputeCentered(frames.Select(frame => frame.Static).ToArray(), center,
                features.AsSpan(LegacyMfccExtractor.StaticFeatureCount));
            result.Add(new LegacyFeatureFrame(frames[center].StartSample, features));
            _pending.Dequeue();
        }
        return result;
    }

    public void Reset()
    {
        _framer.Reset();
        _pending.Clear();
    }
}
