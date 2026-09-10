# Batch vocabulary and readable overloads

The API-preserving renamer collects vocabulary once before renaming definitions.
It uses the current batch's type, method, property and field names plus ldstr
literals. Learning does not persist across batches or recursively promote new
seed names. A seed name must already assess as meaningful using the base lexicon.
A token needs two distinct seed names, or one seed name and a plain-text literal.
Repeated overloads with the same name supply only one piece of evidence.

Evidence is lexical, not proof of the author's intended name. Unsupported names
still defer to the protector-specific checker. Short obfuscated overloads remain
eligible for renaming. Common device/testing/license terms recognize meaningful
names such as IsDeviceTesterLicensed; project acronyms such as ETS are learned.
The journal's NameAssessment remains the base lexical assessment, independent of
learned vocabulary; actual OldName/NewName records describe saved output changes.

Invoke-Regression.ps1 tests standalone assessments, name/literal corroboration,
repeated-overload isolation, batch isolation, input-order independence, private
learned acronym overloads and unchanged external callers of meaningful overloads.
It also checks private short/gibberish renaming and saved journal hashes.

ETS 6.3 probe over 76 input modules found 99,443 distinct names and 45,161 distinct
strings. It learned ETS and assessed all seven IsDeviceTesterLicensed occurrences
as meaningful. These are input-analysis results, not a regenerated full-tree or
ETS runtime compatibility result.
