using De4dot.NameCandidates;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using System.Text.Json;

static class MethodBodyInspectionFixture {
    public static void Run(string root) {
        string path=Path.Combine(root,"inspection.dll");
        using (var module=new ModuleDefUser("inspection.dll")) {
            new AssemblyDefUser("Inspection",new Version(1,0)).Modules.Add(module);
            var type=new TypeDefUser("Example","Capture",module.CorLibTypes.Object.TypeDefOrRef); module.Types.Add(type);
            var field=new FieldDefUser("capturedPath",new FieldSig(module.CorLibTypes.String),FieldAttributes.Public); type.Fields.Add(field);
            var method=new MethodDefUser("a",MethodSig.CreateInstance(module.CorLibTypes.Boolean,module.CorLibTypes.String),MethodAttributes.Public) { Body=new CilBody() };
            type.Methods.Add(method); method.ParamDefs.Add(new ParamDefUser("rootPath",1));
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldfld,field));
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_1));
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt,new MemberRefUser(module,"StartsWith",MethodSig.CreateInstance(module.CorLibTypes.Boolean,module.CorLibTypes.String),module.CorLibTypes.String.TypeDefOrRef)));
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            module.Write(path);
        }
        var saved=Console.Out; var output=new StringWriter();
        try { Console.SetOut(output); MethodBodyInspection.Write(path,new[]{"0x06000001"}); }
        finally { Console.SetOut(saved); }
        using var result=JsonDocument.Parse(output.ToString());
        var body=result.RootElement.GetProperty("Methods")[0];
        if (body.GetProperty("Parameters")[0].GetProperty("Sequence").ValueKind!=JsonValueKind.Null ||
            body.GetProperty("Parameters")[1].GetProperty("Name").GetString()!="rootPath" ||
            body.GetProperty("Instructions")[1].GetProperty("Operand").GetProperty("Text").GetString()!="System.String Example.Capture::capturedPath" ||
            body.GetProperty("Instructions")[2].GetProperty("OpCode").GetString()!="ldarg.1")
            throw new Exception("Inspection lost the distinction between captured receiver field and lambda argument.");
        bool rejected=false;
        try { MethodBodyInspection.Write(path,new[]{"0x04000001"}); }
        catch (ArgumentException) { rejected=true; }
        if (!rejected) throw new Exception("Inspection accepted a field token as a method.");
        Console.WriteLine("PASS instruction evidence preserves captured fields, argument positions and method-token validation without execution.");
    }
}
