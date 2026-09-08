using System;
using System.IO;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

class EmitFixture {
    static void Main(string[] args) {
        if (args[0] == "--remove-get") {
            using var broken = ModuleDefMD.Load(args[1]);
            foreach (var type in broken.Types)
                for (int i = type.Methods.Count - 1; i >= 0; i--)
                    if (type.Methods[i].Name == "Get") type.Methods.RemoveAt(i);
            broken.Write(args[2]);
            return;
        }
        using var library = ModuleDefMD.Load(args[0]);
        using var client = ModuleDefMD.Load(args[1]);
        foreach (var module in new[] { library, client })
            foreach (var type in module.GetTypes())
                foreach (var method in type.Methods)
                    if (method.HasBody)
                        foreach (var instruction in method.Body.Instructions)
                            if (instruction.OpCode == OpCodes.Ldstr && (string)instruction.Operand == "Api") instruction.Operand = "<Api>";
        if (args.Length > 3 && args[3] == "batch")
            library.Types.Add(new TypeDefUser("DotfuscatorAttribute", library.CorLibTypes.Object.TypeDefOrRef));
        foreach (var type in client.GetTypeRefs())
            if (type.Name == "Api") type.Name = "<Api>";
        foreach (var type in library.Types) {
            if (type.Name == "Api") type.Name = "<Api>";
            foreach (var method in type.Methods) {
                if (!method.HasBody) continue;
                foreach (var instruction in method.Body.Instructions)
                    if (instruction.OpCode == OpCodes.Call && instruction.Operand is MethodDef target && target.DeclaringType.Name == "Helper")
                        instruction.Operand = new MemberRefUser(library, target.Name, target.MethodSig,
                            new TypeRefUser(library, "", "<Helper>", new AssemblyRefUser(library.Assembly)));
            }
        }
        foreach (var type in library.Types)
            if (type.Name == "Helper") type.Name = "<Helper>";
        Directory.CreateDirectory(args[2]);
        library.Write(Path.Combine(args[2], "Library.dll"));
        library.Write(Path.Combine(args[2], "AlternateLibrary.dll"));
        client.Write(Path.Combine(args[2], "Client.exe"));
        client.Write(Path.Combine(args[2], "AlternateClient.exe"));
    }
}
