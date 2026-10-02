using System.Globalization;

namespace VocalJoystick.Features;

/// <summary>Recovered parameters for the legacy power/zero-crossing VAD.</summary>
public sealed record LegacyVoiceActivityConfiguration(
    float VoiceStartMultiplier = 5f,
    float VoiceSustainMultiplier = 2f,
    float EnergyFloor = 20f,
    int ZeroCrossingFloor = 3,
    float LowPassPole = .65f,
    float NoiseStandardDeviationLimit = 80f,
    int NoiseWindowFrameCount = 300,
    int MinimumSilenceFrameCount = 5,
    float NoiseUpdateMinimumDifference = 10f)
{
    public const float LegacyDefaultVoiceStartMultiplier = 5f;
    public const float LegacyDefaultVoiceSustainMultiplier = 2f;
    public const float LegacyDefaultEnergyFloor = 20f;
    public const int LegacyDefaultZeroCrossingFloor = 3;
    public const float LegacyDefaultLowPassPole = .65f;
    public const float LegacyDefaultNoiseStandardDeviationLimit = 80f;
    public const int LegacyDefaultNoiseWindowFrameCount = 300;
    public const int LegacyDefaultMinimumSilenceFrameCount = 5;
    public const float LegacyDefaultNoiseUpdateMinimumDifference = 10f;

    /// <summary>Loads the VAD subset of a shared legacy <c>prm.cfg</c> profile.</summary>
    public static LegacyVoiceActivityConfiguration Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in File.ReadLines(path))
        {
            var parts = rawLine.Split('#', 2)[0]
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0].StartsWith("--", StringComparison.Ordinal)) values[parts[0][2..]] = parts[1];
        }

        return new LegacyVoiceActivityConfiguration(
            ReadFinite(values, "vstart", LegacyDefaultVoiceStartMultiplier, 0f, float.PositiveInfinity),
            ReadFinite(values, "vend", LegacyDefaultVoiceSustainMultiplier, 0f, float.PositiveInfinity),
            ReadFinite(values, "efloor", LegacyDefaultEnergyFloor, 0f, float.PositiveInfinity),
            ReadInteger(values, "zcfloor", LegacyDefaultZeroCrossingFloor, 0),
            ReadFinite(values, "lpfpole", LegacyDefaultLowPassPole, 0f, 1f),
            ReadFinite(values, "noiseDiff", LegacyDefaultNoiseStandardDeviationLimit, 0f, float.PositiveInfinity),
            ReadInteger(values, "maxNoSpch", LegacyDefaultNoiseWindowFrameCount, 1),
            ReadInteger(values, "minsil", LegacyDefaultMinimumSilenceFrameCount, 0),
            LegacyDefaultNoiseUpdateMinimumDifference);
    }

    internal void Validate()
    {
        if (!float.IsFinite(VoiceStartMultiplier) || !float.IsFinite(VoiceSustainMultiplier) ||
            !float.IsFinite(EnergyFloor) || !float.IsFinite(LowPassPole) ||
            !float.IsFinite(NoiseStandardDeviationLimit) || !float.IsFinite(NoiseUpdateMinimumDifference) ||
            VoiceStartMultiplier < 0f || VoiceSustainMultiplier < 0f || EnergyFloor < 0f ||
            LowPassPole < 0f || LowPassPole > 1f || NoiseStandardDeviationLimit < 0f ||
            NoiseUpdateMinimumDifference < 0f || ZeroCrossingFloor < 0 || NoiseWindowFrameCount < 1 ||
            MinimumSilenceFrameCount < 0)
            throw new ArgumentOutOfRangeException(nameof(LegacyVoiceActivityConfiguration));
    }

    private static float ReadFinite(IReadOnlyDictionary<string, string> values, string name, float defaultValue, float minimum, float maximum)
    {
        if (!values.TryGetValue(name, out var text)) return defaultValue;
        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
            !float.IsFinite(value) || value < minimum || value > maximum)
            throw new InvalidDataException($"Invalid --{name} value.");
        return value;
    }

    private static int ReadInteger(IReadOnlyDictionary<string, string> values, string name, int defaultValue, int minimum)
    {
        if (!values.TryGetValue(name, out var text)) return defaultValue;
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < minimum)
            throw new InvalidDataException($"Invalid --{name} value.");
        return value;
    }
}

/// <summary>
/// Stateful port of the legacy speech/silence transition logic. It consumes
/// already measured 60 ms energy frames and has no audio-source or command I/O.
/// </summary>
public sealed class LegacyVoiceActivityDetector
{
    private readonly LegacyVoiceActivityConfiguration _config;
    private readonly Queue<float> _noiseWindow = [];
    private double _noiseSum;
    private double _noiseSquaredSum;
    private float _smoothedPower;
    private float? _noisePower;
    private bool _isSpeech;
    private int _utteranceFrameCount;
    private int _silenceFrameCount;

    public LegacyVoiceActivityDetector(LegacyVoiceActivityConfiguration? configuration = null)
    {
        _config = configuration ?? new LegacyVoiceActivityConfiguration();
        _config.Validate();
    }

    public bool IsSpeech => _isSpeech;
    public float? NoisePower => _noisePower;

    public LegacyVoiceActivityResult Push(LegacyEnergyFrame frame)
    {
        if (!float.IsFinite(frame.RootMeanSquare) || frame.RootMeanSquare < 0f || frame.ZeroCrossings < 0)
            throw new ArgumentException("Energy-frame values must be finite and non-negative.", nameof(frame));

        _smoothedPower = _config.LowPassPole * _smoothedPower + (1f - _config.LowPassPole) * frame.RootMeanSquare;
        _noisePower ??= frame.RootMeanSquare;
        var previousSpeech = _isSpeech;
        var startThreshold = _config.EnergyFloor + _config.VoiceStartMultiplier * _noisePower.Value;
        var sustainThreshold = _config.EnergyFloor + _config.VoiceSustainMultiplier * _noisePower.Value;
        var zeroCrossingPasses = frame.ZeroCrossings > _config.ZeroCrossingFloor;
        var strongVoice = zeroCrossingPasses && _smoothedPower > startThreshold;
        var sustainedVoice = zeroCrossingPasses && _smoothedPower > sustainThreshold;
        var transitionBand = false;
        int? completedSpeechFrameCount = null;

        if (strongVoice)
        {
            _utteranceFrameCount++;
            _isSpeech = true;
            _silenceFrameCount = 0;
        }
        else if (sustainedVoice)
        {
            transitionBand = !previousSpeech;
            _utteranceFrameCount++;
            _silenceFrameCount = 0;
        }
        else
        {
            _silenceFrameCount++;
            if (!previousSpeech || _silenceFrameCount > _config.MinimumSilenceFrameCount)
            {
                _isSpeech = false;
                if (previousSpeech) completedSpeechFrameCount = Math.Max(0, _utteranceFrameCount - _config.MinimumSilenceFrameCount);
                _utteranceFrameCount = 0;
            }
            else
            {
                _utteranceFrameCount++;
            }
        }

        if (!_isSpeech && !transitionBand) UpdateNoiseWindow(_smoothedPower);
        return new LegacyVoiceActivityResult(
            frame.StartSample,
            _isSpeech,
            StartedSpeech: !previousSpeech && _isSpeech,
            StartedSilence: previousSpeech && !_isSpeech,
            completedSpeechFrameCount,
            _smoothedPower,
            _noisePower.Value);
    }

    private void UpdateNoiseWindow(float power)
    {
        _noiseWindow.Enqueue(power);
        _noiseSum += power;
        _noiseSquaredSum += power * power;
        if (_noiseWindow.Count > _config.NoiseWindowFrameCount)
        {
            var removed = _noiseWindow.Dequeue();
            _noiseSum -= removed;
            _noiseSquaredSum -= removed * removed;
        }
        if (_noiseWindow.Count != _config.NoiseWindowFrameCount) return;
        var mean = (float)(_noiseSum / _noiseWindow.Count);
        var variance = Math.Max(0d, _noiseSquaredSum / _noiseWindow.Count - mean * mean);
        if (Math.Sqrt(variance) < _config.NoiseStandardDeviationLimit &&
            MathF.Abs(mean - _noisePower!.Value) > _config.NoiseUpdateMinimumDifference)
            _noisePower = mean;
    }
}

public readonly record struct LegacyVoiceActivityResult(
    long StartSample,
    bool IsSpeech,
    bool StartedSpeech,
    bool StartedSilence,
    int? CompletedSpeechFrameCount,
    float SmoothedPower,
    float NoisePower)
{
    public float RootMeanSquareEnergy { get; init; }
    public int ZeroCrossings { get; init; }
}
