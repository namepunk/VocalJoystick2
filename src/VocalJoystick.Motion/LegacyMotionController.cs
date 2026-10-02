namespace VocalJoystick.Motion;

/// <summary>
/// Combines the recovered direction and velocity stages into the numerical
/// motion record produced by the old engine. This is intentionally isolated
/// from Windows cursor APIs: callers decide whether and how a result may move
/// the operating-system cursor.
/// </summary>
public sealed class LegacyMotionController
{
    private readonly LegacyDirectionMapper _directions;
    private readonly LegacyVelocityMapper _velocities;
    private readonly float _axisDeadZone;
    private readonly float _maximumAxisVelocity;

    public LegacyMotionController(
        LegacyDirectionMapper directions,
        LegacyVelocityMapper velocities,
        float axisDeadZone,
        float maximumAxisVelocity)
    {
        _directions = directions ?? throw new ArgumentNullException(nameof(directions));
        _velocities = velocities ?? throw new ArgumentNullException(nameof(velocities));
        if (!float.IsFinite(axisDeadZone) || axisDeadZone < 0f) throw new ArgumentOutOfRangeException(nameof(axisDeadZone));
        if (!float.IsFinite(maximumAxisVelocity) || maximumAxisVelocity <= 0f)
            throw new ArgumentOutOfRangeException(nameof(maximumAxisVelocity));
        _axisDeadZone = axisDeadZone;
        _maximumAxisVelocity = maximumAxisVelocity;
    }

    public LegacyMotionSample Update(float rootMeanSquareEnergy, ReadOnlySpan<float> probabilities)
    {
        var direction = _directions.Map(probabilities);
        var velocity = _velocities.Map(rootMeanSquareEnergy, probabilities);

        // The old code applies each axis curve to the non-normalised direction,
        // then dead-zones and clamps axes before normalising the result.
        var x = ClampAxis(direction.X * direction.Magnitude * velocity.X);
        var y = ClampAxis(direction.Y * direction.Magnitude * velocity.Y);
        var magnitude = MathF.Sqrt(x * x + y * y);
        return magnitude > 0f
            ? new LegacyMotionSample(x / magnitude, y / magnitude, magnitude, velocity.Global)
            : new LegacyMotionSample(0f, 0f, 0f, velocity.Global);
    }

    private float ClampAxis(float value)
    {
        if (MathF.Abs(value) < _axisDeadZone) return 0f;
        return Math.Clamp(value, -_maximumAxisVelocity, _maximumAxisVelocity);
    }
}

/// <summary>Direction and speed values equivalent to legacy <c>MotionData</c>.</summary>
public readonly record struct LegacyMotionSample(float XDirection, float YDirection, float Velocity, float GlobalVelocity);
