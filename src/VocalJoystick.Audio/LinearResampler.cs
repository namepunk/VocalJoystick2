namespace VocalJoystick.Audio;

/// <summary>
/// Deterministic linear PCM resampler for non-realtime import and diagnostics.
/// Live capture requests 16 kHz directly and should not allocate in callbacks.
/// </summary>
public static class LinearResampler
{
    public static float[] Resample(ReadOnlySpan<float> input, int inputSampleRate, int outputSampleRate)
    {
        if (inputSampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(inputSampleRate));
        if (outputSampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(outputSampleRate));
        if (input.IsEmpty) return [];
        if (inputSampleRate == outputSampleRate) return input.ToArray();

        var outputCount = checked((int)(((long)(input.Length - 1) * outputSampleRate) / inputSampleRate + 1));
        var output = new float[outputCount];
        for (var outputIndex = 0; outputIndex < output.Length; outputIndex++)
        {
            var sourcePosition = (double)outputIndex * inputSampleRate / outputSampleRate;
            var left = (int)sourcePosition;
            var fraction = (float)(sourcePosition - left);
            var right = Math.Min(left + 1, input.Length - 1);
            output[outputIndex] = input[left] + (input[right] - input[left]) * fraction;
        }
        return output;
    }
}
