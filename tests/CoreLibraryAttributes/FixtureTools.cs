using System;
using System.IO;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
class FixtureTools {
 static void Main(string[] args) {
  if(args[0]=="verify") {
   foreach(var name in new[]{"Primitive","Shadow"}) {
    using var module=ModuleDefMD.Load(Path.Combine(args[1],name+".dll"));
    var value=(TypeSig)module.Types.Single(t=>t.Name=="Target").CustomAttributes.Single().ConstructorArguments[0].Value;
    var expected=name=="Primitive"?module.CorLibTypes.AssemblyRef.FullName:module.Assembly.FullName;
    if(value.FullName!="System.String" || value.DefinitionAssembly.FullName!=expected) throw new Exception("Attribute scope changed incorrectly: "+name);
   }
   Console.WriteLine("PASS: repaired primitive scope survives serialization; real same-named local type retained");
   return;
  }
  Directory.CreateDirectory(args[1]);
  foreach(var name in args[0]=="bad"?new[]{"Missing"}:new[]{"Primitive","Shadow"}) {
   using var module=new ModuleDefUser(name+".dll"){Kind=ModuleKind.Dll};
   new AssemblyDefUser(name,new Version(1,0,0,0)).Modules.Add(module);
   if(name=="Shadow") module.Types.Add(new TypeDefUser("System","String",module.CorLibTypes.Object.TypeDefOrRef){Attributes=TypeAttributes.Public});
   var attribute=new TypeDefUser("TypeCaptureAttribute",module.CorLibTypes.GetTypeRef("System","Attribute")){Attributes=TypeAttributes.Public};
   module.Types.Add(attribute);
   var typeSig=new ClassSig(module.CorLibTypes.GetTypeRef("System","Type"));
   var ctor=new MethodDefUser(".ctor",MethodSig.CreateInstance(module.CorLibTypes.Void,typeSig),MethodAttributes.Public|MethodAttributes.SpecialName|MethodAttributes.RTSpecialName){Body=new CilBody()};
   attribute.Methods.Add(ctor);
   ctor.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
   ctor.Body.Instructions.Add(Instruction.Create(OpCodes.Call,new MemberRefUser(module,".ctor",MethodSig.CreateInstance(module.CorLibTypes.Void),attribute.BaseType)));
   ctor.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
   var target=new TypeDefUser("Target",module.CorLibTypes.Object.TypeDefOrRef){Attributes=TypeAttributes.Public};
   module.Types.Add(target);
   var custom=new CustomAttribute(ctor);
   custom.ConstructorArguments.Add(new CAArgument(typeSig,new ClassSig(new TypeRefUser(module,"System",name=="Missing"?"NotACoreType":"String",new AssemblyRefUser(module.Assembly)))));
   target.CustomAttributes.Add(custom);
   module.Write(Path.Combine(args[1],name+".dll"));
  }
 }
}
