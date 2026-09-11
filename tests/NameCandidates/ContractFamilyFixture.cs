using System.Text.Json;
using System.Xml.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using De4dot.NameCandidates;
using SourceNameMapping;

static class ContractFamilyFixture {
    public static void Run(string root) {
        void Require(bool test, string message) { if (!test) throw new Exception(message); }
        string input = Path.Combine(root, "contract-inputs"); Directory.CreateDirectory(input);
        foreach (string branch in new[] { "host", "compat" }) {
            string directory = Path.Combine(input, branch); Directory.CreateDirectory(directory);
            using var api = new ModuleDefUser("Contract.dll") { Kind = ModuleKind.Dll };
            new AssemblyDefUser("Contract", new Version(1, 0)).Modules.Add(api);
            var contract = new TypeDefUser("Example", "IContract", null) { Attributes = TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract };
            api.Types.Add(contract);
            var flags = MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot | MethodAttributes.Abstract;
            contract.Methods.Add(new MethodDefUser("a", MethodSig.CreateInstance(api.CorLibTypes.Int32, api.CorLibTypes.Int32), flags));
            contract.Methods.Add(new MethodDefUser("a", MethodSig.CreateInstance(api.CorLibTypes.String, api.CorLibTypes.String), flags));
            var generic = new TypeDefUser("Example", "IGeneric`1", null) { Attributes = contract.Attributes };
            generic.GenericParameters.Add(new GenericParamUser(0, GenericParamAttributes.NonVariant, "T")); api.Types.Add(generic);
            generic.Methods.Add(new MethodDefUser("b", MethodSig.CreateInstance(new GenericVar(0, generic), new GenericVar(0, generic)), flags));
            using var implementation = new ModuleDefUser("Implementation.dll") { Kind = ModuleKind.Dll };
            new AssemblyDefUser("Implementation", new Version(1, 0)).Modules.Add(implementation);
            var owner = new TypeDefUser("Example", "Implementation", implementation.CorLibTypes.Object.TypeDefOrRef) { Attributes = TypeAttributes.Public };
            implementation.Types.Add(owner);
            var importer = new Importer(implementation);
            owner.Interfaces.Add(new InterfaceImplUser(importer.Import(contract)));
            owner.Interfaces.Add(new InterfaceImplUser(new TypeSpecUser(new GenericInstSig(new ClassSig(importer.Import(generic)), implementation.CorLibTypes.String))));
            foreach (var pair in new[] { ("a", implementation.CorLibTypes.Int32), ("a", implementation.CorLibTypes.String), ("b", implementation.CorLibTypes.String) }) {
                var method = new MethodDefUser(pair.Item1, MethodSig.CreateInstance(pair.Item2, pair.Item2), MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot) { Body = new CilBody() };
                method.ParamDefs.Add(new ParamDefUser("value", 1));
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_1)); method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); owner.Methods.Add(method);
            }
            var derived = new TypeDefUser("Example", "Derived", owner) { Attributes = TypeAttributes.Public }; implementation.Types.Add(derived);
            var overridden = new MethodDefUser("a", owner.Methods[0].MethodSig, MethodAttributes.Public | MethodAttributes.Virtual) { Body = new CilBody() };
            overridden.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4, branch == "host" ? 11 : 29)); overridden.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); derived.Methods.Add(overridden);
            api.Write(Path.Combine(directory, "Contract.dll")); implementation.Write(Path.Combine(directory, "Implementation.dll"));
        }
        string reviewPath = Path.Combine(root, "contracts.json"); FamilyReview.Write(input, reviewPath);
        var review = JsonSerializer.Deserialize<MethodReview.Inventory>(File.ReadAllText(reviewPath));
        var first = review.Methods.Single(m => m.Module == "host/Contract.dll" && m.OriginalName == "a" && m.Signature.Contains("Int32"));
        var genericSeed = review.Methods.Single(m => m.Module == "host/Contract.dll" && m.OriginalName == "b");
        var implementationSeed = review.Methods.Single(m => m.Module == "host/Implementation.dll" && m.Type == "Example.Implementation" && m.ContractFamily == first.ContractFamily);
        Require(first.MappingBlocker == null && genericSeed.MappingBlocker == null, "Supported family blocked");
        Require(review.Methods.Count(m => m.ContractFamily == first.ContractFamily) == 3, "Interface/implementation/override not grouped");
        Require(review.Methods.Count(m => m.ContractFamily == genericSeed.ContractFamily) == 2, "Generic substitution not grouped");
        Require(review.Methods.Where(m => m.Module.StartsWith("compat/")).All(m => m.ContractFamily != first.ContractFamily), "Physical duplicate identities merged");
        first.NewName = "ReadIntValue"; genericSeed.NewName = "ConvertValue";
        review.Methods = new() { first, genericSeed };
        File.WriteAllText(reviewPath, JsonSerializer.Serialize(review));
        string basis = Path.Combine(root, "contracts-base.xml"); new XDocument(new XElement("SourceNameMap", new XAttribute("Version", "1"), new XAttribute("InputDirectory", input))).Save(basis);
        string output = Path.Combine(root, "contracts-map.xml");
        Require(ReviewMapUpdate.Run(reviewPath, basis, output, Path.Combine(root, "contracts-update.json")), "Family update failed");
        var mapped = XDocument.Load(output);
        Require(mapped.Descendants("Method").Count() == 5 && mapped.Descendants("Module").All(m => ((string)m.Attribute("Path")).StartsWith("host/")), "Family expansion affected wrong modules");
        Require(mapped.Descendants("Method").Count(m => (string)m.Attribute("NewName") == "ReadIntValue") == 3, "Family members have different aliases");
        Require(ReviewMapUpdate.Run(null, output, null, Path.Combine(root, "contracts-check.json")), "Valid family failed preflight");
        first.NewName = "Implementation";
        review.Methods = new() { first };
        File.WriteAllText(reviewPath, JsonSerializer.Serialize(review));
        string collisionOutput = Path.Combine(root, "contracts-collision-map.xml");
        Require(ReviewMapUpdate.Run(reviewPath, output, collisionOutput, Path.Combine(root, "contracts-collision.json")), "Family collision repair failed");
        var repaired = XDocument.Load(collisionOutput);
        Require(repaired.Descendants("Method").Count(m => (string)m.Attribute("NewName") == "Implementation2") == 3, "Collision suffix was not shared across all physical modules");
        Require(repaired.Descendants("Method").Count(m => (string)m.Attribute("NewName") == "ConvertValue") == 2, "Untouched family changed");
        implementationSeed.ParameterNames[0].NewName = "inputValue";
        review.Methods = new() { implementationSeed };
        File.WriteAllText(reviewPath, JsonSerializer.Serialize(review));
        string parameterOutput = Path.Combine(root, "contracts-parameter-map.xml");
        Require(ReviewMapUpdate.Run(reviewPath, collisionOutput, parameterOutput, Path.Combine(root, "contracts-parameter.json")), "Parameter-only family edit failed");
        var parameters = XDocument.Load(parameterOutput);
        Require(parameters.Descendants("Parameter").Single().Attribute("NewName").Value == "inputValue" &&
            parameters.Descendants("Method").Count(m => (string)m.Attribute("NewName") == "Implementation2") == 3, "Parameter-only edit changed the family name");
        implementationSeed.NewName = "ConflictingName";
        review.Methods = new() { first, implementationSeed };
        File.WriteAllText(reviewPath, JsonSerializer.Serialize(review));
        string conflictOutput = Path.Combine(root, "contracts-conflict-map.xml");
        Require(!ReviewMapUpdate.Run(reviewPath, output, conflictOutput, Path.Combine(root, "contracts-conflict.json")) && !File.Exists(conflictOutput), "Conflicting family seeds published a map");
        foreach (string failure in new[] { "partial", "different", "stale" }) {
            var bad = new XDocument(mapped);
            if (failure == "partial") bad.Descendants("Method").First().Remove();
            if (failure == "different") bad.Descendants("Method").First().SetAttributeValue("NewName", "Different");
            if (failure == "stale") bad.Descendants("Method").First().SetAttributeValue("ContractFamily", "stale");
            string path = Path.Combine(root, "contracts-" + failure + ".xml"); bad.Save(path);
            Require(!ReviewMapUpdate.Run(null, path, null, Path.Combine(root, "contracts-" + failure + ".json")), "Invalid family accepted: " + failure);
        }
        review.Methods = new() { first };
        first.Sha256 = "00"; File.WriteAllText(reviewPath, JsonSerializer.Serialize(review));
        string failed = Path.Combine(root, "contracts-stale-output.xml");
        Require(!ReviewMapUpdate.Run(reviewPath, basis, failed, Path.Combine(root, "contracts-stale-review.json")) && !File.Exists(failed), "Stale family seed was repaired instead of rejected");
        CheckExplicitAndExternal();
        Console.WriteLine("PASS contract-family discovery/expansion, generic substitution, overrides, overload separation, duplicate physical identities, atomic collisions, parameter edits and invalid map rejection.");
    }

    static void CheckExplicitAndExternal() {
        void Require(bool test, string message) { if (!test) throw new Exception(message); }
        using var api = new ModuleDefUser("ExplicitContracts.dll");
        new AssemblyDefUser("ExplicitContracts", new Version(1, 0)).Modules.Add(api);
        var contract = new TypeDefUser("Example", "IContract`1", null) { Attributes = TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract };
        contract.GenericParameters.Add(new GenericParamUser(0, GenericParamAttributes.NonVariant, "T")); api.Types.Add(contract);
        var flags = MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot | MethodAttributes.Abstract;
        var generic = new MethodDefUser("a", MethodSig.CreateInstance(new GenericVar(0, contract), new GenericVar(0, contract)), flags);
        var integer = new MethodDefUser("a", MethodSig.CreateInstance(api.CorLibTypes.Int32, api.CorLibTypes.Int32), flags);
        contract.Methods.Add(generic); contract.Methods.Add(integer);
        using var module = new ModuleDefUser("ExplicitImplementation.dll");
        new AssemblyDefUser("ExplicitImplementation", new Version(1, 0)).Modules.Add(module);
        var owner = new TypeDefUser("Example", "Implementation", module.CorLibTypes.Object.TypeDefOrRef) { Attributes = TypeAttributes.Public };
        module.Types.Add(owner);
        var instance = new TypeSpecUser(new GenericInstSig(new ClassSig(new Importer(module).Import(contract)), module.CorLibTypes.String));
        owner.Interfaces.Add(new InterfaceImplUser(instance));
        var explicitMethod = new MethodDefUser("Example.IContract<System.String>.a", MethodSig.CreateInstance(module.CorLibTypes.String, module.CorLibTypes.String), MethodAttributes.Private | MethodAttributes.Virtual | MethodAttributes.NewSlot | MethodAttributes.Final);
        owner.Methods.Add(explicitMethod);
        explicitMethod.Overrides.Add(new MethodOverride(explicitMethod, new MemberRefUser(module, "a", MethodSig.CreateInstance(new GenericVar(0), new GenericVar(0)), instance)));
        var implicitMethod = new MethodDefUser("a", MethodSig.CreateInstance(module.CorLibTypes.Int32, module.CorLibTypes.Int32), MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot);
        owner.Methods.Add(implicitMethod);
        var outside = new TypeDefUser("Example", "ExternalImplementation", module.CorLibTypes.Object.TypeDefOrRef) { Attributes = TypeAttributes.Public };
        module.Types.Add(outside);
        outside.Interfaces.Add(new InterfaceImplUser(new TypeRefUser(module, "External", "IMissing", new AssemblyRefUser("Missing", new Version(1, 0)))));
        var unknown = new MethodDefUser("b", MethodSig.CreateInstance(module.CorLibTypes.Void), MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot);
        outside.Methods.Add(unknown);
        var graph = new MethodContractFamilies(new[] { api, module }, m => m.Name);
        Require(graph.Get(generic) == graph.Get(explicitMethod) && graph.Get(generic).Blockers.Length != 0, "Generic explicit contract was not blocked as a complete family");
        Require(graph.Get(integer) == graph.Get(implicitMethod) && graph.Get(integer).Methods.Length == 2 && graph.Get(integer).Blockers.Length == 0, "Explicit overload suppressed a different implicit overload");
        Require(graph.Get(unknown).Blockers.Any(b => b.StartsWith("unresolved interface:")), "Unresolved external contract was editable");
    }
}
