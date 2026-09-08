using System.Linq;
using System.Collections.Generic;
using dnlib.DotNet;

namespace de4dot.code {
	/// <summary>Repairs missing self-scoped core-library types in serialized attribute values.</summary>
	public static class CoreLibraryAttributeReferences {
		public static int Repair(ModuleDef module) {
			if (module.Assembly == null) return 0;
			int repaired = 0;
			void VisitSignature(TypeSig signature) {
				if (signature == null) return;
				if (signature is TypeDefOrRefSig simple && simple.TypeDefOrRef is TypeRef reference &&
					(reference.ResolutionScope is AssemblyRef || ReferenceEquals(reference.ResolutionScope, module)) &&
					reference.DefinitionAssembly?.FullName == module.Assembly?.FullName && reference.Namespace == "System" &&
					module.Find(reference.FullName, false) == null && !module.ExportedTypes.Any(t => t.FullName == reference.FullName)) {
					var canonical = module.CorLibTypes.GetCorLibTypeSig(reference.Namespace, reference.Name, module.CorLibTypes.AssemblyRef);
					bool existsInCoreLibrary = canonical != null || new TypeRefUser(module, reference.Namespace, reference.Name, module.CorLibTypes.AssemblyRef).ResolveTypeDef() != null;
					if (existsInCoreLibrary && reference.DefinitionAssembly.FullName != module.CorLibTypes.AssemblyRef.FullName) {
						reference.ResolutionScope = module.CorLibTypes.AssemblyRef;
						repaired++;
					}
				}
				if (signature is GenericInstSig generic) {
					VisitSignature(generic.GenericType);
					foreach (var argument in generic.GenericArguments) VisitSignature(argument);
				}
				VisitSignature(signature.Next);
			}
			void VisitArgument(CAArgument argument) {
				if (argument.Value is TypeSig signature) VisitSignature(signature);
				else if (argument.Value is IList<CAArgument> values) foreach (var value in values) VisitArgument(value);
			}
			foreach (var attribute in new MemberFinder().FindAll(module).CustomAttributes.Keys) {
				foreach (var argument in attribute.ConstructorArguments) VisitArgument(argument);
				foreach (var argument in attribute.NamedArguments) VisitArgument(argument.Argument);
			}
			return repaired;
		}
	}
}
