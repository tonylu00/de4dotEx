using De4dot.NameCandidates;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using System.Security.Cryptography;
using System.Xml.Linq;

static class FieldAccessorFixture {
    public static void Run(string fixtureRoot) {
        string root=Path.Combine(fixtureRoot,"field-accessors"); Directory.CreateDirectory(root);
        var map=new XElement("SourceNameMap",new XAttribute("Version","1"),new XAttribute("InputDirectory",root));
        void Require(bool value,string message) { if (!value) throw new Exception("Field accessors: "+message); }
        foreach (string copy in new[]{"host","compat"}) {
            string relative=copy+"/Library.dll", path=Path.Combine(root,relative); Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var module=new ModuleDefUser("Library.dll") { Kind=ModuleKind.Dll, Mvid=Guid.Parse("65a5a724-f074-4774-8e2e-1443689078a4") }) {
                new AssemblyDefUser("Library",new Version(1,0)).Modules.Add(module);
                var type=new TypeDefUser("Example","Service",module.CorLibTypes.Object.TypeDefOrRef); module.Types.Add(type);
                FieldDef AddField(string name,TypeSig sig,bool isStatic=false) {
                    var field=new FieldDefUser(name,new FieldSig(sig),FieldAttributes.Private|(isStatic?FieldAttributes.Static:0)); type.Fields.Add(field); return field;
                }
                var nameField=AddField("a",module.CorLibTypes.String);
                var readyField=AddField("b",module.CorLibTypes.Boolean,true);
                var unknownField=AddField("c",module.CorLibTypes.String);
                var countField=AddField("d",module.CorLibTypes.Int32);
                var opaqueField=AddField("e",module.CorLibTypes.String);
                MethodDef Method(string name,FieldDef field,bool write=false,bool computed=false,bool virtualMethod=false) {
                    var sig=field.IsStatic?MethodSig.CreateStatic(field.FieldType):MethodSig.CreateInstance(field.FieldType);
                    if (write) { sig.RetType=module.CorLibTypes.Void; sig.Params.Add(field.FieldType); }
                    var method=new MethodDefUser(name,sig,MethodAttributes.Public|(field.IsStatic?MethodAttributes.Static:0)|(virtualMethod?MethodAttributes.Virtual|MethodAttributes.NewSlot:0));
                    method.Body=new CilBody(); type.Methods.Add(method);
                    if (!field.IsStatic) method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
                    if (write) { method.Body.Instructions.Add(Instruction.Create(field.IsStatic?OpCodes.Ldarg_0:OpCodes.Ldarg_1)); method.ParamDefs.Add(new ParamDefUser("x",1)); }
                    method.Body.Instructions.Add(Instruction.Create(write?field.IsStatic?OpCodes.Stsfld:OpCodes.Stfld:field.IsStatic?OpCodes.Ldsfld:OpCodes.Ldfld,field));
                    if (computed) { method.Body.Instructions.Add(Instruction.CreateLdcI4(1)); method.Body.Instructions.Add(Instruction.Create(OpCodes.Add)); }
                    method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); return method;
                }
                Method("aa",nameField); Method("bb",nameField,write:true); Method("cc",readyField);
                Method("dd",unknownField); Method("ee",countField,computed:true); Method("ReadName",nameField);
                var getter=Method("ff",nameField); type.Properties.Add(new PropertyDefUser("Name",PropertySig.CreateInstance(module.CorLibTypes.String)){GetMethod=getter});
                Method("gg",nameField,virtualMethod:true);
                Method("hh",opaqueField);
                module.Write(path);
            }
            using (var module=ModuleDefMD.Load(path)) {
                var type=module.Types.Single(t=>t.Name=="Service");
                var row=new XElement("Module",new XAttribute("Path",relative),new XAttribute("Mvid",module.Mvid),new XAttribute("Sha256",Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))));
                foreach (var field in type.Fields) row.Add(new XElement("Field",new XAttribute("Token","0x"+field.MDToken.Raw.ToString("X8")),
                    new XAttribute("ExpectedName",field.Name),new XAttribute("Signature",field.FullName),
                    new XAttribute("NewName",field.Name.String switch { "a"=>copy=="host"?"projectName":"deviceName", "b"=>"isReady", "c"=>"zz", "e"=>"abcd", _=>"retryCount" })));
                if (copy=="host") {
                    var method=type.Methods.Single(m=>m.Name=="aa");
                    row.Add(new XElement("Method",new XAttribute("Token","0x"+method.MDToken.Raw.ToString("X8")),new XAttribute("ExpectedName",method.Name),
                        new XAttribute("Signature",method.FullName),new XAttribute("NewName","ReadProjectDisplayName")));
                }
                map.Add(row);
            }
        }
        string mapPath=Path.Combine(fixtureRoot,"field-accessors-base.xml"); new XDocument(map).Save(mapPath);
        var report=PropertyFieldReview.Scan(root,mapPath,fieldAccessors:true);
        Require(report.Methods.Count==5,"only pure, unmapped noncontract methods proposed");
        Require(report.Methods.Single(m=>m.Module=="compat/Library.dll"&&m.OriginalName=="aa").NewName=="GetDeviceName","physical field role selects getter name");
        Require(report.Methods.Count(m=>m.NewName=="IsReady")==2,"boolean predicate name");
        Require(report.Methods.Single(m=>m.Module=="host/Library.dll"&&m.OriginalName=="bb").NewName=="SetProjectName","setter role");
        Require(report.Methods.Where(m=>m.OriginalName=="bb").All(m=>m.ParameterNames.Single().NewName=="value"),"setter parameter");
        Require(report.Skips.Any(s=>s.Reason.Contains("field alias lacks"))&&report.Skips.Any(s=>s.Reason.Contains("virtual")),"opaque roles and contracts remain explicit");
        Require(report.Skips.Count(s=>s.Reason.Contains("field alias lacks"))==4,"longer opaque roles are not promoted to method names");
        Require(report.FieldAccessorEvidence.Count==5&&report.FieldAccessorEvidence.All(e=>e.Instructions.Last().Contains("ret")),"body evidence retained");
        string output=Path.Combine(fixtureRoot,"field-accessors.json"); PropertyFieldReview.Write(root,mapPath,output,fieldAccessors:true);
        Require(ReviewMapUpdate.Run(output,mapPath,Path.Combine(fixtureRoot,"field-accessors-updated.xml"),Path.Combine(fixtureRoot,"field-accessors-update.json")),"updater accepts method/parameter proposals");
        Console.WriteLine("PASS pure field reads/writes, reviewed role propagation, boolean naming, physical copies, preserved aliases and contract/computation guards.");
    }
}
