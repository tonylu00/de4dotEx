// Ported from NETReactorSlayer's InitPropertyTests. Module-level checks always
// run; pass the de4dotEx CLI path as a second argument to also cover the batch
// behavior (skip without --force-reactor, restore with it).
using System.Diagnostics;
using System.Security.Cryptography;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using de4dot.code;

if (args.Length is < 1 or > 2 || Directory.Exists(args[0])) throw new ArgumentException("Supply a new fixture directory and optionally the de4dotEx CLI path.");
string root = Path.GetFullPath(args[0]);
Directory.CreateDirectory(root);

void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); Console.WriteLine("PASS: " + message); }

ModuleDefUser NewModule(string name, ModuleKind kind) {
	var corlib = new AssemblyRefUser(new AssemblyNameInfo("mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089"));
	var module = new ModuleDefUser(name + (kind == ModuleKind.Dll ? ".dll" : ".exe"), null, corlib) { Kind = kind, RuntimeVersion = "v4.0.30319" };
	new AssemblyDefUser(name, new Version(1, 0, 0, 0)).Modules.Add(module);
	return module;
}
TypeDef NewType(ModuleDef module, string name) {
	var type = new TypeDefUser("Fixture", name, module.CorLibTypes.Object.TypeDefOrRef) { Attributes = TypeAttributes.Public | TypeAttributes.BeforeFieldInit };
	module.Types.Add(type);
	return type;
}
MethodDef Method(TypeDef type, string name, MethodSig sig) {
	var method = new MethodDefUser(name, sig, MethodImplAttributes.IL | MethodImplAttributes.Managed, MethodAttributes.Public | MethodAttributes.HideBySig | (sig.HasThis ? 0 : MethodAttributes.Static));
	type.Methods.Add(method);
	return method;
}
MethodDef Constructor(TypeDef type) {
	var method = Method(type, ".ctor", MethodSig.CreateInstance(type.Module.CorLibTypes.Void));
	method.Attributes |= MethodAttributes.SpecialName | MethodAttributes.RTSpecialName;
	var baseCtor = new MemberRefUser(type.Module, ".ctor", MethodSig.CreateInstance(type.Module.CorLibTypes.Void), type.Module.CorLibTypes.Object.TypeDefOrRef);
	Body(method, Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Call, baseCtor), Instruction.Create(OpCodes.Ret));
	return method;
}
void Body(MethodDef method, params Instruction[] instructions) {
	method.Body = new CilBody();
	foreach (var instruction in instructions) method.Body.Instructions.Add(instruction);
}

(TypeDef Type, FieldDef Field, MethodDef Getter, MethodDef Setter) InitPair(ModuleDef module, string name) {
	var type = NewType(module, name);
	Constructor(type);
	var marker = module.GetTypes().FirstOrDefault(t => t.FullName == "System.Runtime.CompilerServices.IsExternalInit");
	if (marker == null) {
		marker = new TypeDefUser("System.Runtime.CompilerServices", "IsExternalInit", module.CorLibTypes.Object.TypeDefOrRef)
			{ Attributes = TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed };
		module.Types.Add(marker);
	}
	var field = new FieldDefUser("storage", new FieldSig(module.CorLibTypes.Int32), FieldAttributes.Private | FieldAttributes.InitOnly);
	type.Fields.Add(field);
	var get = Method(type, "QXAvGmnEjSDfrKHtzBRVbcPwaUYLOioF", MethodSig.CreateInstance(module.CorLibTypes.Int32));
	var set = Method(type, "sXAvGmnEjSDfrKHtzBRVbcPwaUYLOioF", MethodSig.CreateInstance(new CModReqdSig(marker, module.CorLibTypes.Void), module.CorLibTypes.Int32));
	foreach (var method in new[] { get, set }) {
		method.IsSpecialName = true;
		var attributeType = module.CorLibTypes.GetTypeRef("System.Runtime.CompilerServices", "CompilerGeneratedAttribute");
		method.CustomAttributes.Add(new CustomAttribute(new MemberRefUser(module, ".ctor", MethodSig.CreateInstance(module.CorLibTypes.Void), attributeType)));
	}
	Body(get, Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldfld, field), Instruction.Create(OpCodes.Ret));
	Body(set, Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldarg_1), Instruction.Create(OpCodes.Stfld, field), Instruction.Create(OpCodes.Ret));
	return (type, field, get, set);
}

using (var module = NewModule("InitGuards", ModuleKind.Dll)) {
	var good = InitPair(module, "Valid");
	good.Type.Fields.Add(new FieldDefUser("RestoredProperty1", new FieldSig(module.CorLibTypes.Int32), FieldAttributes.Private));
	good.Setter.Body.Instructions.Insert(0, Instruction.Create(OpCodes.Nop));
	good.Getter.Body.Instructions[0] = Instruction.Create(OpCodes.Ldarg, good.Getter.Parameters[0]);
	var existing = InitPair(module, "Existing");
	var existingProperty = new PropertyDefUser("Keep", PropertySig.CreateInstance(module.CorLibTypes.Int32)) { GetMethod = existing.Getter, SetMethod = existing.Setter };
	existing.Type.Properties.Add(existingProperty);
	foreach (var guard in new[] { "NoModifier", "OptionalModifier", "Mutable", "NoSpecialName", "NoAttribute", "TwoGetters", "TwoSetters", "SideEffect", "WrongType", "Virtual" }) {
		var pair = InitPair(module, guard);
		switch (guard) {
			case "NoModifier": pair.Setter.MethodSig.RetType = module.CorLibTypes.Void; break;
			case "OptionalModifier": pair.Setter.MethodSig.RetType = new CModOptSig(((ModifierSig)pair.Setter.MethodSig.RetType).Modifier, module.CorLibTypes.Void); break;
			case "Mutable": pair.Field.IsInitOnly = false; break;
			case "NoSpecialName": pair.Setter.IsSpecialName = false; break;
			case "NoAttribute": pair.Setter.CustomAttributes.Clear(); break;
			case "Virtual": pair.Setter.IsVirtual = true; break;
			case "SideEffect": pair.Setter.Body.Instructions.Insert(0, Instruction.Create(OpCodes.Break)); break;
			case "WrongType": pair.Getter.MethodSig.RetType = module.CorLibTypes.String; break;
			default:
				var source = guard == "TwoGetters" ? pair.Getter : pair.Setter;
				var duplicate = Method(pair.Type, "Duplicate", source.MethodSig.Clone());
				duplicate.IsSpecialName = true;
				duplicate.CustomAttributes.Add(source.CustomAttributes[0]);
				Body(duplicate, source.Body.Instructions.Select(i => new Instruction(i.OpCode, i.Operand)).ToArray());
				break;
		}
	}
	Check(InitOnlyPropertyRestorer.Restore(module) == 1, "only a proven unique init pair is restored");
	Check(InitOnlyPropertyRestorer.Restore(module) == 0, "init restoration is idempotent");
	var property = good.Type.Properties.Single();
	Check(property.Name == "RestoredProperty2", "restored property avoids existing source member names");
	Check(property.GetMethod == good.Getter && property.SetMethod == good.Setter && good.Field.IsInitOnly,
		"init restoration retains method identity and readonly storage");
	Check(existing.Type.Properties.Single() == existingProperty, "existing property metadata is preserved");
	var path = Path.Combine(root, "guards.dll");
	module.Write(path);
	using var written = ModuleDefMD.Load(path);
	var restored = written.GetTypes().Single(t => t.Name == "Valid").Properties.Single();
	Check(restored.SetMethod.MethodSig.RetType is CModReqdSig modifier && modifier.Modifier.FullName == "System.Runtime.CompilerServices.IsExternalInit",
		"required init modifier survives metadata writing");
}

if (args.Length == 2) {
	void RunCli(string cli, params string[] arguments) {
		var start = new ProcessStartInfo(cli) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
		foreach (var argument in arguments) start.ArgumentList.Add(argument);
		start.Environment["SHELL"] = "reactor-initprops-test";
		using var process = Process.Start(start)!;
		var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
		process.WaitForExit();
		if (process.ExitCode != 0) throw new Exception("de4dotEx failed (" + process.ExitCode + "): " + output);
	}
	string cli = Path.GetFullPath(args[1]);
	if (!File.Exists(cli)) throw new ArgumentException("CLI not found: " + cli);
	string input = Path.Combine(root, "batch-input");
	Directory.CreateDirectory(input);
	using (var library = NewModule("InitLibrary", ModuleKind.Dll)) {
		InitPair(library, "Container");
		library.Write(Path.Combine(input, "InitLibrary.dll"));
	}
	string inputLibrary = Path.Combine(input, "InitLibrary.dll");
	string originalHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(inputLibrary)));
	string skipOutput = Path.Combine(root, "batch-skip"), forceOutput = Path.Combine(root, "batch-force");
	RunCli(cli, "--batch", input, "--batch-output", skipOutput);
	RunCli(cli, "--batch", input, "--batch-output", forceOutput, "--force-reactor", "InitLibrary.dll");
	string skipLibrary = Path.Combine(skipOutput, "InitLibrary.dll");
	Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(skipLibrary))) == originalHash,
		"batch without --force-reactor copies the clean library unchanged");
	using (var skipped = ModuleDefMD.Load(skipLibrary))
		Check(!skipped.GetTypes().SelectMany(t => t.Properties).Any(), "skipped library keeps no property rows");
	using (var forcedModule = ModuleDefMD.Load(Path.Combine(forceOutput, "InitLibrary.dll"))) {
		var properties = forcedModule.GetTypes().SelectMany(t => t.Properties).ToList();
		Check(properties.Count == 1, "forced batch restores exactly the proven accessor pair");
		var property = properties[0];
		Check(property.GetMethod != null && property.SetMethod != null, "forced batch links both accessors to the property");
		Check(property.SetMethod.MethodSig.RetType is CModReqdSig, "forced batch keeps the required init modifier");
		Check(property.SetMethod.DeclaringType.Fields.Any(f => f.IsInitOnly), "forced batch keeps readonly storage");
	}
	Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(inputLibrary))) == originalHash, "batch input tree unchanged");
}

Console.WriteLine("PASS: all init-only property restoration checks");
