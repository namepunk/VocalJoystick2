namespace VocalJoystick.Features;

public static class FrameMeasurements
{
    /// <summary>Mean-square energy, kept separate from legacy relative-energy calibration.</summary>
    public static float MeanSquareEnergy(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) throw new ArgumentException("A frame must contain samples.", nameof(samples));
        double sum = 0;
        foreach (var sample in samples) sum += sample * sample;
        return (float)(sum / samples.Length);
    }

    /// <summary>Number of strict sign changes; zero samples do not introduce a crossing.</summary>
    public static int ZeroCrossingCount(ReadOnlySpan<float> samples)
    {
        if (samples.Length < 2) return 0;
        var count = 0;
        var previousSign = Math.Sign(samples[0]);
        for (var index = 1; index < samples.Length; index++)
        {
            var currentSign = Math.Sign(samples[index]);
            if (currentSign != 0 && previousSign != 0 && currentSign != previousSign) count++;
            if (currentSign != 0) previousSign = currentSign;
        }
        return count;
    }
}
