using VocalJoystick.Core;

namespace VocalJoystick.Features;

/// <summary>
/// Builds the recovered legacy classifier input from completed 26-value
/// feature frames. Frames are normalized separately, then serialized from
/// oldest to newest into the seven-frame (182-value) model context.
/// </summary>
public sealed class LegacyRecognitionPipeline
{
    public const int LegacyFeatureCount = 26;
    public const int LegacyContextFrameCount = 7;

    private readonly FeatureNormalizer _normalizer;
    private readonly IClassifier _classifier;
    private readonly FrameContextBuilder _context;

    public LegacyRecognitionPipeline(FeatureNormalizer normalizer, IClassifier classifier)
    {
        _normalizer = normalizer ?? throw new ArgumentNullException(nameof(normalizer));
        _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
        if (_normalizer.FeatureCount != LegacyFeatureCount)
            throw new ArgumentException($"Legacy recognition requires {LegacyFeatureCount} features.", nameof(normalizer));
        if (_classifier.InputCount != LegacyFeatureCount * LegacyContextFrameCount)
            throw new ArgumentException(
                $"Legacy recognition classifier requires {LegacyFeatureCount * LegacyContextFrameCount} inputs.",
                nameof(classifier));
        _context = new FrameContextBuilder(LegacyFeatureCount, LegacyContextFrameCount,
            FrameContextOrder.OldestToNewest);
    }

    /// <summary>
    /// Adds one complete static-plus-delta feature frame. Returns false until
    /// seven frames are available; thereafter returns normalized class scores.
    /// </summary>
    public bool TryClassify(ReadOnlySpan<float> features, out float[] scores)
        => TryClassify(features, out scores, out _);

    /// <summary>Classifies a frame and also returns the normalized seven-frame input vector.</summary>
    public bool TryClassify(ReadOnlySpan<float> features, out float[] scores, out float[] contextFeatures)
    {
        if (features.Length != LegacyFeatureCount)
            throw new ArgumentException($"Expected {LegacyFeatureCount} features.", nameof(features));

        var normalized = new float[LegacyFeatureCount];
        _normalizer.Normalize(features, normalized);
        _context.AddFrame(normalized);

        var input = new float[_context.ContextLength];
        if (!_context.TryBuild(input))
        {
            scores = Array.Empty<float>();
            contextFeatures = Array.Empty<float>();
            return false;
        }
        contextFeatures = input;
        scores = _classifier.Classify(input);
        return true;
    }
}
