using System.Runtime.Versioning;

namespace VocalJoystick.Input;

public enum MouseButtonAction
{
    LeftClick,
    RightClick
}

/// <summary>Maps recognized legacy command IDs to user-facing actions.</summary>
public sealed class CommandActionMap
{
    private readonly IReadOnlyDictionary<int, MouseButtonAction> _actions;
    private readonly IReadOnlyDictionary<string, MouseButtonAction> _namedActions;

    public CommandActionMap(IReadOnlyDictionary<int, MouseButtonAction>? actions = null,
        IReadOnlyDictionary<string, MouseButtonAction>? namedActions = null)
    {
        _actions = actions is null
            ? new Dictionary<int, MouseButtonAction>
            {
                [0] = MouseButtonAction.LeftClick,
                [1] = MouseButtonAction.RightClick
            }
            : actions.ToDictionary(pair => pair.Key, pair => pair.Value);
        if (_actions.Keys.Any(id => id < 0)) throw new ArgumentOutOfRangeException(nameof(actions));
        if (_actions.Values.Any(action => !Enum.IsDefined(action))) throw new ArgumentOutOfRangeException(nameof(actions));
        _namedActions = namedActions is null
            ? new Dictionary<string, MouseButtonAction>(StringComparer.Ordinal)
            {
                ["left_click"] = MouseButtonAction.LeftClick,
                ["right_click"] = MouseButtonAction.RightClick
            }
            : namedActions.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        if (_namedActions.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || !Enum.IsDefined(pair.Value)))
            throw new ArgumentException("Named command actions require non-empty IDs and valid actions.", nameof(namedActions));
    }

    public bool TryGetAction(int commandId, out MouseButtonAction action) =>
        _actions.TryGetValue(commandId, out action);

    public bool TryGetAction(string actionId, out MouseButtonAction action) =>
        _namedActions.TryGetValue(actionId, out action);
}

public interface IMouseActionOutput
{
    void Click(MouseButtonAction action);
}

/// <summary>Null-object click output used by diagnostics and non-Windows hosts.</summary>
public sealed class DisabledMouseActionOutput : IMouseActionOutput
{
    public void Click(MouseButtonAction action) { }
}

/// <summary>Executes recognized commands behind the explicit cursor safety gate.</summary>
public sealed class GatedCommandActionRouter
{
    private readonly CursorSafetyGate _gate;
    private readonly CommandActionMap _map;
    private readonly IMouseActionOutput _output;

    public GatedCommandActionRouter(
        CursorSafetyGate gate,
        IMouseActionOutput? output = null,
        CommandActionMap? map = null)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _output = output ?? new DisabledMouseActionOutput();
        _map = map ?? new CommandActionMap();
    }

    public event EventHandler<CommandActionResult>? ActionDispatched;

    public CommandActionResult Dispatch(int commandId)
    {
        if (!_map.TryGetAction(commandId, out var action))
            return new CommandActionResult(commandId, null, false, false);

        var dispatched = _gate.TryExecute(() => _output.Click(action));
        var result = new CommandActionResult(commandId, action, dispatched, true);
        ActionDispatched?.Invoke(this, result);
        return result;
    }

    public CommandActionResult Dispatch(string actionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);
        if (!_map.TryGetAction(actionId, out var action))
            return new CommandActionResult(-1, null, false, false);
        var dispatched = _gate.TryExecute(() => _output.Click(action));
        var result = new CommandActionResult(-1, action, dispatched, true);
        ActionDispatched?.Invoke(this, result);
        return result;
    }
}

public readonly record struct CommandActionResult(
    int CommandId,
    MouseButtonAction? Action,
    bool Executed,
    bool IsMapped);

/// <summary>Windows mouse-button adapter. Call only from an explicitly enabled session.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsMouseActionOutput : IMouseActionOutput
{
    private const uint LeftDown = 0x0002;
    private const uint LeftUp = 0x0004;
    private const uint RightDown = 0x0008;
    private const uint RightUp = 0x0010;

    public WindowsMouseActionOutput()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Mouse-button output is available only on Windows.");
    }

    public void Click(MouseButtonAction action)
    {
        var (down, up) = action switch
        {
            MouseButtonAction.LeftClick => (LeftDown, LeftUp),
            MouseButtonAction.RightClick => (RightDown, RightUp),
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };

        try
        {
            mouse_event(down, 0, 0, 0, UIntPtr.Zero);
        }
        finally
        {
            // Release even if sending the down event throws.
            mouse_event(up, 0, 0, 0, UIntPtr.Zero);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
}
