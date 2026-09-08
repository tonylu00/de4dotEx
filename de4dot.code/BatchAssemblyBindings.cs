using System;
using System.Collections.Generic;
using dnlib.DotNet;

namespace de4dot.code {
	/// <summary>Explicit physical dependency choices shared by transformation and rename passes.</summary>
	public sealed class BatchAssemblyBindings : IAssemblyResolver {
		public const string ContextKey = "de4dot.BatchAssemblyBindings";
		readonly Dictionary<ModuleDef, Dictionary<string, ModuleDef>> bindings = new Dictionary<ModuleDef, Dictionary<string, ModuleDef>>();
		readonly IAssemblyResolver fallback;
		public BatchAssemblyBindings(IAssemblyResolver fallback) => this.fallback = fallback;

		public void Add(ModuleDef source, ModuleDef dependency) {
			if (dependency.Assembly == null || source.Assembly?.FullName == dependency.Assembly.FullName)
				throw new UserException("Batch bindings cannot replace self references: " + source.Location);
			string identity = dependency.Assembly.FullName;
			if (!bindings.TryGetValue(source, out var choices))
				bindings.Add(source, choices = new Dictionary<string, ModuleDef>(StringComparer.Ordinal));
			if (choices.ContainsKey(identity))
				throw new UserException("Duplicate batch binding for " + source.Location + " -> " + identity);
			choices.Add(identity, dependency);
		}

		public ModuleDef Find(IAssembly assembly, ModuleDef source) {
			if (assembly != null && source != null && bindings.TryGetValue(source, out var choices) &&
				choices.TryGetValue(assembly.FullName, out var dependency))
				return dependency;
			return null;
		}

		public AssemblyDef Resolve(IAssembly assembly, ModuleDef source) =>
			Find(assembly, source)?.Assembly ?? fallback.Resolve(assembly, source);
	}
}
