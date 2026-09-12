using System.Text.Json;
using System.Xml.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using De4dot.NameCandidates;

static class FieldReviewFixture {
    public static void Run(string root) {
        void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
        string input = Path.Combine(root, "fields"); Directory.CreateDirectory(input);
        foreach (string folder in new[] { "host", "compat" }) {
            string path = Path.Combine(input, folder); Directory.CreateDirectory(path);
            using var module = new ModuleDefUser("Fields.dll");
            new AssemblyDefUser("Fields", new Version(1, 0)).Modules.Add(module);
            TypeDef AddType(string name, ITypeDefOrRef parent = null) {
                var type = new TypeDefUser("Example", name, parent ?? module.CorLibTypes.Object.TypeDefOrRef) { Attributes = TypeAttributes.Public };
                module.Types.Add(type); return type;
            }
            void Field(TypeDef owner, string name) => owner.Fields.Add(new FieldDefUser(name, new FieldSig(module.CorLibTypes.Int32), FieldAttributes.Public));
            var first = AddType("First"); Field(first, "a"); Field(first, "b");
            var read = new MethodDefUser("Read", MethodSig.CreateStatic(module.CorLibTypes.Int32, module.CorLibTypes.Int32), MethodAttributes.Public | MethodAttributes.Static) { Body = new CilBody() };
            read.ParamDefs.Add(new ParamDefUser("arg", 1)); read.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0)); read.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); first.Methods.Add(read);
            Field(AddType("Second"), "a");
            var parent = AddType("Parent`1"); parent.GenericParameters.Add(new GenericParamUser(0, GenericParamAttributes.NonVariant, "Item")); Field(parent, "a");
            var child = AddType("Child", new TypeSpecUser(new GenericInstSig(new ClassSig(parent), module.CorLibTypes.Int32))); Field(child, "b"); Field(child, "reservedName");
            var kind = AddType("Kind", new TypeRefUser(module, "System", "Enum", module.CorLibTypes.AssemblyRef));
            kind.Fields.Add(new FieldDefUser("value__", new FieldSig(module.CorLibTypes.Int32), FieldAttributes.Public | FieldAttributes.SpecialName | FieldAttributes.RTSpecialName));
            kind.Fields.Add(new FieldDefUser("a", new FieldSig(kind.ToTypeSig()), FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal) { Constant = new ConstantUser(1) });
            module.Write(Path.Combine(path, "Fields.dll"));
        }
        string review = Path.Combine(root, "fields.json"), map = Path.Combine(root, "fields.xml");
        Require(De4dot.NameCandidates.Program.Main(new[] { "--review-fields", input, review }) == 0, "Field CLI inventory");
        var inventory = JsonSerializer.Deserialize<MethodReview.Inventory>(File.ReadAllText(review));
        Require(inventory.Methods.Count == 0 && inventory.Fields.Count == 16 && inventory.Fields.Count(f => f.MappingBlocker == "runtime-special-field") == 2, "Complete field inventory with blockers");
        foreach (var field in inventory.Fields) field.NewName = field.Type switch {
            "Example.First" => "logger", "Example.Second" => "logger", "Example.Parent`1" => "reservedName",
            "Example.Child" when field.OriginalName == "b" => "reservedName", "Example.Kind" when field.OriginalName == "a" => "Ready", _ => ""
        };
        foreach (var method in MethodReview.Scan(input).Methods.Where(m => m.OriginalName == "Read")) {
            method.ParameterNames.Single().NewName = "logger"; inventory.Methods.Add(method);
        }
        File.WriteAllText(review, JsonSerializer.Serialize(inventory));
        MethodReview.WriteMap(review, input, map);
        var doc = XDocument.Load(map);
        Require(doc.Descendants("Field").Count() == 12, "Field-only review map omitted entries");
        Require(doc.Descendants("Parameter").Count() == 2 && doc.Descendants("Parameter").All(p => (string)p.Attribute("NewName") == "logger"), "A parameter may share a field alias");
        foreach (var module in doc.Root.Elements("Module")) {
            Require(module.Elements("Field").Count(f => (string)f.Attribute("NewName") == "logger") == 2, "Unrelated owners should reuse logger");
            Require(module.Elements("Field").Count(f => (string)f.Attribute("NewName") == "logger2") == 1, "Same owner collision should be repaired");
            Require(module.Elements("Field").Any(f => (string)f.Attribute("NewName") == "reservedName2") && module.Elements("Field").Any(f => (string)f.Attribute("NewName") == "reservedName3"), "Inheritance collision should be repaired");
        }
        Require(ReviewMapUpdate.Run(null, map, null, Path.Combine(root, "fields-check.json")), "Field map preflight");
        var special = inventory.Fields.First(f => f.OriginalName == "value__"); special.NewName = "storage";
        File.WriteAllText(review, JsonSerializer.Serialize(inventory));
        string rejected = Path.Combine(root, "fields-rejected.xml");
        Require(!ReviewMapUpdate.Run(review, map, rejected, Path.Combine(root, "fields-rejected.json")) && !File.Exists(rejected), "Runtime field alias must fail transactionally");
        special.NewName = "";
        inventory.Fields.First(f => f.NewName == "logger").Sha256 = "00";
        File.WriteAllText(review, JsonSerializer.Serialize(inventory));
        Require(!ReviewMapUpdate.Run(review, map, rejected, Path.Combine(root, "fields-stale.json")) && !File.Exists(rejected), "Stale field identity must fail");
        Console.WriteLine("PASS field inventory, physical compatibility copies, reusable class-scoped aliases, inherited collisions, enum literals, stale identity and special storage guards.");
    }
}
