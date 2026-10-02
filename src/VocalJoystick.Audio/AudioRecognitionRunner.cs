using VocalJoystick.Features;
using VocalJoystick.Commands;

namespace VocalJoystick.Audio;

/// <summary>Connects an audio source to the recovered feature and recognition pipeline.</summary>
public sealed class AudioRecognitionRunner
{
    private readonly IAudioSource _source;
    private readonly LegacyFeatureStream _features = new();
    private readonly LegacyFrameEnergyStream _energies = new();
    private readonly LegacyVoiceActivityDetector _voiceActivity;
    private readonly LegacyRecognitionPipeline _recognition;
    private readonly LegacyDiscreteCommandRecognizer? _commands;
    private readonly UserCommandSequenceRecognizer? _userCommands;
    private readonly FeatureNormalizer? _commandNormalizer;
    private readonly LegacyPitchVoicingStream? _pitchVoicing;
    private readonly LegacyVoiceActivityConfiguration _voiceActivityConfiguration;
    private readonly SortedDictionary<long, RecognitionResult> _pendingRecognitions = [];
    private readonly SortedDictionary<long, LegacyEnergyFrame> _pendingEnergies = [];
    private readonly SortedDictionary<long, LegacyEnergyFrame> _commandEnergies = [];
    private readonly SortedDictionary<long, LegacyVoiceActivityResult> _commandVoiceActivity = [];
    private readonly SortedDictionary<long, LegacyFeatureFrame> _commandFeatures = [];
    private readonly SortedDictionary<long, LegacyPitchVoicingFrame> _commandVoicing = [];
    private readonly Queue<LegacyDiscreteFeatureFrame> _commandTail = [];

    public AudioRecognitionRunner(
        IAudioSource source,
        LegacyRecognitionPipeline recognition,
        LegacyVoiceActivityConfiguration? voiceActivityConfiguration = null,
        LegacyDiscreteCommandRecognizer? commandRecognizer = null,
        UserCommandSequenceRecognizer? userCommandRecognizer = null,
        FeatureNormalizer? commandFeatureNormalizer = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _recognition = recognition ?? throw new ArgumentNullException(nameof(recognition));
        _voiceActivityConfiguration = voiceActivityConfiguration ?? new LegacyVoiceActivityConfiguration();
        _voiceActivity = new LegacyVoiceActivityDetector(_voiceActivityConfiguration);
        _commands = commandRecognizer;
        _userCommands = userCommandRecognizer;
        _commandNormalizer = commandFeatureNormalizer;
        if (_userCommands is not null && _commandNormalizer is null)
            throw new ArgumentException("User command recognition requires the same feature normalizer used for enrollment.", nameof(commandFeatureNormalizer));
        _pitchVoicing = commandRecognizer is null ? null : new LegacyPitchVoicingStream();
        if (_source.Format != new AudioFormat(LegacyFeatureStream.SampleRate, 1))
            throw new ArgumentException("Audio source must provide mono 16 kHz PCM.", nameof(source));
    }

    public event EventHandler<RecognitionResult>? RecognitionProduced;
    /// <summary>Raised only when model scores and the matching legacy RMS frame share a timestamp.</summary>
    public event EventHandler<RecognitionWithEnergyResult>? RecognitionWithEnergyProduced;
    /// <summary>Raw microphone chunks from the configured mono 16 kHz stream, for opt-in diagnostics or enrollment recording.</summary>
    public event EventHandler<AudioChunk>? AudioChunkProduced;
    /// <summary>
    /// Emits recovered voice/silence transitions for diagnostics. This event
    /// does not initiate command recognition or operating-system input.
    /// </summary>
    public event EventHandler<LegacyVoiceActivityResult>? VoiceActivityProduced;
    public event EventHandler<DiscreteCommandProducedEventArgs>? DiscreteCommandProduced;
    public event EventHandler<LegacyCommandCandidateProducedEventArgs>? LegacyCommandCandidateProduced;
    public event EventHandler<CommandCandidateProducedEventArgs>? CommandCandidateProduced;
    public event EventHandler<CommandFeatureFrameEventArgs>? CommandFeatureProduced;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await _source.StartAsync(cancellationToken);
        await foreach (var chunk in _source.ReadAllAsync(cancellationToken))
        {
            AudioChunkProduced?.Invoke(this, chunk);
            foreach (var energy in _energies.Push(chunk.Samples))
            {
                var activity = _voiceActivity.Push(energy) with
                {
                    RootMeanSquareEnergy = energy.RootMeanSquare,
                    ZeroCrossings = energy.ZeroCrossings
                };
                VoiceActivityProduced?.Invoke(this, activity);
                _pendingEnergies[energy.StartSample] = energy;
                if (_commands is not null || _userCommands is not null)
                {
                    _commandEnergies[energy.StartSample] = energy;
                    _commandVoiceActivity[energy.StartSample] = activity;
                }
                TryProduceJoinedResult(energy.StartSample);
            }
            foreach (var frame in _features.Push(chunk.Samples))
            {
                if (_recognition.TryClassify(frame.Features, out var scores, out var contextFeatures))
                {
                    var recognition = new RecognitionResult(frame.StartSample, scores) { ContextFeatures = contextFeatures };
                    RecognitionProduced?.Invoke(this, recognition);
                    _pendingRecognitions[recognition.StartSample] = recognition;
                    TryProduceJoinedResult(recognition.StartSample);
                }
                if (_commands is not null || _userCommands is not null) _commandFeatures[frame.StartSample] = frame;
            }
            if (_pitchVoicing is not null)
            {
                foreach (var frame in _pitchVoicing.Push(chunk.Samples))
                    _commandVoicing[frame.StartSample] = frame;
            }
            if (_commands is not null || _userCommands is not null) DrainDiscreteCommands();
            TrimPending();
        }
    }

    private void DrainDiscreteCommands()
    {
        if (_commands is null && _userCommands is null) return;
        foreach (var startSample in _commandFeatures.Keys.ToArray())
        {
            if (!_commandEnergies.TryGetValue(startSample, out var energy) ||
                !_commandVoiceActivity.TryGetValue(startSample, out var activity)) break;
            LegacyPitchVoicingFrame? voicing = null;
            if (_commands is not null)
            {
                if (!_commandVoicing.TryGetValue(startSample, out var legacyVoicing)) break;
                voicing = legacyVoicing;
            }

            var feature = _commandFeatures[startSample];
            _commandEnergies.Remove(startSample);
            _commandVoiceActivity.Remove(startSample);
            _commandVoicing.Remove(startSample);
            _commandFeatures.Remove(startSample);

            CommandFeatureProduced?.Invoke(this,
                new CommandFeatureFrameEventArgs(startSample, feature.Features, activity));
            if (_userCommands is not null)
            {
                var normalized = new float[UserCommandSequenceModel.FeatureCount];
                _commandNormalizer!.Normalize(feature.Features, normalized);
                _userCommands.Push(normalized, activity.StartedSpeech, activity.IsSpeech, activity.StartedSilence);
            }

            if (activity.StartedSpeech)
            {
                _commandTail.Clear();
                _commands?.StartOfSpeech();
            }

            if (activity.StartedSilence)
            {
                // VAD holds its speech state for the configured minimum-silence
                // run. Retain that tail until the boundary is known, then omit it.
                _commandTail.Clear();
                if (_commands is not null)
                {
                    var command = _commands.StartOfSilence();
                    LegacyCommandCandidateProduced?.Invoke(this,
                        new LegacyCommandCandidateProducedEventArgs(startSample, command));
                    if (command.Acceptance?.AcceptedScore is { } accepted)
                        DiscreteCommandProduced?.Invoke(this,
                            new DiscreteCommandProducedEventArgs(startSample, accepted.Pronunciation.Id,
                                accepted.Pronunciation.Orthography, accepted.RelativeProbability));
                }
                if (_userCommands is not null)
                {
                    var decision = _userCommands.EndOfSpeech();
                    CommandCandidateProduced?.Invoke(this, new CommandCandidateProducedEventArgs(startSample, decision));
                    if (decision.IsAccepted)
                        DiscreteCommandProduced?.Invoke(this,
                            new DiscreteCommandProducedEventArgs(startSample, decision.ClassId!.Value,
                                decision.Label!, float.NaN, decision.ActionId));
                }
                continue;
            }

            if (_commands is null || !_commands.IsCollecting || !activity.IsSpeech || voicing is null) continue;
            _commandTail.Enqueue(new LegacyDiscreteFeatureFrame(feature.Features,
                energy.ZeroCrossings, voicing.Value.IsVoiced));
            if (_commandTail.Count > _voiceActivityConfiguration.MinimumSilenceFrameCount)
                _commands.Push(_commandTail.Dequeue());
        }

        TrimCommandPending(_commandEnergies);
        TrimCommandPending(_commandVoiceActivity);
        TrimCommandPending(_commandFeatures);
        TrimCommandPending(_commandVoicing);
    }

    private static void TrimCommandPending<T>(SortedDictionary<long, T> pending)
    {
        const int maximumPendingFrames = 64;
        while (pending.Count > maximumPendingFrames) pending.Remove(pending.First().Key);
    }

    private void TryProduceJoinedResult(long startSample)
    {
        if (!_pendingRecognitions.TryGetValue(startSample, out var recognition) ||
            !_pendingEnergies.TryGetValue(startSample, out var energy)) return;
        _pendingRecognitions.Remove(startSample);
        _pendingEnergies.Remove(startSample);
        RecognitionWithEnergyProduced?.Invoke(this,
            new RecognitionWithEnergyResult(startSample, energy.RootMeanSquare, energy.ZeroCrossings, recognition.Scores)
            { ContextFeatures = recognition.ContextFeatures });
    }

    private void TrimPending()
    {
        const int maximumPendingFrames = 64;
        while (_pendingRecognitions.Count > maximumPendingFrames)
            _pendingRecognitions.Remove(_pendingRecognitions.First().Key);
        while (_pendingEnergies.Count > maximumPendingFrames)
            _pendingEnergies.Remove(_pendingEnergies.First().Key);
    }
}

public sealed record DiscreteCommandProducedEventArgs(
    long StartSample,
    int CommandId,
    string Label,
    float Probability,
    string? ActionId = null);

public sealed record CommandCandidateProducedEventArgs(long StartSample, CommandSequenceDecision Decision);
public sealed record LegacyCommandCandidateProducedEventArgs(long StartSample, LegacyDiscreteRecognitionResult Result);
public sealed record CommandFeatureFrameEventArgs(long StartSample, float[] Features, LegacyVoiceActivityResult Activity);

public readonly record struct RecognitionResult(long StartSample, float[] Scores)
{
    public float[] ContextFeatures { get; init; } = Array.Empty<float>();
}
public readonly record struct RecognitionWithEnergyResult(
    long StartSample, float RootMeanSquareEnergy, int ZeroCrossings, float[] Scores)
{
    public float[]? ContextFeatures { get; init; }
}
