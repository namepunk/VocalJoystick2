using System.Security.Cryptography;
using VocalJoystick.Models;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: VocalJoystick.ModelInspector <legacy-model.wts>");
    return 2;
}

var path = args[0];
if (!File.Exists(path))
{
    Console.Error.WriteLine($"File not found: {path}");
    return 2;
}

if (!Path.GetExtension(path).Equals(".wts", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("Only the documented .wts format is supported by this C# inspector currently.");
    return 2;
}

try
{
    var model = LegacyMlpModel.Load(path);
    var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    Console.WriteLine($"file: {path}");
    Console.WriteLine($"sha256: {hash}");
    Console.WriteLine($"dimensions: input={model.InputCount}, hidden={model.HiddenCount}, output={model.ClassCount}");
    Console.WriteLine($"blocks: input-to-hidden={model.InputToHidden.Length}, hidden-to-output={model.HiddenToOutput.Length}, hidden-bias={model.HiddenBias.Length}, output-bias={model.OutputBias.Length}");
    return 0;
}
catch (Exception error) when (error is IOException or FormatException or InvalidDataException)
{
    Console.Error.WriteLine($"Invalid legacy MLP model: {error.Message}");
    return 1;
}
