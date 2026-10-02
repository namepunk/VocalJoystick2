# Desktop UI

`VocalJoystick.Desktop` is the modern WPF application shell. It keeps all
legacy assets as external read-only files and talks only to the managed audio,
recognition, motion, and input modules.

The single-window first version provides:

- a first-run setup wizard with a live, non-recording microphone-level meter;
- microphone selection and refresh;
- editable profile-file paths with file pickers;
- live RMS, zero-crossing, class-score, direction, velocity, and delta display;
- legacy discrete-command recognition using a separate HMM model and dictionary;
- diagnostics mode, which uses a disabled cursor output;
- a separate control button, pause, and stop button.

The cursor button creates a session with Windows cursor and mouse-button
adapters and opens the shared safety gate only after its explicit user click.
Accepted discrete command ID 0 maps to a left click and ID 1 to a right click.
Pause and stop close that gate. The live runner now sends time-aligned feature,
energy, VAD, and periodicity-voicing frames through the discrete segmenter and
HMM scorer. The periodicity detector is a modern approximation, not the old
LPC/dynamic-programming pitch implementation, so command acceptance still
needs real Windows microphone testing and threshold tuning.

The selected microphone, profile paths, and pixel scale are saved as
non-secret settings in `%LocalAppData%\VocalJoystick\settings.json`. A failed
settings write never prevents the control session from stopping safely.

For a development checkout, the UI pre-fills paths to the supplied `s4`
profile. In a packaged product these must be replaced by copied application
assets and user-managed profiles. The present UI is deliberately a staging
surface for the upcoming calibration and training workflows, not a reason to
skip the later Windows hardware tests.
