namespace VocalJoystick.Features;

/// <summary>Explicit MFCC parameters; no preset is claimed to match the legacy frontend.</summary>
public sealed record MfccConfig
{
    public int SampleRate { get; }
    public int FilterBankCount { get; }
    public int CoefficientCount { get; }
    public float LowFrequencyHz { get; }
    public float HighFrequencyHz { get; }

    public MfccConfig(int sampleRate, int filterBankCount, int coefficientCount, float lowFrequencyHz, float highFrequencyHz)
    {
        if (sampleRate <= 0 || filterBankCount <= 0 || coefficientCount <= 0 || coefficientCount > filterBankCount)
            throw new ArgumentOutOfRangeException(nameof(coefficientCount));
        if (lowFrequencyHz < 0 || highFrequencyHz <= lowFrequencyHz || highFrequencyHz > sampleRate / 2f)
            throw new ArgumentOutOfRangeException(nameof(highFrequencyHz));
        SampleRate = sampleRate;
        FilterBankCount = filterBankCount;
        CoefficientCount = coefficientCount;
        LowFrequencyHz = lowFrequencyHz;
        HighFrequencyHz = highFrequencyHz;
    }
}

/// <summary>Deterministic single-frame MFCC extractor for offline validation and streaming use.</summary>
public sealed class MfccExtractor
{
    private const float LogFloor = 1e-12f;
    private readonly MfccConfig _config;

    public MfccExtractor(MfccConfig config) => _config = config;

    public float[] Extract(ReadOnlySpan<float> frame)
    {
        if (frame.Length < 2) throw new ArgumentException("MFCC needs at least two samples.", nameof(frame));
        var spectrum = PowerSpectrum(frame);
        var filters = CreateFilterBank(frame.Length, spectrum.Length);
        var logEnergies = new float[_config.FilterBankCount];
        for (var filter = 0; filter < filters.Length; filter++)
        {
            double energy = 0;
            for (var bin = 0; bin < spectrum.Length; bin++) energy += spectrum[bin] * filters[filter][bin];
            logEnergies[filter] = MathF.Log(MathF.Max((float)energy, LogFloor));
        }
        var coefficients = new float[_config.CoefficientCount];
        for (var coefficient = 0; coefficient < coefficients.Length; coefficient++)
        {
            double sum = 0;
            for (var filter = 0; filter < logEnergies.Length; filter++)
                sum += logEnergies[filter] * Math.Cos(Math.PI * coefficient * (filter + .5) / logEnergies.Length);
            coefficients[coefficient] = (float)sum;
        }
        return coefficients;
    }

    private float[] PowerSpectrum(ReadOnlySpan<float> frame)
    {
        var bins = frame.Length / 2 + 1;
        var result = new float[bins];
        for (var bin = 0; bin < bins; bin++)
        {
            double real = 0, imaginary = 0;
            for (var sample = 0; sample < frame.Length; sample++)
            {
                var windowed = frame[sample] * (0.54 - 0.46 * Math.Cos(2 * Math.PI * sample / (frame.Length - 1)));
                var phase = 2 * Math.PI * bin * sample / frame.Length;
                real += windowed * Math.Cos(phase);
                imaginary -= windowed * Math.Sin(phase);
            }
            result[bin] = (float)((real * real + imaginary * imaginary) / frame.Length);
        }
        return result;
    }

    private float[][] CreateFilterBank(int frameLength, int binCount)
    {
        var points = Enumerable.Range(0, _config.FilterBankCount + 2)
            .Select(index => MelToHz(HzToMel(_config.LowFrequencyHz) + index * (HzToMel(_config.HighFrequencyHz) - HzToMel(_config.LowFrequencyHz)) / (_config.FilterBankCount + 1)))
            .Select(hz => Math.Clamp((int)MathF.Floor((frameLength + 1) * hz / _config.SampleRate), 0, binCount - 1))
            .ToArray();
        var filters = new float[_config.FilterBankCount][];
        for (var filter = 0; filter < filters.Length; filter++)
        {
            var weights = new float[binCount];
            for (var bin = points[filter]; bin < points[filter + 1]; bin++)
                if (points[filter + 1] != points[filter]) weights[bin] = (float)(bin - points[filter]) / (points[filter + 1] - points[filter]);
            for (var bin = points[filter + 1]; bin < points[filter + 2]; bin++)
                if (points[filter + 2] != points[filter + 1]) weights[bin] = (float)(points[filter + 2] - bin) / (points[filter + 2] - points[filter + 1]);
            filters[filter] = weights;
        }
        return filters;
    }

    private static float HzToMel(float hz) => 2595f * MathF.Log10(1f + hz / 700f);
    private static float MelToHz(float mel) => 700f * (MathF.Pow(10f, mel / 2595f) - 1f);
}
