using System.Buffers.Binary;
using VocalJoystick.Core;

namespace VocalJoystick.Models;

/// <summary>
/// Read-only classifier for the recovered legacy binary <c>.mlrsc</c> format.
/// The score is the squared norm of the class-specific transformed hidden
/// vector, normalized across classes as done by the legacy DLL.
/// </summary>
public sealed class LegacyMlrscModel : IClassifier
{
    public int FeatureCount { get; }
    public int InputCount { get; }
    public int HiddenCount { get; }
    public int ClassCount { get; }
    public float[] ClassMatrices { get; }
    public float[] ClassShifts { get; }
    public float[] InputToHidden { get; }
    public float[] HiddenBias { get; }

    private LegacyMlrscModel(int featureCount, int inputCount, int hiddenCount, int classCount,
        float[] classMatrices, float[] classShifts, float[] inputToHidden, float[] hiddenBias)
    {
        FeatureCount = featureCount;
        InputCount = inputCount;
        HiddenCount = hiddenCount;
        ClassCount = classCount;
        ClassMatrices = classMatrices;
        ClassShifts = classShifts;
        InputToHidden = inputToHidden;
        HiddenBias = hiddenBias;
    }

    public static LegacyMlrscModel Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 4 * sizeof(int) || bytes.Length % sizeof(float) != 0)
            throw new InvalidDataException($"Invalid ML-RSC binary model: {path}.");

        var cursor = 0;
        var featureCount = ReadInt();
        var inputCount = ReadInt();
        var hiddenCount = ReadInt();
        var classCount = ReadInt();
        if (featureCount <= 0 || inputCount <= 0 || hiddenCount <= 0 || classCount <= 0)
            throw new InvalidDataException("ML-RSC dimensions must be positive.");

        int MatrixCount;
        int ShiftCount;
        int WeightCount;
        try
        {
            MatrixCount = checked(classCount * hiddenCount * hiddenCount);
            ShiftCount = checked(classCount * hiddenCount);
            WeightCount = checked(hiddenCount * inputCount);
        }
        catch (OverflowException error)
        {
            throw new InvalidDataException("ML-RSC dimensions are too large.", error);
        }

        var matrices = ReadBlock(MatrixCount);
        var shifts = ReadBlock(ShiftCount);
        var weights = ReadBlock(WeightCount);
        var biases = ReadBlock(hiddenCount);
        if (cursor != bytes.Length)
            throw new InvalidDataException($"Unexpected trailing data in ML-RSC model: {path}.");
        return new LegacyMlrscModel(featureCount, inputCount, hiddenCount, classCount,
            matrices, shifts, weights, biases);

        int ReadInt()
        {
            if (cursor + sizeof(int) > bytes.Length) throw new InvalidDataException("Truncated ML-RSC model header.");
            var value = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(cursor, sizeof(int)));
            cursor += sizeof(int);
            return value;
        }

        float[] ReadBlock(int count)
        {
            if (count < 0 || count > (bytes.Length - cursor) / sizeof(float))
                throw new InvalidDataException("Truncated ML-RSC model payload.");
            var result = new float[count];
            for (var index = 0; index < result.Length; index++)
            {
                result[index] = BitConverter.Int32BitsToSingle(
                    BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(cursor, sizeof(float))));
                cursor += sizeof(float);
            }
            return result;
        }
    }

    public float[] Classify(ReadOnlySpan<float> input)
    {
        if (input.Length != InputCount)
            throw new ArgumentException($"Expected {InputCount} input values, received {input.Length}.", nameof(input));

        var hidden = new float[HiddenCount];
        for (var hiddenIndex = 0; hiddenIndex < HiddenCount; hiddenIndex++)
        {
            var sum = HiddenBias[hiddenIndex];
            var offset = hiddenIndex * InputCount;
            for (var inputIndex = 0; inputIndex < InputCount; inputIndex++)
                sum += InputToHidden[offset + inputIndex] * input[inputIndex];
            hidden[hiddenIndex] = 1f / (1f + MathF.Exp(sum));
        }

        var scores = new float[ClassCount];
        var total = 0f;
        for (var classIndex = 0; classIndex < ClassCount; classIndex++)
        {
            var shiftOffset = classIndex * HiddenCount;
            var matrixOffset = classIndex * HiddenCount * HiddenCount;
            var score = 0f;
            for (var outputIndex = 0; outputIndex < HiddenCount; outputIndex++)
            {
                var transformed = 0f;
                for (var hiddenIndex = 0; hiddenIndex < HiddenCount; hiddenIndex++)
                {
                    var shifted = hidden[hiddenIndex] - ClassShifts[shiftOffset + hiddenIndex];
                    transformed += shifted * ClassMatrices[matrixOffset + hiddenIndex * HiddenCount + outputIndex];
                }
                score += transformed * transformed;
            }
            scores[classIndex] = score;
            total += score;
        }

        for (var classIndex = 0; classIndex < scores.Length; classIndex++) scores[classIndex] /= total;
        return scores;
    }
}
