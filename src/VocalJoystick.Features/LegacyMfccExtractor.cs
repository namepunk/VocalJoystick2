using System.Numerics;

namespace VocalJoystick.Features;

/// <summary>
/// The recovered static-MFCC path of the 2008 legacy DLL. It emits the first
/// 13 values of a 26-value feature record; callers add the verified temporal
/// deltas separately.
/// </summary>
public sealed class LegacyMfccExtractor
{
    public const int FrameLength = 400;
    public const int RequiredSampleCount = FrameLength;
    public const int FftLength = 512;
    public const int FilterBankCount = 23;
    public const int StaticFeatureCount = 13;

    private const float PreEmphasis = .97f;
    private const float DcBlockerCoefficient = .999f;
    private const double LogFloor = -50d;
    private static readonly int[] BinPoints =
    [
        2, 5, 8, 11, 14, 18, 23, 27, 33, 38, 45, 52, 60,
        69, 79, 89, 101, 115, 129, 145, 163, 183, 205, 229, 256
    ];

    /// <summary>Copies the 25 FFT-bin vertices recovered from the running XP DLL.</summary>
    public static int[] GetFilterBinPoints() => (int[])BinPoints.Clone();

    /// <summary>
    /// Builds the exact 23 stored weight arrays: each edge has a non-zero
    /// weight, matching the legacy initializer rather than a textbook triangle.
    /// </summary>
    public static float[][] CreateFilterBank()
    {
        var result = new float[FilterBankCount][];
        for (var filter = 0; filter < result.Length; filter++)
        {
            var start = BinPoints[filter];
            var peak = BinPoints[filter + 1];
            var end = BinPoints[filter + 2];
            var weights = new float[end - start + 1];
            for (var bin = start; bin <= peak; bin++)
                weights[bin - start] = (float)(bin - start + 1) / (peak - start + 1);
            for (var bin = peak + 1; bin <= end; bin++)
                weights[bin - start] = (float)(end - bin + 1) / (end - peak + 1);
            result[filter] = weights;
        }
        return result;
    }

    public float[] Extract(ReadOnlySpan<float> samples)
    {
        if (samples.Length < RequiredSampleCount)
        {
            throw new ArgumentException(
                $"Legacy MFCC needs {FrameLength} frame samples.",
                nameof(samples));
        }

        // FrameData::removeDC is a recursive DC blocker, not a mean
        // subtraction. Its output deliberately contains one extra value;
        // pre-emphasis consumes it for the final 400-sample frame value.
        var dcRemoved = new double[FrameLength + 1];
        dcRemoved[0] = 0;
        dcRemoved[1] = samples[0];
        for (var index = 2; index < dcRemoved.Length; index++)
            dcRemoved[index] = DcBlockerCoefficient * dcRemoved[index - 1] + samples[index - 1] - samples[index - 2];

        double energy = 0;
        var preEmphasized = new Complex[FftLength];
        for (var index = 0; index < FrameLength; index++)
        {
            var current = dcRemoved[index];
            energy += current * current;
            var following = dcRemoved[index + 1];
            var emphasized = following - PreEmphasis * current;
            var hamming = .54 - .46 * Math.Cos(2 * Math.PI * index / (FrameLength - 1));
            preEmphasized[index] = new Complex(emphasized * hamming, 0);
        }

        TransformInPlace(preEmphasized);
        var spectrum = new double[FftLength / 2 + 1];
        for (var bin = 0; bin < spectrum.Length; bin++) spectrum[bin] = preEmphasized[bin].Magnitude;

        var filters = CreateFilterBank();
        var logBands = new double[FilterBankCount];
        for (var filter = 0; filter < filters.Length; filter++)
        {
            var start = BinPoints[filter];
            double sum = 0;
            for (var weightIndex = 0; weightIndex < filters[filter].Length; weightIndex++)
                sum += spectrum[start + weightIndex] * filters[filter][weightIndex];
            logBands[filter] = LogWithLegacyFloor(sum);
        }

        var output = new float[StaticFeatureCount];
        for (var coefficient = 1; coefficient < StaticFeatureCount; coefficient++)
        {
            double sum = 0;
            for (var band = 0; band < FilterBankCount; band++)
                sum += logBands[band] * Math.Cos(Math.PI * coefficient * (band + .5) / FilterBankCount);
            output[coefficient - 1] = (float)sum;
        }
        output[^1] = (float)LogWithLegacyFloor(energy);
        return output;
    }

    private static double LogWithLegacyFloor(double value) => value > Math.Exp(LogFloor) ? Math.Log(value) : LogFloor;

    private static void TransformInPlace(Complex[] values)
    {
        for (int index = 1, reversed = 0; index < values.Length; index++)
        {
            var bit = values.Length >> 1;
            while ((reversed & bit) != 0)
            {
                reversed ^= bit;
                bit >>= 1;
            }
            reversed ^= bit;
            if (index < reversed) (values[index], values[reversed]) = (values[reversed], values[index]);
        }

        for (var length = 2; length <= values.Length; length <<= 1)
        {
            var rotation = Complex.FromPolarCoordinates(1, -2 * Math.PI / length);
            for (var offset = 0; offset < values.Length; offset += length)
            {
                var factor = Complex.One;
                for (var index = 0; index < length / 2; index++)
                {
                    var even = values[offset + index];
                    var odd = values[offset + index + length / 2] * factor;
                    values[offset + index] = even + odd;
                    values[offset + index + length / 2] = even - odd;
                    factor *= rotation;
                }
            }
        }
    }
}
