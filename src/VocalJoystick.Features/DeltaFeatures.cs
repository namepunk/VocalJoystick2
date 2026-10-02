namespace VocalJoystick.Features;

/// <summary>
/// Computes the interior delta contract measured from the legacy frontend.
/// The legacy boundary policy is not yet known; callers must supply two prior
/// and two subsequent frames.
/// </summary>
public static class DeltaFeatures
{
    public const int RequiredContextRadius = 2;

    public static void ComputeCentered(
        IReadOnlyList<float[]> staticFrames,
        int centerFrame,
        Span<float> destination)
    {
        ArgumentNullException.ThrowIfNull(staticFrames);
        if (centerFrame < RequiredContextRadius || centerFrame + RequiredContextRadius >= staticFrames.Count)
            throw new ArgumentOutOfRangeException(nameof(centerFrame), "Two frames of context are required on each side.");

        var featureCount = staticFrames[centerFrame].Length;
        if (destination.Length != featureCount)
            throw new ArgumentException($"Expected {featureCount} destination values.", nameof(destination));

        for (var frame = centerFrame - RequiredContextRadius; frame <= centerFrame + RequiredContextRadius; frame++)
        {
            if (staticFrames[frame].Length != featureCount)
                throw new ArgumentException("All static frames must have the same feature count.", nameof(staticFrames));
        }

        for (var feature = 0; feature < featureCount; feature++)
        {
            destination[feature] = (
                staticFrames[centerFrame + 1][feature] - staticFrames[centerFrame - 1][feature]
                + 2f * (staticFrames[centerFrame + 2][feature] - staticFrames[centerFrame - 2][feature])) / 10f;
        }
    }
}
