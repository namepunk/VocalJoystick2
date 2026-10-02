using System.Globalization;

namespace VocalJoystick.Motion;

/// <summary>
/// Per-vowel power references and linear scaling coefficients loaded from the
/// legacy user <c>*.power</c> and parameter <c>*.lr</c> files.
/// </summary>
public sealed record LegacyMotionCalibration(float[] PowerReferences, float[] Slopes, float[] Intercepts)
{
    public static LegacyMotionCalibration Load(string powerPath, string linearRegressionPath, int classCount)
    {
        if (classCount is not (4 or 5 or 8 or 9)) throw new ArgumentOutOfRangeException(nameof(classCount));
        var powers = ReadNumbers(powerPath);
        if (powers.Length != classCount)
            throw new InvalidDataException($"Expected {classCount} power values in {powerPath}, found {powers.Length}.");
        if (powers.Any(value => value <= 0f))
            throw new InvalidDataException($"Power values in {powerPath} must be positive.");

        var linearValues = ReadNumbers(linearRegressionPath);
        if (linearValues.Length < classCount * 2)
            throw new InvalidDataException($"Expected at least {classCount} slope/intercept pairs in {linearRegressionPath}.");
        var slopes = new float[classCount];
        var intercepts = new float[classCount];
        for (var index = 0; index < classCount; index++)
        {
            slopes[index] = linearValues[index * 2];
            intercepts[index] = linearValues[index * 2 + 1];
        }
        return new LegacyMotionCalibration(powers, slopes, intercepts);
    }

    private static float[] ReadNumbers(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var values = new List<float>();
        foreach (var line in File.ReadLines(path))
        {
            var content = line.Split('#', 2)[0];
            foreach (var token in content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !float.IsFinite(value))
                    throw new InvalidDataException($"Invalid numeric value '{token}' in {path}.");
                values.Add(value);
            }
        }
        return values.ToArray();
    }
}
