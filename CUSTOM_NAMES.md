# Custom batch name maps

All three frontends (dnSpy.Console, de4dot-x64 and NETReactorSlayer.CLI) accept
this separate, metadata-only batch mode. Run it **after** deobfuscation and
before dnSpy source export. It does not rerun protection removal or automatically
guess names. Unmapped assemblies and non-managed files are copied unchanged.

```powershell
# Substitute any of the three executable names below.
dnSpy.Console.exe --name-map-input D:\analysis\cleaned --name-map-export D:\analysis\names.xml
# Edit NewName attributes; leave unused entries empty or remove them.
dnSpy.Console.exe --name-map-input D:\analysis\cleaned --name-map D:\analysis\names.xml --name-map-preview --name-map-report D:\analysis\preview.xml
dnSpy.Console.exe --name-map-input D:\analysis\cleaned --name-map D:\analysis\names.xml --name-map-output D:\analysis\named
```

`--name-map-help` prints the options. Preview changes no assemblies. Output and
report/inventory files must be new; application output must not overlap input.
A failed write leaves an unpublished `.staging-*` directory for diagnosis.
The output retains the input directory layout, including empty directories.
Ordinary deobfuscation options cannot be combined with custom-name mode.

## Agent workflow and format

1. Export an inventory from the exact restored binaries being analyzed.
2. Inspect behavior, call sites, signatures and parameter uses in dnSpy.
3. Set `NewName` for evidence-supported suggestions. Optional `Reason` and
   `Confidence` attributes can document hypotheses; they do not execute code or
   affect validation. Proposed descriptive names are not proof of original names.
4. Preview the full expansion, then apply to a new tree and test behavior.
5. Re-export the inventory after changing/rebuilding the input binaries.

```xml
<NameMap Version="1">
  <Module Path="plugins\Library.dll" Mvid="00000000-0000-0000-0000-000000000001">
    <Type Token="0x02000004" ExpectedName="GClass4" NewName="PacketDecoder"
          Reason="Parses packet headers and payloads" />
    <Method Token="0x06000007" ExpectedName="method_0" NewName="Decode">
      <Parameter Sequence="1" ExpectedName="byte_0" NewName="payload" />
    </Method>
  </Module>
</NameMap>
```

Use real MVIDs and tokens from the inventory; the example values are placeholders.
Types include nested types and enum types. Generic type names retain the exact
metadata arity suffix (for example ``Decoder`1``). Method overloads are identified
by token, not name. `Sequence` is one-based in the method signature, excluding
`this`; zero/return parameters are rejected. Missing parameter metadata is created
when a mapped signature parameter needs a name. Names are identifiers, without
namespace changes. C# keywords can be escaped by the decompiler.

## References and compatibility contexts

References are captured before names change, including IL, constructed generic
calls, signatures, attributes and explicit overrides. Implicit virtual families
are renamed together. Conservatively, same-name virtual overloads in a connected
hierarchy follow one name; conflicting proposals are rejected. External virtual
contracts, accessor/runtime method names and mixed-mode rewrites are rejected.

Assembly lookup uses exact assembly identity and the consumer's nearest input
folder. Separate compatibility folders stay separate even when assembly names
and MVIDs collide. An ambiguity requires an explicit binding in the map:

```xml
<Binding Source="host\Client.exe" Dependency="compat\Library.dll" />
```

Paths are relative to `--name-map-input`. Bindings must match a real assembly
reference. This mode does not import the other pipelines' configuration/context
manifests; express necessary choices with `Binding`. Include the entire relevant
managed dependency tree so callers and implementations can be updated together.

Stale MVIDs/names/tokens, invalid identifiers, name/signature collisions and
unresolvable mapped references fail before publication. Preview reports physical
module paths so identical tokens in different assemblies remain distinguishable.

## Limits and verification

Custom naming changes metadata contracts. Persisted type names, external callers,
reflection, WPF/BAML and serialized resources can carry names outside ordinary
metadata references. Detected matching IL string literals and type-owned resource
names are rejected for separate review; this is not a complete reflection or
resource migration engine. Private signing keys cannot be recovered; signed
outputs require the owner's normal re-signing workflow. Do not treat a successful
preview as proof of compatibility with external plug-ins or persisted data.

`Tests/Invoke-CustomNameMapRegression.ps1 -Tools <exe-paths> -OutputDirectory <new-dir>`
checks each frontend with duplicate-identity libraries, generic interface/override
dispatch, generic calls, nested enum type references, parameter names, tree/input
preservation and invalid-map rejection. The shared `BatchNameMap.cs` implementations
are kept byte-identical in the three repositories.
