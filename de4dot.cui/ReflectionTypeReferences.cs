using System;
using System.Collections.Generic;
using System.Linq;
using de4dot.code.renamer.asmmodules;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace de4dot.cui {
	// Capture the binding before renaming; never replace arbitrary matching strings.
	sealed class ReflectionTypeReferences {
		readonly Dictionary<ModuleDef, List<Tuple<Instruction, TypeDef>>> references = new Dictionary<ModuleDef, List<Tuple<Instruction, TypeDef>>>();

		public ReflectionTypeReferences(Modules graph) {
			foreach (var module in graph.TheModules) {
				var captured = new List<Tuple<Instruction, TypeDef>>();
				foreach (var type in module.ModuleDefMD.GetTypes())
				foreach (var method in type.Methods) {
					if (!method.HasBody) continue;
					var instructions = method.Body.Instructions;
					var entryPoints = new HashSet<Instruction>();
					foreach (var instruction in instructions) {
						if (instruction.Operand is Instruction target) entryPoints.Add(target);
						else if (instruction.Operand is IList<Instruction> targets)
							foreach (var branchTarget in targets) entryPoints.Add(branchTarget);
					}
					foreach (var handler in method.Body.ExceptionHandlers) {
						entryPoints.Add(handler.TryStart);
						entryPoints.Add(handler.HandlerStart);
						entryPoints.Add(handler.FilterStart);
					}
					for (int i = 4; i < instructions.Count; i++) {
						// typeof(T).Assembly.GetType("Name") has an unambiguous assembly owner,
						// even when compatibility folders contain identical assembly identities.
						if (!Calls(instructions[i], "System.Type System.Reflection.Assembly::GetType(System.String)") ||
							instructions[i - 1].OpCode != OpCodes.Ldstr ||
							!Calls(instructions[i - 2], "System.Reflection.Assembly System.Type::get_Assembly()") ||
							!Calls(instructions[i - 3], "System.Type System.Type::GetTypeFromHandle(System.RuntimeTypeHandle)") ||
							instructions[i - 4].OpCode != OpCodes.Ldtoken ||
							!(instructions[i - 4].Operand is ITypeDefOrRef owner)) continue;
						if (Enumerable.Range(i - 3, 4).Any(index => entryPoints.Contains(instructions[index]))) continue;
						var ownerDefinition = owner as TypeDef ?? graph.ResolveType(owner)?.TypeDef;
						if (ownerDefinition?.Module?.Assembly == null) continue;
						var name = (string)instructions[i - 1].Operand;
						var matches = ownerDefinition.Module.Assembly.Modules.SelectMany(m => m.GetTypes())
							.Where(t => ReflectionName(t) == name).Take(2).ToList();
						if (matches.Count == 1) captured.Add(Tuple.Create(instructions[i - 1], matches[0]));
					}
				}
				references[module.ModuleDefMD] = captured;
			}
		}

		public bool Apply(ModuleDef module) {
			bool changed = false;
			foreach (var reference in references[module]) {
				var name = ReflectionName(reference.Item2);
				if ((string)reference.Item1.Operand == name) continue;
				reference.Item1.Operand = name;
				changed = true;
			}
			return changed;
		}

		static string ReflectionName(TypeDef type) => type.ReflectionFullName;
		static bool Calls(Instruction instruction, string signature) =>
			(instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt) &&
			instruction.Operand is IMethod method && method.FullName == signature;
	}
}
