using System.IO;
using System.Text.Json;

namespace VocalJoystick.Desktop;

/// <summary>Persisted non-secret preferences for the desktop shell.</summary>
public sealed record DesktopSettings(
    int? MicrophoneIndex,
    string NormsPath,
    string ModelPath,
    string ParametersPath,
    string PowerPath,
    string RegressionPath,
    float PixelsPerVelocityUnit,
    string? CommandModelPath = null,
    string? CommandDictionaryPath = null,
    float CommandDistanceThresholdMultiplier = 1f);

internal sealed class DesktopSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private readonly string _path;

    public DesktopSettingsStore()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VocalJoystick");
        _path = Path.Combine(directory, "settings.json");
    }

    public DesktopSettings? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            return JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(_path), SerializerOptions);
        }
        catch (IOException) { return null; }
        catch (JsonException) { return null; }
    }

    public void Save(DesktopSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = _path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, SerializerOptions));
        File.Move(temporaryPath, _path, overwrite: true);
    }
}
