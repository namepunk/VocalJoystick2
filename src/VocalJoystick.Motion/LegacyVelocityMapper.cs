namespace VocalJoystick.Motion;

/// <summary>Legacy velocity mode read from <c>--velMode</c>.</summary>
public enum LegacyVelocityMode
{
    Single = 0,
    PerAxis = 1
}

/// <summary>Optional power transformation performed before legacy power scaling.</summary>
public enum LegacyEnergyTransform
{
    Identity,
    Exponential,
    Logarithmic
}

/// <summary>
/// Immutable inputs to the recovered legacy velocity equation. The three
/// per-class arrays come from profile/adaptation data, not from a model file.
/// </summary>
public sealed class LegacyVelocityConfiguration
{
    public int ClassCount { get; }
    public float ProbabilityThreshold { get; }
    public float[] PowerReferences { get; }
    public float[] Slopes { get; }
    public float[] Intercepts { get; }
    public LegacyVelocityMode Mode { get; }
    public LegacyEnergyTransform EnergyTransform { get; }
    public float Beta { get; }
    public float Gamma { get; }
    public float Alpha { get; }
    public float Offset { get; }

    public LegacyVelocityConfiguration(
        int classCount,
        float probabilityThreshold,
        IReadOnlyList<float> powerReferences,
        IReadOnlyList<float> slopes,
        IReadOnlyList<float> intercepts,
        LegacyVelocityMode mode,
        float beta,
        float gamma,
        float alpha,
        float offset,
        LegacyEnergyTransform energyTransform = LegacyEnergyTransform.Identity)
    {
        if (classCount is not (4 or 5 or 8 or 9)) throw new ArgumentOutOfRangeException(nameof(classCount));
        if (!float.IsFinite(probabilityThreshold) || probabilityThreshold < 0f)
            throw new ArgumentOutOfRangeException(nameof(probabilityThreshold));
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (!Enum.IsDefined(energyTransform)) throw new ArgumentOutOfRangeException(nameof(energyTransform));
        if (!float.IsFinite(beta) || !float.IsFinite(gamma) || !float.IsFinite(alpha) || !float.IsFinite(offset))
            throw new ArgumentException("Velocity parameters must be finite.");

        ClassCount = classCount;
        ProbabilityThreshold = probabilityThreshold;
        PowerReferences = CopyAndValidate(powerReferences, classCount, nameof(powerReferences), requirePositive: true);
        Slopes = CopyAndValidate(slopes, classCount, nameof(slopes), requirePositive: false);
        Intercepts = CopyAndValidate(intercepts, classCount, nameof(intercepts), requirePositive: false);
        Mode = mode;
        Beta = beta;
        Gamma = gamma;
        Alpha = alpha;
        Offset = offset;
        EnergyTransform = energyTransform;
    }

    private static float[] CopyAndValidate(IReadOnlyList<float> source, int expectedCount, string name, bool requirePositive)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Count != expectedCount) throw new ArgumentException($"Expected {expectedCount} values.", name);
        var copy = source.ToArray();
        if (copy.Any(value => !float.IsFinite(value) || (requirePositive && value <= 0f)))
            throw new ArgumentException(requirePositive ? "Values must be finite and positive." : "Values must be finite.", name);
        return copy;
    }
}

/// <summary>
/// Computes the legacy per-axis and global velocity values. It never clamps or
/// emits cursor steps; the legacy caller applied its maximum-speed limit after
/// multiplying these values by the directional axes.
/// </summary>
public sealed class LegacyVelocityMapper
{
    private const float DiagonalWeight = 0.7071067811865476f;
    private readonly LegacyVelocityConfiguration _configuration;

    public LegacyVelocityMapper(LegacyVelocityConfiguration configuration) =>
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

    public LegacyVelocity Map(float energy, ReadOnlySpan<float> probabilities)
    {
        if (!float.IsFinite(energy)) throw new ArgumentOutOfRangeException(nameof(energy));
        if (probabilities.Length != _configuration.ClassCount)
            throw new ArgumentException($"Expected {_configuration.ClassCount} class probabilities.", nameof(probabilities));

        energy = _configuration.EnergyTransform switch
        {
            LegacyEnergyTransform.Identity => energy,
            LegacyEnergyTransform.Exponential => MathF.Exp(energy),
            LegacyEnergyTransform.Logarithmic when energy > 0f => MathF.Log(energy),
            LegacyEnergyTransform.Logarithmic => throw new ArgumentOutOfRangeException(nameof(energy), "Logarithmic energy must be positive."),
            _ => throw new InvalidOperationException("Unsupported energy transform.")
        };

        Span<float> scaled = stackalloc float[9];
        for (var index = 0; index < probabilities.Length; index++)
        {
            var probability = probabilities[index];
            if (!float.IsFinite(probability)) throw new ArgumentException("Probabilities must be finite.", nameof(probabilities));
            if (probability <= _configuration.ProbabilityThreshold) continue;
            scaled[index] = MathF.Max(0f,
                (energy / _configuration.PowerReferences[index] * _configuration.Slopes[index] + _configuration.Intercepts[index]) * probability);
        }

        var globalInput = 0f;
        for (var index = 0; index < _configuration.ClassCount; index++)
            globalInput += scaled[index];
        float xInput;
        float yInput;
        if (_configuration.Mode is LegacyVelocityMode.Single)
        {
            xInput = globalInput;
            yInput = globalInput;
        }
        else
        {
            var diagonalInput = (scaled[4] + scaled[5] + scaled[6] + scaled[7]) * DiagonalWeight;
            xInput = scaled[1] + scaled[3] + diagonalInput;
            yInput = scaled[0] + scaled[2] + diagonalInput;
        }

        return new LegacyVelocity(Curve(xInput), Curve(yInput), Curve(globalInput));
    }

    private float Curve(float value) => _configuration.Offset + _configuration.Beta *
        MathF.Pow(value, _configuration.Alpha) * MathF.Exp(_configuration.Gamma * value);
}

public readonly record struct LegacyVelocity(float X, float Y, float Global);
