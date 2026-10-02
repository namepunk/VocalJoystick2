# Windows microphone diagnostics

`WindowsMicrophoneSource` is the modern Windows capture implementation. It
uses NAudio/WinMM, requests mono 16-bit PCM at 16 kHz, and never loads a
legacy binary. A 20 ms callback writes into a bounded channel; if a consumer
falls behind, the oldest chunk is discarded rather than blocking the audio
callback.

`VocalJoystick.LiveDiagnostic` is deliberately safe: it prints classifier
scores plus matching legacy RMS and zero-crossing values. It does not move the
cursor, send clicks, or persist audio.

On Windows, build and list devices:

```powershell
dotnet run --project tools/VocalJoystick.LiveDiagnostic -- --list-devices
```

Then capture from a listed device until `Ctrl+C`:

```powershell
dotnet run --project tools/VocalJoystick.LiveDiagnostic -- `
  1_VocalJoystick/SourceDir/vjlib/data/params/vj.norms `
  1_VocalJoystick/SourceDir/vjlib/data/users/default/s4.mlrsc 0
```

To also calculate the recovered motion values using the bundled profile,
append `--motion` and its three legacy data files. This still only prints data:

```powershell
dotnet run --project tools/VocalJoystick.LiveDiagnostic -- `
  1_VocalJoystick/SourceDir/vjlib/data/params/vj.norms `
  1_VocalJoystick/SourceDir/vjlib/data/users/default/s4.mlrsc 0 `
  --motion `
  1_VocalJoystick/SourceDir/vjlib/data/params/prm.cfg `
  1_VocalJoystick/SourceDir/vjlib/data/users/default/s4.power `
  1_VocalJoystick/SourceDir/vjlib/data/params/vj.lr
```

The output adds `motion-x`, `motion-y`, `velocity`, and `global-velocity`.
It does not create a cursor-output object and cannot trigger clicks.

The driver must accept the requested 16 kHz mono capture format. If it does
not, the tool reports the driver error; automatic fallback/resampling has not
been added yet, because it needs a measured capture contract rather than a
silent quality change.
