using System.Globalization;

namespace VocalJoystick.Features;

/// <summary>Applies the normalization encoded in a legacy <c>vj.norms</c> file.</summary>
public sealed class FeatureNormalizer
{
    public int FeatureCount => Means.Length;
    public float[] Means { get; }
    public float[] InverseStandardDeviations { get; }

    private FeatureNormalizer(float[] means, float[] inverseStandardDeviations)
    {
        Means = means;
        InverseStandardDeviations = inverseStandardDeviations;
    }

    public static FeatureNormalizer Load(string path)
    {
        var values = File.ReadLines(path)
            .Select(line => line.Split('#', 2)[0].Trim())
            .Where(line => line.Length > 0)
            .ToArray();
        var cursor = 0;
        var count = ReadInt();
        var means = ReadFloats(count);
        var inverseStandardDeviationCount = ReadInt();
        if (inverseStandardDeviationCount != count)
            throw new InvalidDataException("Mean and inverse-standard-deviation counts differ.");
        var inverseStandardDeviations = ReadFloats(count);
        if (cursor != values.Length)
            throw new InvalidDataException($"Unexpected trailing values in {path}.");
        return new FeatureNormalizer(means, inverseStandardDeviations);

        int ReadInt() => int.Parse(values[cursor++], CultureInfo.InvariantCulture);
        float[] ReadFloats(int expectedCount)
        {
            var result = new float[expectedCount];
            for (var index = 0; index < result.Length; index++)
                result[index] = float.Parse(values[cursor++], CultureInfo.InvariantCulture);
            return result;
        }
    }

    public void Normalize(ReadOnlySpan<float> features, Span<float> destination)
    {
        if (features.Length != FeatureCount || destination.Length != FeatureCount)
            throw new ArgumentException($"Expected {FeatureCount} features.");
        for (var index = 0; index < FeatureCount; index++)
            destination[index] = (features[index] - Means[index]) * InverseStandardDeviations[index];
    }
}
