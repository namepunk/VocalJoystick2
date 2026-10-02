using System.Globalization;

namespace VocalJoystick.Motion;

/// <summary>
/// Motion-related values read from a legacy <c>prm.cfg</c>.  The old file
/// contains settings for other engine subsystems too; those are deliberately
/// left outside this focused compatibility boundary.
/// </summary>
public sealed record LegacyMotionParameters(
    LegacyVelocityMode VelocityMode,
    float ProbabilityThreshold,
    float Beta,
    float Gamma,
    float Alpha,
    float Offset,
    float MaximumVelocity,
    float AxisDeadZone)
{
    // Recovered from ParametersClass::setDefaults in libvj.dll. prm.cfg does
    // not override alpha, so this is material to reproducing bundled profiles.
    public const float LegacyDefaultAlpha = 1f;
    // _DAT_1004c288, assigned to ParametersClass+0x2c4 by setDefaults.
    // MotionData construction compares each absolute axis to this value.
    public const float LegacyDefaultAxisDeadZone = 0.000001f;

    public static LegacyMotionParameters Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Split('#', 2)[0].Trim();
            if (!line.StartsWith("--", StringComparison.Ordinal)) continue;

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2) values[parts[0][2..]] = parts[1];
        }

        var velocityMode = ReadInt(values, "velMode") switch
        {
            0 => LegacyVelocityMode.Single,
            1 => LegacyVelocityMode.PerAxis,
            var value => throw new InvalidDataException($"Unsupported --velMode {value} in {path}.")
        };
        return new LegacyMotionParameters(
            velocityMode,
            ReadFinite(values, "pthresh"),
            ReadFinite(values, "beta"),
            ReadFinite(values, "gamma"),
            ReadOptionalFinite(values, "alpha", LegacyDefaultAlpha),
            ReadFinite(values, "offset"),
            ReadFinite(values, "maxvel"),
            ReadOptionalFinite(values, "vthresh", LegacyDefaultAxisDeadZone));
    }

    private static int ReadInt(IReadOnlyDictionary<string, string> values, string name)
    {
        if (!values.TryGetValue(name, out var text) ||
            !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw new InvalidDataException($"Missing or invalid --{name}.");
        return value;
    }

    private static float ReadFinite(IReadOnlyDictionary<string, string> values, string name)
    {
        if (!values.TryGetValue(name, out var text) ||
            !float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !float.IsFinite(value))
            throw new InvalidDataException($"Missing or invalid --{name}.");
        return value;
    }

    private static float ReadOptionalFinite(IReadOnlyDictionary<string, string> values, string name, float defaultValue) =>
        values.ContainsKey(name) ? ReadFinite(values, name) : defaultValue;
}

/// <summary>Creates an isolated recovered motion controller from legacy files.</summary>
public static class LegacyMotionControllerFactory
{
    /// <param name="axisDeadZone">
    /// Optional modern override. When omitted, the recovered <c>--vthresh</c>
    /// value (or its legacy default) is used.
    /// </param>
    public static LegacyMotionController Create(
        string parameterPath,
        string powerPath,
        string linearRegressionPath,
        int classCount,
        float? axisDeadZone = null,
        LegacyEnergyTransform energyTransform = LegacyEnergyTransform.Identity)
    {
        var parameters = LegacyMotionParameters.Load(parameterPath);
        if (parameters.ProbabilityThreshold < 0f)
            throw new InvalidDataException("--pthresh must not be negative.");
        if (parameters.MaximumVelocity <= 0f)
            throw new InvalidDataException("--maxvel must be positive.");
        var effectiveAxisDeadZone = axisDeadZone ?? parameters.AxisDeadZone;
        if (!float.IsFinite(effectiveAxisDeadZone) || effectiveAxisDeadZone < 0f)
            throw new ArgumentOutOfRangeException(nameof(axisDeadZone));

        var calibration = LegacyMotionCalibration.Load(powerPath, linearRegressionPath, classCount);
        var velocities = new LegacyVelocityMapper(new LegacyVelocityConfiguration(
            classCount,
            parameters.ProbabilityThreshold,
            calibration.PowerReferences,
            calibration.Slopes,
            calibration.Intercepts,
            parameters.VelocityMode,
            parameters.Beta,
            parameters.Gamma,
            parameters.Alpha,
            parameters.Offset,
            energyTransform));
        return new LegacyMotionController(
            new LegacyDirectionMapper(classCount, parameters.ProbabilityThreshold),
            velocities,
            effectiveAxisDeadZone,
            parameters.MaximumVelocity);
    }
}
