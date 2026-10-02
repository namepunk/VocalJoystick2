namespace VocalJoystick.Desktop;

public sealed record MicrophoneOption(int Index, string Name)
{
    public override string ToString() => $"{Index}: {Name}";
}
