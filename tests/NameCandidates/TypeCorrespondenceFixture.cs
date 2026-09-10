using System.Security.Cryptography;
using System.Xml.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using De4dot.NameCandidates;

static class TypeCorrespondenceFixture {
    public static void Run(string root) {
        foreach (string mode in new[] { "match", "changed", "ambiguous", "single", "localtype", "readable" }) {
            string directory = Path.Combine(root, "type-" + mode);
            string reference = Path.Combine(directory, "reference"), target = Path.Combine(directory, "target");
            Build(reference, false, "match"); Build(target, true, mode);
            string input = Path.Combine(reference, "Types.dll");
            using var module = ModuleDefMD.Load(input);
            var owner = module.GetTypes().Single(t => t.Name == "oldOwner");
            var names = new[] { "GetMajorVersion", "GetMinorVersion", "ToVersionKey", "ReadLocalValue" };
            var row = new XElement("Module", new XAttribute("Path", "Types.dll"), new XAttribute("Mvid", module.Mvid),
                new XAttribute("Sha256", Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(input)))));
            for (int i = 0; i < (mode == "single" ? 1 : mode == "changed" ? 3 : 4); i++) {
                var method = owner.Methods[i];
                row.Add(new XElement("Method", new XAttribute("Token", method.MDToken), new XAttribute("ExpectedName", method.Name),
                    new XAttribute("Signature", method.FullName), new XAttribute("NewName", names[i])));
            }
            string mapPath = Path.Combine(directory, "names.xml");
            new XDocument(new XElement("SourceNameMap", new XAttribute("Version", "1"), new XAttribute("InputDirectory", reference), row)).Save(mapPath);
            var report = Scanner.Scan(reference, target, mapPath);
            if (mode == "match" || mode == "localtype" || mode == "readable") {
                if (report.Candidates.Count != (mode == "match" ? 4 : 3) || report.Candidates.Any(c => c.DeclaringTypeEvidence.Length != (mode == "localtype" ? 2 : 3)) ||
                    report.Candidates.Single(c => c.SuggestedName == "ToVersionKey").MatchRound != 2)
                    throw new Exception("Reviewed short methods did not establish renamed-owner correspondence.");
                if (mode == "readable" && report.Candidates.Any(c => c.SuggestedName == "GetMajorVersion")) throw new Exception("Unknown readable target renamed.");
            } else if (report.Candidates.Count != 0) throw new Exception("Unproven renamed-owner match: " + mode);
        }
        Console.WriteLine("PASS renamed declaring types from two reviewed unique bodies; changed, ambiguous and single-seed types rejected.");
    }
    static void Build(string directory, bool renamed, string mode) {
        Directory.CreateDirectory(directory);
        using var module = new ModuleDefUser("Types.dll") { Kind = ModuleKind.Dll };
        new AssemblyDefUser("Types", new Version(renamed ? 2 : 1, 0)).Modules.Add(module);
        void Owner(string name) {
            var type = new TypeDefUser("", name, module.CorLibTypes.Object.TypeDefOrRef) { Attributes = TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed };
            module.Types.Add(type);
            foreach (string methodName in renamed ? new[] { "x", "y", "z", "q" } : new[] { "a", "b", "c", "d" })
                type.Methods.Add(new MethodDefUser(methodName, MethodSig.CreateStatic(module.CorLibTypes.Int32, module.CorLibTypes.Int32),
                    MethodAttributes.Public | MethodAttributes.Static) { Body = new CilBody() });
            if (mode == "readable") type.Methods[0].Name = "ToMetricKey";
            for (int i = 0; i < 2; i++)
                foreach (var instruction in new[] { Instruction.Create(OpCodes.Ldarg_0), Instruction.CreateLdcI4(mode == "changed" && i == 1 ? 11 : 10),
                    Instruction.Create(i == 0 ? OpCodes.Div : OpCodes.Rem), Instruction.Create(OpCodes.Ret) }) type.Methods[i].Body.Instructions.Add(instruction);
            if (!renamed) foreach (var method in type.Methods) {
                method.Body.InitLocals = true;
                method.Body.Variables.Add(new Local(module.CorLibTypes.Int32));
            }
            foreach (var instruction in new[] { Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Call, type.Methods[0]),
                Instruction.CreateLdcI4(100), Instruction.Create(OpCodes.Mul), Instruction.Create(OpCodes.Ldarg_0),
                Instruction.Create(OpCodes.Call, type.Methods[1]), Instruction.Create(OpCodes.Add), Instruction.Create(OpCodes.Ret) }) type.Methods[2].Body.Instructions.Add(instruction);
            var localMethod = type.Methods[3];
            localMethod.MethodSig = MethodSig.CreateStatic(module.CorLibTypes.Object);
            var local = new Local(mode == "localtype" ? module.CorLibTypes.Object : module.CorLibTypes.String);
            localMethod.Body.Variables.Add(local);
            localMethod.Body.InitLocals = true;
            foreach (var instruction in new[] { Instruction.Create(OpCodes.Ldstr, "test local value"), Instruction.Create(OpCodes.Stloc, local),
                Instruction.Create(OpCodes.Ldloc, local), Instruction.Create(OpCodes.Ret) }) localMethod.Body.Instructions.Add(instruction);
        }
        Owner(renamed ? "newOwner" : "oldOwner");
        if (mode == "ambiguous") Owner("duplicateOwner");
        module.Write(Path.Combine(directory, "Types.dll"));
    }
}
