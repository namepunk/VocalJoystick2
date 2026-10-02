namespace VocalJoystick.Features;

public enum FrameContextOrder { OldestToNewest, NewestToOldest }

/// <summary>
/// Maintains a fixed-size context. The recovered legacy classifier contract
/// serializes its seven 26-value frames from oldest to newest.
/// </summary>
public sealed class FrameContextBuilder
{
    private readonly Queue<float[]> _frames;
    public int FeatureCount { get; }
    public int FrameCount { get; }
    public FrameContextOrder Order { get; }
    public int ContextLength => FeatureCount * FrameCount;
    public bool IsReady => _frames.Count == FrameCount;

    public FrameContextBuilder(int featureCount, int frameCount, FrameContextOrder order)
    {
        if (featureCount <= 0) throw new ArgumentOutOfRangeException(nameof(featureCount));
        if (frameCount <= 0) throw new ArgumentOutOfRangeException(nameof(frameCount));
        FeatureCount = featureCount;
        FrameCount = frameCount;
        Order = order;
        _frames = new Queue<float[]>(frameCount);
    }

    public void AddFrame(ReadOnlySpan<float> frame)
    {
        if (frame.Length != FeatureCount) throw new ArgumentException($"Expected {FeatureCount} features.", nameof(frame));
        if (_frames.Count == FrameCount) _frames.Dequeue();
        _frames.Enqueue(frame.ToArray());
    }

    public bool TryBuild(Span<float> context)
    {
        if (context.Length != ContextLength) throw new ArgumentException($"Expected context length {ContextLength}.", nameof(context));
        if (!IsReady) return false;
        var frames = Order == FrameContextOrder.OldestToNewest ? _frames : _frames.Reverse();
        var offset = 0;
        foreach (var frame in frames)
        {
            frame.CopyTo(context[offset..]);
            offset += FeatureCount;
        }
        return true;
    }

    public void Reset() => _frames.Clear();
}
