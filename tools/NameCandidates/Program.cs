using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using de4dot.code.renamer;
using FileAttributes = System.IO.FileAttributes;

namespace De4dot.NameCandidates;

public static class Program {
    public static int Main(string[] args) {
        try {
            if (args.Length == 5 && args[0] == "--update-map")
                return ReviewMapUpdate.Run(args[1], args[2], args[3], args[4]) ? 0 : 1;
            if (args.Length == 5 && args[0] == "--merge-map")
                return ReviewMapUpdate.Run(args[1], args[2], args[3], args[4], true) ? 0 : 1;
            if (args.Length == 3 && args[0] == "--check-map")
                return ReviewMapUpdate.Run(null, args[1], null, args[2]) ? 0 : 1;
            if (args.Length == 1 && (args[0] == "--help" || args[0] == "-h")) {
                Console.WriteLine("NameCandidates (.NET 8, Windows/macOS/Linux)\n" +
                    "  --review <assembly-or-tree> <new-all-methods.json>\n" +
                    "  --review-obfuscated <assembly-or-tree> <new-worklist.json>\n" +
                    "  --review-types <assembly-or-tree> <new-type-review.json>\n" +
                    "  --review-families <assembly-or-tree> <new-contract-review.json>\n" +
                    "  --review-map <reviewed.json> <input-tree> <new-source-map.xml>\n" +
                    "  --update-map <reviewed.json> <base-map.xml> <new-map.xml> <new-report.json>\n" +
                    "  --merge-map <generated-method-map.xml> <base-map.xml> <new-map.xml> <new-report.json>\n" +
                    "  --check-map <map.xml> <new-report.json>\n" +
                    "  [--reference-source-map map.xml] <reference-tree> <target-tree> <new-candidates.json>\n" +
                    "  --source-map <selected-candidates.json> <target-tree> <new-source-map.xml>\n" +
                    "Edit NewName on Methods, Types or ParameterNames entries in a review inventory. MappingBlocker describes unsupported source contracts. Inputs are never executed or modified.");
                return 0;
            }
            if (args.Length == 3 && (args[0] == "--review" || args[0] == "--review-obfuscated")) {
                MethodReview.Write(args[1], args[2], args[0] == "--review-obfuscated");
                return 0;
            }
            if (args.Length == 3 && args[0] == "--review-types") {
                MethodReview.Write(args[1], args[2], typesOnly: true);
                return 0;
            }
            if (args.Length == 3 && args[0] == "--review-families") {
                FamilyReview.Write(args[1], args[2]);
                return 0;
            }
            if (args.Length == 4 && args[0] == "--review-map") {
                MethodReview.WriteMap(args[1], args[2], args[3]);
                return 0;
            }
            if (args.Length == 4 && args[0] == "--source-map") {
                SourceMapWriter.Write(args[1], args[2], args[3]);
                Console.WriteLine("Source map written; runtime names are restored by dnSpy's SDK build task.");
                return 0;
            }
            string referenceMap = null;
            if (args.Length == 5 && args[0] == "--reference-source-map") { referenceMap = args[1]; args = args.Skip(2).ToArray(); }
            if (args.Length != 3) throw new ArgumentException("Usage: NameCandidates [--reference-source-map map.xml] <reference-tree> <target-tree> <new-report.json>");
            var report = Scanner.Scan(args[0], args[1], referenceMap);
            using var output = new FileStream(args[2], FileMode.CreateNew, FileAccess.Write);
            JsonSerializer.Serialize(output, report, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine($"Compared {report.Modules} physical modules; {report.Candidates.Count} review candidates; {report.Skips.Count} skipped cases.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }
}

public sealed record Candidate(string Module, string ReferenceIdentity, string TargetIdentity,
    string ReferenceMvid, string TargetMvid, string ReferenceHash, string TargetHash,
    string ReferenceToken, string TargetToken, string Reference, string Target, string SuggestedName,
    string Fingerprint, bool ExternallyVisible) {
    public int MatchRound { get; init; } = 1;
    public string[] MatchedCallees { get; init; } = Array.Empty<string>();
    public string[] DeclaringTypeEvidence { get; init; } = Array.Empty<string>();
}
public sealed record Skip(string Module, string Reason, string Token = null);
public sealed class Report {
    public int Format { get; } = 1;
    public string ReferenceRoot { get; init; }
    public string TargetRoot { get; init; }
    public string ReferenceSourceMap { get; init; }
    public string ReferenceSourceMapHash { get; init; }
    public int Modules { get; set; }
    public List<Candidate> Candidates { get; init; } = new();
    public List<Skip> Skips { get; } = new();
}

public static class Scanner {
    static string Json(object value) => JsonSerializer.Serialize(value);
    static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    // Versions intentionally differ across reference builds. Assembly name,
    // culture and key remain part of each symbol's scope.
    static string Assembly(IAssembly a) => a == null ? "" : Json(new[] {
        a.Name.String, a.Culture.String, a.PublicKeyOrToken?.Token?.ToString() ?? "" });
    static string Scope(ITypeDefOrRef t) => t == null ? "" : Json(new[] { t.FullName, Assembly(t.DefinitionAssembly) });
    static string Type(TypeSig t) => t == null ? "" : Json(new object[] {
        t.ElementType.ToString(), t.FullName, Scope(t.ToTypeDefOrRef()), Type(t.Next),
        t is GenericInstSig g ? g.GenericArguments.Select(Type).ToArray() : Array.Empty<string>(),
        t is ModifierSig mod ? Scope(mod.Modifier) : "",
        t is FnPtrSig fn ? Signature(fn.Signature as MethodSig) : "" });
    static string Signature(MethodSig s) => s == null ? "" : Json(new object[] {
        s.CallingConvention.ToString(), s.GenParamCount, Type(s.RetType), s.Params.Select(Type).ToArray(),
        s.ParamsAfterSentinel?.Select(Type).ToArray() ?? Array.Empty<string>() });
    static bool Eligible(MethodDef m) => Matchable(m) && m.Body.Instructions.Count >= 6;
    static bool Matchable(MethodDef m) => m.HasBody && !m.IsConstructor && !m.IsVirtual &&
        !m.IsSpecialName && !m.IsPinvokeImpl && !m.HasOverrides && m.Body.Instructions.Count >= 3;
    static bool Placeholder(string name) => Regex.IsMatch(name, @"\A[gsv]?method_[0-9]+\z");
    static bool NeedsReadableName(string name, ISet<string> vocabulary) => MethodReview.Reason(name, vocabulary) != null;

    // Shared with full-source regression audits. This compares normalized IL,
    // not semantic equivalence: assembly versions and unused locals are omitted,
    // while dependency identity, signatures, flags and exception regions remain.
    // Macro/branch normalization mutates the supplied in-memory body; callers
    // auditing a binary must load a disposable module and never write it back.
    public static string Fingerprint(MethodDef m) => ContextFingerprint(m, null);
    static MethodDef LocalDefinition(IMethod method) {
        if (method is MethodDef definition) return definition;
        if (method is MethodSpec specification) return LocalDefinition(specification.Method);
        if (method is not MemberRef member || !member.IsMethodRef) return null;
        // Generic declaring types use MemberRef even for calls inside this module.
        // Resolve only embedded TypeDefs, never an assembly resolver: a sibling
        // compatibility directory may contain the same identity with another body.
        var type = member.Class as TypeDef;
        if (member.Class is TypeSpec { TypeSig: GenericInstSig generic })
            type = generic.GenericType.TypeDefOrRef as TypeDef;
        if (type == null || type.Module != member.Module) return null;
        var comparer = new SigComparer();
        var matches = type.Methods.Where(m => m.Name == member.Name && comparer.Equals(m.MethodSig, member.MethodSig)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
    static string ContextFingerprint(MethodDef m, IReadOnlyDictionary<MethodDef, string> anchors,
        IReadOnlyDictionary<TypeDef, string> typeAnchors = null, bool omitOwner = false) {
        m.Body.SimplifyMacros(m.Parameters);
        m.Body.SimplifyBranches();
        var il = m.Body.Instructions;
        // Obfuscation cleanup can leave unreferenced local slots. They do not
        // participate in the IL computation; retain every referenced slot/type.
        var referencedLocals = new HashSet<Local>(il.Select(i => i.Operand).OfType<Local>());
        var locals = m.Body.Variables.Where(referencedLocals.Contains).ToArray();
        var localPositions = locals.Select((local, index) => (local, index)).ToDictionary(p => p.local, p => p.index);
        var positions = il.Select((instruction, index) => (instruction, index)).ToDictionary(p => p.instruction, p => p.index);
        string OwnerScope(ITypeDefOrRef type) => type is TypeDef definition && typeAnchors != null && typeAnchors.TryGetValue(definition, out var anchor) ?
            Json(new[] { "matched-type", anchor, Assembly(type.DefinitionAssembly) }) : Scope(type);
        string MethodIdentity(IMethod method) {
            var definition = LocalDefinition(method);
            if (anchors != null && definition == m) return Json(new[] { "self" });
            if (anchors != null && definition != null && anchors.TryGetValue(definition, out var anchor)) return Json(new[] { "matched", anchor });
            return Json(new[] { "name", method.Name.String });
        }
        string Operand(object operand) => operand switch {
            null => "",
            Instruction i => Json(new object[] { "branch", positions[i] }),
            IList<Instruction> list => Json(new object[] { "switch", list.Select(i => positions[i]).ToArray() }),
            Local l => "local:" + localPositions[l],
            Parameter p => "argument:" + p.Index,
            IMethod r => Json(new object[] { "method", MethodIdentity(r), OwnerScope(r.DeclaringType), Signature(r.MethodSig),
                r is MethodSpec ms ? ms.GenericInstMethodSig.GenericArguments.Select(Type).ToArray() : Array.Empty<string>() }),
            IField f => Json(new[] { "field", f.Name.String, Scope(f.DeclaringType), Type(f.FieldSig.Type) }),
            ITypeDefOrRef t => Json(new[] { "type", Type(t.ToTypeSig()) }),
            string s => Json(new[] { "string", s }),
            float f => "float-bits:" + BitConverter.SingleToInt32Bits(f),
            double d => "double-bits:" + BitConverter.DoubleToInt64Bits(d),
            IFormattable f => operand.GetType().FullName + ":" + f.ToString(null, CultureInfo.InvariantCulture),
            _ => throw new NotSupportedException("Unsupported IL operand " + operand.GetType().Name)
        };
        var body = Json(new object[] {
            omitOwner ? Assembly(m.Module.Assembly) : OwnerScope(m.DeclaringType), Signature(m.MethodSig), (int)m.Attributes, (int)m.ImplAttributes,
            m.GenericParameters.Select(p => new object[] { (int)p.Flags, p.GenericParamConstraints.Select(c => Scope(c.Constraint)).ToArray() }).ToArray(),
            locals.Length != 0 && m.Body.InitLocals, locals.Select(v => Type(v.Type)).ToArray(),
            il.Select(i => new[] { i.OpCode.Code.ToString(), Operand(i.Operand) }).ToArray(),
            m.Body.ExceptionHandlers.Select(h => new[] { h.HandlerType.ToString(), Operand(h.TryStart), Operand(h.TryEnd),
                Operand(h.HandlerStart), Operand(h.HandlerEnd), Operand(h.FilterStart), Operand(h.CatchType) }).ToArray()
        });
        return Hash(Encoding.UTF8.GetBytes(body));
    }

    static IEnumerable<string> Files(string root) {
        // Do not leave a tree through junctions or follow cycles.
        foreach (string path in Directory.EnumerateFileSystemEntries(root).OrderBy(p => p, StringComparer.Ordinal)) {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked input is not supported: " + path);
            if ((attributes & FileAttributes.Directory) != 0) { foreach (var child in Files(path)) yield return child; }
            else if (Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
                     Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)) yield return path;
        }
    }
    static bool Visible(TypeDef t) => t.IsNested ? (t.IsNestedPublic || t.IsNestedFamily || t.IsNestedFamilyOrAssembly) && Visible(t.DeclaringType) : t.IsPublic;

    public static Report Scan(string referenceRoot, string targetRoot, string referenceSourceMap = null) {
        referenceRoot = Path.GetFullPath(referenceRoot); targetRoot = Path.GetFullPath(targetRoot);
        if (!Directory.Exists(referenceRoot) || !Directory.Exists(targetRoot)) throw new DirectoryNotFoundException("Both input trees must exist.");
        if ((File.GetAttributes(referenceRoot) & FileAttributes.ReparsePoint) != 0 || (File.GetAttributes(targetRoot) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Linked input roots are not supported.");
        byte[] referenceMapBytes = referenceSourceMap == null ? null : File.ReadAllBytes(referenceSourceMap);
        var report = new Report { ReferenceRoot = referenceRoot, TargetRoot = targetRoot,
            ReferenceSourceMap = referenceSourceMap == null ? null : Path.GetFullPath(referenceSourceMap),
            ReferenceSourceMapHash = referenceMapBytes == null ? null : Hash(referenceMapBytes) };
        var referenceNames = referenceMapBytes == null ? new Dictionary<string, SourceMapWriter.ReferenceNames>(InputPaths.Comparer) :
            SourceMapWriter.ReadReferenceNames(referenceMapBytes, referenceRoot);
        // Pair by relative path, never by simple assembly name: compatibility
        // directories can contain unrelated implementations of the same identity.
        var references = Files(referenceRoot).ToDictionary(p => InputPaths.Relative(referenceRoot, p), InputPaths.Comparer);
        foreach (var targetPath in Files(targetRoot)) {
            string relative = InputPaths.Relative(targetRoot, targetPath);
            if (!references.TryGetValue(relative, out var referencePath)) { report.Skips.Add(new(relative, "No reference at the same physical path")); continue; }
            var aBytes = File.ReadAllBytes(referencePath); var bBytes = File.ReadAllBytes(targetPath);
            if (referenceNames.TryGetValue(relative, out var checkedNames) && checkedNames.Hash != Hash(aBytes))
                throw new InvalidDataException("Reference module changed after source-map validation.");
            ModuleDefMD a = null, b = null;
            try {
                try { a = ModuleDefMD.Load(aBytes); b = ModuleDefMD.Load(bBytes); }
                catch (BadImageFormatException) { report.Skips.Add(new(relative, "Non-managed or invalid module")); continue; }
                if (a.Assembly == null || b.Assembly == null || Assembly(a.Assembly) != Assembly(b.Assembly)) {
                    report.Skips.Add(new(relative, "Assembly name, culture or key differs")); continue;
                }
                report.Modules++;
                if (aBytes.AsSpan().SequenceEqual(bBytes) && !referenceNames.ContainsKey(relative)) continue;
                referenceNames.TryGetValue(relative, out var moduleNames);
                var typeAnchors = new Dictionary<TypeDef, string>();
                var typeEvidence = new Dictionary<TypeDef, string[]>();
                if (moduleNames != null) {
                    var referenceSeeds = a.GetTypes().SelectMany(t => t.Methods).Where(m => Matchable(m) && m.IsStatic && moduleNames.Names.ContainsKey(m.MDToken.Raw));
                    var targetSeeds = b.GetTypes().SelectMany(t => t.Methods).Where(m => Matchable(m) && m.IsStatic);
                    string SeedKey(MethodDef method) => ContextFingerprint(method, null, null, true);
                    ILookup<string, MethodDef> SeedIndex(IEnumerable<MethodDef> methods) {
                        var entries = new List<(string Key, MethodDef Method)>();
                        foreach (var method in methods) {
                            try { entries.Add((SeedKey(method), method)); }
                            catch (NotSupportedException) { /* Unsupported bodies cannot establish a type match. */ }
                        }
                        return entries.ToLookup(p => p.Key, p => p.Method);
                    }
                    var oldSeeds = SeedIndex(referenceSeeds);
                    var newSeeds = SeedIndex(targetSeeds);
                    var seeds = oldSeeds.Where(g => g.Count() == 1 && newSeeds[g.Key].Count() == 1)
                        .Select(g => (Reference: g.Single(), Target: newSeeds[g.Key].Single())).ToArray();
                    foreach (var group in seeds.GroupBy(p => (p.Reference.DeclaringType, p.Target.DeclaringType))) {
                        var referenceType = group.Key.Item1; var targetType = group.Key.Item2;
                        if (group.Count() < 2 || referenceType.FullName == targetType.FullName ||
                            referenceType.IsValueType != targetType.IsValueType || referenceType.IsInterface != targetType.IsInterface ||
                            referenceType.GenericParameters.Count != targetType.GenericParameters.Count || Scope(referenceType.BaseType) != Scope(targetType.BaseType) ||
                            Json(referenceType.Interfaces.Select(i => Scope(i.Interface)).OrderBy(s => s, StringComparer.Ordinal)) != Json(targetType.Interfaces.Select(i => Scope(i.Interface)).OrderBy(s => s, StringComparer.Ordinal)) ||
                            Json(referenceType.GenericParameters.Select(p => new object[] { (int)p.Flags, p.GenericParamConstraints.Select(c => Scope(c.Constraint)).OrderBy(s => s, StringComparer.Ordinal).ToArray() })) !=
                                Json(targetType.GenericParameters.Select(p => new object[] { (int)p.Flags, p.GenericParamConstraints.Select(c => Scope(c.Constraint)).OrderBy(s => s, StringComparer.Ordinal).ToArray() })) ||
                            seeds.Any(p => p.Reference.DeclaringType == referenceType && p.Target.DeclaringType != targetType ||
                                p.Target.DeclaringType == targetType && p.Reference.DeclaringType != referenceType)) continue;
                        var evidence = group.Select(p => p.Reference.MDToken + ":" + p.Target.MDToken).OrderBy(s => s, StringComparer.Ordinal).ToArray();
                        string key = referenceType.MDToken + ":" + targetType.MDToken;
                        typeAnchors.Add(referenceType, key); typeAnchors.Add(targetType, key);
                        typeEvidence.Add(targetType, evidence);
                    }
                }
                var before = a.GetTypes().SelectMany(t => t.Methods).Where(m => Eligible(m) || Matchable(m) && moduleNames != null && moduleNames.Names.ContainsKey(m.MDToken.Raw)).ToArray();
                var after = b.GetTypes().SelectMany(t => t.Methods).Where(m => Eligible(m) || Matchable(m) && typeAnchors.ContainsKey(m.DeclaringType)).ToArray();
                string ReferenceName(MethodDef method) => moduleNames != null && moduleNames.Names.TryGetValue(method.MDToken.Raw, out var alias) ? alias : method.Name.String;
                var vocabulary = MethodNameAnalysis.Learn(before.Select(ReferenceName),
                    before.SelectMany(m => m.Body.Instructions).Where(i => i.OpCode == OpCodes.Ldstr).Select(i => (string)i.Operand));
                var anchors = new Dictionary<MethodDef, string>();
                var paired = new HashSet<MethodDef>();
                var pairs = new List<(MethodDef Reference, MethodDef Target, string Key, int Round, string[] Callees)>();
                var skipped = new HashSet<string>(StringComparer.Ordinal);
                ILookup<string, MethodDef> Index(IEnumerable<MethodDef> methods) {
                    var entries = new List<(string Key, MethodDef Method)>();
                    foreach (var method in methods) {
                        try { entries.Add((ContextFingerprint(method, anchors, typeAnchors), method)); }
                        catch (NotSupportedException ex) {
                            if (skipped.Add(ex.Message + method.MDToken)) report.Skips.Add(new(relative, ex.Message, method.MDToken.ToString()));
                        }
                    }
                    return entries.ToLookup(e => e.Key, e => e.Method);
                }
                // Only proven unique pairs seed the next round. Do not mutate
                // anchors during a round: results must not depend on token order.
                for (int round = 1; round <= 8; round++) {
                    var old = Index(before); var next = Index(after);
                    var additions = new List<(MethodDef Reference, MethodDef Target, string Key, int Round, string[] Callees)>();
                    foreach (var group in next) {
                        var matches = old[group.Key].ToArray();
                        if (matches.Length == 0) continue;
                        if (matches.Length != 1 || group.Count() != 1) {
                            if (skipped.Add(group.Key)) report.Skips.Add(new(relative, "Ambiguous fingerprint " + group.Key));
                            continue;
                        }
                        var target = group.Single(); var reference = matches[0];
                        if (paired.Contains(reference) || paired.Contains(target)) continue;
                        var callees = target.Body.Instructions.Select(i => i.Operand as IMethod).Where(r => r != null)
                            .Select(LocalDefinition).Where(d => d != null && anchors.ContainsKey(d)).Select(d => d.MDToken.ToString()).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToArray();
                        additions.Add((reference, target, group.Key, round, callees));
                    }
                    if (additions.Count == 0) break;
                    int previousAnchors = anchors.Count;
                    foreach (var pair in additions) {
                        paired.Add(pair.Reference); paired.Add(pair.Target);
                        // Equal names already compare equally. Only changed
                        // names can unlock an additional caller match.
                        if (pair.Reference.Name == pair.Target.Name) continue;
                        string anchor = pair.Reference.MDToken + ":" + pair.Target.MDToken;
                        anchors.Add(pair.Reference, anchor); anchors.Add(pair.Target, anchor);
                    }
                    pairs.AddRange(additions);
                    if (anchors.Count == previousAnchors) break;
                }
                string aHash = Hash(aBytes), bHash = Hash(bBytes);
                foreach (var pair in pairs) {
                    var method = pair.Target; var reference = pair.Reference;
                    string suggestedName = ReferenceName(reference);
                    bool reviewedName = moduleNames != null && moduleNames.Names.ContainsKey(reference.MDToken.Raw);
                    if (method.Name == suggestedName || !reviewedName && (Placeholder(suggestedName) || MethodNameAnalysis.Analyze(suggestedName, vocabulary) != MethodNameAnalysis.Assessment.Meaningful) ||
                        !NeedsReadableName(method.Name.String, vocabulary)) continue;
                    report.Candidates.Add(new(relative, a.Assembly.FullName, b.Assembly.FullName, a.Mvid.ToString(), b.Mvid.ToString(), aHash, bHash,
                        reference.MDToken.ToString(), method.MDToken.ToString(), reference.FullName, method.FullName, suggestedName, pair.Key,
                        Visible(method.DeclaringType) && (method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly)) { MatchRound = pair.Round, MatchedCallees = pair.Callees,
                            DeclaringTypeEvidence = typeEvidence.TryGetValue(method.DeclaringType, out var evidence) ? evidence : Array.Empty<string>() });
                }
            }
            finally { b?.Dispose(); a?.Dispose(); }
        }
        return report;
    }
}
