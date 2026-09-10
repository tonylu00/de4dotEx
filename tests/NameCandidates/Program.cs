using dnlib.DotNet;
using dnlib.DotNet.Emit;
using De4dot.NameCandidates;

if (args.Length != 1 || Directory.Exists(args[0])) throw new ArgumentException("Supply a new fixture directory.");
string root = Path.GetFullPath(args[0]), reference = Path.Combine(root, "reference"), target = Path.Combine(root, "target");
Directory.CreateDirectory(reference); Directory.CreateDirectory(target);
void Build(string directory, string relative, bool renamed, int seed = 1, bool ambiguous = false, bool differentScope = false, bool meaningful = false) {
    string path = Path.Combine(directory, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    using var module = new ModuleDefUser("Library.dll") { Kind = ModuleKind.Dll };
    new AssemblyDefUser("Library", new Version(renamed ? 2 : 1, 0)).Modules.Add(module);
    var type = new TypeDefUser("Example", "Service", module.CorLibTypes.Object.TypeDefOrRef) { Attributes = TypeAttributes.Public };
    module.Types.Add(type);
    void Method(string name) {
        var m = new MethodDefUser(name, MethodSig.CreateStatic(module.CorLibTypes.Int32), MethodAttributes.Public | MethodAttributes.Static);
        m.Body = new CilBody(); type.Methods.Add(m);
        m.Body.Instructions.Add(Instruction.CreateLdcI4(seed));
        m.Body.Instructions.Add(Instruction.CreateLdcI4(2));
        m.Body.Instructions.Add(Instruction.Create(OpCodes.Add));
        // Same type name with different dependency scopes must not match.
        var dependency = new AssemblyRefUser(differentScope && renamed ? "OtherDependency" : "Dependency", new Version(renamed ? 2 : 1, 0));
        m.Body.Instructions.Add(Instruction.Create(OpCodes.Ldtoken, new TypeRefUser(module, "Shared", "Value", dependency)));
        m.Body.Instructions.Add(Instruction.Create(OpCodes.Pop));
        m.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
    }
    Method(renamed ? meaningful ? "ReadValue" : "xqz" : "GetValue");
    if (ambiguous && !renamed) Method("GetResult");
    module.Write(path);
}
foreach (var dir in new[] { reference, target }) {
    bool renamed = dir == target;
    Build(dir, "host/Library.dll", renamed);
    Build(dir, "compat/Library.dll", renamed, seed: 8);
    Build(dir, "ambiguous/Library.dll", renamed, ambiguous: true);
    Build(dir, "scoped/Library.dll", renamed, differentScope: true);
    Build(dir, "readable/Library.dll", renamed, meaningful: true);
    File.WriteAllText(Path.Combine(dir, "native.exe"), "not a managed image");
}
Build(target, "target-only/Library.dll", true);
var hashes = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(p => p, p => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p))));
var report = Scanner.Scan(reference, target);
void Require(bool condition, string description) { if (!condition) throw new Exception(description); }
Require(report.Modules == 5, "Module count");
Require(report.Candidates.Count == 2, "Only independent host and compatibility candidates expected");
Require(report.Candidates.Select(c => c.Module.Replace('\\', '/')).Order().SequenceEqual(new[] { "compat/Library.dll", "host/Library.dll" }), "Physical context pairing");
Require(report.Candidates.All(c => c.SuggestedName == "GetValue" && c.ExternallyVisible && c.TargetHash.Length == 64 && c.ReferenceHash.Length == 64), "Names, visibility and provenance");
Require(report.Candidates.Select(c => c.Fingerprint).Distinct().Count() == 2, "Different implementations remain separate");
Require(report.Skips.Any(s => s.Reason.StartsWith("Ambiguous")), "Duplicate fingerprint rejected");
Require(report.Skips.Any(s => s.Reason.StartsWith("Non-managed")), "Native input skipped");
Require(report.Skips.Any(s => s.Reason.StartsWith("No reference")), "Missing reference reported");
string json = Path.Combine(root, "candidates.json");
Require(De4dot.NameCandidates.Program.Main(new[] { reference, target, json }) == 0, "CLI report");
byte[] original = File.ReadAllBytes(json);
Require(De4dot.NameCandidates.Program.Main(new[] { reference, target, json }) == 1 && original.SequenceEqual(File.ReadAllBytes(json)), "No overwrite");
Require(System.Text.Json.JsonSerializer.Serialize(report) == System.Text.Json.JsonSerializer.Serialize(Scanner.Scan(reference, target)), "Deterministic scan");
Require(Scanner.Scan(reference, reference).Candidates.Count == 0, "Identical input shortcut");
Require(hashes.All(p => p.Value == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p.Key)))), "Input files unchanged");
string mapPath = Path.Combine(root, "names.xml");
Require(De4dot.NameCandidates.Program.Main(new[] { "--source-map", json, target, mapPath }) == 0, "Source map conversion");
var map = System.Xml.Linq.XDocument.Load(mapPath);
Require(map.Root.Elements("Module").Count() == 2 && map.Descendants("Method").All(m => (string)m.Attribute("NewName") == "GetValue" && (string)m.Attribute("ExpectedName") == "xqz"), "Physical source map rows");
Require(De4dot.NameCandidates.Program.Main(new[] { "--source-map", json, target, mapPath }) == 1, "Source map no overwrite");
foreach (string test in new[] { "hash", "signature", "path", "duplicate", "identifier", "collision" }) {
    var candidate = report.Candidates[0];
    candidate = test switch {
        "hash" => candidate with { TargetHash = "00" },
        "signature" => candidate with { Target = "wrong signature" },
        "path" => candidate with { Module = "../escape.dll" },
        "identifier" => candidate with { SuggestedName = "not.valid" },
        "collision" => candidate with { SuggestedName = "Service" },
        _ => candidate
    };
    var selected = new Report { TargetRoot = target, Candidates = new() { candidate } };
    if (test == "duplicate") selected.Candidates.Add(candidate);
    string reportPath = Path.Combine(root, test + ".json"), output = Path.Combine(root, test + ".xml");
    File.WriteAllText(reportPath, System.Text.Json.JsonSerializer.Serialize(selected));
    int result = De4dot.NameCandidates.Program.Main(new[] { "--source-map", reportPath, target, output });
    Require(result == (test == "collision" ? 0 : 1), "Map guard: " + test);
    if (test != "collision") Require(!File.Exists(output), "Reject before writing: " + test);
    else Require((string)System.Xml.Linq.XDocument.Load(output).Descendants("Method").Single().Attribute("NewName") == "Service2", "Deterministic collision suffix");
}
Console.WriteLine("PASS guarded source-map conversion, collision suffixes, stale input rejection and no overwrite.");
Console.WriteLine("PASS physical duplicates, dependency scopes, version changes, ambiguity, readable names, native/missing inputs, provenance, deterministic output and no overwrite.");
