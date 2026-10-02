namespace VocalJoystick.Features;

/// <summary>
/// The legacy <c>FrameData::loadWave</c> power path: 60 ms windows at 16 kHz,
/// advanced every 10 ms. Values whose absolute amplitude is below 10 are
/// centre-clipped before root-mean-square power and zero crossings are read.
/// </summary>
public sealed class LegacyFrameEnergyStream
{
    public const int SampleRate = 16_000;
    public const int FrameLength = 960;
    public const int HopLength = 160;
    public const float CenterClipThreshold = 10f;

    private readonly AudioFramer _framer = new(new AudioFrameSpec(SampleRate, FrameLength, HopLength));

    public IReadOnlyList<LegacyEnergyFrame> Push(ReadOnlySpan<float> samples)
    {
        var result = new List<LegacyEnergyFrame>();
        foreach (var frame in _framer.Push(samples))
        {
            double sumSquares = 0d;
            var zeroCrossings = 0;
            var previous = CenterClip(frame.Samples[0]);
            sumSquares += previous * previous;
            for (var index = 1; index < frame.Samples.Length; index++)
            {
                var current = CenterClip(frame.Samples[index]);
                sumSquares += current * current;
                if (previous * current < 0f) zeroCrossings++;
                previous = current;
            }
            result.Add(new LegacyEnergyFrame(frame.StartSample,
                (float)Math.Sqrt(sumSquares / frame.Samples.Length), zeroCrossings));
        }
        return result;
    }

    public void Reset() => _framer.Reset();

    private static float CenterClip(float sample) => MathF.Abs(sample) < CenterClipThreshold ? 0f : sample;
}

public readonly record struct LegacyEnergyFrame(long StartSample, float RootMeanSquare, int ZeroCrossings);
