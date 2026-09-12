# Cross-version name candidates

## Complete method review (start here for manual naming)

This is a C#/.NET 8 console tool for Windows, macOS and Linux. It does not need
PowerShell, Windows registry access, dnSpy GUI or execution of the analyzed
assemblies. Build and invoke the DLL through `dotnet` on any of those systems:

```text
dotnet build tools/NameCandidates/NameCandidates.csproj -c Release
dotnet tools/NameCandidates/bin/Release/net8.0/NameCandidates.dll --review-obfuscated ./restored ./methods.json
dotnet tools/NameCandidates/bin/Release/net8.0/NameCandidates.dll --review-map ./reviewed.json ./restored ./source-names.xml
```

The examples assume the repository working directory and a `restored` input
directory. Change those paths for your checkout. Relative module paths are
written with `/`; Unix paths are not collapsed using Windows case comparisons.
Generate a fresh inventory after moving inputs to another machine so its root
matches that machine; the physical module hashes/tokens still bind each entry.
The GitHub Actions name-review matrix is configured to run the metadata-only regression on
Windows, macOS and Linux. Building and launching the ETS application itself is
a separate Windows task; name analysis and map production are cross-platform.

Local validation on 2026-09-11 passed the metadata regression on Windows,
including all earlier cross-version matching cases. macOS/Linux jobs have not
been run locally. Full ETS inventories found 19,727/77,667 review methods in
6.3/6.4. The reported Common `Load`, `Map`, `On`, `Save` false positives are absent;
short LicenseManager names are included. Editing one real schema-helper row in
each version, converting its review map and rebuilding Common passed both SDK
and original-name audits, in 6.3-first order. This is a focused map-workflow
check, not a new whole-ETS live run.

Cross-version candidates are only methods with a qualifying match; they are not
an inventory of obfuscated methods. To discover names in a single assembly or
an entire tree, use:

```powershell
dotnet tools/NameCandidates/bin/Release/net8.0/NameCandidates.dll --review D:\analysis\input D:\analysis\methods.json
```

The JSON includes **every method**, including interfaces, accessors, virtual
methods and tiny bodies. Filter `NeedsReadableName` for likely naming work;
`MappingBlocker` independently explains current source-map restrictions. Short
unrecognized names (one to three letters, also `_N` suffixes such as `c_1`) are
included. Recognized words such as `Map`, `On`, `Load`, `Save`, and corroborated
project vocabulary are preserved. Unknown longer names remain in the complete
inventory for agent review even when not positively classified as obfuscated.
Mixed alphanumeric names with several tiny case fragments, such as Reactor's
`jn7oUifpKYO`, are also flagged for manual review. Recognized words and learned
acronyms suppress this additional heuristic. This changes inventory coverage
only; it does not loosen the automatic binary renamer's conservative rules.
`ParameterNames` contains editable parameter rows with `Sequence`, `OriginalName`,
`HasMetadata` and `NewName`. Sequences are actual signature positions, excluding
`this`. The older `Parameters` strings remain for display; do not edit those to
request a rename.

Use `--review-obfuscated` instead of `--review` to write only the rows marked
`NeedsReadableName`, with the same editable format and mapping restrictions.
This is useful for large trees; use full review when investigating unknown names.

Edit `NewName` on accepted rows in a copy of the JSON. Leave others empty, or
retain only selected rows without changing the top-level identity fields:

```powershell
dotnet tools/NameCandidates/bin/Release/net8.0/NameCandidates.dll --review-map D:\analysis\reviewed.json D:\analysis\input D:\analysis\new-source-names.xml
```

No reference version or body match is required. Conversion verifies physical
paths, hashes, MVIDs, identities, tokens, signatures and actual mapping eligibility
from the binary; editable `MappingBlocker` flags cannot bypass validation. Name
collisions receive deterministic suffixes. Review the resulting map before
export. This produces a new map, not an in-place merge with an existing seed map;
preserve existing type/parameter aliases when merging. Unsupported contracts
are visible work items, not permission to rename their binaries and break SDK
callers. The complete inventory is read-only and has no effect on automatic
binary-renaming policy.

## Update existing maps and preflight

Methods retaining only the metadata `SpecialName` bit can be reviewed and
aliased when no property or event actually owns them. Obfuscators sometimes
remove those association rows while retaining the bit; dnSpy emits the methods
as ordinary declarations and restores their original names and flags on build.
Actual property/event accessors, operators, constructors, runtime special names,
native methods and unsupported virtual contracts remain guarded. The metadata
regression covers both orphan methods and accessors missing the flag; dnSpy's
`OrphanSpecialNameRegression` additionally executes original and rebuilt callers.

For editing an existing source map, use the executable updater instead of
manually merging XML:

```text
dotnet tools/NameCandidates/bin/Release/net8.0/NameCandidates.dll --update-map ./reviewed.json ./base-names.xml ./updated-names.xml ./update-report.json
dotnet tools/NameCandidates/bin/Release/net8.0/NameCandidates.dll --check-map ./updated-names.xml ./check-report.json
dotnet tools/NameCandidates/bin/Release/net8.0/NameCandidates.dll --merge-map ./generated-methods.xml ./base-names.xml ./merged-names.xml ./merge-report.json
```

`--update-map` validates the exact module snapshots, replaces accepted symbol
aliases by physical module/token, retains untouched type/method/parameter rows,
and repairs requested-name collisions against the entire merged module. Its
report records `Kind`, `Before`, `Requested`, and `Applied` names, plus
`ParameterSequence` for parameter edits. It collects per-entry
errors with module/token identifiers, including stale identities, unsupported
contracts, invalid identifiers and malformed existing rows. Any error prevents
map publication; the original map is never changed. `--check-map` performs the
same metadata/alias preflight without generating source or changing a map.
Each loaded module and hash is cached in memory for the operation. These tools
are .NET commands with no PowerShell dependency. Preflight addresses map validity;
compiler and runtime checks are still necessary after accepted changes.

`--merge-map` accepts the method-only XML produced by `--source-map`, so the
cross-version workflow also needs no manual XML merge. Existing type and
parameter rows stay in the base map. The generated proposal map may contain
method renames only; unsupported proposal row kinds are rejected explicitly.

## Review types and parameters

Type discovery and editing also use the portable executable:

```text
dotnet tools/NameCandidates/bin/Release/net8.0/NameCandidates.dll --review-types ./restored ./types.json
dotnet tools/NameCandidates/bin/Release/net8.0/NameCandidates.dll --update-map ./reviewed-types.json ./base-names.xml ./with-types.xml ./type-report.json
dotnet tools/NameCandidates/bin/Release/net8.0/NameCandidates.dll --update-map ./reviewed-methods.json ./with-types.xml ./with-parameters.xml ./parameter-report.json
```

`--review-types` writes every type to `Types`; it does not claim to infer which
types are obfuscated. Set a type row's `NewName` after reviewing its behavior.
The alias changes its simple source name and retains its namespace and nesting.
Global module and generic type aliases remain unsupported and are reported.

To name parameters, start with `--review` or `--review-obfuscated`, and set
`ParameterNames[].NewName` on the selected method. Leave the method's own
`NewName` empty to preserve its current alias. Parameter edits require an actual
metadata parameter at the recorded sequence and a matching original name.
Unsupported method contracts remain blocked even for parameter-only edits.
Untouched parameter aliases are retained, and collisions with other parameters
or enclosing generic names receive reported suffixes.

Both `--update-map` and `--review-map` accept type and parameter edits. A single
inventory may contain both `Methods` and `Types` for one input root. No manual
XML editing is required, and validation failure publishes no partial map.
These are source aliases: dnSpy's restoration build target must run to retain
the original binary names and SDK contracts.

## Rename complete interface and virtual method families

Use the metadata graph when a name belongs to an implicit interface contract or
virtual override chain:

```text
dotnet tools/NameCandidates/bin/Release/net8.0/NameCandidates.dll --review-families ./restored ./families.json
dotnet tools/NameCandidates/bin/Release/net8.0/NameCandidates.dll --update-map ./reviewed-families.json ./base-names.xml ./family-names.xml ./family-report.json
dotnet tools/NameCandidates/bin/Release/net8.0/NameCandidates.dll --check-map ./family-names.xml ./family-check.json
```

Review the bodies and call sites, then set `NewName` on one member of an eligible
family. The updater finds every declaration, implementation and override from
the actual assemblies and applies one alias atomically. You can keep only the
selected seed rows in the JSON; removing other members cannot create a partial
rename. Conflicting proposals for the same family fail without publishing a map.
Name collisions receive one shared suffix across all affected modules.

`ContractFamily` identifies a sorted set of physical module paths and method
tokens. Every seed is also checked against its hash, MVID, identity, original
name and signature. Generic interface/base arguments are substituted when
matching signatures; overloads remain distinct. Duplicate assembly identities
are resolved in their physical context, and ambiguous dependencies are blocked.
Supply the complete application tree for discovery and export. dnSpy independently
recomputes the graph and rejects stale or incomplete family maps before export.

`ParameterNames[].NewName` edits apply to the individual method row, because
parameter metadata names are independent across declarations. Include each
implementation row whose parameters you want to name. Existing family aliases
can be retained while updating only their parameters.

The inventory includes readable names as well as likely obfuscated names.
`MappingBlocker` reports constructors, accessors, native/runtime methods, explicit
`MethodImpl` declarations, static virtual contracts and unresolved external
contracts that this workflow cannot yet rename. These entries remain available
for review, but editing the blocker field cannot make them eligible. Use the JSON
updater for family edits; `--merge-map` accepts ordinary generated method maps.
The portable metadata regression covers duplicate physical identities, generic
substitution, inherited implementations, overloads, overrides and invalid maps.

## Cross-version matching

Build and run this read-only helper with .NET 8 or newer:

```powershell
dotnet build tools/NameCandidates/NameCandidates.csproj -c Release
dotnet tools/NameCandidates/bin/Release/net8.0/NameCandidates.dll D:\analysis\reference D:\analysis\target D:\analysis\candidates.json
```

The report must be a new file. Inputs are never rewritten or loaded for execution.
All DLL/EXE files are considered recursively, without product-specific filters.
Pairing uses relative physical paths; duplicate assembly names in compatibility
folders remain independent. Moved modules need an explicitly arranged reference
tree. Linked files/directories are rejected rather than traversed outside the tree.

Candidates require exactly one reference and one target method with matching
declaring type, scoped signature, method flags, generic constraints, normalized
IL, locals and exception handlers. Symbol scopes retain assembly name, culture
and public-key token while ignoring versions. A changed dependency scope rejects
a match even if type names are identical. Branch/macro encodings are normalized;
floating-point constants preserve their bits. Unknown operands are reported and
skipped. Constructors, virtual/accessor/PInvoke methods and very small bodies are
excluded, except for explicitly reviewed short methods in established declaring
type correspondences (described below). Ambiguous bodies are reported, never
resolved by enumeration order.

Unique local method pairs also provide call targets for subsequent matching
rounds, allowing callers to match when their callee names changed. Constructed
generic calls retain their type arguments. Direct recursive self calls compare
by their self-reference rather than their obfuscated name. Call targets outside
the module or represented by unresolved member references keep their symbolic
names and scopes. Ambiguous callees never establish a correspondence.

Rounds are simultaneous, deterministic and limited to eight; the scanner stops
earlier when no new changed-name pair is found. Reports include `MatchRound` and
the target metadata tokens of `MatchedCallees` used as evidence. Each round still
requires a unique complete fingerprint on both sides. This does not remove the
stable declaring-type/signature requirement or infer equivalence of dependencies.

The existing renamer tokenizer learns vocabulary from reference names and prose.
Only meaningful reference names are proposed; meaningful target names are kept.
Short lowercase names, generated placeholders and names positively assessed as
obfuscated may produce review candidates, not automatic edits. Unknown descriptive
names are retained.
Each candidate includes both physical identities, MVIDs, SHA-256 hashes, tokens,
full signatures and a fingerprint. Public/protected API candidates are marked.
Results are deterministic for unchanged inputs. Hashes describe the exact byte
snapshots scanned, so verify them against the current input before applying a map.
Byte-identical modules skip body analysis; branch targets use a per-method index
instead of repeated linear searches. Module snapshots and hashes are reused for
all candidates in that module.

Review call sites and intended behavior before accepting a suggestion. Matching
IL is evidence, not proof of original naming or behavior: dependency implementations,
custom attributes, resources and reflection may differ. This tool does not rename
binaries. Scanning only writes a candidate report.

After selecting candidates, keep just those entries in a copy of the report and
optionally edit their `SuggestedName`. Convert it to a dnSpy source-only map:

```powershell
dotnet tools/NameCandidates/bin/Release/net8.0/NameCandidates.dll --source-map D:\analysis\selected.json D:\analysis\target D:\analysis\source-names.xml
dnSpy.Console.exe --public-sign --source-name-map D:\analysis\source-names.xml -o D:\analysis\source <complete-input-assembly-list>
```

Conversion rechecks the target root, physical paths, assembly identities, MVIDs,
hashes, method tokens and full signatures. It rejects links, stale entries,
duplicate tokens, unsupported methods and invalid identifiers before writing.
Existing files are never overwritten. Names colliding with module declarations
receive deterministic suffixes (`Validate2`, `Validate3`, etc.); other candidates'
suggestions are reserved first. Review the resulting aliases before export.
The source map preserves runtime method names through dnSpy's SDK build task;
it is distinct from the binary name map described below. Supply the application's
configuration and dependency contexts when exporting complex application trees.

For binary changes, export the target inventory using `--name-map-export`, then
copy reviewed names into the corresponding physical module/token entries and run
`--name-map-preview` as described in [CUSTOM_NAMES.md](../../CUSTOM_NAMES.md).
For SDK-compatible dnSpy source aliases, use the target hash/MVID/token/signature
in a `SourceNameMap`; choose aliases unique throughout each module. dnSpy restores
the original runtime names during compilation. Do not apply binary name changes
to public APIs merely because a candidate was found.

Regression fixture (supply a new output directory):

```powershell
dotnet run --project tests/NameCandidates/NameCandidates.Tests.csproj -c Release -- D:\analysis\name-candidate-test
```

It covers duplicate physical identities with different bodies, changed assembly
versions, changed dependency scopes, ambiguous methods, meaningful target names,
native inputs, missing references, hash evidence, repeatability and no-overwrite.

The 2026-09-10 full-tree check compared 528 managed ETS 6.3 modules against
themselves and proposed zero names. Comparing 492 paired modules in ETS 6.4
against 6.3 produced ten candidates. All ten matched the physical paths, hashes,
MVIDs, tokens and signatures in the separately compiled/audited/live-tested
source11 map. One repeated `Validate` suggestion still needed a distinct source
alias in that map. Optimized and initial scans produced identical candidate
evidence. These are real-project checks of the generic scanner, not ETS-specific
rules or a claim that every obfuscated name has been recovered.

The call-correspondence follow-up retained all ten ETS candidates, without adding
new ones for this version pair. Its synthetic regression additionally recovers a
generic-call wrapper, a second-level caller and a recursive method, and rejects
changed/ambiguous callees with either declaration order. The unchanged 6.3
self-comparison still proposes zero names.

Local calls encoded as `MemberRef` on an instantiated generic declaring type
also participate in call correspondence, including generic `MethodSpec` calls.
Resolution requires a unique matching method signature on a TypeDef in the
same physical module; the scanner does not resolve external assemblies for
this step. Declaring-type and method generic arguments remain in the fingerprint.
The regression verifies caller recovery, changed generic argument rejection,
and independence from declaration order.

To use method names reviewed during the reference version's source export:

```powershell
dotnet tools/NameCandidates/bin/Release/net8.0/NameCandidates.dll --reference-source-map reference-names.xml reference-tree target-tree new-report.json
```

The reference map's input directory, physical module paths, hashes, MVIDs, method
tokens, original names and signatures are checked before matching. Its method
aliases seed vocabulary and suggested names; matching still uses the original
metadata and IL. Type/parameter aliases do not establish type correspondence.
Meaningful existing target names remain protected. Reports record the reference
map path and hash, and no input binaries are changed. This allows reviewed names
to inform later versions without renaming the reference assembly's SDK surface.

Reviewed static methods can also establish a declaring-type correspondence when
at least two distinct exact-body fingerprints are individually unique in both
physical modules and point to the same type pair. Conflicting pairs, differing
base/interface/generic contracts and unsupported bodies do not qualify. This
allows explicitly reviewed short methods to contribute evidence without broadly
renaming every short overload. Reports list the supporting token pairs in
`DeclaringTypeEvidence`; callers still require matching signatures, operands and
control flow. Existing meaningful target names are preserved even when the
reviewed reference uses another name. A reviewed source identifier need not be
present in the tokenizer's general vocabulary.

Fingerprints omit local-variable slots that no instruction references after
macro expansion, and number the remaining slots consistently. Referenced local
types and initialization behavior remain part of the comparison. This handles
unused locals left behind by control-flow restoration without accepting changes
to the types of locals that the method actually uses.

An unknown word is not evidence that a descriptive target name is obfuscated.
Automatic review candidates therefore require either a positive obfuscation
assessment, a short lowercase name, or a generated `method_`/`smethod_`/`vmethod_`/
`gmethod_` placeholder. Unknown descriptive names such as `ToMetricKey` remain
unchanged; an explicit user-authored source map can still select another alias.
Generated reference placeholders are not proposed as recovered names, even if
the word `method` itself is recognized by the tokenizer.

The subsequent full-tree scan using the reviewed 6.3 reference map produced 496
private-method candidates across 25 target modules in 6.4. None suggested a
generated placeholder as a readable name, and the existing `ToMetricKey` name
was retained. The current matcher still proposes zero names for the 528-module
6.3 self-comparison. These results supersede the earlier ten-candidate scan above;
they measure candidate recovery, not full runtime equivalence.
