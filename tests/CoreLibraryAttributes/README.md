# Core-library attribute scope regression

Run `Invoke-Regression.ps1 -De4dot <de4dot-x64.exe> -OutputDirectory <new-directory>` with the .NET 10 SDK.

The emitter writes a malformed `System.Type` attribute argument naming `System.String` in its own assembly. Batch processing must repair the scope, and the corrected value must survive serialization. A second assembly really defines a local `System.String`; its type value and entire file must remain unchanged. An unresolved `System.NotACoreType` must still prevent publication. All input hashes must remain unchanged.
