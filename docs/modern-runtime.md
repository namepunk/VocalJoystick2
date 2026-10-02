# Modern recognition runtime

The current runtime is an offline, deterministic foundation for the future
Windows microphone service and UI. It does not load or invoke legacy binaries.

## Processing contract

1. Supply mono signed PCM samples already resampled to 16 kHz.
2. `LegacyFeatureStream` forms 400-sample MFCC windows every 160 samples.
3. It waits for two prior and two subsequent static windows, then emits the
   centre window's 13 static values followed by its 13 centred deltas.
4. `LegacyRecognitionPipeline` normalizes each 26-value frame with `vj.norms`
   and supplies seven frames in oldest-to-newest order to an `IClassifier`.
5. `LegacyMlpModel`, `LegacyRscModel`, and `LegacyMlrscModel` implement the
   three recovered legacy classifier formats.

The stream deliberately does not invent start/end delta padding. A real-time
client therefore has a two-hop feature latency; classification starts after
seven completed feature frames.

## Discrete-command foundation

`LegacyDiscreteCommandRecognizer` composes an explicitly supplied speech
start/end boundary, the recovered discrete segment checks, EBMM Viterbi
scoring, normalized word probabilities, and `pthresh` acceptance. It returns
diagnostic data only and has no mouse, keyboard, microphone, or callback side
effect. `LegacyVoiceActivityDetector` now produces the recovered VAD
transitions in the audio runner, but the feature-to-segment bridge remains a
separate recovery task.

## Motion and cursor safety

`AudioRecognitionRunner` matches a classification with the RMS and
zero-crossing record carrying the same source-sample timestamp. The recovered
motion modules then calculate a direction and velocity, but do not perform OS
input themselves.

`VocalJoystick.Input` is the only module that knows how to move a Windows
cursor. Its default `DisabledCursorOutput` is a no-op. `CursorSafetyGate` must
be explicitly enabled before a nonzero delta reaches any output, and both
`Pause()` and `EmergencyStop()` close that gate. The live diagnostic never
constructs or enables `WindowsCursorOutput`.

## WAV diagnostic

`VocalJoystick.WavDiagnostic` verifies the full modern path against an input
WAV. It accepts only mono 16-bit PCM at 16 kHz so its sample values match the
legacy microphone conversion exactly.

```bash
DOTNET_CLI_HOME=/tmp/vocaljoystick-dotnet DOTNET_NOLOGO=1 \
dotnet run --project tools/VocalJoystick.WavDiagnostic/VocalJoystick.WavDiagnostic.csproj \
  --no-build --no-restore -- \
  input/legacy-16khz/sine_440hz_5s.wav \
  1_VocalJoystick/SourceDir/vjlib/data/params/vj.norms \
  1_VocalJoystick/SourceDir/vjlib/data/users/default/s4.mlrsc
```

The diagnostic prints sample count, emitted feature-frame count, number of
model classifications, final best class, and final normalized class scores.
