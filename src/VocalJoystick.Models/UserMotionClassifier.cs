using System.Text.Json;
using VocalJoystick.Core;

namespace VocalJoystick.Models;

/// <summary>
/// A user-adapted four-direction classifier. Each enrollment utterance is
/// represented by the mean of its legacy 7-frame MFCC context vectors. At
/// inference time, class prototypes are compared with a shared, regularized
/// diagonal metric and converted to normalized scores.
/// </summary>
public sealed class UserMotionClassifier : IClassifier
{
    public const int RequiredInputCount = 182;
    public const int RequiredClassCount = 4;
    public const int MinimumExamplesPerClass = 3;
    private const float VarianceFloor = .25f;
    // Legacy direction mapping has a 0.7 confidence cutoff. Keep the
    // prototype score sharp enough that a well-separated learned class clears it.
    private const float Temperature = .005f;

    private readonly float[][] _prototypes;
    private readonly float[] _inverseVariances;

    private UserMotionClassifier(float[][] prototypes, float[] inverseVariances)
    {
        _prototypes = prototypes;
        _inverseVariances = inverseVariances;
    }

    public int InputCount => RequiredInputCount;
    public int ClassCount => RequiredClassCount;

    public static UserMotionClassifier Train(IReadOnlyList<IReadOnlyList<float[][]>> examplesByClass)
    {
        ArgumentNullException.ThrowIfNull(examplesByClass);
        if (examplesByClass.Count != RequiredClassCount)
            throw new ArgumentException($"Exactly {RequiredClassCount} direction classes are required.", nameof(examplesByClass));
        if (examplesByClass.Any(examples => examples is null || examples.Count < MinimumExamplesPerClass))
            throw new InvalidOperationException($"Record at least {MinimumExamplesPerClass} utterances for every direction.");

        var meansByClass = new float[RequiredClassCount][];
        var allMeans = new List<float[]>(examplesByClass.Sum(examples => examples.Count));
        for (var classIndex = 0; classIndex < examplesByClass.Count; classIndex++)
        {
            var classExamples = examplesByClass[classIndex];
            meansByClass[classIndex] = new float[RequiredInputCount];
            foreach (var example in classExamples)
            {
                if (example is null || example.Length == 0 || example.Any(frame => frame is null || frame.Length != RequiredInputCount))
                    throw new ArgumentException($"Each utterance must contain non-empty {RequiredInputCount}-feature frames.", nameof(examplesByClass));
                var utteranceMean = new float[RequiredInputCount];
                foreach (var frame in example)
                    for (var feature = 0; feature < RequiredInputCount; feature++)
                    {
                        if (!float.IsFinite(frame[feature])) throw new ArgumentException("Training features must be finite.", nameof(examplesByClass));
                        utteranceMean[feature] += frame[feature] / example.Length;
                    }
                allMeans.Add(utteranceMean);
                for (var feature = 0; feature < RequiredInputCount; feature++)
                    meansByClass[classIndex][feature] += utteranceMean[feature] / classExamples.Count;
            }
        }

        // A shared variance scale avoids one noisy direction dominating the
        // distance metric. The floor protects dimensions with little data.
        var globalMean = new float[RequiredInputCount];
        foreach (var sample in allMeans)
            for (var feature = 0; feature < RequiredInputCount; feature++)
                globalMean[feature] += sample[feature] / allMeans.Count;
        var inverseVariances = new float[RequiredInputCount];
        for (var feature = 0; feature < RequiredInputCount; feature++)
        {
            var variance = 0f;
            foreach (var sample in allMeans)
            {
                var difference = sample[feature] - globalMean[feature];
                variance += difference * difference / allMeans.Count;
            }
            inverseVariances[feature] = 1f / MathF.Max(variance, VarianceFloor);
        }

        return new UserMotionClassifier(meansByClass, inverseVariances);
    }

    public float[] Classify(ReadOnlySpan<float> input)
    {
        if (input.Length != InputCount) throw new ArgumentException($"Expected {InputCount} input values.", nameof(input));
        Span<float> logits = stackalloc float[RequiredClassCount];
        var maximum = float.NegativeInfinity;
        for (var classIndex = 0; classIndex < RequiredClassCount; classIndex++)
        {
            var distance = 0f;
            for (var feature = 0; feature < RequiredInputCount; feature++)
            {
                if (!float.IsFinite(input[feature])) throw new ArgumentException("Input features must be finite.", nameof(input));
                var difference = input[feature] - _prototypes[classIndex][feature];
                distance += difference * difference * _inverseVariances[feature];
            }
            logits[classIndex] = -distance / (2f * RequiredInputCount * Temperature);
            maximum = MathF.Max(maximum, logits[classIndex]);
        }

        var scores = new float[RequiredClassCount];
        var total = 0f;
        for (var index = 0; index < scores.Length; index++)
        {
            scores[index] = MathF.Exp(logits[index] - maximum);
            total += scores[index];
        }
        for (var index = 0; index < scores.Length; index++) scores[index] /= total;
        return scores;
    }

    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var payload = new PersistedModel(_prototypes, _inverseVariances);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(payload));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public static UserMotionClassifier Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var payload = JsonSerializer.Deserialize<PersistedModel>(File.ReadAllText(path))
            ?? throw new InvalidDataException("The user motion model is empty.");
        if (payload.Prototypes.Length != RequiredClassCount || payload.Prototypes.Any(row => row.Length != RequiredInputCount) ||
            payload.InverseVariances.Length != RequiredInputCount ||
            payload.Prototypes.SelectMany(row => row).Concat(payload.InverseVariances).Any(value => !float.IsFinite(value)) ||
            payload.InverseVariances.Any(value => value <= 0f))
            throw new InvalidDataException("The user motion model has invalid dimensions or values.");
        return new UserMotionClassifier(payload.Prototypes, payload.InverseVariances);
    }

    private sealed record PersistedModel(float[][] Prototypes, float[] InverseVariances);
}
