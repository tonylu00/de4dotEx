using System;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
class Verify {
 static void Main(string[] args) {
  using var module=ModuleDefMD.Load(args[0]);
  var method=module.GetTypes().Single(t=>t.Name=="Fixture").Methods.Single(m=>m.Name=="Values");
  if(method.Body.Instructions.Any(i=>i.Operand is IMethod m && m.Name=="Decode")) throw new Exception("Encrypted decoder calls remain");
  string[] expected={"{0}.signature","certificate:{0}","\0\u4e2d\ud83d\ude00\uffff","","validation succeeded:{0}"};
  var actual=method.Body.Instructions.Where(i=>i.OpCode==OpCodes.Ldstr).Select(i=>(string)i.Operand);
  if(!actual.SequenceEqual(expected)) throw new Exception("Exported string literals differ from expected values");
  Console.WriteLine("PASS: static decoder calls replaced with exact readable literals");
 }
}
