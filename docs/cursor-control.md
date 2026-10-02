# Cursor control

`VocalJoystick.CursorControl` is the first modern runtime that can move the
Windows cursor. It uses the recovered recognizer, motion equations and profile
files; it does not load the legacy executables or DLLs.

It is intentionally opt-in. The program refuses to start without both a
positive pixel scale and the literal `--enable-cursor` flag. It has no click,
drag, keyboard, recording, or persistence feature. `Ctrl+C` closes the cursor
gate before it cancels microphone capture.

First identify the microphone index:

```powershell
.\VocalJoystick.CursorControl.exe --list-devices
```

Then, after confirming that the safe `LiveDiagnostic` output is sensible, run
with a deliberately small initial scale such as `0.1`:

```powershell
.\VocalJoystick.CursorControl.exe `
  .\vj.norms .\s4.mlrsc 0 `
  .\prm.cfg .\s4.power .\vj.lr `
  --pixels-per-velocity-unit 0.1 --enable-cursor
```

The printed `applied=True` means only that a nonzero delta was accepted by the
open safety gate. Press `Ctrl+C` to stop immediately. Do not use this tool for
critical desktop actions until its response has been calibrated with the
diagnostic mode.
