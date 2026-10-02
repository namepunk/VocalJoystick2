using System.Buffers.Binary;

namespace VocalJoystick.Features;

/// <summary>
/// Reads raw feature streams emitted by the legacy <c>frontend.exe</c>.
/// The stream contract is evidenced by Windows XP captures: little-endian
/// IEEE-754 float32 values, with one pitch value per MFCC record.
/// </summary>
public sealed record LegacyFeatureDump(float[][] MfccFrames, float[] Pitch)
{
    public static LegacyFeatureDump Read(Stream mfccStream, Stream pitchStream, int featureCount = 26)
    {
        ArgumentNullException.ThrowIfNull(mfccStream);
        ArgumentNullException.ThrowIfNull(pitchStream);
        if (featureCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(featureCount));

        var mfccValues = ReadFloat32LittleEndian(mfccStream);
        var pitch = ReadFloat32LittleEndian(pitchStream);
        if (mfccValues.Length % featureCount != 0)
            throw new InvalidDataException($"MFCC stream has {mfccValues.Length} values, not a multiple of {featureCount}.");

        var frameCount = mfccValues.Length / featureCount;
        if (pitch.Length != frameCount)
            throw new InvalidDataException($"MFCC stream has {frameCount} frames but pitch stream has {pitch.Length} values.");

        var frames = new float[frameCount][];
        for (var frame = 0; frame < frameCount; frame++)
        {
            frames[frame] = new float[featureCount];
            Array.Copy(mfccValues, frame * featureCount, frames[frame], 0, featureCount);
        }

        return new LegacyFeatureDump(frames, pitch);
    }

    private static float[] ReadFloat32LittleEndian(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();
        if (bytes.Length % sizeof(float) != 0)
            throw new InvalidDataException("Raw float32 stream has a partial trailing value.");

        var values = new float[bytes.Length / sizeof(float)];
        for (var index = 0; index < values.Length; index++)
        {
            var bits = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(index * sizeof(float), sizeof(float)));
            values[index] = BitConverter.Int32BitsToSingle(bits);
        }
        return values;
    }
}
