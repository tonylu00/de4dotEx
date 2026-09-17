/*
    Copyright (C) 2011-2015 de4dot@gmail.com
    Copyright (C) 2021 CodeStrikers.org

    This file is part of de4dot.

    de4dot is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    de4dot is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with de4dot.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace de4dot.code {
	// Ported from NETReactorSlayer (InitPropertyRestorer). .NET Reactor can clear
	// Property rows while retaining the compiler-generated accessors: an init-only
	// setter whose return type carries an IsExternalInit modifier and the matching
	// getter over the same readonly field. Restoring only unambiguous pairs before
	// the renamer runs keeps the accessors associated as a property; the original
	// methods, required return modifier and readonly storage stay intact so
	// binary callers and persisted metadata remain valid.
	public static class InitOnlyPropertyRestorer {
		public static int Restore(ModuleDef module) {
			int restored = 0;
			var comparer = new SigComparer();
			foreach (var type in module.GetTypes()) {
				var associated = new HashSet<MethodDef>(type.Properties.SelectMany(p =>
					p.GetMethods.Concat(p.SetMethods).Concat(p.OtherMethods)));
				foreach (var evt in type.Events) {
					if (evt.AddMethod != null) associated.Add(evt.AddMethod);
					if (evt.RemoveMethod != null) associated.Add(evt.RemoveMethod);
					if (evt.InvokeMethod != null) associated.Add(evt.InvokeMethod);
					associated.UnionWith(evt.OtherMethods);
				}
				var getters = new Dictionary<FieldDef, List<MethodDef>>();
				var setters = new Dictionary<FieldDef, List<MethodDef>>();
				foreach (var method in type.Methods) {
					if (associated.Contains(method) || method.SemanticsAttributes != 0 ||
						!method.IsSpecialName || method.IsStatic || method.HasGenericParameters ||
						method.HasOverrides || method.IsVirtual || !method.HasBody ||
						!method.CustomAttributes.Any(a => a.TypeFullName == compilerGeneratedAttribute) ||
						method.Body.HasExceptionHandlers || method.MethodSig == null || !method.MethodSig.HasThis ||
						method.MethodSig.ExplicitThis || method.MethodSig.ParamsAfterSentinel?.Count > 0)
						continue;
					var instructions = method.Body.Instructions.Where(i => i.OpCode.Code != Code.Nop).ToArray();
					bool setter = IsInitOnly(method.MethodSig.RetType) && method.MethodSig.Params.Count == 1;
					bool getter = method.MethodSig.Params.Count == 0;
					if ((!setter && !getter) || instructions.Length != (setter ? 4 : 3) ||
						!instructions[0].IsLdarg() || instructions[0].GetParameterIndex() != 0 ||
						instructions[instructions.Length - 1].OpCode.Code != Code.Ret)
						continue;
					int fieldIndex = setter ? 2 : 1;
					if (setter && (!instructions[1].IsLdarg() || instructions[1].GetParameterIndex() != 1))
						continue;
					if (instructions[fieldIndex].OpCode.Code != (setter ? Code.Stfld : Code.Ldfld))
						continue;
					var field = (instructions[fieldIndex].Operand as IField)?.ResolveFieldDef();
					if (field == null || field.DeclaringType != type || field.IsStatic || !field.IsInitOnly ||
						!comparer.Equals(field.FieldType, setter ? method.MethodSig.Params[0] : method.MethodSig.RetType))
						continue;
					var candidates = setter ? setters : getters;
					if (!candidates.TryGetValue(field, out var methods))
						candidates[field] = methods = new List<MethodDef>();
					methods.Add(method);
				}
				var names = new HashSet<string>(type.Methods.Select(m => m.Name.String)
					.Concat(type.Fields.Select(f => f.Name.String)).Concat(type.Properties.Select(p => p.Name.String))
					.Concat(type.Events.Select(e => e.Name.String)).Concat(type.NestedTypes.Select(t => t.Name.String))
					.Concat(new[] { type.Name.String }), StringComparer.Ordinal);
				int ordinal = 0;
				foreach (var pair in setters) {
					if (pair.Value.Count != 1 || !getters.TryGetValue(pair.Key, out var reads) || reads.Count != 1)
						continue;
					string name;
					do { name = "RestoredProperty" + ++ordinal; } while (!names.Add(name));
					var property = new PropertyDefUser(name, PropertySig.CreateInstance(pair.Key.FieldType), 0) {
						GetMethod = reads[0],
						SetMethod = pair.Value[0],
					};
					type.Properties.Add(module.UpdateRowId(property));
					restored++;
				}
			}
			return restored;
		}

		static bool IsInitOnly(TypeSig returnType) {
			if (returnType?.RemovePinnedAndModifiers().ElementType != ElementType.Void)
				return false;
			while (returnType is ModifierSig modifier) {
				if (modifier is CModReqdSig && modifier.Modifier.FullName == externalInitModifier)
					return true;
				returnType = modifier.Next;
			}
			return false;
		}

		const string compilerGeneratedAttribute = "System.Runtime.CompilerServices.CompilerGeneratedAttribute";
		const string externalInitModifier = "System.Runtime.CompilerServices.IsExternalInit";
	}
}
