using VocalJoystick.Audio;
using VocalJoystick.Motion;

namespace VocalJoystick.Input;

/// <summary>
/// The deliberately explicit bridge from recognised audio to cursor deltas.
/// It can calculate every result while paused, but applies a delta only when
/// its caller has explicitly opened <see cref="CursorSafetyGate"/>.
/// </summary>
public sealed class VoiceCursorCoordinator
{
    private readonly LegacyMotionController _motion;
    private readonly MotionDeltaQuantizer _quantizer;
    private readonly CursorSafetyGate _gate;
    private readonly ICursorOutput _output;
    private readonly GatedCommandActionRouter _commandActions;

    public VoiceCursorCoordinator(
        LegacyMotionController motion,
        MotionDeltaQuantizer quantizer,
        CursorSafetyGate gate,
        ICursorOutput? output = null,
        IMouseActionOutput? mouseActionOutput = null,
        CommandActionMap? commandActionMap = null)
    {
        _motion = motion ?? throw new ArgumentNullException(nameof(motion));
        _quantizer = quantizer ?? throw new ArgumentNullException(nameof(quantizer));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _output = output ?? new DisabledCursorOutput();
        _commandActions = new GatedCommandActionRouter(_gate, mouseActionOutput, commandActionMap);
    }

    public event EventHandler<CursorMotionResult>? MotionCalculated;
    public bool IsInputEnabled => _gate.IsEnabled;
    public event EventHandler<CommandActionResult>? CommandActionDispatched
    {
        add => _commandActions.ActionDispatched += value;
        remove => _commandActions.ActionDispatched -= value;
    }

    /// <summary>Routes an accepted discrete-command ID through the shared safety gate.</summary>
    public CommandActionResult ProcessDiscreteCommand(int commandId) => _commandActions.Dispatch(commandId);

    /// <summary>Routes a personalized command by its persisted action ID.</summary>
    public CommandActionResult ProcessCommandAction(string actionId) => _commandActions.Dispatch(actionId);

    public CursorMotionResult Process(RecognitionWithEnergyResult recognition)
    {
        var motion = _motion.Update(recognition.RootMeanSquareEnergy, recognition.Scores);
        var delta = _quantizer.Quantize(motion);
        var applied = _gate.TryMove(_output, delta);
        var result = new CursorMotionResult(recognition.StartSample, recognition.RootMeanSquareEnergy,
            recognition.ZeroCrossings, motion, delta, applied);
        MotionCalculated?.Invoke(this, result);
        return result;
    }

    public void Pause()
    {
        _gate.Pause();
        _quantizer.Reset();
    }

    public void EmergencyStop()
    {
        _gate.EmergencyStop();
        _quantizer.Reset();
    }
}

public readonly record struct CursorMotionResult(
    long StartSample,
    float RootMeanSquareEnergy,
    int ZeroCrossings,
    LegacyMotionSample Motion,
    CursorDelta Delta,
    bool AppliedToCursor);
