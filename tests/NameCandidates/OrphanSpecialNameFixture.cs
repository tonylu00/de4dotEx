using System.Text.Json;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using De4dot.NameCandidates;
using SourceNameMapping;

static class OrphanSpecialNameFixture {
    public static void Run(string root) {
        void Require(bool test, string message) { if (!test) throw new Exception(message); }
        using var module = new ModuleDefUser("Orphan.dll") { Kind = ModuleKind.Dll };
        new AssemblyDefUser("Orphan", new Version(1, 0)).Modules.Add(module);
        var owner = new TypeDefUser("Example", "Api", module.CorLibTypes.Object.TypeDefOrRef) { Attributes = TypeAttributes.Public };
        module.Types.Add(owner);
        MethodDef Add(string name, MethodAttributes flags) {
            var method = new MethodDefUser(name, MethodSig.CreateStatic(module.CorLibTypes.Int32), MethodAttributes.Public | MethodAttributes.Static | flags) { Body = new CilBody() };
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4, 42)); method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            owner.Methods.Add(method); return method;
        }
        var orphan = Add("a", MethodAttributes.SpecialName);
        var meaningful = Add("Load", MethodAttributes.SpecialName);
        var propertyMethod = Add("ReadValue", 0);
        var property = new PropertyDefUser("Value", PropertySig.CreateStatic(module.CorLibTypes.Int32));
        property.GetMethods.Add(propertyMethod); owner.Properties.Add(property);
        var operatorMethod = Add("op_Implicit", MethodAttributes.SpecialName);
        var runtimeSpecial = Add("c", MethodAttributes.RTSpecialName);
        var eventMethod = new MethodDefUser("Subscribe", MethodSig.CreateStatic(module.CorLibTypes.Void, module.CorLibTypes.Object), MethodAttributes.Public | MethodAttributes.Static);
        owner.Methods.Add(eventMethod);
        owner.Events.Add(new EventDefUser("Changed", module.CorLibTypes.Object.TypeDefOrRef) { AddMethod = eventMethod });
        Require(MethodContractFamilies.MethodBlocker(orphan) == null, "Unassociated SpecialName method blocked");
        Require(MethodContractFamilies.MethodBlocker(propertyMethod) != null, "Property accessor without SpecialName accepted");
        Require(MethodContractFamilies.MethodBlocker(eventMethod) != null, "Event accessor without SpecialName accepted");
        Require(MethodContractFamilies.MethodBlocker(operatorMethod) != null && MethodContractFamilies.MethodBlocker(runtimeSpecial) != null, "Operator/runtime contract accepted");
        // Write only valid ordinary/orphan methods for the portable review and
        // conversion check. The malformed guard cases above never execute.
        owner.Properties.Clear(); owner.Events.Clear();
        foreach (var method in new[] { propertyMethod, operatorMethod, runtimeSpecial, eventMethod }) owner.Methods.Remove(method);
        string path = Path.Combine(root, "orphan.dll"); module.Write(path);
        var review = MethodReview.Scan(path);
        var row = review.Methods.Single(m => m.OriginalName == "a");
        Require(row.NeedsReadableName && row.MappingBlocker == null, "Orphan method was not reviewable");
        Require(!review.Methods.Single(m => m.OriginalName == "Load").NeedsReadableName, "Meaningful SpecialName method was misclassified");
        row.NewName = "ReadStoredValue";
        review.Methods = new() { row };
        string reviewed = Path.Combine(root, "orphan-reviewed.json"), map = Path.Combine(root, "orphan-map.xml");
        File.WriteAllText(reviewed, JsonSerializer.Serialize(review));
        MethodReview.WriteMap(reviewed, root, map);
        Require(ReviewMapUpdate.Run(null, map, null, Path.Combine(root, "orphan-check.json")), "Orphan method map failed preflight");
        Console.WriteLine("PASS orphan SpecialName review/conversion and property/event/operator/runtime guards.");
    }
}
