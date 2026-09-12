using De4dot.NameCandidates;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

static class PropertyFieldFixture {
    public static void Run(string fixtureRoot) {
        string root=Path.Combine(fixtureRoot,"property-fields"); Directory.CreateDirectory(root);
        void Require(bool test,string message) { if (!test) throw new Exception("Property fields: "+message); }
        void Build(string relative,string readableProperty) {
            string path=Path.Combine(root,relative); Directory.CreateDirectory(Path.GetDirectoryName(path));
            using var module=new ModuleDefUser("Library.dll") { Kind=ModuleKind.Dll, Mvid=Guid.Parse("27b70034-1c32-4ee8-8bc6-68056ebefcd3") };
            new AssemblyDefUser("Library",new Version(1,0)).Modules.Add(module);
            var type=new TypeDefUser("Example","Settings",module.CorLibTypes.Object.TypeDefOrRef); module.Types.Add(type);
            FieldDef Field(string name,TypeSig signature=null,bool isStatic=false) {
                var field=new FieldDefUser(name,new FieldSig(signature??module.CorLibTypes.String),FieldAttributes.Private|(isStatic?FieldAttributes.Static:0));
                type.Fields.Add(field); return field;
            }
            void Property(string name,FieldDef field,bool computed=false,bool indexed=false) {
                var signature=field.IsStatic?PropertySig.CreateStatic(field.FieldType):PropertySig.CreateInstance(field.FieldType);
                if (indexed) signature.Params.Add(module.CorLibTypes.Int32);
                var property=new PropertyDefUser(name,signature); type.Properties.Add(property);
                var getter=new MethodDefUser("get_"+name,field.IsStatic?MethodSig.CreateStatic(field.FieldType):MethodSig.CreateInstance(field.FieldType),
                    MethodAttributes.Public|MethodAttributes.SpecialName|(field.IsStatic?MethodAttributes.Static:0));
                if (indexed) getter.MethodSig.Params.Add(module.CorLibTypes.Int32);
                type.Methods.Add(getter); property.GetMethod=getter; getter.Body=new CilBody();
                if (!field.IsStatic) getter.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
                getter.Body.Instructions.Add(Instruction.Create(field.IsStatic?OpCodes.Ldsfld:OpCodes.Ldfld,field));
                if (computed) { getter.Body.Instructions.Add(Instruction.CreateLdcI4(1)); getter.Body.Instructions.Add(Instruction.Create(OpCodes.Add)); }
                getter.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            }
            Property(readableProperty,Field("a"));
            Property("Count",Field("b",module.CorLibTypes.Int32));
            Property("Default",Field("c",type.ToTypeSig(),true));
            var shared=Field("d"); Property("Name",shared); Property("FileName",shared);
            Property("NextValue",Field("e",module.CorLibTypes.Int32),computed:true);
            Property("Item",Field("f"),indexed:true);
            Property("A",Field("g"));
            Property("Value",Field("alreadyReadable"));
            Property("Path",Field("string_0"));
            Property("Event",Field("h"));
            Property("CurrentValue",Field("<CurrentValue>k__BackingField"));
            Property("Property12",Field("i"));
            Property("file",Field("j"));
            module.Write(path);
        }
        Build("host/Library.dll","ProjectName"); Build("compat/Library.dll","DeviceName");
        File.WriteAllText(Path.Combine(root,"native.dll"),"unmanaged fixture");
        string mapPath=Path.Combine(fixtureRoot,"property-fields-base.xml");
        var map=new XElement("SourceNameMap",new XAttribute("Version","1"),new XAttribute("InputDirectory",root));
        using (var module=ModuleDefMD.Load(Path.Combine(root,"host/Library.dll"))) {
            var field=module.Types.Single(t=>t.Name=="Settings").Fields.Single(f=>f.Name=="b");
            map.Add(new XElement("Module",new XAttribute("Path","host/Library.dll"),new XAttribute("Mvid",module.Mvid),
                new XAttribute("Sha256",Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root,"host/Library.dll"))))),
                new XElement("Field",new XAttribute("Token","0x"+field.MDToken.Raw.ToString("X8")),new XAttribute("ExpectedName",field.Name),
                    new XAttribute("Signature",field.FullName),new XAttribute("NewName","reviewedItemCount"))));
        }
        new XDocument(map).Save(mapPath);
        var hashes=Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories).ToDictionary(p=>p,p=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
        var report=PropertyFieldReview.Scan(root,mapPath);
        Require(report.Fields.Single(f=>f.Module=="host/Library.dll"&&f.OriginalName=="a").NewName=="projectName","host semantics");
        Require(report.Fields.Single(f=>f.Module=="compat/Library.dll"&&f.OriginalName=="a").NewName=="deviceName","compatibility semantics remain separate despite identical identities and MVIDs");
        Require(!report.Fields.Any(f=>f.Module=="host/Library.dll"&&f.OriginalName=="b")&&report.Fields.Any(f=>f.Module=="compat/Library.dll"&&f.NewName=="count"),"custom alias applies only to its physical file");
        Require(report.Fields.Count(f=>f.OriginalName=="c"&&f.NewName=="defaultInstance")==2,"static singleton name");
        Require(report.Fields.Count(f=>f.OriginalName=="h"&&f.NewName=="eventValue")==2,"keyword handling");
        Require(report.Fields.Count(f=>f.OriginalName=="j"&&f.NewName=="fileField")==2,"lowercase property collisions receive a role, not a number");
        Require(report.Fields.Count==11&&report.Fields.Any(f=>f.OriginalName=="string_0"&&f.NewName=="path"),"only eligible fields proposed");
        Require(report.Skips.Any(s=>s.Reason.Contains("multiple properties"))&&report.Skips.Any(s=>s.Reason.Contains("readable evidence")),"ambiguous and unreadable roles stay explicit");
        Require(report.PropertyEvidence.Count==report.Fields.Count&&report.PropertyEvidence.All(e=>e.GetterInstructions.Last().Contains("ret")),"getter evidence retained");
        Require(JsonSerializer.Serialize(report)==JsonSerializer.Serialize(PropertyFieldReview.Scan(root,mapPath)),"deterministic scan");
        string output=Path.Combine(fixtureRoot,"property-fields.json"); PropertyFieldReview.Write(root,mapPath,output);
        Require(ReviewMapUpdate.Run(output,mapPath,Path.Combine(fixtureRoot,"property-fields-updated.xml"),Path.Combine(fixtureRoot,"property-fields-update.json")),"portable updater accepts proposals");
        Require(hashes.All(p=>p.Value==Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p.Key)))),"inputs unchanged");
        byte[] original=File.ReadAllBytes(output);
        Require(De4dot.NameCandidates.Program.Main(new[]{"--suggest-property-fields",root,mapPath,output})==1&&original.SequenceEqual(File.ReadAllBytes(output)),"no overwrite");
        map.Element("Module").SetAttributeValue("Sha256","00"); new XDocument(map).Save(mapPath);
        string staleOutput=Path.Combine(fixtureRoot,"property-fields-stale.json");
        Require(De4dot.NameCandidates.Program.Main(new[]{"--suggest-property-fields",root,mapPath,staleOutput})==1&&!File.Exists(staleOutput),"stale map fails before publication");
        Console.WriteLine("PASS direct-property field evidence, physical compatibility scope, existing aliases, computed/indexed/ambiguous exclusions and stale-input guards.");
    }
}
