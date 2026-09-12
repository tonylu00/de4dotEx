#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;

// Shared verbatim with dnSpy.Decompiler/MSBuild/FieldAliasScope.cs.
namespace SourceNameMapping {
    /// <summary>Conservative declaration scopes for reversible source field aliases.</summary>
    public sealed class FieldAliasScope {
        readonly Dictionary<TypeDef, HashSet<TypeDef>> related = new Dictionary<TypeDef, HashSet<TypeDef>>();
        readonly Dictionary<TypeDef, HashSet<TypeDef>> bases = new Dictionary<TypeDef, HashSet<TypeDef>>();
        readonly Dictionary<string, ModuleDef[]> identities;
        readonly bool useHostResolver;
        public static string Blocker(FieldDef field) {
            if (field.IsRuntimeSpecialName) return "runtime-special-field";
            if (field.DeclaringType == null || field.DeclaringType.IsGlobalModuleType) return "global-module-field";
            // These carry a second, storage-specific restoration instruction.
            // Do not accept a custom alias until its interaction is verified.
            if (field.HasFieldRVA || field.IsCompilerControlled || field.Constant != null && !field.IsLiteral)
                return "field-storage-restoration requires separate review";
            return null;
        }
        public FieldAliasScope(IEnumerable<ModuleDef> inputs, bool useHostResolver = false) {
            this.useHostResolver = useHostResolver;
            var modules = inputs.Distinct().ToArray();
            identities = modules.Where(m => m.Assembly != null).GroupBy(m => m.Assembly.FullName, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
            foreach (var type in modules.SelectMany(m => m.GetTypes())) {
                var ancestors = new HashSet<TypeDef>();
                var queue = new Queue<TypeDef>(); queue.Enqueue(type);
                while (queue.Count != 0) {
                    var next = queue.Dequeue();
                    if (!ancestors.Add(next)) continue;
                    foreach (var parent in Parents(next)) queue.Enqueue(parent);
                }
                bases[type] = ancestors;
                foreach (var ancestor in ancestors) {
                    if (!related.TryGetValue(ancestor, out var descendants)) related[ancestor] = descendants = new HashSet<TypeDef>();
                    descendants.Add(type);
                }
            }
        }
        IEnumerable<TypeDef> Parents(TypeDef type) {
            var reference = type.BaseType;
            if (reference is TypeSpec spec) reference = spec.TypeSig.RemovePinnedAndModifiers().ToTypeDefOrRef();
            if (reference is TypeSpec genericSpec && genericSpec.TypeSig.RemovePinnedAndModifiers() is GenericInstSig generic)
                reference = generic.GenericType.TypeDefOrRef;
            if (reference == null) yield break;
            if (reference is TypeDef definition) { yield return definition; yield break; }
            if (useHostResolver) {
                var resolved = reference.ResolveTypeDef();
                if (resolved != null) { yield return resolved; yield break; }
            }
            if (reference.DefinitionAssembly != null && identities.TryGetValue(reference.DefinitionAssembly.FullName, out var candidates)) {
                // Ambiguous compatibility inputs contribute reservations only.
                // This never binds a field reference to a guessed physical file.
                foreach (var module in candidates) {
                    var candidate = module.Find(reference.FullName, false);
                    if (candidate != null) yield return candidate;
                }
            }
        }
        public HashSet<TypeDef> Types(FieldDef field) {
            var result = new HashSet<TypeDef>();
            if (bases.TryGetValue(field.DeclaringType, out var ancestors)) result.UnionWith(ancestors);
            if (related.TryGetValue(field.DeclaringType, out var descendants)) result.UnionWith(descendants);
            result.Add(field.DeclaringType);
            return result;
        }
        public HashSet<string> Reserved(FieldDef field, Func<IMemberDef, string> alias) {
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var type in Types(field)) {
                var members = new IMemberDef[] { type }.Concat(type.Fields).Concat(type.Methods)
                    .Concat(type.Properties).Concat(type.Events).Concat(type.NestedTypes);
                foreach (var member in members) {
                    string original = member.Name.String;
                    if (member is TypeDef) original = original.Split('`')[0];
                    used.Add(original);
                    if (member != field) {
                        string requested = alias(member);
                        if (requested != null) used.Add(requested);
                    }
                }
                used.UnionWith(type.GenericParameters.Select(p => p.Name.String));
            }
            return used;
        }
    }
}
