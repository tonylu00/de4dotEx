using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using de4dot.code.renamer;
using FileAttributes = System.IO.FileAttributes;

namespace De4dot.NameCandidates;

public static class Program {
    public static int Main(string[] args) {
        try {
            if (args.Length == 4 && args[0] == "--source-map") {
                SourceMapWriter.Write(args[1], args[2], args[3]);
                Console.WriteLine("Source map written; runtime names are restored by dnSpy's SDK build task.");
                return 0;
            }
            if (args.Length != 3) throw new ArgumentException("Usage: NameCandidates <reference-tree> <target-tree> <new-report.json>");
            var report = Scanner.Scan(args[0], args[1]);
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
}
public sealed record Skip(string Module, string Reason, string Token = null);
public sealed class Report {
    public int Format { get; } = 1;
    public string ReferenceRoot { get; init; }
    public string TargetRoot { get; init; }
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
    static bool Eligible(MethodDef m) => m.HasBody && !m.IsConstructor && !m.IsVirtual &&
        !m.IsSpecialName && !m.IsPinvokeImpl && !m.HasOverrides && m.Body.Instructions.Count >= 6;

    static string Fingerprint(MethodDef m) => ContextFingerprint(m, null);
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
    static string ContextFingerprint(MethodDef m, IReadOnlyDictionary<MethodDef, string> anchors) {
        m.Body.SimplifyMacros(m.Parameters);
        m.Body.SimplifyBranches();
        var il = m.Body.Instructions;
        var positions = il.Select((instruction, index) => (instruction, index)).ToDictionary(p => p.instruction, p => p.index);
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
            Local l => "local:" + l.Index,
            Parameter p => "argument:" + p.Index,
            IMethod r => Json(new object[] { "method", MethodIdentity(r), Scope(r.DeclaringType), Signature(r.MethodSig),
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
            Scope(m.DeclaringType), Signature(m.MethodSig), (int)m.Attributes, (int)m.ImplAttributes,
            m.GenericParameters.Select(p => new object[] { (int)p.Flags, p.GenericParamConstraints.Select(c => Scope(c.Constraint)).ToArray() }).ToArray(),
            m.Body.InitLocals, m.Body.Variables.Select(v => Type(v.Type)).ToArray(),
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

    public static Report Scan(string referenceRoot, string targetRoot) {
        referenceRoot = Path.GetFullPath(referenceRoot); targetRoot = Path.GetFullPath(targetRoot);
        if (!Directory.Exists(referenceRoot) || !Directory.Exists(targetRoot)) throw new DirectoryNotFoundException("Both input trees must exist.");
        if ((File.GetAttributes(referenceRoot) & FileAttributes.ReparsePoint) != 0 || (File.GetAttributes(targetRoot) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Linked input roots are not supported.");
        var report = new Report { ReferenceRoot = referenceRoot, TargetRoot = targetRoot };
        // Pair by relative path, never by simple assembly name: compatibility
        // directories can contain unrelated implementations of the same identity.
        var references = Files(referenceRoot).ToDictionary(p => Path.GetRelativePath(referenceRoot, p), StringComparer.OrdinalIgnoreCase);
        foreach (var targetPath in Files(targetRoot)) {
            string relative = Path.GetRelativePath(targetRoot, targetPath);
            if (!references.TryGetValue(relative, out var referencePath)) { report.Skips.Add(new(relative, "No reference at the same physical path")); continue; }
            var aBytes = File.ReadAllBytes(referencePath); var bBytes = File.ReadAllBytes(targetPath);
            ModuleDefMD a = null, b = null;
            try {
                try { a = ModuleDefMD.Load(aBytes); b = ModuleDefMD.Load(bBytes); }
                catch (BadImageFormatException) { report.Skips.Add(new(relative, "Non-managed or invalid module")); continue; }
                if (a.Assembly == null || b.Assembly == null || Assembly(a.Assembly) != Assembly(b.Assembly)) {
                    report.Skips.Add(new(relative, "Assembly name, culture or key differs")); continue;
                }
                report.Modules++;
                if (aBytes.AsSpan().SequenceEqual(bBytes)) continue;
                var before = a.GetTypes().SelectMany(t => t.Methods).Where(Eligible).ToArray();
                var after = b.GetTypes().SelectMany(t => t.Methods).Where(Eligible).ToArray();
                var vocabulary = MethodNameAnalysis.Learn(before.Select(m => m.Name.String),
                    before.SelectMany(m => m.Body.Instructions).Where(i => i.OpCode == OpCodes.Ldstr).Select(i => (string)i.Operand));
                var anchors = new Dictionary<MethodDef, string>();
                var paired = new HashSet<MethodDef>();
                var pairs = new List<(MethodDef Reference, MethodDef Target, string Key, int Round, string[] Callees)>();
                var skipped = new HashSet<string>(StringComparer.Ordinal);
                ILookup<string, MethodDef> Index(IEnumerable<MethodDef> methods) {
                    var entries = new List<(string Key, MethodDef Method)>();
                    foreach (var method in methods) {
                        try { entries.Add((ContextFingerprint(method, anchors), method)); }
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
                    if (method.Name == reference.Name || MethodNameAnalysis.Analyze(reference.Name.String, vocabulary) != MethodNameAnalysis.Assessment.Meaningful ||
                        MethodNameAnalysis.Analyze(method.Name.String, vocabulary) == MethodNameAnalysis.Assessment.Meaningful) continue;
                    report.Candidates.Add(new(relative, a.Assembly.FullName, b.Assembly.FullName, a.Mvid.ToString(), b.Mvid.ToString(), aHash, bHash,
                        reference.MDToken.ToString(), method.MDToken.ToString(), reference.FullName, method.FullName, reference.Name.String, pair.Key,
                        Visible(method.DeclaringType) && (method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly)) { MatchRound = pair.Round, MatchedCallees = pair.Callees });
                }
            }
            finally { b?.Dispose(); a?.Dispose(); }
        }
        return report;
    }
}
