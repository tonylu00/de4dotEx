using System;
using dnlib.DotNet;
using de4dot.code;
using de4dot.code.deobfuscators;
sealed class FileContext : IDeobfuscatedFile {
 public IDeobfuscatorContext DeobfuscatorContext { get; } = new DeobfuscatorContext();
 public void CreateAssemblyFile(byte[] data,string name,string extension) { throw new NotSupportedException(); }
 public void StringDecryptersAdded() { throw new NotSupportedException(); }
 public void SetDeobfuscator(IDeobfuscator value) { throw new NotSupportedException(); }
 public void MethodModified(MethodDef method) { throw new NotSupportedException(); }
 public void Deobfuscate(MethodDef method) { throw new NotSupportedException(); }
 public void Deobfuscate(MethodDef method,SimpleDeobfuscatorFlags flags) { throw new NotSupportedException(); }
 public void DecryptStrings(MethodDef method,IDeobfuscator value) { throw new NotSupportedException(); }
}
sealed class PolicyProbe : DeobfuscatorBase {
 public PolicyProbe() : base(new OptionsBase()) { }
 public override string Name => "policy";
 public override string Type => "policy";
 public override string TypeLong => "policy";
 protected override int DetectInternal() => 0;
 protected override void ScanForObfuscator() { }
 public override System.Collections.Generic.IEnumerable<int> GetStringDecrypterMethods() => Array.Empty<int>();
 public bool Preserve => PreserveBinarySignatures;
}
static class Program {
 static void Check(bool value) { if(!value) throw new Exception("Binary contract policy lifecycle changed"); }
 static void Main() {
  var file=new FileContext(); var probe=new PolicyProbe {DeobfuscatedFile=file};
  file.DeobfuscatorContext.SetData(BinaryContractPolicy.ContextKey,true);
  probe.DeobfuscateBegin(); Check(probe.Preserve);
  // The real method-body phase clears this property before DeobfuscateEnd.
  probe.DeobfuscatedFile=null; file.DeobfuscatorContext.ClearData(BinaryContractPolicy.ContextKey);
  Check(probe.Preserve);
  var standalone=new PolicyProbe(); standalone.DeobfuscateBegin(); Check(!standalone.Preserve);
  file.DeobfuscatorContext.SetData(BinaryContractPolicy.ContextKey,false);
  probe.DeobfuscatedFile=file; probe.DeobfuscateBegin(); Check(!probe.Preserve);
  Console.WriteLine("PASS batch policy survives method processing, resets between runs and leaves standalone inference enabled");
 }
}
