# Cross-assembly field collision regression

Run `Invoke-Regression.ps1` with `-De4dot`, `-DnSpyConsole`, and a new `-OutputDirectory`. Requires the .NET 10 SDK and .NET Framework 4.8 targeting/runtime support on Windows; dnSpy's directory must include dnlib.dll.

The fixture emits valid IL containing duplicate field names, fields sharing names with a property, method, or enclosing type, and generic field references across two assemblies. It also reserves a property named `int_0` to exercise generated-name collisions.

The script checks original and processed execution, directly rejects collisions in processed metadata, then exports, compiles, and executes the source with one and four export workers. It checks input hashes and deterministic generated source. The metadata assertion prevents a decompiler's own field aliasing from masking a de4dot regression.
