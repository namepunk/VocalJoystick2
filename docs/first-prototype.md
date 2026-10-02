# First Windows prototype

The first prototype is a portable x64 Windows build for checking microphone
selection, live recognition diagnostics, and voice-driven cursor movement. It
ships with the supplied `s4.mlrsc` profile and its normalization and motion
configuration files. It does not include mouse clicks or user-model training.

## Run

1. Extract the complete `win-x64` folder from the prototype archive.
2. Run `VocalJoystick.Desktop.exe` on a Windows 10 or Windows 11 machine.
3. Choose the microphone and use **Начать диагностику**. Speak and check that
   the VAD and class scores respond.
4. Start cursor control with a low scale such as `0.1`. Keep the pointer over
   an empty area while checking movement.
5. **Пауза** stops cursor output while capture continues. **Остановить** ends
   capture. Close the application to finish.

The application stores its microphone choice, profile paths, and scale under
`%LocalAppData%\VocalJoystick\settings.json`. The packaged profile is in the
`profiles` subfolder and remains editable through the file pickers.

The first run must be done on Windows because the application uses Windows
audio capture and WPF. A successful build in WSL does not verify that a
particular microphone driver supports the requested 16 kHz mono capture.
