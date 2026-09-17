# Init-only property restoration regression

Builds synthetic modules with dnlib (init-only setter carrying the
`IsExternalInit` return modifier, matching getter over the same readonly field)
and calls `de4dot.code.InitOnlyPropertyRestorer.Restore` directly, mirroring
NETReactorSlayer's `InitPropertyTests`.

Covered: unique proven pairs are restored with a collision-free
`RestoredProperty` name; idempotency; existing property metadata is preserved;
ten guard variants (missing/optional modifier, mutable field, missing
attributes, duplicate accessors, side effects, wrong types, virtual setters) are
not restored; the required init modifier survives metadata writing.

```text
dotnet run --project tests/ReactorInitProps -c Release -- <new-folder>
```

Batch-level coverage (--force-reactor across compatibility copies) is added with
the batch option port.
