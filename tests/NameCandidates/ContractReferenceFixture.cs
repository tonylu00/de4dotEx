using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using De4dot.NameCandidates;

static class ContractReferenceFixture {
    public static void Run(string root) {
        void Require(bool test,string message) { if (!test) throw new Exception(message); }
        string input=Path.Combine(root,"reference-context-input"); Directory.CreateDirectory(input);
        string dependency=Path.Combine(root,"External.dll");
        using (var external=new ModuleDefUser("External.dll")) {
            new AssemblyDefUser("External",new Version(1,0)).Modules.Add(external);
            var resource=new TypeDefUser("Example","IResource",null) { Attributes=TypeAttributes.Public|TypeAttributes.Interface|TypeAttributes.Abstract };
            external.Types.Add(resource);
            resource.Methods.Add(new MethodDefUser("Close",MethodSig.CreateInstance(external.CorLibTypes.Void),MethodAttributes.Public|MethodAttributes.Virtual|MethodAttributes.NewSlot|MethodAttributes.Abstract));
            using var module=new ModuleDefUser("Input.dll"); new AssemblyDefUser("Input",new Version(1,0)).Modules.Add(module);
            var api=new TypeDefUser("Example","IReader",null) { Attributes=resource.Attributes }; module.Types.Add(api);
            api.Interfaces.Add(new InterfaceImplUser(new Importer(module).Import(resource)));
            api.Methods.Add(new MethodDefUser("a",MethodSig.CreateInstance(module.CorLibTypes.Void),resource.Methods[0].Attributes));
            var type=new TypeDefUser("Example","Reader",module.CorLibTypes.Object.TypeDefOrRef) { Attributes=TypeAttributes.Public }; module.Types.Add(type);
            type.Interfaces.Add(new InterfaceImplUser(api));
            foreach (string name in new[]{"a","Close"}) {
                var method=new MethodDefUser(name,MethodSig.CreateInstance(module.CorLibTypes.Void),MethodAttributes.Public|MethodAttributes.Virtual|MethodAttributes.NewSlot) { Body=new CilBody() };
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); type.Methods.Add(method);
            }
            external.Write(dependency); module.Write(Path.Combine(input,"Input.dll"));
        }
        string manifest=Path.Combine(root,"contract-references.json");
        using (var external=ModuleDefMD.Load(dependency))
            File.WriteAllText(manifest,JsonSerializer.Serialize(new ContractReferences.Manifest { Assemblies=new[]{new ContractReferences.Entry("External.dll",external.Assembly.FullName,external.Mvid.ToString(),Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dependency))))} }));
        string absent=Path.Combine(root,"families-without-reference.json"), present=Path.Combine(root,"families-with-reference.json");
        FamilyReview.Write(input,absent); FamilyReview.Write(input,present,manifest);
        var before=JsonSerializer.Deserialize<MethodReview.Inventory>(File.ReadAllText(absent));
        var after=JsonSerializer.Deserialize<MethodReview.Inventory>(File.ReadAllText(present));
        Require(before.Methods.Where(m=>m.OriginalName=="a").All(m=>m.MappingBlocker!=null),"Missing interface was silently ignored");
        Require(after.Methods.Where(m=>m.OriginalName=="a").Count()==2 && after.Methods.Where(m=>m.OriginalName=="a").All(m=>m.MappingBlocker==null),"Reference did not isolate the unrelated local contract");
        Require(after.Methods.Single(m=>m.OriginalName=="Close").MappingBlocker.Contains("external contract"),"External method became editable");
        Require(after.Methods.All(m=>m.Module=="Input.dll") && after.ContractReferenceSha256!=null,"Reference became an editable input or lost provenance");
        after.Methods=after.Methods.Where(m=>m.OriginalName=="a").ToList(); after.Methods[0].NewName="ReadNextNode";
        File.WriteAllText(present,JsonSerializer.Serialize(after));
        string basis=Path.Combine(root,"reference-context-base.xml");
        new XDocument(new XElement("SourceNameMap",new XAttribute("Version","1"),new XAttribute("InputDirectory",input))).Save(basis);
        string output=Path.Combine(root,"reference-context-map.xml");
        Require(ReviewMapUpdate.Run(present,basis,output,Path.Combine(root,"reference-context-update.json"),contractReferences:manifest),"Resolved contract update failed");
        Require(!ReviewMapUpdate.Run(present,basis,Path.Combine(root,"reference-context-missing.xml"),Path.Combine(root,"reference-context-missing.json")),"Review accepted a missing reference manifest");
        Require(ReviewMapUpdate.Run(null,output,null,Path.Combine(root,"reference-context-check.json"),contractReferences:manifest),"Resolved contract preflight failed");
        File.AppendAllText(dependency,"changed");
        Require(!ReviewMapUpdate.Run(null,output,null,Path.Combine(root,"reference-context-stale.json"),contractReferences:manifest),"Changed reference accepted");
        Require(!File.Exists(Path.Combine(root,"reference-context-missing.xml")),"Failed update published a map");
        Console.WriteLine("PASS explicit reference provenance, unrelated local contracts, external method preservation, missing/stale reference rejection and no partial map.");
    }
}
