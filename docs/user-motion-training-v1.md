# User motion training (first implementation)

The desktop app can collect labeled motion utterances and create a personal
four-class model. This is a new, lightweight prototype classifier; it does not
run the legacy `adapt.bat` MLP/RSC/ML-RSC adaptation pipeline.

The labels retain the recovered four-class motion order:

| Class | Direction |
| --- | --- |
| 0 | Up |
| 1 | Right |
| 2 | Down |
| 3 | Left |

For each direction, the user records at least three utterances. A button starts
one enrollment; the app collects 60 recognition contexts while VAD reports
speech, then treats those contexts as one labeled example. The context is the
same normalized 182-value, seven-frame MFCC input used by the runtime
recognizer. The trainer averages each utterance, forms one prototype per
direction, and uses regularized shared per-feature variance to compare live
contexts. The generated model is stored in the current user's
`%LOCALAPPDATA%\VocalJoystick\motion.user.json` and is loaded automatically on
the next diagnostics or control session.

The raw audio captured during each enrollment is also retained as a mono PCM16,
16 kHz WAV under `%LOCALAPPDATA%\VocalJoystick\training\motion\`, in a
class-specific directory such as `class-0-вверх`. WAVs have a UTC timestamp
and unique suffix in their names. These files are for listening and debugging;
the current trainer consumes the in-memory MFCC contexts, not the WAV files.

During enrollment, start or keep a diagnostics session running; cursor output
is disabled. Press the direction button, then say the chosen sound until the
sample-complete status appears. Repeat at least three times per direction and
select **Train and save model**. That action restarts diagnostics with the new
model active but does not enable cursor control. Cursor control remains an
explicit separate action.

This implementation intentionally begins with four cardinal directions and
uses the current feature extractor and motion controller. It does not yet
support incremental enrollment after restarting the application, a neutral
class, automated holdout evaluation, or an erase/reset button. The first
Windows test should confirm that samples complete, the trained model persists
after restart, and each held-out utterance produces the intended direction in
diagnostics before enabling the cursor. Score sharpness has only synthetic
unit-test coverage so far; microphone and user-specific accuracy remain
unverified.
