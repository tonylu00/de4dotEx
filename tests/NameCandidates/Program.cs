using dnlib.DotNet;
using dnlib.DotNet.Emit;
using De4dot.NameCandidates;
using System.Text.Json;

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
CallGraphFixture.Run(root);
PropertyFieldFixture.Run(root);
FieldAccessorFixture.Run(root);
TypeCorrespondenceFixture.Run(root);
var seed = report.Candidates.First(c => c.Module.Replace('\\', '/') == "host/Library.dll");
string referenceMapPath = Path.Combine(root, "reference-names.xml");
var referenceMap = new System.Xml.Linq.XDocument(new System.Xml.Linq.XElement("SourceNameMap",
    new System.Xml.Linq.XAttribute("Version", "1"), new System.Xml.Linq.XAttribute("InputDirectory", reference),
    new System.Xml.Linq.XElement("Module", new System.Xml.Linq.XAttribute("Path", seed.Module),
        new System.Xml.Linq.XAttribute("Mvid", seed.ReferenceMvid), new System.Xml.Linq.XAttribute("Sha256", seed.ReferenceHash),
        new System.Xml.Linq.XElement("Method", new System.Xml.Linq.XAttribute("Token", seed.ReferenceToken),
            new System.Xml.Linq.XAttribute("ExpectedName", "GetValue"), new System.Xml.Linq.XAttribute("Signature", seed.Reference),
            new System.Xml.Linq.XAttribute("NewName", "ReadValue")))));
referenceMap.Save(referenceMapPath);
var seeded = Scanner.Scan(reference, target, referenceMapPath);
Require(seeded.Candidates.Single(c => c.Module == seed.Module).SuggestedName == "ReadValue", "Reviewed reference alias propagated");
Require(seeded.Candidates.Single(c => c.Module != seed.Module).SuggestedName == "GetValue", "Reference alias stays in physical module");
Require(seeded.ReferenceSourceMapHash.Length == 64, "Reference map provenance");
Require(Scanner.Scan(reference, reference, referenceMapPath).Candidates.Count == 0, "Meaningful existing names remain unchanged");
foreach (string invalidReference in new[] { "hash", "signature", "path", "duplicate" }) {
    var invalidMap = new System.Xml.Linq.XDocument(referenceMap);
    var moduleRow = invalidMap.Descendants("Module").Single();
    var methodRow = moduleRow.Element("Method");
    if (invalidReference == "hash") moduleRow.SetAttributeValue("Sha256", "00");
    if (invalidReference == "signature") methodRow.SetAttributeValue("Signature", "wrong method");
    if (invalidReference == "path") moduleRow.SetAttributeValue("Path", "../escape.dll");
    if (invalidReference == "duplicate") moduleRow.Add(new System.Xml.Linq.XElement(methodRow));
    invalidMap.Save(referenceMapPath);
    bool staleRejected = false;
    try { Scanner.Scan(reference, target, referenceMapPath); } catch (InvalidDataException) { staleRejected = true; }
    Require(staleRejected, "Reference map guard: " + invalidReference);
}
Console.WriteLine("PASS reviewed reference method aliases, physical scope, provenance and stale-map rejection.");
Console.WriteLine("PASS physical duplicates, dependency scopes, version changes, ambiguity, readable names, native/missing inputs, provenance, deterministic output and no overwrite.");
foreach (string name in new[] { "a", "b", "c_1", "d_12", "acv", "Q", "method_12" })
    Require(MethodReview.Reason(name) != null, "Review includes " + name);
foreach (string name in new[] { "Load", "Map", "On", "Save", "Get", "Set", "IsDeviceTesterLicensed", "Save_1", "ToMetricKey" })
    Require(MethodReview.Reason(name) == null, "Review preserves " + name);
Require(MethodReview.Reason("ETS", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ETS" }) == null, "Review learned acronym");
foreach (string name in new[] { "jn7oUifpKYO", "qxMoUcH04cb", "l5poUvKFq0o" }) {
    Require(MethodReview.Reason(name) == "mixed-alphanumeric-review-candidate", "Review Reactor name " + name);
    Require(MethodReview.Reason("Example." + name + "_2") == "mixed-alphanumeric-review-candidate", "Review qualified Reactor collision " + name);
    Require(de4dot.code.renamer.MethodNameAnalysis.Analyze(name) == de4dot.code.renamer.MethodNameAnalysis.Assessment.Unknown, "Automatic rename classification unchanged " + name);
}
foreach (string name in new[] { "IsIPv6Only", "GetIPv6Only", "CalculateBlake2Hash", "SHA256Managed", "X509Certificate2UI", "VB6GetObject", "D3D11On12" })
    Require(MethodReview.Reason(name) == null, "Review preserves numeric API " + name);
Require(MethodReview.Reason("qxMoUcH04cb", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Uc" }) == null, "Review preserves learned fragment");
string reviewRoot = Path.Combine(root, "review");
Directory.CreateDirectory(reviewRoot);
using (var module = new ModuleDefUser("Review.dll") { Kind = ModuleKind.Dll }) {
    new AssemblyDefUser("Review", new Version(1, 0)).Modules.Add(module);
    var type = new TypeDefUser("Example", "Manager", module.CorLibTypes.Object.TypeDefOrRef);
    module.Types.Add(type);
    foreach (string name in new[] { "a", "b", "c_1", "d_1", "Load", "Save", "Map", "On" }) {
        var method = new MethodDefUser(name, MethodSig.CreateInstance(module.CorLibTypes.Void, module.CorLibTypes.Int32),
            MethodAttributes.Public | (name == "b" ? MethodAttributes.Virtual : 0));
        method.ParamDefs.Add(new ParamDefUser("count", 1));
        method.Body = new CilBody(); method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        type.Methods.Add(method);
    }
    module.Write(Path.Combine(reviewRoot, "Review.dll"));
}
var review = MethodReview.Scan(reviewRoot);
Require(review.Methods.Count == 8 && review.Methods.Count(m => m.NeedsReadableName) == 4, "Complete inventory, independent of body size");
Require(review.Methods.Single(m => m.OriginalName == "b").MappingBlocker != null, "Unsupported virtual visible, not silently omitted");
Require(review.Methods.Single(m => m.OriginalName == "a").Parameters.Single().Contains("count"), "Parameter metadata is visible");
review.Methods.Single(m => m.OriginalName == "a").NewName = "Save";
string reviewPath = Path.Combine(root, "review.json"), reviewMap = Path.Combine(root, "review.xml");
File.WriteAllText(reviewPath, JsonSerializer.Serialize(review));
MethodReview.WriteMap(reviewPath, reviewRoot, reviewMap);
Require(System.Xml.Linq.XDocument.Load(reviewMap).Descendants("Method").Single().Attribute("NewName").Value == "Save2", "Review map repairs collision");
review.Methods.Single(m => m.OriginalName == "a").Sha256 = "00";
File.WriteAllText(reviewPath, JsonSerializer.Serialize(review));
bool badReview = false;
try { MethodReview.WriteMap(reviewPath, reviewRoot, Path.Combine(root, "invalid-review.xml")); } catch (InvalidDataException) { badReview = true; }
Require(badReview && !File.Exists(Path.Combine(root, "invalid-review.xml")), "Review rejects stale identity before writing");
Console.WriteLine("PASS complete method review, short suffixes, meaningful names, blockers, parameter display, editable names and collision-safe conversion.");
ReviewUpdateFixture.Run(root, reviewRoot, reviewMap);
SymbolReviewFixture.Run(root, reviewRoot, reviewMap);
ContractFamilyFixture.Run(root);
OrphanSpecialNameFixture.Run(root);
FieldReviewFixture.Run(root);
