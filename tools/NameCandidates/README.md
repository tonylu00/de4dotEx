# Cross-version name candidates

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
