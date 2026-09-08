# Duplicate assembly binding regression

Run `Invoke-Regression.ps1 -De4dot <de4dot.exe> -OutputDirectory <new-directory>` on Windows with the .NET 10 SDK and .NET Framework 4.8 targeting/runtime support.

The fixture creates two compatibility directories with identical assembly identities and different runtime results. Each directory includes alternate DLL and EXE filenames. Invalid type names force renaming, so binding a reference to the wrong copy cannot be hidden by unchanged names. Explicit assembly-scoped self-references exercise alternate library copies too.

Both forward and reversed input order must preserve client behavior and library self-references. Each reflection probe runs in a separate process to avoid assembly loader caching across copies. Input hashes must remain unchanged.

The resolver prefers the referring assembly itself, then the nearest directory containing a candidate, exact identity, and the conventional assembly filename. It resolves members only within the selected assembly. Truly ambiguous layouts still produce an explicit diagnostic rather than silently binding to the first input. This does not implement arbitrary application-specific assembly loading policies or XAML renaming.
