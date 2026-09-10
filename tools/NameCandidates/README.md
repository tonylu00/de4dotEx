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
excluded. Ambiguous bodies are reported, never resolved by enumeration order.

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
Short or unknown target names may produce review candidates, not automatic edits.
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
