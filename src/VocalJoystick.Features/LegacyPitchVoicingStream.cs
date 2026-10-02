namespace VocalJoystick.Features;

/// <summary>
/// Streaming periodicity-based voicing signal for discrete-command segmentation.
/// It reuses the recovered 960-sample normalized-correlation primitive but is
/// intentionally not a port of the legacy LPC/dynamic-programming pitch path.
/// </summary>
public sealed class LegacyPitchVoicingStream
{
    public const float MinimumFrameRms = 20f;
    public const float MinimumCorrelation = .70f;

    private readonly AudioFramer _framer = new(new AudioFrameSpec(
        LegacyFrameEnergyStream.SampleRate,
        LegacyPitchCorrelation.LegacyPitchFrameLength,
        LegacyFrameEnergyStream.HopLength));

    public IReadOnlyList<LegacyPitchVoicingFrame> Push(ReadOnlySpan<float> samples)
    {
        var frames = new List<LegacyPitchVoicingFrame>();
        foreach (var frame in _framer.Push(samples))
        {
            double energy = 0d;
            foreach (var sample in frame.Samples) energy += sample * sample;
            var rms = (float)Math.Sqrt(energy / frame.Samples.Length);
            var correlations = LegacyPitchCorrelation.Compute(frame.Samples);
            var bestLag = -1;
            var bestCorrelation = float.NegativeInfinity;
            for (var lag = LegacyPitchCorrelation.MinimumLag;
                 lag <= LegacyPitchCorrelation.MaximumLag; lag++)
            {
                if (correlations[lag] <= bestCorrelation) continue;
                bestCorrelation = correlations[lag];
                bestLag = lag;
            }

            var voiced = rms > MinimumFrameRms && bestCorrelation >= MinimumCorrelation;
            frames.Add(new LegacyPitchVoicingFrame(frame.StartSample, rms, bestLag,
                bestCorrelation, voiced));
        }
        return frames;
    }

    public void Reset() => _framer.Reset();
}

public readonly record struct LegacyPitchVoicingFrame(
    long StartSample,
    float RootMeanSquare,
    int BestLag,
    float BestCorrelation,
    bool IsVoiced);
