using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using de4dot.code.renamer;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using SourceNameMapping;

namespace De4dot.NameCandidates;

// Deterministic behavioral evidence, never a binary rename or analyzed-code run.
public static class PropertyFieldReview {
    public sealed record Evidence(string Module, string FieldToken, string PropertyToken,
        string PropertyName, string PropertySignature, string GetterToken, string GetterSignature,
        string[] GetterInstructions, string Inference);
    public sealed class Report {
        public int Format { get; } = 1;
        public string Kind { get; } = "SymbolReview";
        public string TargetRoot { get; init; }
        public string BaseMapSha256 { get; init; }
        public List<MethodReview.Entry> Methods { get; } = new();
        public List<MethodReview.Entry> Types { get; } = new();
        public List<MethodReview.Entry> Fields { get; } = new();
        public List<Evidence> PropertyEvidence { get; } = new();
        public List<FieldAccessorReview.Evidence> FieldAccessorEvidence { get; } = new();
        public string InferenceKind { get; init; } = "PropertyField";
        public List<Skip> Skips { get; } = new();
    }
    static readonly HashSet<string> keywords = new((
        "abstract as base bool break byte case catch char checked class const continue decimal default delegate do double else enum event explicit extern false finally fixed float for foreach goto if implicit in int interface internal is lock long namespace new null object operator out override params private protected public readonly ref return sbyte sealed short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using virtual void volatile while").Split(' '), StringComparer.Ordinal);
    static uint Token(string text) => Convert.ToUInt32(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text, 16);
    static string Token(IMDTokenProvider member) => "0x" + member.MDToken.Raw.ToString("X8");
    static bool NeedsName(FieldDef field) =>
        // Normal compiler backing fields already carry their property role and
        // decompilers can hide them behind auto-properties. Do not expand them.
        !Regex.IsMatch(field.Name, @"\A<.+>k__BackingField\z") &&
        (MethodReview.Reason(field.Name) != null || field.Name.String.StartsWith('<') ||
         Regex.IsMatch(field.Name, @"\A[A-Za-z_][A-Za-z0-9_]*_[0-9]+\z"));
    static FieldDef DirectField(PropertyDef property) {
        var getter = property.GetMethod;
        if (property.PropertySig == null || property.PropertySig.Params.Count != 0 || getter?.HasBody != true ||
            getter.Body.ExceptionHandlers.Count != 0 || getter.MethodSig.Params.Count != 0) return null;
        var il = getter.Body.Instructions.Where(i => i.OpCode.Code != Code.Nop).ToArray();
        int load = getter.IsStatic ? 0 : 1;
        if (il.Length != load + 2 || il[^1].OpCode.Code != Code.Ret ||
            load == 1 && il[0].OpCode.Code != Code.Ldarg_0 ||
            il[load].OpCode.Code != (getter.IsStatic ? Code.Ldsfld : Code.Ldfld)) return null;
        // Resolve only a literal FieldDef operand in the same declaring type.
        // Never guess across generic MemberRefs, assembly names or input copies.
        if (il[load].Operand is not FieldDef field || field.DeclaringType != property.DeclaringType ||
            field.IsStatic != getter.IsStatic || !new SigComparer().Equals(field.FieldType, property.PropertySig.RetType)) return null;
        return field;
    }
    static string ProposedName(PropertyDef property) {
        string name = property.Name;
        if (!Regex.IsMatch(name, @"\A[A-Za-z][A-Za-z0-9]*\z") ||
            Regex.IsMatch(name, @"\A(?:G?Property|Field|Type|G?Class|Method|Value)[0-9]+\z") ||
            MethodNameAnalysis.Analyze(name) != MethodNameAnalysis.Assessment.Meaningful) return null;
        name = name == "Default" ? new SigComparer().Equals(property.PropertySig.RetType.ToTypeDefOrRef(), property.DeclaringType) ? "defaultInstance" : "defaultValue" : JsonNamingPolicy.CamelCase.ConvertName(name);
        if (keywords.Contains(name)) name += "Value";
        var owner = property.DeclaringType;
        // XML model properties often already use camelCase. Describe their
        // backing field explicitly instead of requesting an inevitable "2".
        if (new IMemberDef[] { owner }.Concat(owner.Fields).Concat(owner.Methods).Concat(owner.Properties)
            .Concat(owner.Events).Concat(owner.NestedTypes).Any(m => m.Name == name)) name += "Field";
        return name;
    }
    public static Report Scan(string inputDirectory, string baseMapPath, bool fieldAccessors = false) {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(inputDirectory));
        if (!Directory.Exists(root)) throw new ArgumentException("Supply the complete input directory, not a single assembly.");
        byte[] mapBytes = File.ReadAllBytes(baseMapPath);
        using var mapStream = new MemoryStream(mapBytes);
        using var reader = XmlReader.Create(mapStream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var map = XDocument.Load(reader).Root;
        if (map?.Name != "SourceNameMap" || (string)map.Attribute("Version") != "1" ||
            !InputPaths.Comparer.Equals(root, Path.TrimEndingDirectorySeparator(Path.GetFullPath((string)map.Attribute("InputDirectory") ?? throw new InvalidDataException("Missing InputDirectory.")))))
            throw new InvalidDataException("Expected a Version=1 map for this exact input root.");
        string prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        var mappedModules = new Dictionary<string, XElement>(InputPaths.Comparer);
        foreach (var row in map.Elements()) {
            string relative = (string)row.Attribute("Path");
            if (row.Name != "Module" || string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative)) throw new InvalidDataException("Expected a relative Module path.");
            string path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(prefix, InputPaths.Comparison)) throw new InvalidDataException("Map module leaves input root.");
            if (!mappedModules.TryAdd(path, row)) throw new InvalidDataException("Duplicate physical module in map.");
        }
        var report = new Report { TargetRoot = root, BaseMapSha256 = Convert.ToHexString(SHA256.HashData(mapBytes)),
            InferenceKind = fieldAccessors ? "FieldAccessor" : "PropertyField" };
        var visited = new HashSet<string>(InputPaths.Comparer);
        foreach (var path in MethodReview.Files(root).Where(p => Path.GetExtension(p).Equals(".dll", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(p).Equals(".exe", StringComparison.OrdinalIgnoreCase))) {
            string relative = InputPaths.Relative(root, path);
            byte[] bytes = File.ReadAllBytes(path);
            ModuleDefMD module;
            try { module = ModuleDefMD.Load(bytes); }
            catch (BadImageFormatException) { report.Skips.Add(new(relative, "non-managed input")); continue; }
            using (module) {
                string hash = Convert.ToHexString(SHA256.HashData(bytes));
                visited.Add(path);
                var existing = new Dictionary<uint, XElement>();
                if (mappedModules.TryGetValue(path, out var moduleRow)) {
                    if (!Guid.TryParse((string)moduleRow.Attribute("Mvid"), out var id) || id != module.Mvid ||
                        !StringComparer.OrdinalIgnoreCase.Equals(hash, (string)moduleRow.Attribute("Sha256"))) throw new InvalidDataException("Stale mapped module: " + relative);
                    foreach (var row in moduleRow.Elements("Field")) {
                        uint token = Token((string)row.Attribute("Token"));
                        if (!existing.TryAdd(token, row)) throw new InvalidDataException("Duplicate mapped field: " + relative);
                        if (module.ResolveToken(token) is not FieldDef field || field.Name != (string)row.Attribute("ExpectedName") ||
                            field.FullName != (string)row.Attribute("Signature")) throw new InvalidDataException("Stale mapped field: " + relative);
                    }
                }
                if (fieldAccessors) {
                    FieldAccessorReview.Collect(module, relative, hash, existing, moduleRow, report);
                    continue;
                }
                foreach (var type in module.GetTypes()) {
                    var pairs = type.Properties.Select(p => (Property: p, Field: DirectField(p))).Where(p => p.Field != null).GroupBy(p => p.Field);
                    foreach (var group in pairs) {
                        var field = group.Key;
                        if (!NeedsName(field)) continue;
                        if (existing.ContainsKey(field.MDToken.Raw)) { report.Skips.Add(new(relative, "existing custom field alias preserved", Token(field))); continue; }
                        string blocker = FieldAliasScope.Blocker(field);
                        if (blocker != null) { report.Skips.Add(new(relative, blocker, Token(field))); continue; }
                        var properties = group.Select(p => p.Property).ToArray();
                        if (properties.Length != 1) { report.Skips.Add(new(relative, "field returned by multiple properties; behavior review required", Token(field))); continue; }
                        var property = properties[0];
                        string name = ProposedName(property);
                        if (name == null) { report.Skips.Add(new(relative, "property name lacks readable evidence: " + property.Name, Token(field))); continue; }
                        report.Fields.Add(new MethodReview.Entry {
                            Module = relative, Identity = module.Assembly?.FullName, Mvid = module.Mvid.ToString(), Sha256 = hash,
                            Type = type.FullName, Token = Token(field), Signature = field.FullName, OriginalName = field.Name,
                            Reason = "direct backing field of readable property " + property.FullName,
                            NeedsReadableName = true, NewName = name, Parameters = Array.Empty<string>()
                        });
                        var getter = property.GetMethod;
                        report.PropertyEvidence.Add(new(relative, Token(field), Token(property), property.Name, property.FullName,
                            Token(getter), getter.FullName, getter.Body.Instructions.Select(i => i.ToString()).ToArray(),
                            "Getter only loads this field and returns it; field name describes the value exposed by the existing property. Setter behavior is not inferred."));
                    }
                }
            }
        }
        if (mappedModules.Keys.Any(path => !visited.Contains(path))) throw new InvalidDataException("A mapped physical module was missing or could not be read as managed metadata.");
        return report;
    }
    public static void Write(string inputDirectory, string baseMapPath, string output, bool fieldAccessors = false) {
        if (File.Exists(output)) throw new IOException("Choose a new field/property-access review output.");
        var report = Scan(inputDirectory, baseMapPath, fieldAccessors);
        using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write);
        JsonSerializer.Serialize(stream, report, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(fieldAccessors ? $"Proposed {report.Methods.Count} field-access method names with body/alias evidence; {report.Skips.Count} explicit skips. No inputs or maps changed." :
            $"Proposed {report.Fields.Count} property-backed field names with getter evidence; {report.Skips.Count} explicit skips. No inputs or maps changed.");
    }
}
