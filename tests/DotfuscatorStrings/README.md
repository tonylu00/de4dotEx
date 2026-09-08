# Static string extraction regression

Run `Invoke-Regression.ps1 -De4dot <de4dot-x64.exe> -OutputDirectory <new-directory>` on Windows with the .NET 10 SDK and .NET Framework 4.8 targeting/runtime support.

This synthetic Dotfuscator fixture uses a string/int decoder with native-integer key additions and byte swapping. It covers readable format strings, embedded NUL, non-ASCII and surrogate code units, empty strings, negative keys and overflow. The original and statically decrypted batch must return identical values. A separate metadata check requires actual plaintext literals in the caller and no remaining decoder calls, so retaining an encrypted-but-working runtime implementation cannot pass as successful extraction. Input hashes must stay unchanged.
