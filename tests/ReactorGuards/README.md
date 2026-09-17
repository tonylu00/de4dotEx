# Reactor guard neutralization regression

Compiles `Fixture.cs` (net48), runs it, processes it with a forced `.NET Reactor`
deobfuscator (`-p dr4`) and runs the processed copy. The fixture embeds Reactor's
tamper/debug message fragments ("is tampered", "Debugger Detected") in two guard
methods that `Main` calls.

Expected: the original prints both guard lines plus `PASS`; the processed copy
prints only `PASS` (guard calls removed). Passing also proves the input file was
not modified.

```powershell
.\Invoke-Regression.ps1 -De4dot <path-to-de4dot-exe-or-dll> -OutputDirectory <new-folder>
```

The `-De4dot` value may be an executable or a DLL (it is run through `dotnet`).
