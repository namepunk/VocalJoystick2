using System.Text.Json;

namespace VocalJoystick.Commands;

/// <summary>A stable acoustic class ID decoupled from the action it triggers.</summary>
public sealed record CommandClassDefinition(int Id, string Label, string ActionId);

/// <summary>A labeled utterance stored as normalized 26-value feature frames.</summary>
public sealed record CommandTrainingExample(int ClassId, float[][] Frames, string? WavPath = null);

public enum CommandRejectionReason
{
    None,
    NoSpeechSegment,
    TooShort,
    TooLong,
    DistanceTooLarge,
    Ambiguous,
    NoValidAlignment
}

public sealed record CommandSequenceDecision(
    int? ClassId,
    string? Label,
    string? ActionId,
    float BestDistance,
    float SecondBestDistance,
    float AcceptanceThreshold,
    CommandRejectionReason Rejection)
{
    public bool IsAccepted => ClassId.HasValue && Rejection == CommandRejectionReason.None;
}

/// <summary>
/// User-specific sequence-template recognizer for short discrete vocal sounds.
/// It uses constrained dynamic time warping to tolerate speaking-rate changes,
/// then rejects distant or ambiguous segments instead of forcing a class.
/// </summary>
public sealed class UserCommandSequenceModel
{
    public const int FeatureCount = 26;
    public const int MinimumExamplesPerClass = 3;
    public const int MinimumFrameCount = 3;
    public const int MaximumFrameCount = 200;
    public const float MinimumThresholdMultiplier = .1f;
    public const float MaximumThresholdMultiplier = 10f;
    private const float AcceptanceDistanceFloor = .05f;
    private const float AmbiguityMarginFloor = .08f;
    private const float AmbiguityMarginRatio = .15f;

    private readonly CommandClassDefinition[] _classes;
    private readonly Dictionary<int, float[][][]> _templates;
    private readonly Dictionary<int, float> _thresholds;

    private UserCommandSequenceModel(
        CommandClassDefinition[] classes,
        Dictionary<int, float[][][]> templates,
        Dictionary<int, float> thresholds)
    {
        _classes = classes;
        _templates = templates;
        _thresholds = thresholds;
    }

    public IReadOnlyList<CommandClassDefinition> Classes => _classes;

    public static UserCommandSequenceModel Train(
        IReadOnlyList<CommandClassDefinition> classes,
        IReadOnlyList<CommandTrainingExample> examples)
    {
        ValidateCatalog(classes);
        ArgumentNullException.ThrowIfNull(examples);
        var classArray = classes.OrderBy(item => item.Id).ToArray();
        var templates = new Dictionary<int, float[][][]>();
        var thresholds = new Dictionary<int, float>();

        foreach (var definition in classArray)
        {
            var classExamples = examples.Where(example => example.ClassId == definition.Id).ToArray();
            if (classExamples.Length < MinimumExamplesPerClass)
                throw new InvalidOperationException($"Command '{definition.Label}' requires at least {MinimumExamplesPerClass} samples.");
            foreach (var example in classExamples) ValidateSample(example.Frames);
            templates[definition.Id] = classExamples.Select(example => Clone(example.Frames)).ToArray();

            var leaveOneOutDistances = new List<float>();
            for (var sampleIndex = 0; sampleIndex < classExamples.Length; sampleIndex++)
            {
                var nearest = float.PositiveInfinity;
                for (var otherIndex = 0; otherIndex < classExamples.Length; otherIndex++)
                {
                    if (sampleIndex == otherIndex) continue;
                    nearest = MathF.Min(nearest, DynamicTimeWarping.Distance(
                        classExamples[sampleIndex].Frames, classExamples[otherIndex].Frames));
                }
                leaveOneOutDistances.Add(nearest);
            }

            if (leaveOneOutDistances.Any(distance => !float.IsFinite(distance)))
                throw new InvalidOperationException($"Command '{definition.Label}' samples could not be aligned. Record samples with audible speech and silence boundaries.");

            // Calibrate to within-class variability, with a small floor so
            // identical enrollment repetitions still have a usable threshold.
            var mean = leaveOneOutDistances.Average();
            var variance = leaveOneOutDistances.Select(distance => (distance - mean) * (distance - mean)).Average();
            thresholds[definition.Id] = MathF.Max(AcceptanceDistanceFloor, mean + 2f * MathF.Sqrt(variance));
        }

        if (examples.Any(example => !templates.ContainsKey(example.ClassId)))
            throw new ArgumentException("Training data contains a class absent from the command catalog.", nameof(examples));
        return new UserCommandSequenceModel(classArray, templates, thresholds);
    }

    public CommandSequenceDecision Classify(IReadOnlyList<float[]> frames, float thresholdMultiplier = 1f)
    {
        if (!float.IsFinite(thresholdMultiplier) || thresholdMultiplier < MinimumThresholdMultiplier ||
            thresholdMultiplier > MaximumThresholdMultiplier)
            throw new ArgumentOutOfRangeException(nameof(thresholdMultiplier),
                $"Threshold multiplier must be between {MinimumThresholdMultiplier} and {MaximumThresholdMultiplier}.");
        if (frames is null || frames.Count == 0)
            return Rejected(CommandRejectionReason.NoSpeechSegment);
        if (frames.Count < MinimumFrameCount) return Rejected(CommandRejectionReason.TooShort);
        if (frames.Count > MaximumFrameCount) return Rejected(CommandRejectionReason.TooLong);
        ValidateSample(frames);

        var ranked = new List<(CommandClassDefinition Class, float Distance)>();
        foreach (var definition in _classes)
        {
            var bestForClass = _templates[definition.Id]
                .Min(template => DynamicTimeWarping.Distance(frames, template));
            ranked.Add((definition, bestForClass));
        }
        ranked.Sort((left, right) => left.Distance.CompareTo(right.Distance));
        var best = ranked[0];
        var secondDistance = ranked.Count > 1 ? ranked[1].Distance : float.PositiveInfinity;
        if (!float.IsFinite(best.Distance)) return Rejected(CommandRejectionReason.NoValidAlignment);
        var threshold = _thresholds[best.Class.Id] * thresholdMultiplier;
        if (best.Distance > threshold)
            return new CommandSequenceDecision(null, best.Class.Label, best.Class.ActionId,
                best.Distance, secondDistance, threshold, CommandRejectionReason.DistanceTooLarge);

        var requiredMargin = MathF.Max(AmbiguityMarginFloor, best.Distance * AmbiguityMarginRatio);
        if (secondDistance - best.Distance < requiredMargin)
            return new CommandSequenceDecision(null, best.Class.Label, best.Class.ActionId,
                best.Distance, secondDistance, threshold, CommandRejectionReason.Ambiguous);

        return new CommandSequenceDecision(best.Class.Id, best.Class.Label, best.Class.ActionId,
            best.Distance, secondDistance, threshold, CommandRejectionReason.None);
    }

    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var payload = new PersistedModel(_classes,
            _templates.Select(pair => new PersistedClass(pair.Key, pair.Value, _thresholds[pair.Key])).ToArray());
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporaryPath = fullPath + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(payload));
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public static UserCommandSequenceModel Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var payload = JsonSerializer.Deserialize<PersistedModel>(File.ReadAllText(path))
            ?? throw new InvalidDataException("The user command model is empty.");
        ValidateCatalog(payload.Classes);
        var templates = new Dictionary<int, float[][][]>();
        var thresholds = new Dictionary<int, float>();
        foreach (var item in payload.Models)
        {
            if (!payload.Classes.Any(definition => definition.Id == item.ClassId) || item.Templates.Length < MinimumExamplesPerClass ||
                !float.IsFinite(item.AcceptanceThreshold) || item.AcceptanceThreshold <= 0f)
                throw new InvalidDataException("The user command model contains invalid class data.");
            foreach (var sequence in item.Templates) ValidateSample(sequence);
            if (!templates.TryAdd(item.ClassId, item.Templates) || !thresholds.TryAdd(item.ClassId, item.AcceptanceThreshold))
                throw new InvalidDataException("The user command model contains duplicate class IDs.");
        }
        if (payload.Classes.Any(definition => !templates.ContainsKey(definition.Id)))
            throw new InvalidDataException("The user command model is missing a class template.");
        return new UserCommandSequenceModel(payload.Classes.OrderBy(item => item.Id).ToArray(), templates, thresholds);
    }

    private CommandSequenceDecision Rejected(CommandRejectionReason reason) =>
        new(null, null, null, float.PositiveInfinity, float.PositiveInfinity, 0f, reason);

    internal static void ValidateCatalog(IReadOnlyList<CommandClassDefinition> classes)
    {
        ArgumentNullException.ThrowIfNull(classes);
        if (classes.Count < 2) throw new ArgumentException("At least two command classes are required.", nameof(classes));
        if (classes.Any(item => item is null || item.Id < 0 || string.IsNullOrWhiteSpace(item.Label) || string.IsNullOrWhiteSpace(item.ActionId)) ||
            classes.Select(item => item.Id).Distinct().Count() != classes.Count)
            throw new ArgumentException("Command classes need unique non-negative IDs, labels, and action IDs.", nameof(classes));
    }

    internal static void ValidateSample(IReadOnlyList<float[]> frames)
    {
        if (frames is null || frames.Count < MinimumFrameCount || frames.Count > MaximumFrameCount)
            throw new ArgumentException($"A command sample must contain {MinimumFrameCount}..{MaximumFrameCount} frames.", nameof(frames));
        foreach (var frame in frames)
            if (frame is null || frame.Length != FeatureCount || frame.Any(value => !float.IsFinite(value)))
                throw new ArgumentException($"Each command frame must contain {FeatureCount} finite features.", nameof(frames));
    }

    private static float[][] Clone(IReadOnlyList<float[]> frames) => frames.Select(frame => (float[])frame.Clone()).ToArray();

    private sealed record PersistedModel(CommandClassDefinition[] Classes, PersistedClass[] Models);
    private sealed record PersistedClass(int ClassId, float[][][] Templates, float AcceptanceThreshold);
}

/// <summary>Collects feature frames between the shared VAD speech/silence boundaries.</summary>
public sealed class UserCommandSequenceRecognizer
{
    private readonly UserCommandSequenceModel _model;
    private readonly float _thresholdMultiplier;
    private readonly List<float[]> _frames = [];
    private bool _collecting;

    public UserCommandSequenceRecognizer(UserCommandSequenceModel model, float thresholdMultiplier = 1f)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        if (!float.IsFinite(thresholdMultiplier) || thresholdMultiplier < UserCommandSequenceModel.MinimumThresholdMultiplier ||
            thresholdMultiplier > UserCommandSequenceModel.MaximumThresholdMultiplier)
            throw new ArgumentOutOfRangeException(nameof(thresholdMultiplier));
        _thresholdMultiplier = thresholdMultiplier;
    }

    public void Push(float[] features, bool startedSpeech, bool isSpeech, bool startedSilence)
    {
        if (startedSpeech)
        {
            _frames.Clear();
            _collecting = true;
        }
        if (_collecting && isSpeech) _frames.Add((float[])features.Clone());
        if (startedSilence) _collecting = false;
    }

    public CommandSequenceDecision EndOfSpeech()
    {
        var decision = _model.Classify(_frames, _thresholdMultiplier);
        _frames.Clear();
        _collecting = false;
        return decision;
    }
}

/// <summary>Persistent labeled enrollment corpus used to rebuild the command model after restart.</summary>
public sealed class CommandTrainingStore
{
    public const int MinimumExamplesPerClass = UserCommandSequenceModel.MinimumExamplesPerClass;
    private static readonly CommandClassDefinition[] InitialClasses =
    [
        new(0, "К", "left_click"),
        new(1, "Ч", "right_click")
    ];

    private readonly string _manifestPath;
    private readonly object _sync = new();
    private List<CommandClassDefinition> _classes;
    private List<StoredExample> _examples;

    public CommandTrainingStore(string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        _manifestPath = Path.GetFullPath(manifestPath);
        if (!File.Exists(_manifestPath))
        {
            _classes = InitialClasses.ToList();
            _examples = [];
            return;
        }

        var payload = JsonSerializer.Deserialize<StoredCorpus>(File.ReadAllText(_manifestPath))
            ?? throw new InvalidDataException("The command training manifest is empty.");
        UserCommandSequenceModel.ValidateCatalog(payload.Classes);
        _classes = payload.Classes.OrderBy(item => item.Id).ToList();
        _examples = payload.Examples.ToList();
        if (_examples.Any(example => !_classes.Any(item => item.Id == example.ClassId)))
            throw new InvalidDataException("The command training manifest references an unknown class.");
        foreach (var example in _examples) UserCommandSequenceModel.ValidateSample(example.Frames);
    }

    public IReadOnlyList<CommandClassDefinition> Classes
    {
        get { lock (_sync) return _classes.ToArray(); }
    }

    public IReadOnlyList<CommandTrainingExample> Examples
    {
        get
        {
            lock (_sync)
                return _examples.Select(example => new CommandTrainingExample(example.ClassId,
                    example.Frames.Select(frame => (float[])frame.Clone()).ToArray(), example.WavPath)).ToArray();
        }
    }

    public IReadOnlyDictionary<int, int> Counts
    {
        get
        {
            lock (_sync) return _classes.ToDictionary(item => item.Id,
                item => _examples.Count(example => example.ClassId == item.Id));
        }
    }

    public void AddClass(CommandClassDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        lock (_sync)
        {
            if (_classes.Any(item => item.Id == definition.Id ||
                string.Equals(item.Label, definition.Label, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Command IDs and labels must be unique.");
            var original = _classes;
            _classes = _classes.Append(definition).OrderBy(item => item.Id).ToList();
            try
            {
                UserCommandSequenceModel.ValidateCatalog(_classes);
                Persist();
            }
            catch
            {
                _classes = original;
                throw;
            }
        }
    }

    public void AddExample(int classId, IReadOnlyList<float[]> frames, string? wavPath)
    {
        UserCommandSequenceModel.ValidateSample(frames);
        lock (_sync)
        {
            if (!_classes.Any(item => item.Id == classId)) throw new ArgumentOutOfRangeException(nameof(classId));
            var storedPath = wavPath is null ? null : Path.GetRelativePath(Path.GetDirectoryName(_manifestPath)!, Path.GetFullPath(wavPath));
            _examples.Add(new StoredExample(classId, frames.Select(frame => (float[])frame.Clone()).ToArray(), storedPath));
            try { Persist(); }
            catch
            {
                _examples.RemoveAt(_examples.Count - 1);
                throw;
            }
        }
    }

    public UserCommandSequenceModel Train() => UserCommandSequenceModel.Train(Classes, Examples);

    public string? ResolveWavePath(CommandTrainingExample example) => example.WavPath is null
        ? null
        : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(_manifestPath)!, example.WavPath));

    private void Persist()
    {
        var directory = Path.GetDirectoryName(_manifestPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = _manifestPath + ".tmp";
        var payload = new StoredCorpus(_classes.ToArray(), _examples.ToArray());
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(payload));
            File.Move(temporaryPath, _manifestPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private sealed record StoredCorpus(CommandClassDefinition[] Classes, StoredExample[] Examples);
    private sealed record StoredExample(int ClassId, float[][] Frames, string? WavPath);
}

internal static class DynamicTimeWarping
{
    public static float Distance(IReadOnlyList<float[]> left, IReadOnlyList<float[]> right)
    {
        var n = left.Count;
        var m = right.Count;
        var band = Math.Max(3, Math.Max(n, m) / 4);
        var previous = Enumerable.Repeat(float.PositiveInfinity, m + 1).ToArray();
        var current = new float[m + 1];
        previous[0] = 0f;
        for (var i = 1; i <= n; i++)
        {
            Array.Fill(current, float.PositiveInfinity);
            // Center the Sakoe-Chiba band on the linear mapping between the
            // two sequence lengths. A band around i itself has no valid path
            // when utterances differ substantially in duration.
            var center = 1d + (double)(i - 1) * (m - 1) / (n - 1);
            var lower = Math.Max(1, (int)Math.Floor(center - band));
            var upper = Math.Min(m, (int)Math.Ceiling(center + band));
            for (var j = lower; j <= upper; j++)
            {
                var cost = 0f;
                for (var feature = 0; feature < UserCommandSequenceModel.FeatureCount; feature++)
                {
                    var difference = left[i - 1][feature] - right[j - 1][feature];
                    cost += difference * difference;
                }
                cost /= UserCommandSequenceModel.FeatureCount;
                current[j] = cost + MathF.Min(previous[j], MathF.Min(current[j - 1], previous[j - 1]));
            }
            (previous, current) = (current, previous);
        }
        return previous[m] / Math.Max(n, m);
    }
}
