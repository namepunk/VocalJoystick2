using System.Runtime.Versioning;
using VocalJoystick.Motion;

namespace VocalJoystick.Input;

/// <summary>Integer desktop-pixel delta. It carries no click or drag action.</summary>
public readonly record struct CursorDelta(int X, int Y)
{
    public bool IsZero => X == 0 && Y == 0;
}

/// <summary>Low-level cursor output, kept separate from recognition and motion policy.</summary>
public interface ICursorOutput
{
    void MoveBy(CursorDelta delta);
}

/// <summary>Default output for every new runtime: intentionally does nothing.</summary>
public sealed class DisabledCursorOutput : ICursorOutput
{
    public void MoveBy(CursorDelta delta) { }
}

/// <summary>
/// Converts a floating-point motion record to whole pixels while preserving
/// subpixel residuals. The scale is a modern user setting, not an asserted
/// legacy pixel-conversion formula.
/// </summary>
public sealed class MotionDeltaQuantizer
{
    private readonly float _pixelsPerVelocityUnit;
    private float _residualX;
    private float _residualY;

    public MotionDeltaQuantizer(float pixelsPerVelocityUnit)
    {
        if (!float.IsFinite(pixelsPerVelocityUnit) || pixelsPerVelocityUnit <= 0f)
            throw new ArgumentOutOfRangeException(nameof(pixelsPerVelocityUnit));
        _pixelsPerVelocityUnit = pixelsPerVelocityUnit;
    }

    public CursorDelta Quantize(LegacyMotionSample motion)
    {
        var x = motion.XDirection * motion.Velocity * _pixelsPerVelocityUnit + _residualX;
        var y = motion.YDirection * motion.Velocity * _pixelsPerVelocityUnit + _residualY;
        var wholeX = (int)MathF.Truncate(x);
        var wholeY = (int)MathF.Truncate(y);
        _residualX = x - wholeX;
        _residualY = y - wholeY;
        return new CursorDelta(wholeX, wholeY);
    }

    public void Reset()
    {
        _residualX = 0f;
        _residualY = 0f;
    }
}

/// <summary>
/// Explicit enable/pause/emergency-stop boundary. A recognizer cannot emit
/// cursor movement merely by producing a motion value.
/// </summary>
public sealed class CursorSafetyGate
{
    private readonly object _sync = new();
    private bool _isEnabled;

    public bool IsEnabled
    {
        get
        {
            lock (_sync) return _isEnabled;
        }
    }

    public void Enable()
    {
        lock (_sync) _isEnabled = true;
    }

    public void Pause()
    {
        lock (_sync) _isEnabled = false;
    }

    public void EmergencyStop()
    {
        lock (_sync) _isEnabled = false;
    }

    public bool TryMove(ICursorOutput output, CursorDelta delta)
    {
        ArgumentNullException.ThrowIfNull(output);
        lock (_sync)
        {
            if (!_isEnabled || delta.IsZero) return false;
            output.MoveBy(delta);
            return true;
        }
    }

    /// <summary>Runs a discrete input action only while the same safety gate is enabled.</summary>
    public bool TryExecute(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_sync)
        {
            if (!_isEnabled) return false;
            action();
            return true;
        }
    }
}

/// <summary>Windows desktop cursor adapter. It is never enabled by itself.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCursorOutput : ICursorOutput
{
    public WindowsCursorOutput()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows cursor output is available only on Windows.");
    }

    public void MoveBy(CursorDelta delta)
    {
        if (delta.IsZero) return;
        if (!GetCursorPos(out var point))
            throw new InvalidOperationException("Unable to read the current cursor position.");
        if (!SetCursorPos(checked(point.X + delta.X), checked(point.Y + delta.Y)))
            throw new InvalidOperationException("Unable to move the cursor.");
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out Point point);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetCursorPos(int x, int y);

    private struct Point
    {
        public int X;
        public int Y;
    }
}
