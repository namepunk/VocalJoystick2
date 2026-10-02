# Prototype v2

The portable Windows x64 build is in `artifacts/prototype-v2/win-x64/`.
Compared with v1, it packages the supplied `d.hmmf`/`vj.dict` command profile,
feeds live frames into discrete-command segmentation/scoring, and maps IDs 0
and 1 to left and right click respectively.

The voicing flag for command segmentation currently comes from a normalized
periodicity detector over 960-sample frames. This is a modern approximation,
not an exact implementation of the legacy LPC plus pitch dynamic program.
Consequently this build is for controlled initial testing only. Use
**Начать диагностику** first; clicks are disabled there. To permit clicks,
explicitly start cursor control. **Пауза** and **Остановить** close the common
safety gate.

The command model and dictionary paths are editable in the profile section and
are saved with the other settings. The default files are included under
`profiles/`.

Validation performed: .NET 8 Release publish for `win-x64` succeeded with no
warnings or errors. This does not verify Windows audio hardware behavior or
voice-command precision; those require running the build on Windows.
