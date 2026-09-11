using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using dnlib.DotNet;
using de4dot.code.renamer;

namespace De4dot.NameCandidates;

// Discovery is independent of cross-version matching and source-map eligibility.
public static class MethodReview {
    public sealed class Inventory {
        public int Format { get; set; } = 1;
        public string Kind { get; set; } = "MethodReview";
        public string TargetRoot { get; set; }
        public List<Entry> Methods { get; set; } = new();
        public List<Skip> Skips { get; set; } = new();
    }
    public sealed class Entry {
        public string Module { get; set; }
        public string Identity { get; set; }
        public string Mvid { get; set; }
        public string Sha256 { get; set; }
        public string Type { get; set; }
        public string Token { get; set; }
        public string Signature { get; set; }
        public string OriginalName { get; set; }
        public string Reason { get; set; }
        public bool NeedsReadableName { get; set; }
        public string MappingBlocker { get; set; }
        public string[] Parameters { get; set; }
        public string NewName { get; set; } = "";
    }
    public static string Reason(string name, ISet<string> vocabulary = null) {
        if (Regex.IsMatch(name ?? "", @"\A[gsv]?method_[0-9]+\z")) return "generated-placeholder";
        // Assess the base of deobfuscator collision suffixes, e.g. c_1 or d_12.
        string stem = Regex.Replace(name ?? "", @"_[0-9]+\z", "");
        int qualifier = stem.LastIndexOf('.');
        if (qualifier >= 0) stem = stem.Substring(qualifier + 1);
        var assessment = MethodNameAnalysis.Analyze(stem, vocabulary);
        if (assessment == MethodNameAnalysis.Assessment.Meaningful) return null;
        if (Regex.IsMatch(stem, @"\A[A-Za-z]{1,3}\z")) return "short-unrecognized-name";
        if (assessment == MethodNameAnalysis.Assessment.Obfuscated) return "lexically-obfuscated";
        return null;
    }
    internal static string Blocker(MethodDef m) {
        if (m.IsConstructor) return "constructor";
        if (m.IsVirtual || m.HasOverrides) return "virtual-or-override: requires contract-family source-map support";
        if (m.IsSpecialName || m.IsRuntimeSpecialName) return "accessor-or-special-name";
        if (m.IsPinvokeImpl || m.IsRuntime) return "native-or-runtime-method";
        return null;
    }
    static IEnumerable<string> Files(string path) {
        if ((File.GetAttributes(path) & System.IO.FileAttributes.ReparsePoint) != 0)
            throw new IOException("Linked input is not supported: " + path);
        if (File.Exists(path)) { yield return path; yield break; }
        foreach (string child in Directory.EnumerateFileSystemEntries(path).OrderBy(p => p, StringComparer.Ordinal))
            foreach (string file in Files(child)) yield return file;
    }
    public static Inventory Scan(string input) {
        input = Path.GetFullPath(input);
        string root = File.Exists(input) ? Path.GetDirectoryName(input) : Path.TrimEndingDirectorySeparator(input);
        var result = new Inventory { TargetRoot = root };
        foreach (string file in Files(input).Where(p => Path.GetExtension(p).Equals(".dll", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(p).Equals(".exe", StringComparison.OrdinalIgnoreCase))) {
            string relative = InputPaths.Relative(root, file);
            byte[] bytes = File.ReadAllBytes(file);
            ModuleDefMD module;
            try { module = ModuleDefMD.Load(bytes); }
            catch (BadImageFormatException) { result.Skips.Add(new Skip(relative, "non-managed input")); continue; }
            using (module) {
                var methods = module.GetTypes().SelectMany(t => t.Methods).ToArray();
                var vocabulary = MethodNameAnalysis.Learn(methods.Select(m => m.Name.String),
                    methods.Where(m => m.HasBody).SelectMany(m => m.Body.Instructions).Where(i => i.OpCode.Code == dnlib.DotNet.Emit.Code.Ldstr).Select(i => i.Operand as string));
                string hash = Convert.ToHexString(SHA256.HashData(bytes));
                foreach (var method in methods) {
                    string reason = method.IsConstructor ? null : Reason(method.Name, vocabulary);
                    result.Methods.Add(new Entry {
                        Module = relative, Identity = module.Assembly?.FullName, Mvid = module.Mvid.ToString(), Sha256 = hash,
                        Type = method.DeclaringType.FullName, Token = "0x" + method.MDToken.Raw.ToString("X8"),
                        Signature = method.FullName, OriginalName = method.Name, Reason = reason,
                        NeedsReadableName = reason != null, MappingBlocker = Blocker(method),
                        Parameters = method.Parameters.Where(p => p.IsNormalMethodParameter).Select(p => $"{p.MethodSigIndex + 1}: {p.Type} {p.Name}").ToArray()
                    });
                }
            }
        }
        return result;
    }
    public static void Write(string input, string output, bool onlyObfuscated = false) {
        if (File.Exists(output)) throw new IOException("Choose a new review output file.");
        var inventory = Scan(input);
        int total = inventory.Methods.Count;
        if (onlyObfuscated) inventory.Methods = inventory.Methods.Where(m => m.NeedsReadableName).ToList();
        using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write);
        JsonSerializer.Serialize(stream, inventory, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine($"Inventoried {total} methods; wrote {inventory.Methods.Count}; {inventory.Methods.Count(m => m.NeedsReadableName)} need review; {inventory.Methods.Count(m => m.NeedsReadableName && m.MappingBlocker != null)} require unsupported source-map contracts.");
    }
    public static void WriteMap(string input, string targetRoot, string output) {
        var inventory = JsonSerializer.Deserialize<Inventory>(File.ReadAllText(input));
        if (inventory?.Format != 1 || inventory.Kind != "MethodReview") throw new InvalidDataException("Expected a method review inventory.");
        var selected = inventory.Methods.Where(m => !string.IsNullOrWhiteSpace(m.NewName)).ToArray();
        if (selected.Length == 0) throw new InvalidDataException("Set NewName on at least one method.");
        // Reuse the source-map writer's authoritative byte/token/signature and
        // eligibility checks. User-editable status fields are not trusted.
        var report = new Report { TargetRoot = inventory.TargetRoot, Candidates = selected.Select(m =>
            new Candidate(m.Module, null, m.Identity, null, m.Mvid, null, m.Sha256, null, m.Token,
                null, m.Signature, m.NewName, null, false)).ToList() };
        string temp = Path.GetTempFileName();
        try {
            File.WriteAllText(temp, JsonSerializer.Serialize(report));
            SourceMapWriter.Write(temp, targetRoot, output);
        }
        finally { File.Delete(temp); }
        Console.WriteLine("Source map written. Review collision suffixes in NewName; original runtime names are preserved.");
    }
}
