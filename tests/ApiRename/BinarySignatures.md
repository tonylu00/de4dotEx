# Batch signature preservation

The default API-preserving batch mode now retains declared field and method
types during the .NET Reactor 3/4 type-inference stage. That stage operates on
one module at a time: narrowing an object parameter or return type changes its
binary signature without updating callers in other modules. Renamer API guards
run later and cannot undo that change. Even private methods can have MemberRefs
on generic instantiations which the module-local restorer does not update.

BatchProcessor sets a per-run BinaryContractPolicy before deobfuscation, and
clears it on success or failure. DeobfuscatorBase captures it at Begin so that
clearing the file context during method processing cannot lose the policy.
This applies to inferred private signatures too,
since they participate in the same reference graph. Existing standalone
type-restoration behavior is unchanged. Disabling API preservation permits the
legacy inference; batch graph validation still rejects unresolved callers.

Run `Invoke-ContractPolicyRegression.ps1 -De4dot <exe> -OutputDirectory <new-folder>`
to verify context loss during method processing, standalone behavior and reuse
with a different policy. The complete batch regression verifies that Reactor
actually obeys the captured policy during its final transformation stage.

Reproduction: process a Reactor-protected library plus an unchanged consumer in
a batch, with a method whose object parameter can be inferred as a concrete
type. Before this guard, inference changes the declaration before graph binding
and the consumer's old MethodRef cannot resolve. Default batch mode must retain
the declaration and let the consumer bind without patching its API expectations.

The ETS 6.4 regression used a fresh Slayer tree retaining original names/types.
Its initial de4dot pass failed on EtecDOM public calls, a ViewModel undo-command
factory call, and generic self MemberRefs. The complete default batch is the
integration regression for this guard. The separate ApiRename regression checks
unchanged binary caller behavior, meaningful overload preservation,
private obfuscated names and rename-journal hashes.

With the captured policy, the 6.4 batch completed: 108 protected assemblies,
445 managed assemblies copied unchanged. Comparing all 553 managed inputs and
outputs found zero assembly-identity or public-contract differences across
59,007 visible types, 465,168 methods and 90,043 fields. This does not establish
method-body or full application equivalence. The ApiRename regression also
passed after the change.
