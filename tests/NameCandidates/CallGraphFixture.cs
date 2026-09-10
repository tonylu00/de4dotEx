using dnlib.DotNet;
using dnlib.DotNet.Emit;
using De4dot.NameCandidates;

static class CallGraphFixture {
    public static void Run(string root) {
        foreach (bool reversed in new[] { false, true }) {
            string directory = Path.Combine(root, reversed ? "graph-reversed" : "graph");
            string reference = Path.Combine(directory, "reference"), target = Path.Combine(directory, "target");
            Build(reference, false, false); Build(target, true, reversed);
            var report = Scanner.Scan(reference, target);
            var names = report.Candidates.ToDictionary(c => c.SuggestedName);
            if (names.Count != 6 || names["ReadGenericValue"].MatchRound != 1 || names["GetGenericResult"].MatchRound != 2 ||
                names["GetGenericResult"].MatchedCallees.Length != 1 || names["ReadValue"].MatchRound != 1 || names["GetResult"].MatchRound != 2 ||
                names["ParseResult"].MatchRound != 3 || names["CountItems"].MatchRound != 1 ||
                names["GetResult"].MatchedCallees.Length != 1 || names["ParseResult"].MatchedCallees.Length != 1)
                throw new Exception("Call graph correspondence failed.");
            if (names.ContainsKey("GetChanged") || names.ContainsKey("GetAmbiguous") || names.ContainsKey("GetChangedGeneric")) throw new Exception("Unproven callee used as a match.");
        }
        Console.WriteLine("PASS generic-call propagation, recursive self calls, changed/ambiguous callees and reversed declaration order.");
    }
    static void Build(string directory, bool renamed, bool reverse) {
        Directory.CreateDirectory(directory);
        using var module = new ModuleDefUser("Graph.dll") { Kind = ModuleKind.Dll };
        new AssemblyDefUser("Graph", new Version(renamed ? 2 : 1, 0)).Modules.Add(module);
        var type = new TypeDefUser("Sample", "Service", module.CorLibTypes.Object.TypeDefOrRef) { Attributes = TypeAttributes.Public };
        module.Types.Add(type);
        MethodDef Method(string readable, string hidden, bool generic = false) {
            var signature = generic ? MethodSig.CreateStaticGeneric(1, module.CorLibTypes.Int32) : MethodSig.CreateStatic(module.CorLibTypes.Int32);
            var method = new MethodDefUser(renamed ? hidden : readable, signature, MethodAttributes.Public | MethodAttributes.Static) { Body = new CilBody() };
            if (generic) method.GenericParameters.Add(new GenericParamUser(0, GenericParamAttributes.NonVariant, "T"));
            type.Methods.Add(method); return method;
        }
        void Leaf(MethodDef method, int seed) {
            foreach (var i in new[] { Instruction.CreateLdcI4(seed), Instruction.CreateLdcI4(2), Instruction.Create(OpCodes.Add),
                Instruction.CreateLdcI4(3), Instruction.Create(OpCodes.Mul), Instruction.Create(OpCodes.Ret) }) method.Body.Instructions.Add(i);
        }
        void Caller(MethodDef method, IMethod callee, int seed) {
            foreach (var i in new[] { Instruction.Create(OpCodes.Call, callee), Instruction.CreateLdcI4(seed), Instruction.Create(OpCodes.Add),
                Instruction.CreateLdcI4(2), Instruction.Create(OpCodes.Mul), Instruction.Create(OpCodes.Ret) }) method.Body.Instructions.Add(i);
        }
        var leaf = Method("ReadValue", "qzx", true); Leaf(leaf, 5);
        var first = Method("GetResult", "zqx"); Caller(first, new MethodSpecUser(leaf, new GenericInstMethodSig(module.CorLibTypes.String)), 7);
        var container = new TypeDefUser("Sample", "Container`1", module.CorLibTypes.Object.TypeDefOrRef) { Attributes = TypeAttributes.Public };
        container.GenericParameters.Add(new GenericParamUser(0, GenericParamAttributes.NonVariant, "T"));
        module.Types.Add(container);
        var genericLeaf = Method("ReadGenericValue", "xzq", true);
        type.Methods.Remove(genericLeaf); container.Methods.Add(genericLeaf); Leaf(genericLeaf, 23);
        var instantiated = new TypeSpecUser(new GenericInstSig(new ClassSig(container), module.CorLibTypes.String));
        var member = new MemberRefUser(module, genericLeaf.Name, genericLeaf.MethodSig, instantiated);
        var genericCaller = Method("GetGenericResult", "xzz");
        Caller(genericCaller, new MethodSpecUser(member, new GenericInstMethodSig(module.CorLibTypes.Int32)), 29);
        var changedGeneric = Method("GetChangedGeneric", "zxx");
        Caller(changedGeneric, new MethodSpecUser(member, new GenericInstMethodSig(renamed ? module.CorLibTypes.String : module.CorLibTypes.Int32)), 31);
        var second = Method("ParseResult", "xqz"); Caller(second, first, 11);
        var other = Method("Other", "Other"); Leaf(other, 9);
        var changed = Method("GetChanged", "qqx"); Caller(changed, renamed ? other : first, 17);
        var ambiguousA = Method("ReadData", "zzq"); Leaf(ambiguousA, 13);
        var ambiguousB = Method("ReadCount", "zzx"); Leaf(ambiguousB, 13);
        var ambiguousCaller = Method("GetAmbiguous", "xxq"); Caller(ambiguousCaller, ambiguousA, 19);
        var recursive = Method("CountItems", "qxx");
        recursive.MethodSig.Params.Add(module.CorLibTypes.Int32);
        var exit = Instruction.CreateLdcI4(0);
        foreach (var i in new[] { Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Brfalse, exit),
            Instruction.Create(OpCodes.Ldarg_0), Instruction.CreateLdcI4(1), Instruction.Create(OpCodes.Sub),
            Instruction.Create(OpCodes.Call, recursive), Instruction.CreateLdcI4(1), Instruction.Create(OpCodes.Add), Instruction.Create(OpCodes.Ret),
            exit, Instruction.Create(OpCodes.Ret) }) recursive.Body.Instructions.Add(i);
        if (reverse) { var methods = type.Methods.Reverse().ToArray(); type.Methods.Clear(); foreach (var m in methods) type.Methods.Add(m); }
        module.Write(Path.Combine(directory, "Graph.dll"));
    }
}
