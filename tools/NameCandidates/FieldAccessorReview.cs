using System.Text.RegularExpressions;
using System.Xml.Linq;
using de4dot.code.renamer;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using SourceNameMapping;

namespace De4dot.NameCandidates;

// Name a pure read/write from an already reviewed field role. No name transfer
// across assembly identities, field MemberRefs, interfaces or virtual slots.
public static class FieldAccessorReview {
    public sealed record Evidence(string Module, string MethodToken, string MethodSignature,
        string FieldToken, string FieldSignature, string FieldAlias, string Operation,
        string[] Instructions, string Inference);
    static uint Token(string value) => Convert.ToUInt32(value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value, 16);
    static string Token(IMDTokenProvider member) => "0x" + member.MDToken.Raw.ToString("X8");
    static bool ReadableRole(string name) {
        if (name == null || !Regex.IsMatch(name, @"\A[A-Za-z][A-Za-z0-9]*\z") ||
            Regex.IsMatch(name, @"\A(?:G?Class|Type|Field|field|Method|method)[0-9]+\z")) return false;
        var assessment = MethodNameAnalysis.Analyze(name);
        if (assessment == MethodNameAnalysis.Assessment.Obfuscated) return false;
        if (assessment == MethodNameAnalysis.Assessment.Meaningful) return true;
        // A long alias alone is not a role: do not turn abcd into GetAbcd.
        // At least one semantic word (including an is/has/can predicate) must
        // support inference. Domain-only roles remain available for manual review.
        return name.Length > 3 && MethodNameAnalysis.Tokenize(name).Any(word =>
            MethodNameAnalysis.Analyze(word) == MethodNameAnalysis.Assessment.Meaningful);
    }
    static (FieldDef Field, bool Write) Match(MethodDef method) {
        if (!method.HasBody || method.Body.ExceptionHandlers.Count != 0 || method.MethodSig == null || method.HasGenericParameters) return (null, false);
        var il = method.Body.Instructions.Where(i => i.OpCode.Code != Code.Nop).ToArray();
        bool write = method.MethodSig.RetType.ElementType == ElementType.Void;
        if (method.MethodSig.Params.Count != (write ? 1 : 0)) return (null, false);
        int instance = method.IsStatic ? 0 : 1, load = instance + (write ? 1 : 0);
        if (il.Length != load + 2 || il[^1].OpCode.Code != Code.Ret ||
            instance == 1 && il[0].OpCode.Code != Code.Ldarg_0 ||
            write && il[instance].OpCode.Code != (method.IsStatic ? Code.Ldarg_0 : Code.Ldarg_1) ||
            il[load].OpCode.Code != (write ? method.IsStatic ? Code.Stsfld : Code.Stfld : method.IsStatic ? Code.Ldsfld : Code.Ldfld)) return (null, false);
        if (il[load].Operand is not FieldDef field || field.DeclaringType != method.DeclaringType || field.IsStatic != method.IsStatic ||
            FieldAliasScope.Blocker(field) != null || write && field.IsInitOnly ||
            !new SigComparer().Equals(field.FieldType, write ? method.MethodSig.Params[0] : method.MethodSig.RetType)) return (null, false);
        return (field, write);
    }
    internal static void Collect(ModuleDefMD module, string relative, string hash, Dictionary<uint, XElement> fields,
        XElement moduleRow, PropertyFieldReview.Report report) {
        var existing = new HashSet<uint>();
        foreach (var row in moduleRow?.Elements("Method") ?? Enumerable.Empty<XElement>()) {
            uint token = Token((string)row.Attribute("Token"));
            if (!existing.Add(token) || module.ResolveToken(token) is not MethodDef method ||
                method.Name != (string)row.Attribute("ExpectedName") || method.FullName != (string)row.Attribute("Signature"))
                throw new InvalidDataException("Stale or duplicate mapped method: " + relative);
        }
        foreach (var method in module.GetTypes().SelectMany(t => t.Methods)) {
            if (MethodReview.Reason(method.Name) == null) continue;
            var (field, write) = Match(method);
            if (field == null || !fields.TryGetValue(field.MDToken.Raw, out var fieldRow)) continue;
            if (existing.Contains(method.MDToken.Raw)) { report.Skips.Add(new(relative, "existing custom method alias preserved", Token(method))); continue; }
            string blocker = MethodReview.Blocker(method);
            if (blocker != null) { report.Skips.Add(new(relative, blocker, Token(method))); continue; }
            string role = (string)fieldRow.Attribute("NewName");
            if (!ReadableRole(role)) { report.Skips.Add(new(relative, "field alias lacks a readable role", Token(method))); continue; }
            string title = char.ToUpperInvariant(role[0]) + role[1..];
            bool predicate = !write && field.FieldType.ElementType == ElementType.Boolean && Regex.IsMatch(role, @"\A(?:is|has|can|should)[A-Z]");
            string name = predicate ? title : (write ? "Set" : "Get") + title;
            var parameters = method.Parameters.Where(p => p.IsNormalMethodParameter).ToArray();
            report.Methods.Add(new MethodReview.Entry {
                Module = relative, Identity = module.Assembly?.FullName, Mvid = module.Mvid.ToString(), Sha256 = hash,
                Type = method.DeclaringType.FullName, Token = Token(method), Signature = method.FullName, OriginalName = method.Name,
                Reason = (write ? "assigns only the reviewed " : "returns only the reviewed ") + role + " field", NeedsReadableName = true, NewName = name,
                ParameterNames = write && parameters[0].ParamDef != null ? new[] { new MethodReview.ParameterEdit {
                    Sequence = 1, OriginalName = parameters[0].Name, HasMetadata = true, NewName = "value" } } : Array.Empty<MethodReview.ParameterEdit>()
            });
            report.FieldAccessorEvidence.Add(new(relative, Token(method), method.FullName, Token(field), field.FullName, role,
                write ? "write" : "read", method.Body.Instructions.Select(i => i.ToString()).ToArray(),
                "Name derives from the existing physical field alias and a body containing only this field access; other behavior is not inferred."));
        }
    }
}
