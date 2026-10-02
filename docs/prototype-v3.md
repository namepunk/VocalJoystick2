# Prototype v3

The portable Windows x64 build is in `artifacts/prototype-v3/win-x64/`, with
the distributable archive at `artifacts/VocalJoystick-prototype-v3-win-x64.zip`.

This version adds four-direction motion enrollment to v2. It collects the
normalized seven-frame MFCC context used by the runtime classifier and retains
the corresponding raw enrollment audio as a labeled PCM16/16 kHz WAV under the
current user's `%LOCALAPPDATA%\VocalJoystick\training\motion\` directory.
After at least three samples per direction, **Обучить и сохранить модель**
creates and activates the user-specific prototype classifier. The classifier
is persisted separately under `%LOCALAPPDATA%\VocalJoystick\motion.user.json`.

Cursor control remains disabled while enrolling and after the app activates a
trained classifier. First validate each direction in diagnostics. See
[`user-motion-training-v1.md`](user-motion-training-v1.md) for the training
workflow and known limitations.

Validation: Release `win-x64` publish succeeded; solution build has zero
warnings/errors; all 69 automated tests pass. Actual microphone enrollment and
direction accuracy remain to be tested on Windows.
