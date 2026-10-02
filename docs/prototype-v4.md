# Prototype v4

This version adds personalized discrete-command learning to the v3 motion
prototype. The initial catalog is `К → left_click` and `Ч → right_click`.
Command class IDs are distinct from action IDs; the catalog/model code supports
five or more classes. Each class requires three saved utterances.

Portable Windows x64 build: `artifacts/prototype-v4/win-x64/`;
archive: `artifacts/VocalJoystick-prototype-v4-win-x64.zip`.

To enroll, start diagnostics, select **Записать К** or **Записать Ч**, then say
the selected sound once. The shared voice-activity detector ends the sample
after silence. A PCM16/16 kHz WAV and normalized MFCC feature sequence are
persisted immediately under `%LOCALAPPDATA%\VocalJoystick`; restart does not
lose enrollment. After all classes have three samples, **Обучить команды**
builds and saves a constrained-DTW sequence-template model. Unknown or
ambiguous utterances are rejected and their distances are shown in diagnostics.

When a saved user command model exists, it replaces the legacy command scorer;
the normal voice-to-motion pipeline remains active and does not incur a new
wait before moving. Accepted class action IDs route to the click output behind
the existing explicit safety gate. Diagnostics do not move the cursor or click.

The action catalog separates acoustic class from action mapping and supports
named actions, while the first prototype exposes only left and right click.
Additional action types and GUI catalog editing remain future work. The new
classifier and persistence tests pass; microphone enrollment, recognition
quality, and click behavior must still be validated by the user on Windows.
Release x64 build completed with zero warnings/errors; all 74 automated tests
passed. The archive integrity check passed.

Cursor output uses one configurable pixel scale, matching the prior prototype
behavior. The command recognizer exposes a distance-threshold multiplier (1.0
uses automatic calibration; lower values are stricter and higher values more
permissive). Both the scale and threshold multiplier persist in desktop
preferences and take effect on the next diagnostics/control start.

Diagnostics are divided into independent latest-state panels: common audio
stream/VAD measurements, direction classification and cursor deltas, and click
command segmentation/classification/action routing. Direction updates do not
overwrite click results. In diagnostics mode the safety gate remains disabled;
in control mode the click panel reports whether the action was mapped, blocked,
or whether the Windows mouse-event call returned.
