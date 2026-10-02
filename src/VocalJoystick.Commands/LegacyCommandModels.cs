using System.Globalization;

namespace VocalJoystick.Commands;

/// <summary>
/// Read-only loader and scorer for the diagonal Gaussian mixtures in the
/// legacy <c>vj.hmmf</c> acoustic-model file. It intentionally does not emit
/// mouse commands or decode temporal HMM paths.
/// </summary>
public sealed class LegacyGaussianMixtureCollection
{
    public const float LogProbabilityFloor = -1.0e10f;
    private readonly LegacyGaussianMixture[] _mixtures;
    private readonly LegacyBmmModel[] _bmmModels;

    private LegacyGaussianMixtureCollection(int featureCount, LegacyGaussianMixture[] mixtures, LegacyBmmModel[] bmmModels)
    {
        FeatureCount = featureCount;
        _mixtures = mixtures;
        _bmmModels = bmmModels;
    }

    public int FeatureCount { get; }
    public int MixtureCount => _mixtures.Length;
    public LegacyGaussianMixture this[int index] => _mixtures[index];
    public int BmmCount => _bmmModels.Length;
    public LegacyBmmModel GetBmm(int index) => _bmmModels[index];

    public static LegacyGaussianMixtureCollection Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var reader = new NumberReader(path);
        var featureCount = reader.ReadInt("feature count");
        var mixtureCount = reader.ReadInt("mixture count");
        if (featureCount <= 0 || mixtureCount <= 0) throw new InvalidDataException("Feature and mixture counts must be positive.");

        var mixtures = new LegacyGaussianMixture[mixtureCount];
        for (var mixtureIndex = 0; mixtureIndex < mixtureCount; mixtureIndex++)
        {
            var id = reader.ReadInt("mixture ID");
            if (id != mixtureIndex) throw new InvalidDataException($"Expected mixture ID {mixtureIndex}, found {id}.");
            var componentCount = reader.ReadInt("component count");
            if (componentCount <= 0) throw new InvalidDataException($"Mixture {id} has no components.");
            var components = new LegacyDiagonalGaussian[componentCount];
            var logWeights = new float[componentCount];
            for (var componentIndex = 0; componentIndex < componentCount; componentIndex++)
            {
                var componentId = reader.ReadInt("component ID");
                if (componentId != componentIndex)
                    throw new InvalidDataException($"Expected component ID {componentIndex}, found {componentId} in mixture {id}.");
                var weight = reader.ReadFloat("mixing coefficient");
                if (weight <= 0f) throw new InvalidDataException("Mixing coefficients must be positive.");
                var means = reader.ReadVector(featureCount, "mean");
                if (reader.ReadInt("covariance type") != 0)
                    throw new InvalidDataException("Only legacy diagonal covariance type 0 is supported.");
                var variances = reader.ReadVector(featureCount, "diagonal variance");
                components[componentIndex] = new LegacyDiagonalGaussian(means, variances);
                logWeights[componentIndex] = MathF.Log(weight);
            }
            mixtures[mixtureIndex] = new LegacyGaussianMixture(components, logWeights);
        }
        var bmmCount = reader.ReadInt("BMM count");
        if (bmmCount < 0) throw new InvalidDataException("BMM count must not be negative.");
        var bmmModels = new LegacyBmmModel[bmmCount];
        for (var bmmIndex = 0; bmmIndex < bmmCount; bmmIndex++)
        {
            var id = reader.ReadInt("BMM ID");
            if (id != bmmIndex) throw new InvalidDataException($"Expected BMM ID {bmmIndex}, found {id}.");
            var stateCount = reader.ReadInt("BMM state count");
            if (stateCount < 3) throw new InvalidDataException("A BMM needs start, end, and at least one emitting state.");
            if (reader.ReadInt("BMM transition type") != 0)
                throw new InvalidDataException("Only legacy upper-triangular BMM transition type 0 is supported.");
            var transitions = new float[stateCount][];
            for (var row = 0; row < stateCount; row++)
                transitions[row] = reader.ReadVector(stateCount - row, "BMM transition probability");
            var emissionMixtureIds = new int[stateCount - 2];
            for (var state = 0; state < emissionMixtureIds.Length; state++)
            {
                var mixtureId = reader.ReadInt("BMM emission mixture ID");
                if (mixtureId < 0 || mixtureId >= mixtureCount)
                    throw new InvalidDataException($"BMM {id} references missing mixture {mixtureId}.");
                emissionMixtureIds[state] = mixtureId;
            }
            bmmModels[bmmIndex] = new LegacyBmmModel(id, transitions, emissionMixtureIds);
        }
        reader.EnsureEnd();
        return new LegacyGaussianMixtureCollection(featureCount, mixtures, bmmModels);
    }
}

/// <summary>
/// A legacy left-to-right BMM. Transition row <c>i</c> stores probabilities to
/// states <c>i..N-1</c>; the first and last states are non-emitting.
/// </summary>
public sealed record LegacyBmmModel(int Id, IReadOnlyList<float[]> TransitionRows, IReadOnlyList<int> EmissionMixtureIds)
{
    public int StateCount => TransitionRows.Count;
    public float GetTransitionProbability(int fromState, int toState)
    {
        if (fromState < 0 || fromState >= StateCount || toState < fromState || toState >= StateCount)
            throw new ArgumentOutOfRangeException();
        return TransitionRows[fromState][toState - fromState];
    }
}

/// <summary>
/// Scores one already-segmented feature sequence against a recovered
/// left-to-right BMM. Start and end states are non-emitting; transition values
/// are probabilities from the legacy upper-triangular matrix.
/// </summary>
public static class LegacyBmmViterbiDecoder
{
    public static LegacyBmmViterbiResult Decode(
        LegacyGaussianMixtureCollection acousticModel,
        LegacyBmmModel model,
        IReadOnlyList<float[]> observations)
    {
        ArgumentNullException.ThrowIfNull(acousticModel);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(observations);
        if (observations.Count == 0) throw new ArgumentException("At least one observation is required.", nameof(observations));
        if (model.EmissionMixtureIds.Count != model.StateCount - 2)
            throw new ArgumentException("BMM must have exactly one mixture ID per emitting state.", nameof(model));
        if (observations.Any(observation => observation is null || observation.Length != acousticModel.FeatureCount))
            throw new ArgumentException($"Each observation must contain {acousticModel.FeatureCount} features.", nameof(observations));

        var emittingStateCount = model.StateCount - 2;
        var previous = Enumerable.Repeat(float.NegativeInfinity, emittingStateCount).ToArray();
        var current = new float[emittingStateCount];
        var backPointers = new int[observations.Count, emittingStateCount];
        for (var time = 0; time < observations.Count; time++)
        {
            for (var destination = 0; destination < emittingStateCount; destination++)
            {
                var destinationState = destination + 1;
                var bestScore = float.NegativeInfinity;
                var bestSource = -1;
                if (time == 0)
                {
                    var initialTransition = LogTransition(model.GetTransitionProbability(0, destinationState));
                    bestScore = initialTransition;
                }
                else
                {
                    for (var source = 0; source <= destination; source++)
                    {
                        var transition = LogTransition(model.GetTransitionProbability(source + 1, destinationState));
                        var candidate = previous[source] + transition;
                        if (candidate > bestScore)
                        {
                            bestScore = candidate;
                            bestSource = source;
                        }
                    }
                }

                var emission = acousticModel[model.EmissionMixtureIds[destination]].LogProbability(observations[time]);
                current[destination] = bestScore + emission;
                backPointers[time, destination] = bestSource;
            }
            (previous, current) = (current, previous);
        }

        var finalScore = float.NegativeInfinity;
        var finalState = -1;
        for (var source = 0; source < emittingStateCount; source++)
        {
            var candidate = previous[source] + LogTransition(model.GetTransitionProbability(source + 1, model.StateCount - 1));
            if (candidate > finalScore)
            {
                finalScore = candidate;
                finalState = source;
            }
        }
        if (finalState < 0 || float.IsNegativeInfinity(finalScore))
            return new LegacyBmmViterbiResult(float.NegativeInfinity, []);

        var path = new int[observations.Count];
        for (var time = observations.Count - 1; time >= 0; time--)
        {
            path[time] = finalState + 1;
            finalState = backPointers[time, finalState];
            if (time > 0 && finalState < 0) throw new InvalidOperationException("Viterbi path terminated before the first observation.");
            if (time == 0 && finalState != -1) throw new InvalidOperationException("Viterbi path did not originate at the start state.");
        }
        return new LegacyBmmViterbiResult(finalScore, path);
    }

    private static float LogTransition(float probability) => probability > 0f ? MathF.Log(probability) : float.NegativeInfinity;
}

public readonly record struct LegacyBmmViterbiResult(float LogScore, IReadOnlyList<int> EmittingStatePath);

/// <summary>
/// Scores an EBMM pronunciation graph by expanding each of its BMM states into
/// its non-emitting-start / emitting / non-emitting-end topology. This is the
/// composition used by the legacy dictionary: graph state zero is the global
/// start state and the final graph state is the global end state.
/// </summary>
public static class LegacyEbmmViterbiDecoder
{
    public static LegacyEbmmViterbiResult Decode(
        LegacyGaussianMixtureCollection acousticModel,
        LegacyCommandPronunciation pronunciation,
        IReadOnlyList<float[]> observations)
    {
        ArgumentNullException.ThrowIfNull(acousticModel);
        ArgumentNullException.ThrowIfNull(pronunciation);
        ArgumentNullException.ThrowIfNull(observations);
        if (observations.Count == 0) throw new ArgumentException("At least one observation is required.", nameof(observations));
        if (observations.Any(observation => observation is null || observation.Length != acousticModel.FeatureCount))
            throw new ArgumentException($"Each observation must contain {acousticModel.FeatureCount} features.", nameof(observations));

        ValidateGraph(acousticModel, pronunciation);
        var nodes = BuildNodes(acousticModel, pronunciation);
        var previous = Enumerable.Repeat(float.NegativeInfinity, nodes.Length).ToArray();
        var current = new float[nodes.Length];
        var backPointers = new int[observations.Count, nodes.Length];

        for (var time = 0; time < observations.Count; time++)
        {
            for (var destination = 0; destination < nodes.Length; destination++)
            {
                var destinationNode = nodes[destination];
                var bestScore = float.NegativeInfinity;
                var bestSource = -1;
                if (time == 0)
                {
                    var graphTransition = GetGraphTransition(pronunciation, 0, destinationNode.EbmmStateIndex);
                    var bmmTransition = LogTransition(destinationNode.Model.GetTransitionProbability(0, destinationNode.EmittingState));
                    bestScore = graphTransition + bmmTransition;
                }
                else
                {
                    for (var source = 0; source < nodes.Length; source++)
                    {
                        var transition = GetExpandedTransition(pronunciation, nodes[source], destinationNode);
                        var candidate = previous[source] + transition;
                        if (candidate > bestScore)
                        {
                            bestScore = candidate;
                            bestSource = source;
                        }
                    }
                }

                var emission = acousticModel[destinationNode.Model.EmissionMixtureIds[destinationNode.EmittingState - 1]]
                    .LogProbability(observations[time]);
                current[destination] = bestScore + emission;
                backPointers[time, destination] = bestSource;
            }
            (previous, current) = (current, previous);
        }

        var finalGraphState = pronunciation.States.Count - 1;
        var finalScore = float.NegativeInfinity;
        var finalNode = -1;
        for (var source = 0; source < nodes.Length; source++)
        {
            var node = nodes[source];
            var candidate = previous[source]
                + LogTransition(node.Model.GetTransitionProbability(node.EmittingState, node.Model.StateCount - 1))
                + GetGraphTransition(pronunciation, node.EbmmStateIndex, finalGraphState);
            if (candidate > finalScore)
            {
                finalScore = candidate;
                finalNode = source;
            }
        }
        if (finalNode < 0 || float.IsNegativeInfinity(finalScore))
            return new LegacyEbmmViterbiResult(float.NegativeInfinity, []);

        var path = new LegacyEbmmViterbiFrame[observations.Count];
        for (var time = observations.Count - 1; time >= 0; time--)
        {
            var node = nodes[finalNode];
            path[time] = new LegacyEbmmViterbiFrame(node.EbmmStateIndex, node.Model.Id, node.EmittingState);
            finalNode = backPointers[time, finalNode];
            if (time > 0 && finalNode < 0) throw new InvalidOperationException("Viterbi path terminated before the first observation.");
            if (time == 0 && finalNode != -1) throw new InvalidOperationException("Viterbi path did not originate at the global start state.");
        }
        return new LegacyEbmmViterbiResult(finalScore, path);
    }

    private static ExpandedNode[] BuildNodes(LegacyGaussianMixtureCollection acousticModel, LegacyCommandPronunciation pronunciation)
    {
        var nodes = new List<ExpandedNode>();
        for (var bmmIndex = 0; bmmIndex < pronunciation.BmmIds.Count; bmmIndex++)
        {
            var model = acousticModel.GetBmm(pronunciation.BmmIds[bmmIndex]);
            for (var emittingState = 1; emittingState < model.StateCount - 1; emittingState++)
                nodes.Add(new ExpandedNode(bmmIndex + 1, model, emittingState));
        }
        return nodes.ToArray();
    }

    private static float GetExpandedTransition(LegacyCommandPronunciation pronunciation, ExpandedNode source, ExpandedNode destination)
    {
        if (source.EbmmStateIndex == destination.EbmmStateIndex)
        {
            if (destination.EmittingState < source.EmittingState) return float.NegativeInfinity;
            return LogTransition(source.Model.GetTransitionProbability(source.EmittingState, destination.EmittingState));
        }
        if (source.EbmmStateIndex > destination.EbmmStateIndex) return float.NegativeInfinity;

        return LogTransition(source.Model.GetTransitionProbability(source.EmittingState, source.Model.StateCount - 1))
            + GetGraphTransition(pronunciation, source.EbmmStateIndex, destination.EbmmStateIndex)
            + LogTransition(destination.Model.GetTransitionProbability(0, destination.EmittingState));
    }

    private static float GetGraphTransition(LegacyCommandPronunciation pronunciation, int sourceState, int targetState)
    {
        var transition = pronunciation.States[sourceState].Transitions.FirstOrDefault(item => item.TargetState == targetState);
        return transition.TargetState == targetState ? LogTransition(transition.Probability) : float.NegativeInfinity;
    }

    private static void ValidateGraph(LegacyGaussianMixtureCollection acousticModel, LegacyCommandPronunciation pronunciation)
    {
        if (pronunciation.States.Count != pronunciation.BmmIds.Count + 2)
            throw new ArgumentException("EBMM state count must equal the BMM count plus start and end states.", nameof(pronunciation));
        if (pronunciation.States.Count < 3) throw new ArgumentException("EBMM needs start, end, and at least one BMM state.", nameof(pronunciation));
        for (var stateIndex = 0; stateIndex < pronunciation.States.Count; stateIndex++)
        {
            var state = pronunciation.States[stateIndex];
            if (state is null || state.Index != stateIndex) throw new ArgumentException("EBMM state indexes must be consecutive.", nameof(pronunciation));
            if (state.Transitions is null || state.Transitions.Any(transition =>
                    transition.TargetState <= stateIndex || transition.TargetState >= pronunciation.States.Count ||
                    transition.Probability < 0f || transition.Probability > 1f || !float.IsFinite(transition.Probability)))
                throw new ArgumentException("EBMM transitions must be finite left-to-right probabilities.", nameof(pronunciation));
        }
        foreach (var bmmId in pronunciation.BmmIds)
        {
            if (bmmId < 0 || bmmId >= acousticModel.BmmCount)
                throw new ArgumentException($"EBMM references missing BMM {bmmId}.", nameof(pronunciation));
        }
    }

    private static float LogTransition(float probability) => probability > 0f ? MathF.Log(probability) : float.NegativeInfinity;

    private sealed record ExpandedNode(int EbmmStateIndex, LegacyBmmModel Model, int EmittingState);
}

public readonly record struct LegacyEbmmViterbiResult(float LogScore, IReadOnlyList<LegacyEbmmViterbiFrame> FramePath);
public readonly record struct LegacyEbmmViterbiFrame(int EbmmStateIndex, int BmmId, int BmmEmittingState);

/// <summary>
/// Read-only comparison of every pronunciation in a legacy command dictionary.
/// Ranking is deliberately separate from command acceptance and all system I/O.
/// </summary>
public static class LegacyCommandScorer
{
    public static IReadOnlyList<LegacyCommandScore> ScoreAll(
        LegacyGaussianMixtureCollection acousticModel,
        LegacyCommandDictionary dictionary,
        IReadOnlyList<float[]> observations)
    {
        ArgumentNullException.ThrowIfNull(acousticModel);
        ArgumentNullException.ThrowIfNull(dictionary);
        ArgumentNullException.ThrowIfNull(observations);

        var scores = dictionary.Pronunciations
            .Select(pronunciation => new LegacyCommandScore(
                pronunciation,
                LegacyEbmmViterbiDecoder.Decode(acousticModel, pronunciation, observations)))
            .ToArray();

        // The legacy discrete recognizer uses this direct exp-and-divide
        // normalization rather than a numerically stabilized softmax.
        var unnormalized = scores.Select(score => MathF.Exp(score.Viterbi.LogScore)).ToArray();
        var total = unnormalized.Sum();
        for (var index = 0; index < scores.Length; index++)
            scores[index] = scores[index] with { RelativeProbability = total > 0f ? unnormalized[index] / total : 0f };
        return scores;
    }

    /// <summary>
    /// Applies the recovered legacy probability threshold to an already
    /// ranked result. The return value is diagnostic data only; it has no
    /// command-event or operating-system side effect.
    /// </summary>
    public static LegacyCommandAcceptance AcceptBest(IReadOnlyList<LegacyCommandScore> rankedScores, float probabilityThreshold)
    {
        ArgumentNullException.ThrowIfNull(rankedScores);
        if (!float.IsFinite(probabilityThreshold) || probabilityThreshold < 0f || probabilityThreshold > 1f)
            throw new ArgumentOutOfRangeException(nameof(probabilityThreshold), "Probability threshold must be in [0, 1].");
        // FUN_1003e4a0 scans hypotheses in dictionary order and updates its
        // selection only for a strictly greater probability. Preserve that
        // order so a tie remains assigned to the first legacy hypothesis.
        LegacyCommandScore? best = null;
        foreach (var score in rankedScores)
        {
            if (best is null || score.RelativeProbability > best.RelativeProbability)
                best = score;
        }
        return best is not null && best.RelativeProbability >= probabilityThreshold
            ? new LegacyCommandAcceptance(best, probabilityThreshold)
            : new LegacyCommandAcceptance(null, probabilityThreshold);
    }
}

public sealed record LegacyCommandScore(
    LegacyCommandPronunciation Pronunciation,
    LegacyEbmmViterbiResult Viterbi)
{
    public float RelativeProbability { get; init; }
    public float LogScorePerFrame => FrameCount == 0 ? float.NegativeInfinity : Viterbi.LogScore / FrameCount;
    public int FrameCount => Viterbi.FramePath.Count;
}

public sealed record LegacyCommandAcceptance(LegacyCommandScore? AcceptedScore, float ProbabilityThreshold)
{
    public bool IsAccepted => AcceptedScore is not null;
}

/// <summary>
/// Recovered discrete-sound boundary checks. The caller supplies speech start
/// and silence start events; this type intentionally does not invent VAD or
/// access a microphone. A completed segment can subsequently be scored by
/// <see cref="LegacyCommandScorer"/>.
/// </summary>
public sealed class LegacyDiscreteCommandSegmenter
{
    private readonly LegacyDiscreteSegmentationConfig _config;
    private readonly List<float[]> _features = [];
    private int _totalFrameCount;
    private long _zeroCrossingSum;
    private int _leadingUnvoicedFrameCount;
    private bool _hasVoicedFrame;

    public LegacyDiscreteCommandSegmenter(LegacyDiscreteSegmentationConfig? config = null)
    {
        _config = config ?? new LegacyDiscreteSegmentationConfig();
        _config.Validate();
    }

    public bool IsCollecting { get; private set; }

    public void StartOfSpeech()
    {
        _features.Clear();
        _totalFrameCount = 0;
        _zeroCrossingSum = 0;
        _leadingUnvoicedFrameCount = 0;
        _hasVoicedFrame = false;
        IsCollecting = true;
    }

    public void Push(LegacyDiscreteFeatureFrame frame)
    {
        if (!IsCollecting) throw new InvalidOperationException("A discrete segment must begin with StartOfSpeech.");
        if (frame.Features is null || frame.Features.Length != LegacyRecognitionPipelineFeatureCount)
            throw new ArgumentException($"A discrete frame must contain {LegacyRecognitionPipelineFeatureCount} features.", nameof(frame));
        if (frame.ZeroCrossings < 0) throw new ArgumentOutOfRangeException(nameof(frame), "Zero-crossing count must not be negative.");

        _totalFrameCount++;
        _zeroCrossingSum += frame.ZeroCrossings;
        if (!_hasVoicedFrame && !frame.IsVoiced) _leadingUnvoicedFrameCount++;
        if (frame.IsVoiced) _hasVoicedFrame = true;
        // The legacy maximum is an acceptance limit, not a collection limit.
        // Retaining only the bounded prefix prevents an invalid long utterance
        // from consuming unbounded memory while preserving the rejection result.
        if (_features.Count < _config.MaximumFrameCountExclusive)
            _features.Add((float[])frame.Features.Clone());
    }

    public LegacyDiscreteSegmentationResult StartOfSilence()
    {
        if (!IsCollecting) return new LegacyDiscreteSegmentationResult(null, LegacyDiscreteSegmentRejection.NotCollecting);
        IsCollecting = false;
        if (_totalFrameCount < _config.MinimumFrameCount)
            return new LegacyDiscreteSegmentationResult(null, LegacyDiscreteSegmentRejection.TooShort);
        if (_totalFrameCount >= _config.MaximumFrameCountExclusive)
            return new LegacyDiscreteSegmentationResult(null, LegacyDiscreteSegmentRejection.TooLong);

        var averageZeroCrossings = (float)_zeroCrossingSum / _totalFrameCount;
        if (averageZeroCrossings <= _config.MinimumAverageZeroCrossings)
            return new LegacyDiscreteSegmentationResult(null, LegacyDiscreteSegmentRejection.InsufficientZeroCrossings);
        if (_leadingUnvoicedFrameCount <= _config.MinimumInitialUnvoicedFrameCount)
            return new LegacyDiscreteSegmentationResult(null, LegacyDiscreteSegmentRejection.InsufficientInitialUnvoicedFrames);

        return new LegacyDiscreteSegmentationResult(
            new LegacyDiscreteSegment(_features.ToArray(), averageZeroCrossings, _leadingUnvoicedFrameCount),
            LegacyDiscreteSegmentRejection.None);
    }

    // Kept local to Commands so it does not take a dependency on the movement classifier pipeline.
    private const int LegacyRecognitionPipelineFeatureCount = 26;
}

public sealed record LegacyDiscreteSegmentationConfig(
    int MinimumFrameCount = 5,
    int MaximumFrameCountExclusive = 70,
    int MinimumInitialUnvoicedFrameCount = 1,
    float MinimumAverageZeroCrossings = .5f)
{
    internal void Validate()
    {
        if (MinimumFrameCount < 1 || MaximumFrameCountExclusive <= MinimumFrameCount)
            throw new ArgumentOutOfRangeException(nameof(MaximumFrameCountExclusive));
        if (MinimumInitialUnvoicedFrameCount < 0 || !float.IsFinite(MinimumAverageZeroCrossings) || MinimumAverageZeroCrossings < 0f)
            throw new ArgumentOutOfRangeException(nameof(MinimumInitialUnvoicedFrameCount));
    }
}

public readonly record struct LegacyDiscreteFeatureFrame(float[] Features, int ZeroCrossings, bool IsVoiced);
public sealed record LegacyDiscreteSegment(IReadOnlyList<float[]> Features, float AverageZeroCrossings, int InitialUnvoicedFrameCount);
public sealed record LegacyDiscreteSegmentationResult(LegacyDiscreteSegment? Segment, LegacyDiscreteSegmentRejection Rejection)
{
    public bool IsAccepted => Segment is not null;
}
public enum LegacyDiscreteSegmentRejection
{
    None,
    NotCollecting,
    TooShort,
    TooLong,
    InsufficientZeroCrossings,
    InsufficientInitialUnvoicedFrames
}

/// <summary>
/// Safe composition of the recovered discrete-command stages. Speech
/// boundaries remain an explicit input until the preceding VAD state machine
/// is fully ported. This class has no callbacks and no operating-system I/O.
/// </summary>
public sealed class LegacyDiscreteCommandRecognizer
{
    private readonly LegacyGaussianMixtureCollection _acousticModel;
    private readonly LegacyCommandDictionary _dictionary;
    private readonly LegacyDiscreteCommandSegmenter _segmenter;
    private readonly float _probabilityThreshold;

    public LegacyDiscreteCommandRecognizer(
        LegacyGaussianMixtureCollection acousticModel,
        LegacyCommandDictionary dictionary,
        LegacyDiscreteSegmentationConfig segmentation,
        float probabilityThreshold)
    {
        _acousticModel = acousticModel ?? throw new ArgumentNullException(nameof(acousticModel));
        _dictionary = dictionary ?? throw new ArgumentNullException(nameof(dictionary));
        if (!float.IsFinite(probabilityThreshold) || probabilityThreshold < 0f || probabilityThreshold > 1f)
            throw new ArgumentOutOfRangeException(nameof(probabilityThreshold));
        _segmenter = new LegacyDiscreteCommandSegmenter(segmentation);
        _probabilityThreshold = probabilityThreshold;
    }

    public static LegacyDiscreteCommandRecognizer Load(
        string acousticModelPath,
        string dictionaryPath,
        string parametersPath)
    {
        var parameters = LegacyDiscreteCommandParameters.Load(parametersPath);
        return new LegacyDiscreteCommandRecognizer(
            LegacyGaussianMixtureCollection.Load(acousticModelPath),
            LegacyCommandDictionary.Load(dictionaryPath),
            parameters.CreateSegmentationConfig(),
            parameters.ProbabilityThreshold);
    }

    public bool IsCollecting => _segmenter.IsCollecting;

    public void StartOfSpeech() => _segmenter.StartOfSpeech();
    public void Push(LegacyDiscreteFeatureFrame frame) => _segmenter.Push(frame);

    public LegacyDiscreteRecognitionResult StartOfSilence()
    {
        var segmentation = _segmenter.StartOfSilence();
        if (!segmentation.IsAccepted)
            return new LegacyDiscreteRecognitionResult(segmentation, [], null);

        var ranked = LegacyCommandScorer.ScoreAll(_acousticModel, _dictionary, segmentation.Segment!.Features);
        return new LegacyDiscreteRecognitionResult(
            segmentation,
            ranked,
            LegacyCommandScorer.AcceptBest(ranked, _probabilityThreshold));
    }
}

public sealed record LegacyDiscreteRecognitionResult(
    LegacyDiscreteSegmentationResult Segmentation,
    IReadOnlyList<LegacyCommandScore> RankedScores,
    LegacyCommandAcceptance? Acceptance)
{
    public bool IsAccepted => Acceptance?.IsAccepted == true;
}

/// <summary>Discrete-command settings read from the shared legacy profile.</summary>
public sealed record LegacyDiscreteCommandParameters(float ProbabilityThreshold, float MinimumAverageZeroCrossings)
{
    public const float LegacyDefaultProbabilityThreshold = .85f;
    public const float LegacyDefaultMinimumAverageZeroCrossings = .5f;

    public static LegacyDiscreteCommandParameters Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in File.ReadLines(path))
        {
            var parts = rawLine.Split('#', 2)[0]
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0].StartsWith("--", StringComparison.Ordinal)) values[parts[0][2..]] = parts[1];
        }
        return new LegacyDiscreteCommandParameters(
            ReadFinite(values, "pthresh", LegacyDefaultProbabilityThreshold, 0f, 1f),
            ReadFinite(values, "minzcross", LegacyDefaultMinimumAverageZeroCrossings, 0f, float.PositiveInfinity));
    }

    public LegacyDiscreteSegmentationConfig CreateSegmentationConfig() =>
        new(MinimumAverageZeroCrossings: MinimumAverageZeroCrossings);

    private static float ReadFinite(IReadOnlyDictionary<string, string> values, string name, float defaultValue, float minimum, float maximum)
    {
        if (!values.TryGetValue(name, out var text)) return defaultValue;
        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
            !float.IsFinite(value) || value < minimum || value > maximum)
            throw new InvalidDataException($"Invalid --{name} value.");
        return value;
    }
}

public sealed class LegacyGaussianMixture
{
    private readonly LegacyDiagonalGaussian[] _components;
    private readonly float[] _logWeights;

    internal LegacyGaussianMixture(LegacyDiagonalGaussian[] components, float[] logWeights)
    {
        _components = components;
        _logWeights = logWeights;
    }

    public int ComponentCount => _components.Length;

    public float LogProbability(ReadOnlySpan<float> observation)
    {
        var total = LegacyGaussianMixtureCollection.LogProbabilityFloor;
        for (var index = 0; index < _components.Length; index++)
            total = LogAdd(total, _logWeights[index] + _components[index].LogProbability(observation));
        return total;
    }

    private static float LogAdd(float left, float right)
    {
        if (left <= LegacyGaussianMixtureCollection.LogProbabilityFloor) return right;
        if (right <= LegacyGaussianMixtureCollection.LogProbabilityFloor) return left;
        var maximum = MathF.Max(left, right);
        var minimum = MathF.Min(left, right);
        return maximum + MathF.Log(1f + MathF.Exp(minimum - maximum));
    }
}

public sealed class LegacyDiagonalGaussian
{
    private readonly float[] _means;
    private readonly float[] _inverseVariances;
    private readonly float _logNormalizer;

    internal LegacyDiagonalGaussian(float[] means, float[] variances)
    {
        _means = means;
        _inverseVariances = new float[variances.Length];
        var logVarianceSum = 0f;
        for (var index = 0; index < variances.Length; index++)
        {
            if (!float.IsFinite(variances[index]) || variances[index] <= 0f)
                throw new InvalidDataException("Diagonal variances must be finite and positive.");
            _inverseVariances[index] = 1f / variances[index];
            logVarianceSum += MathF.Log(variances[index]);
        }
        _logNormalizer = -.5f * (_means.Length * MathF.Log(2f * MathF.PI) + logVarianceSum);
    }

    public float LogProbability(ReadOnlySpan<float> observation)
    {
        if (observation.Length != _means.Length)
            throw new ArgumentException($"Expected {_means.Length} features.", nameof(observation));
        var squaredMahalanobisDistance = 0f;
        for (var index = 0; index < observation.Length; index++)
        {
            if (!float.IsFinite(observation[index])) throw new ArgumentException("Observations must be finite.", nameof(observation));
            var difference = _means[index] - observation[index];
            squaredMahalanobisDistance += difference * difference * _inverseVariances[index];
        }
        return MathF.Max(LegacyGaussianMixtureCollection.LogProbabilityFloor,
            _logNormalizer - .5f * squaredMahalanobisDistance);
    }
}

/// <summary>Pronunciation paths from the legacy EBMM text dictionary.</summary>
public sealed record LegacyCommandDictionary(IReadOnlyList<LegacyCommandPronunciation> Pronunciations)
{
    public static LegacyCommandDictionary Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var reader = new DictionaryTokenReader(path);
        if (reader.ReadString("dictionary magic") != "EBMM_MPF") throw new InvalidDataException("Expected EBMM_MPF dictionary.");
        var count = reader.ReadInt("pronunciation count");
        if (count < 0) throw new InvalidDataException("Invalid pronunciation count.");
        var entries = new List<LegacyCommandPronunciation>(count);
        for (var entryIndex = 0; entryIndex < count; entryIndex++)
        {
            var id = reader.ReadInt("pronunciation ID");
            var orthography = reader.ReadString("orthography");
            var transcription = reader.ReadString("transcription");
            var stateCount = reader.ReadInt("EBMM state count");
            if (stateCount < 3) throw new InvalidDataException("An EBMM needs start, end, and at least one BMM state.");
            if (reader.ReadInt("EBMM start marker") != -1) throw new InvalidDataException("Expected EBMM start marker -1.");
            var bmmIds = new int[stateCount - 2];
            for (var bmmIndex = 0; bmmIndex < bmmIds.Length; bmmIndex++)
            {
                bmmIds[bmmIndex] = reader.ReadInt("EBMM BMM ID");
                if (bmmIds[bmmIndex] < 0) throw new InvalidDataException("EBMM BMM IDs must be non-negative.");
            }
            if (reader.ReadInt("EBMM end marker") != -2) throw new InvalidDataException("Expected EBMM end marker -2.");
            var states = new LegacyEbmmState[stateCount];
            for (var stateIndex = 0; stateIndex < stateCount; stateIndex++)
            {
                if (reader.ReadInt("EBMM state index") != stateIndex)
                    throw new InvalidDataException($"Expected EBMM state {stateIndex}.");
                var transitionCount = reader.ReadInt("EBMM transition count");
                if (transitionCount < 0 || transitionCount > stateCount - stateIndex - 1)
                    throw new InvalidDataException("Invalid EBMM transition count.");
                var transitions = new LegacyEbmmTransition[transitionCount];
                for (var transitionIndex = 0; transitionIndex < transitionCount; transitionIndex++)
                {
                    var target = reader.ReadInt("EBMM transition target");
                    if (target <= stateIndex || target >= stateCount)
                        throw new InvalidDataException("EBMM transitions must be left-to-right.");
                    var probability = reader.ReadFloat("EBMM transition probability");
                    if (probability < 0f || probability > 1f || !float.IsFinite(probability))
                        throw new InvalidDataException("EBMM transition probabilities must be in [0, 1].");
                    transitions[transitionIndex] = new LegacyEbmmTransition(target, probability);
                }
                states[stateIndex] = new LegacyEbmmState(stateIndex, transitions);
            }
            entries.Add(new LegacyCommandPronunciation(id, orthography, transcription, bmmIds, states));
        }
        reader.EnsureEnd();
        return new LegacyCommandDictionary(entries);
    }
}

public sealed record LegacyCommandPronunciation(
    int Id,
    string Orthography,
    string Transcription,
    IReadOnlyList<int> BmmIds,
    IReadOnlyList<LegacyEbmmState> States);

public sealed record LegacyEbmmState(int Index, IReadOnlyList<LegacyEbmmTransition> Transitions);
public readonly record struct LegacyEbmmTransition(int TargetState, float Probability);

internal sealed class DictionaryTokenReader
{
    private readonly string[] _tokens;
    private int _position;

    public DictionaryTokenReader(string path)
    {
        _tokens = File.ReadLines(path)
            .Select(line => line.Split('#', 2)[0])
            .SelectMany(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToArray();
    }

    public string ReadString(string description) =>
        _position < _tokens.Length ? _tokens[_position++] : throw new InvalidDataException($"Expected {description}.");

    public int ReadInt(string description)
    {
        var token = ReadString(description);
        if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw new InvalidDataException($"Expected {description}.");
        return value;
    }

    public float ReadFloat(string description)
    {
        var token = ReadString(description);
        if (!float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            throw new InvalidDataException($"Expected {description}.");
        return value;
    }

    public void EnsureEnd()
    {
        if (_position != _tokens.Length) throw new InvalidDataException("Unexpected extra command dictionary data.");
    }
}

internal sealed class NumberReader
{
    private readonly string[] _tokens;
    private int _position;

    public NumberReader(string path)
    {
        _tokens = File.ReadLines(path)
            .Select(line => line.Split('#', 2)[0])
            .SelectMany(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToArray();
    }

    public int ReadInt(string description)
    {
        if (_position >= _tokens.Length || !int.TryParse(_tokens[_position++], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw new InvalidDataException($"Expected {description}.");
        return value;
    }

    public float ReadFloat(string description)
    {
        if (_position >= _tokens.Length || !float.TryParse(_tokens[_position++], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !float.IsFinite(value))
            throw new InvalidDataException($"Expected finite {description}.");
        return value;
    }

    public float[] ReadVector(int count, string description)
    {
        var values = new float[count];
        for (var index = 0; index < count; index++) values[index] = ReadFloat(description);
        return values;
    }

    public void EnsureEnd()
    {
        if (_position != _tokens.Length) throw new InvalidDataException("Unexpected data after the final mixture.");
    }
}
