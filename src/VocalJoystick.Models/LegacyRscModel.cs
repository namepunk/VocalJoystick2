using System.Buffers.Binary;
using VocalJoystick.Core;

namespace VocalJoystick.Models;

/// <summary>Read-only classifier for the recovered legacy binary <c>.rsc</c> format.</summary>
public sealed class LegacyRscModel : IClassifier
{
    public int FeatureCount { get; }
    public int InputCount { get; }
    public int ClassCount { get; }
    public float[] ClassMatrices { get; }
    public float[] ClassShifts { get; }

    private LegacyRscModel(int featureCount, int inputCount, int classCount,
        float[] classMatrices, float[] classShifts)
    {
        FeatureCount = featureCount;
        InputCount = inputCount;
        ClassCount = classCount;
        ClassMatrices = classMatrices;
        ClassShifts = classShifts;
    }

    public static LegacyRscModel Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 3 * sizeof(int) || bytes.Length % sizeof(float) != 0)
            throw new InvalidDataException($"Invalid RSC binary model: {path}.");

        var cursor = 0;
        var featureCount = ReadInt();
        var inputCount = ReadInt();
        var classCount = ReadInt();
        if (featureCount <= 0 || inputCount <= 0 || classCount <= 0)
            throw new InvalidDataException("RSC dimensions must be positive.");
        int matrixCount;
        int shiftCount;
        try
        {
            matrixCount = checked(classCount * inputCount * inputCount);
            shiftCount = checked(classCount * inputCount);
        }
        catch (OverflowException error)
        {
            throw new InvalidDataException("RSC dimensions are too large.", error);
        }
        var matrices = ReadBlock(matrixCount);
        var shifts = ReadBlock(shiftCount);
        if (cursor != bytes.Length)
            throw new InvalidDataException($"Unexpected trailing data in RSC model: {path}.");
        return new LegacyRscModel(featureCount, inputCount, classCount, matrices, shifts);

        int ReadInt()
        {
            if (cursor + sizeof(int) > bytes.Length) throw new InvalidDataException("Truncated RSC model header.");
            var value = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(cursor, sizeof(int)));
            cursor += sizeof(int);
            return value;
        }

        float[] ReadBlock(int count)
        {
            if (count < 0 || count > (bytes.Length - cursor) / sizeof(float))
                throw new InvalidDataException("Truncated RSC model payload.");
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

        var scores = new float[ClassCount];
        var total = 0f;
        for (var classIndex = 0; classIndex < ClassCount; classIndex++)
        {
            var shiftOffset = classIndex * InputCount;
            var matrixOffset = classIndex * InputCount * InputCount;
            var score = 0f;
            for (var outputIndex = 0; outputIndex < InputCount; outputIndex++)
            {
                var transformed = 0f;
                for (var inputIndex = 0; inputIndex < InputCount; inputIndex++)
                {
                    var shifted = input[inputIndex] - ClassShifts[shiftOffset + inputIndex];
                    transformed += shifted * ClassMatrices[matrixOffset + inputIndex * InputCount + outputIndex];
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
