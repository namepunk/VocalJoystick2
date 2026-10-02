using System.Buffers.Binary;

namespace VocalJoystick.Audio;

/// <summary>Writes mono little-endian PCM16 WAV files from the audio pipeline samples.</summary>
public static class Pcm16WaveFile
{
    public static void WriteMono(string path, int sampleRate, ReadOnlySpan<float> samples)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (samples.IsEmpty) throw new ArgumentException("A WAV file must contain audio samples.", nameof(samples));
        var dataLength = checked(samples.Length * sizeof(short));
        var fileBytes = new byte[checked(44 + dataLength)];
        "RIFF"u8.CopyTo(fileBytes);
        BinaryPrimitives.WriteInt32LittleEndian(fileBytes.AsSpan(4), checked(36 + dataLength));
        "WAVE"u8.CopyTo(fileBytes.AsSpan(8));
        "fmt "u8.CopyTo(fileBytes.AsSpan(12));
        BinaryPrimitives.WriteInt32LittleEndian(fileBytes.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(fileBytes.AsSpan(20), 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(fileBytes.AsSpan(22), 1); // mono
        BinaryPrimitives.WriteInt32LittleEndian(fileBytes.AsSpan(24), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(fileBytes.AsSpan(28), checked(sampleRate * sizeof(short)));
        BinaryPrimitives.WriteInt16LittleEndian(fileBytes.AsSpan(32), sizeof(short));
        BinaryPrimitives.WriteInt16LittleEndian(fileBytes.AsSpan(34), 16);
        "data"u8.CopyTo(fileBytes.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(fileBytes.AsSpan(40), dataLength);
        for (var index = 0; index < samples.Length; index++)
        {
            if (!float.IsFinite(samples[index])) throw new ArgumentException("Samples must be finite.", nameof(samples));
            var sample = (short)Math.Clamp(MathF.Round(samples[index]), short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(fileBytes.AsSpan(44 + index * sizeof(short)), sample);
        }

        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, fileBytes);
    }
}
