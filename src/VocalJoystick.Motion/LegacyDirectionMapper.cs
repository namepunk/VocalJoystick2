namespace VocalJoystick.Motion;

/// <summary>
/// Maps the recognised vowel probability vector to the unit direction emitted
/// by the recovered legacy motion path. This type deliberately does not
/// calculate speed or issue any operating-system input.
/// </summary>
public sealed class LegacyDirectionMapper
{
    private const float DiagonalWeight = 0.7071067811865476f;
    private readonly int _classCount;
    private readonly float _probabilityThreshold;

    /// <param name="classCount">One of the cardinalities used by the legacy models: 4, 5, 8, or 9.</param>
    /// <param name="probabilityThreshold">Legacy vowel-probability cutoff, applied before direction mixing.</param>
    public LegacyDirectionMapper(int classCount, float probabilityThreshold)
    {
        if (classCount is not (4 or 5 or 8 or 9))
            throw new ArgumentOutOfRangeException(nameof(classCount), "Legacy motion supports 4, 5, 8, or 9 classes.");
        if (!float.IsFinite(probabilityThreshold) || probabilityThreshold < 0f)
            throw new ArgumentOutOfRangeException(nameof(probabilityThreshold));
        _classCount = classCount;
        _probabilityThreshold = probabilityThreshold;
    }

    /// <summary>
    /// Applies the legacy class layout. Classes 0..3 are up, right, down, and
    /// left. Four-class models synthesize the four diagonal values from their
    /// adjacent cardinal probabilities; 5/9-class layouts reserve the final
    /// class as neutral, while 8-class models provide diagonals directly.
    /// </summary>
    public LegacyDirection Map(ReadOnlySpan<float> probabilities)
    {
        if (probabilities.Length != _classCount)
            throw new ArgumentException($"Expected {_classCount} class probabilities.", nameof(probabilities));

        Span<float> directions = stackalloc float[8];
        for (var index = 0; index < _classCount; index++)
        {
            var value = probabilities[index];
            if (!float.IsFinite(value)) throw new ArgumentException("Probabilities must be finite.", nameof(probabilities));
            if (index < 8) directions[index] = value > _probabilityThreshold ? value : 0f;
        }

        if (_classCount is 4 or 5)
        {
            var up = directions[0];
            var right = directions[1];
            var down = directions[2];
            var left = directions[3];
            directions[4] = (up + right) * DiagonalWeight;
            directions[5] = (right + down) * DiagonalWeight;
            directions[6] = (down + left) * DiagonalWeight;
            directions[7] = (left + up) * DiagonalWeight;
        }

        var x = (directions[1] - directions[3]) +
                (directions[4] + directions[5] - directions[6] - directions[7]) * DiagonalWeight;
        var y = (directions[2] - directions[0]) +
                (directions[6] + directions[5] - directions[4] - directions[7]) * DiagonalWeight;
        var magnitude = MathF.Sqrt(x * x + y * y);
        return magnitude > 0f
            ? new LegacyDirection(x / magnitude, y / magnitude, magnitude)
            : LegacyDirection.None;
    }
}

/// <summary>Unit motion direction and its pre-normalisation confidence magnitude.</summary>
public readonly record struct LegacyDirection(float X, float Y, float Magnitude)
{
    public static LegacyDirection None { get; } = new(0f, 0f, 0f);
}
