namespace VocalJoystick.Core;

/// <summary>Returns a probability-like score for each configured class.</summary>
public interface IClassifier
{
    int InputCount { get; }
    int ClassCount { get; }
    float[] Classify(ReadOnlySpan<float> input);
}
