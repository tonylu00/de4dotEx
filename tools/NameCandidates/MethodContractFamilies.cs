#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using dnlib.DotNet;

// Shared verbatim with dnSpy.Decompiler/MSBuild/MethodContractFamilies.cs.
// Metadata only: groups CLR contracts without renaming or executing inputs.
namespace SourceNameMapping {
    public sealed class MethodContractFamilies {
        public sealed class Family {
            public string Id { get; internal set; }
            public MethodDef[] Methods { get; internal set; }
            public string[] Blockers { get; internal set; }
        }
        sealed class Use {
            public TypeDef Type;
            public IList<TypeSig> Arguments;
            public Use(TypeDef type, IList<TypeSig> arguments = null) { Type = type; Arguments = arguments; }
        }
        readonly HashSet<ModuleDef> modules;
        readonly Dictionary<string, ModuleDef[]> identities;
        readonly Func<ModuleDef, string> moduleKey;
        readonly bool useHostResolver;
        readonly Dictionary<MethodDef, MethodDef> parents = new Dictionary<MethodDef, MethodDef>();
        readonly Dictionary<MethodDef, HashSet<string>> blockers = new Dictionary<MethodDef, HashSet<string>>();
        readonly Dictionary<MethodDef, Family> families = new Dictionary<MethodDef, Family>();
        public IEnumerable<Family> Families => families.Values.Distinct().OrderBy(f => f.Id, StringComparer.Ordinal);
        public Family Get(MethodDef method) => families.TryGetValue(method, out var family) ? family : null;
        public static string MethodBlocker(MethodDef method) {
            if (method.IsConstructor) return "constructor";
            if (method.IsRuntimeSpecialName || method.SemanticsAttributes != 0 || method.Name.String.StartsWith("op_", StringComparison.Ordinal)) return "accessor-operator-or-runtime-special-name";
            var owner = method.DeclaringType;
            if (owner != null && (owner.Properties.Any(p => p.GetMethods.Contains(method) || p.SetMethods.Contains(method) || p.OtherMethods.Contains(method)) ||
                owner.Events.Any(e => e.AddMethod == method || e.RemoveMethod == method || e.InvokeMethod == method || e.OtherMethods.Contains(method))))
                return "property-or-event-accessor";
            // Obfuscators may remove the property/event row while leaving its
            // method's SpecialName bit. With no syntax contract, this is emitted
            // as an ordinary method; the build task restores the original flag.
            if (method.IsPinvokeImpl || method.IsRuntime) return "native-or-runtime-method";
            if (method.HasOverrides) return "explicit-method-implementation requires qualified source-name restoration";
            if (method.IsStatic && method.IsVirtual) return "static virtual contract";
            return null;
        }
        public MethodContractFamilies(IEnumerable<ModuleDef> inputs, Func<ModuleDef, string> key, bool useHostResolver = false) {
            modules = new HashSet<ModuleDef>(inputs);
            moduleKey = key;
            this.useHostResolver = useHostResolver;
            identities = modules.Where(m => m.Assembly != null).GroupBy(m => m.Assembly.FullName, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
            var types = modules.SelectMany(m => m.GetTypes()).ToArray();
            foreach (var method in types.SelectMany(t => t.Methods).Where(m => m.IsVirtual || m.HasOverrides)) Root(method);
            foreach (var type in types) {
                var hierarchy = Hierarchy(new Use(type)).ToArray();
                foreach (var method in type.Methods.Where(m => m.IsVirtual && !m.IsNewSlot)) {
                    bool found = false;
                    foreach (var parent in hierarchy.Skip(1)) {
                        var matches = parent.Type.Methods.Where(m => m.IsVirtual && m.Name == method.Name && Same(m, parent.Arguments, method, null)).ToArray();
                        if (matches.Length > 1) { Block(method, "ambiguous base slot"); break; }
                        if (matches.Length == 1) { Join(method, matches[0]); found = true; break; }
                    }
                    if (!found) Block(method, "unresolved or external base slot");
                }
                foreach (var method in type.Methods.Where(m => m.HasOverrides)) foreach (var implementation in method.Overrides) {
                    var declaration = implementation.MethodDeclaration;
                    var use = Resolve(declaration.DeclaringType, type.Module);
                    if (use == null) { Block(method, "unresolved explicit contract"); continue; }
                    var matches = use.Type.Methods.Where(m => m.Name == declaration.Name &&
                        new SigComparer().Equals(Substitute(m.MethodSig, use.Arguments), Substitute(declaration.MethodSig, use.Arguments))).ToArray();
                    if (matches.Length == 1) Join(method, matches[0]); else Block(method, "ambiguous explicit contract");
                }
                if (type.IsInterface) continue;
                var visited = new HashSet<string>(StringComparer.Ordinal);
                foreach (var owner in hierarchy) foreach (var item in owner.Type.Interfaces)
                    VisitInterface(Substitute(item.Interface.ToTypeSig(), owner.Arguments)?.ToTypeDefOrRef(), owner.Type.Module, hierarchy, visited, type);
            }
            foreach (var group in parents.Keys.ToArray().GroupBy(Root)) {
                var members = group.OrderBy(m => moduleKey(m.Module), StringComparer.Ordinal).ThenBy(m => m.MDToken.Raw).ToArray();
                string evidence = string.Join("\n", members.Select(m => moduleKey(m.Module).Replace('\\', '/') + "|" + m.MDToken.Raw.ToString("X8")));
                string id;
                using (var hash = SHA256.Create()) id = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(evidence))).Replace("-", "");
                var reasons = members.Select(MethodBlocker).Where(s => s != null).Concat(members.SelectMany(m => blockers[m])).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToArray();
                var family = new Family { Id = id, Methods = members, Blockers = reasons };
                foreach (var method in members) families.Add(method, family);
            }
        }
        MethodDef Root(MethodDef method) {
            if (!parents.TryGetValue(method, out var parent)) {
                parents.Add(method, method); blockers.Add(method, new HashSet<string>(StringComparer.Ordinal)); return method;
            }
            return parent == method ? method : parents[method] = Root(parent);
        }
        void Block(MethodDef method, string reason) { Root(method); blockers[method].Add(reason); }
        void Join(MethodDef left, MethodDef right) {
            if (!modules.Contains(right.Module)) { Block(left, "external contract: " + right.FullName); return; }
            var a = Root(left); var b = Root(right);
            if (a != b) parents[b] = a;
        }
        IEnumerable<Use> Hierarchy(Use current) {
            var seen = new HashSet<TypeDef>();
            for (; current != null && seen.Add(current.Type); current = Resolve(Substitute(current.Type.BaseType?.ToTypeSig(), current.Arguments)?.ToTypeDefOrRef(), current.Type.Module))
                yield return current;
        }
        void VisitInterface(ITypeDefOrRef reference, ModuleDef caller, Use[] hierarchy, HashSet<string> visited, TypeDef implementingType) {
            if (reference == null || !visited.Add((reference.DefinitionAssembly?.FullName ?? "") + "|" + reference.FullName)) return;
            var use = Resolve(reference, caller);
            if (use == null) {
                // An unresolved interface could bind any public virtual slot on
                // this hierarchy. Keep those families visible but uneditable.
                foreach (var method in hierarchy.SelectMany(t => t.Type.Methods).Where(m => m.IsPublic && m.IsVirtual && modules.Contains(m.Module)))
                    Block(method, "unresolved interface: " + reference.FullName);
                return;
            }
            foreach (var contract in use.Type.Methods.Where(m => !m.IsStatic)) {
                bool explicitMatch = hierarchy.Any(h => h.Type.Methods.Any(m => m.Overrides.Any(o => {
                    var declaration = o.MethodDeclaration;
                    if (declaration.Name != contract.Name) return false;
                    var declaredUse = Resolve(Substitute(declaration.DeclaringType.ToTypeSig(), h.Arguments)?.ToTypeDefOrRef(), h.Type.Module);
                    return declaredUse?.Type == contract.DeclaringType && new SigComparer().Equals(
                        Substitute(declaration.MethodSig, declaredUse.Arguments), Substitute(contract.MethodSig, use.Arguments));
                })));
                if (explicitMatch) continue;
                bool found = false;
                foreach (var owner in hierarchy) {
                    var matches = owner.Type.Methods.Where(m => m.IsPublic && !m.IsStatic && m.Name == contract.Name && Same(m, owner.Arguments, contract, use.Arguments)).ToArray();
                    if (matches.Length > 1) { if (modules.Contains(contract.Module)) Block(contract, "ambiguous implicit implementation: " + implementingType.FullName); found = true; break; }
                    if (matches.Length == 1) {
                        if (modules.Contains(matches[0].Module)) Join(matches[0], contract);
                        else if (modules.Contains(contract.Module)) Block(contract, "external inherited implementation: " + matches[0].FullName);
                        found = true; break;
                    }
                }
                if (!found && !implementingType.IsAbstract && modules.Contains(contract.Module)) Block(contract, "missing implementation: " + implementingType.FullName);
            }
            foreach (var parent in use.Type.Interfaces)
                VisitInterface(Substitute(parent.Interface.ToTypeSig(), use.Arguments)?.ToTypeDefOrRef(), use.Type.Module, hierarchy, visited, implementingType);
        }
        Use Resolve(ITypeDefOrRef reference, ModuleDef caller) {
            if (reference == null) return null;
            IList<TypeSig> arguments = null;
            if (reference is TypeSpec spec) {
                if (!(spec.TypeSig.RemovePinnedAndModifiers() is GenericInstSig generic)) return null;
                arguments = generic.GenericArguments; reference = generic.GenericType.TypeDefOrRef;
            }
            if (reference is TypeDef definition) return new Use(definition, arguments);
            // Respect a host's already-resolved physical context first.
            var resolved = reference.ResolveTypeDef();
            if (useHostResolver && resolved != null && modules.Contains(resolved.Module)) return new Use(resolved, arguments);
            if (reference.DefinitionAssembly != null && identities.TryGetValue(reference.DefinitionAssembly.FullName, out var candidates)) {
                if (candidates.Length > 1) {
                    string directory = Path.GetDirectoryName(moduleKey(caller));
                    var local = candidates.Where(m => string.Equals(Path.GetDirectoryName(moduleKey(m)), directory,
                        Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)).ToArray();
                    if (local.Length == 1) candidates = local;
                    else return null; // Never choose a duplicate identity by enumeration order.
                }
                var type = candidates[0].Find(reference.FullName, false);
                return type == null ? null : new Use(type, arguments);
            }
            return resolved == null ? null : new Use(resolved, arguments);
        }
        static bool Same(MethodDef left, IList<TypeSig> leftArguments, MethodDef right, IList<TypeSig> rightArguments) =>
            new SigComparer().Equals(Substitute(left.MethodSig, leftArguments), Substitute(right.MethodSig, rightArguments));
        static MethodSig Substitute(MethodSig sig, IList<TypeSig> arguments) {
            if (sig == null || arguments == null || arguments.Count == 0) return sig;
            var result = new MethodSig(sig.CallingConvention) { GenParamCount = sig.GenParamCount, RetType = Substitute(sig.RetType, arguments) };
            foreach (var parameter in sig.Params) result.Params.Add(Substitute(parameter, arguments));
            if (sig.ParamsAfterSentinel != null) result.ParamsAfterSentinel = sig.ParamsAfterSentinel.Select(t => Substitute(t, arguments)).ToList();
            return result;
        }
        static TypeSig Substitute(TypeSig sig, IList<TypeSig> arguments, int depth = 0) {
            if (sig == null || arguments == null || arguments.Count == 0) return sig;
            if (depth > 100) throw new InvalidDataException("Cyclic generic contract signature.");
            if (sig is GenericVar variable) return variable.Number < arguments.Count ? arguments[(int)variable.Number] : sig;
            TypeSig Next() => Substitute(sig.Next, arguments, depth + 1);
            if (sig is GenericInstSig generic) return new GenericInstSig(generic.GenericType, generic.GenericArguments.Select(a => Substitute(a, arguments, depth + 1)).ToArray());
            if (sig is SZArraySig) return new SZArraySig(Next());
            if (sig is ArraySig array) return new ArraySig(Next(), array.Rank, array.Sizes, array.LowerBounds);
            if (sig is ByRefSig) return new ByRefSig(Next());
            if (sig is PtrSig) return new PtrSig(Next());
            if (sig is CModReqdSig required) return new CModReqdSig(required.Modifier, Next());
            if (sig is CModOptSig optional) return new CModOptSig(optional.Modifier, Next());
            if (sig is PinnedSig) return new PinnedSig(Next());
            if (sig is FnPtrSig || sig is ValueArraySig || sig is ModuleSig) throw new InvalidDataException("Unsupported generic contract signature element.");
            return sig;
        }
    }
}
