using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace de4dot.code.renamer {
	public static class ReflectionNames {
		public const string ContextKey = "de4dot.reflection-names";
		public static HashSet<string> Collect(IEnumerable<ModuleDef> modules) {
			var names = new HashSet<string>(System.StringComparer.Ordinal);
			foreach (var method in modules.SelectMany(m => m.GetTypes()).SelectMany(t => t.Methods).Where(m => m.HasBody)) {
				// A receiver may be obtained in another method or assembly. Pin matching
				// names throughout the graph instead of guessing which overload it means.
				if (!method.Body.Instructions.Any(i => i.Operand is IMethod call && IsLookup(call))) continue;
				foreach (var instruction in method.Body.Instructions)
					if (instruction.OpCode == OpCodes.Ldstr && instruction.Operand is string name) {
						names.Add(name);
						// Reflection uses '+' for nesting; dnlib uses '/'. Retaining a
						// nested name also requires retaining each enclosing type name.
						string typeName = name.Split(',')[0].Replace('+', '/');
						while (true) {
							names.Add(typeName);
							int separator = typeName.LastIndexOf('/');
							if (separator < 0) break;
							typeName = typeName.Substring(0, separator);
						}
					}
			}
			return names;
		}
		static bool IsLookup(IMethod method) {
			string owner = method.DeclaringType.FullName;
			if (owner != "System.Type" && owner != "System.Reflection.Assembly" && owner != "System.Reflection.Module") return false;
			switch ((string)method.Name) {
				case "GetType": case "GetMethod": case "GetField": case "GetProperty":
				case "GetEvent": case "GetMember": case "GetNestedType": case "InvokeMember": return true;
				case "GetFields": case "GetMethods": case "GetProperties": case "GetEvents":
				case "GetMembers": case "GetNestedTypes": case "GetTypes": return true;
				default: return false;
			}
		}
	}
}
