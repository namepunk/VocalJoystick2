namespace VocalJoystick.Features;

/// <summary>
/// The normalized-correlation input used by the recovered legacy pitch path.
/// This is deliberately an isolated primitive: candidate scoring and dynamic
/// tracking remain separate until their constants are verified against XP.
/// </summary>
public static class LegacyPitchCorrelation
{
    public const int LegacyPitchFrameLength = LegacyFrameEnergyStream.FrameLength;
    public const int MinimumLag = 32;
    public const int MaximumLag = 319;
    public const float NearZeroEnergy = 0.000001f;

    /// <summary>
    /// Computes lags zero through <paramref name="maximumLag"/>. Legacy code
    /// forces lag zero to one and returns one if the normalization energy is
    /// too small.
    /// </summary>
    public static float[] Compute(ReadOnlySpan<float> samples, int maximumLag = MaximumLag)
    {
        if (samples.Length != LegacyPitchFrameLength)
            throw new ArgumentException($"Legacy pitch correlation requires {LegacyPitchFrameLength} samples.", nameof(samples));
        if (maximumLag < 0 || maximumLag >= samples.Length)
            throw new ArgumentOutOfRangeException(nameof(maximumLag));

        var correlation = new float[maximumLag + 1];
        correlation[0] = 1f;
        for (var lag = 1; lag <= maximumLag; lag++)
        {
            double dot = 0d;
            double leftEnergy = 0d;
            double rightEnergy = 0d;
            for (var index = 0; index < samples.Length - lag; index++)
            {
                var left = samples[index];
                var right = samples[index + lag];
                dot += left * right;
                leftEnergy += left * left;
                rightEnergy += right * right;
            }
            var denominator = Math.Sqrt(leftEnergy * rightEnergy);
            correlation[lag] = denominator <= NearZeroEnergy ? 1f : (float)(dot / denominator);
        }
        return correlation;
    }
}
