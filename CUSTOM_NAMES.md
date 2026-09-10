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

For a clearer build of the same project, the read-only
[NameCandidates tool](tools/NameCandidates/README.md) produces cross-version
method-name suggestions with scoped IL evidence and exact input identities.
It compares physical module paths and reports ambiguous matches. Review its
suggestions before adding them to a binary or dnSpy source-only map.

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

## Automatic rename compatibility and audit

Automatic renaming now preserves externally visible types/namespaces, public and
protected methods/fields/properties/events, and public method parameter names.
Valid method names are retained, including overloads and managed P/Invoke aliases.
A virtual/interface group is kept intact when it contains a preserved contract.
A short public name is not enough evidence to rename an API. Private invalid names
still receive placeholders; these are not recovered original semantic names.
Use `--rename-public-api` to explicitly select the old aggressive behavior. This
switch also permits configured API renames. Custom `--name-map` mode remains an
explicit opt-in and is independent of this automatic policy.

Each `--batch` output includes `de4dot-rename-map.xml`. The journal records changed
and removed type/method/field/property/event/parameter definitions, old/new names
and full signatures, physical module paths, MVIDs, SHA-256 hashes and scoped input
and output tokens. Output definitions are checked by reloading the saved image
before publication. Parameters use the owning method token and metadata sequence.
The root also records `RequestedStringDecryption`, `ControlFlowDeobfuscation`
and `RenameSymbols`. `Default` means the selected protector chooses its string
mode; `None` explicitly leaves decoder calls intact. `--only-cflow-deob` selects
`None`. For readable string extraction, explicitly use `--default-strtyp static`
(after `--only-cflow-deob` if composing those options). Preserving decoder calls
can keep runtime behavior correct while leaving unreadable literals in exported
source, so compilation alone is not a string-extraction test.
`*Utf16` attributes contain lossless base64 UTF-16LE when obfuscated names cannot
be represented in XML; readable attributes are also supplied when valid.

Use the exact path/hash/token tuple to trace SDK references or construct a reviewed
custom name map. This journal is an audit/migration format, not an input accepted
by `--name-map`; removed members and arbitrary invalid names cannot be restored by
that mode. It does not reconstruct method bodies or recover vendor names. Keep
the journal with its binary tree. An input with that reserved filename is rejected
to prevent silently overwriting an earlier processing history.

External reflection, serialized names, signing identities and APIs absent from a
particular SDK version still require separate validation. The regression in
`tests/ApiRename` runs a caller compiled against the original library without
processing that caller, and checks overloads, generic calls, dispatch and P/Invoke.

The conservative policy also scans reflection lookup methods across the full
batch, including unprocessed dependencies. Matching literal names are kept for
types, fields, methods, properties and events. This is intentionally conservative
when the receiver is not statically known: it can leave extra short names intact.
Dynamically constructed/encrypted names and callers outside the supplied tree
cannot be inferred. Include runtime plug-ins and use explicit mappings only after
reviewing their reflection contracts.
# Method-name analysis

Default compatibility renaming also examines method names lexically. The tokenizer
recognizes camel/Pascal word boundaries, acronym runs, underscores and numbers.
Common verbs, words and acronyms protect names such as `GetHTTPResponseAsync` and
`LoadX509Certificate`. Strongly fragmented long names and long hexadecimal-looking
identifiers are candidates for placeholder renaming. Unknown domain words and
non-Latin names fall back to the protector's existing rules. Short names such as
`a` and `ab` also use those rules; private short overloads are not skipped.

This is a conservative readability heuristic, not semantic recovery. Overload
counts are never evidence of obfuscation. Public/protected APIs, reflected names
and their virtual/interface families remain protected independently of lexical
classification. `--rename-public-api` retains the legacy checker behavior.
Changed method rows in `de4dot-rename-map.xml` include `NameAssessment`; this is
lexical evidence, not the complete reason for the final rename decision.
