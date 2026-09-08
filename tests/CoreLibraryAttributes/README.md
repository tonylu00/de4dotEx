# Core-library attribute scope regression

Run `Invoke-Regression.ps1 -De4dot <de4dot-x64.exe> -OutputDirectory <new-directory>` with the .NET 10 SDK.

The emitter writes malformed `System.Type` attribute arguments naming `System.String` and `System.DateTime` in their own assemblies. Batch processing must repair their scopes, and the corrected values must survive serialization. Another assembly really defines a local `System.String`; its type value and entire file must remain unchanged. An unresolved `System.NotACoreType` must still prevent publication. All input hashes must remain unchanged.
