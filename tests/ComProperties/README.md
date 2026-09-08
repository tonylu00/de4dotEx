# Imported COM property restoration

Run `Invoke-Regression.ps1 -De4dot <de4dot-x64.exe> -DnSpyConsole <dnSpy.Console.exe> -OutputDirectory <new-folder>` with .NET 10 and .NET Framework 4.8 installed.

The fixture models an imported coclass whose parameterized COM property accessors are runtime methods without property rows. Its named, parameterized interface property resides in a separate binary contract assembly. An emitter removes the coclass property row and the interface's default-member marker to represent this interop layout. Existing coclass properties and interface metadata remain present. A managed implementation separately loses a property row that de4dot must restore.

Original, emitted, processed and one/four-worker rebuilt programs verify imported identity, accessor signatures/runtime flags, existing properties, interface metadata and managed interface dispatch. Input hashes and worker-independent source hashes are checked. No external COM server is instantiated. This test does not claim support for exporting named, parameterized interface properties themselves as C# source.
