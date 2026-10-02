using System.Globalization;
using VocalJoystick.Core;

namespace VocalJoystick.Models;

/// <summary>
/// Read-only legacy ASCII <c>.wts</c> MLP. Each matrix is stored as contiguous
/// rows in output-neuron-major order.
/// </summary>
public sealed class LegacyMlpModel : IClassifier
{
    public int InputCount { get; }
    public int HiddenCount { get; }
    public int ClassCount { get; }
    public float[] InputToHidden { get; }
    public float[] HiddenToOutput { get; }
    public float[] HiddenBias { get; }
    public float[] OutputBias { get; }

    private LegacyMlpModel(int inputCount, int hiddenCount, int classCount,
        float[] inputToHidden, float[] hiddenToOutput, float[] hiddenBias, float[] outputBias)
    {
        InputCount = inputCount;
        HiddenCount = hiddenCount;
        ClassCount = classCount;
        InputToHidden = inputToHidden;
        HiddenToOutput = hiddenToOutput;
        HiddenBias = hiddenBias;
        OutputBias = outputBias;
    }

    public static LegacyMlpModel Load(string path)
    {
        var values = File.ReadLines(path)
            .Select(line => line.Split('#', 2)[0].Trim())
            .Where(line => line.Length > 0)
            .ToArray();
        var cursor = 0;
        var input = ReadInt();
        var hidden = ReadInt();
        var output = ReadInt();
        var inputToHidden = ReadBlock(input * hidden);
        var hiddenToOutput = ReadBlock(hidden * output);
        var hiddenBias = ReadBlock(hidden);
        var outputBias = ReadBlock(output);
        if (cursor != values.Length)
            throw new InvalidDataException($"Unexpected trailing values in {path}.");
        return new LegacyMlpModel(input, hidden, output, inputToHidden, hiddenToOutput, hiddenBias, outputBias);

        int ReadInt() => int.Parse(values[cursor++], CultureInfo.InvariantCulture);
        float[] ReadBlock(int expectedCount)
        {
            var declaredCount = ReadInt();
            if (declaredCount != expectedCount)
                throw new InvalidDataException($"Expected block of {expectedCount} values, found {declaredCount}.");
            var block = new float[declaredCount];
            for (var index = 0; index < block.Length; index++)
                block[index] = float.Parse(values[cursor++], CultureInfo.InvariantCulture);
            return block;
        }
    }

    public float[] Classify(ReadOnlySpan<float> input)
    {
        if (input.Length != InputCount)
            throw new ArgumentException($"Expected {InputCount} input values, received {input.Length}.", nameof(input));

        var hidden = new float[HiddenCount];
        for (var hiddenIndex = 0; hiddenIndex < HiddenCount; hiddenIndex++)
        {
            var offset = hiddenIndex * InputCount;
            var sum = HiddenBias[hiddenIndex];
            for (var inputIndex = 0; inputIndex < InputCount; inputIndex++)
                sum += InputToHidden[offset + inputIndex] * input[inputIndex];

            // The legacy helper is 1 / (1 + exp(x)), rather than the more
            // common 1 / (1 + exp(-x)).
            hidden[hiddenIndex] = 1f / (1f + MathF.Exp(sum));
        }

        var output = new float[ClassCount];
        var total = 0f;
        for (var classIndex = 0; classIndex < ClassCount; classIndex++)
        {
            var offset = classIndex * HiddenCount;
            var sum = OutputBias[classIndex];
            for (var hiddenIndex = 0; hiddenIndex < HiddenCount; hiddenIndex++)
                sum += HiddenToOutput[offset + hiddenIndex] * hidden[hiddenIndex];

            var probabilityNumerator = MathF.Exp(sum);
            output[classIndex] = probabilityNumerator;
            total += probabilityNumerator;
        }

        for (var classIndex = 0; classIndex < output.Length; classIndex++)
            output[classIndex] /= total;
        return output;
    }
}
