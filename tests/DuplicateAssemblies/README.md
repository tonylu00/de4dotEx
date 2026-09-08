# Duplicate assembly binding regression

Run `Invoke-Regression.ps1 -De4dot <de4dot.exe> -OutputDirectory <new-directory>` on Windows with the .NET 10 SDK and .NET Framework 4.8 targeting/runtime support.

The fixture creates two compatibility directories with identical assembly identities and different runtime results. Each directory includes alternate DLL and EXE filenames. Invalid type names force renaming, so binding a reference to the wrong copy cannot be hidden by unchanged names. Explicit assembly-scoped self-references exercise alternate library copies too.

Both forward and reversed input order must preserve client behavior and library self-references. Each reflection probe runs in a separate process to avoid assembly loader caching across copies. Input hashes must remain unchanged.

The resolver prefers the referring assembly itself, then the nearest directory containing a candidate, exact identity, and the conventional assembly filename. It resolves members only within the selected assembly. Truly ambiguous layouts still produce an explicit diagnostic rather than silently binding to the first input. This does not implement arbitrary application-specific assembly loading policies or XAML renaming.

Add `-Batch` to test the application-folder command. This adds synthetic Dotfuscator detection markers to the library copies (a dispatch fixture, not a real protector sample), leaving the clients unprotected. The test forces library type renaming and attempts to rename a virtual method overridden by an unprotected client. It checks consumer reference repair, virtual dispatch, unchanged unrelated managed/native files and sidecars, and empty directories. The two batch runs check repeatability; directory enumeration itself is sorted independently of command-line input order.

Batch fixtures also call `typeof(T).Assembly.GetType` from protected libraries and unprotected clients. Both must locate their renamed type in the correct compatibility copy and return that copy's runtime result.

Batch mode additionally tests two different APIs under the same assembly identity in one directory. Without explicit bindings the graph must fail before publication; with repeatable `--batch-binding source=dependency` options both clients must run with their selected libraries, including reflection and virtual calls. Binding order is reversed in a second run. Missing/outside inputs, duplicate choices, invalid identities and malformed options must not publish output.
