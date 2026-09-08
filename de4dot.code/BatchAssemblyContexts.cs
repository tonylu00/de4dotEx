using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using dnlib.DotNet;

namespace de4dot.code {
	/// <summary>Isolated dependency installations for explicitly selected batch inputs.</summary>
	public sealed class BatchAssemblyContexts : IAssemblyResolver, IDisposable {
		public const string ContextKey = "de4dot.BatchAssemblyContexts";
		readonly Dictionary<ModuleDef, ContextResolver> contexts = new Dictionary<ModuleDef, ContextResolver>();
		readonly Dictionary<string, ModuleDef> modules;
		readonly IAssemblyResolver fallback;
		public BatchAssemblyContexts(IEnumerable<ModuleDef> modules, IAssemblyResolver fallback) {
			this.modules = modules.ToDictionary(m => m.Location, StringComparer.OrdinalIgnoreCase);
			this.fallback = fallback;
		}
		public bool Contains(ModuleDef source) => source != null && contexts.ContainsKey(source);
		public void Add(ModuleDef source, string directory, string config) {
			if (contexts.ContainsKey(source)) throw new UserException("Duplicate assembly context: " + source.Location);
			contexts.Add(source, new ContextResolver(source, directory, config, modules));
		}
		public AssemblyDef Resolve(IAssembly assembly, ModuleDef source) => Contains(source) ? contexts[source].Resolve(assembly, source) : fallback.Resolve(assembly, source);
		public void Dispose() { foreach (var context in contexts.Values) context.Dispose(); }

		sealed class ContextResolver : IAssemblyResolver, IDisposable {
			readonly ModuleDef root;
			readonly Dictionary<string, ModuleDef> modules;
			readonly Dictionary<string, AssemblyDef> cache = new Dictionary<string, AssemblyDef>(StringComparer.Ordinal);
			readonly dnlib.DotNet.AssemblyResolver resolver = new dnlib.DotNet.AssemblyResolver { FindExactMatch = true, EnableFrameworkRedirect = false, EnableTypeDefCache = true };
			readonly XmlDocument config = new XmlDocument { XmlResolver = null };
			public ContextResolver(ModuleDef source, string directory, string configPath, Dictionary<string, ModuleDef> modules) {
				root = source; this.modules = modules;
				resolver.PreSearchPaths.Add(directory);
				resolver.DefaultModuleContext = new ModuleContext(this);
				if (configPath != null) {
					using (var reader = XmlReader.Create(configPath, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) config.Load(reader);
					foreach (XmlElement probing in config.SelectNodes("//*[local-name()='probing']"))
						foreach (var relative in probing.GetAttribute("privatePath").Split(';')) {
							if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) continue;
							var path = Path.GetFullPath(Path.Combine(directory, relative));
							if (path.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) resolver.PreSearchPaths.Add(path);
						}
				}
			}
			public AssemblyDef Resolve(IAssembly assembly, ModuleDef source) {
				if (assembly == null) return null;
				if (source?.Assembly?.FullName == assembly.FullName) return source.Assembly;
				if (root.Assembly?.FullName == assembly.FullName) return root.Assembly;
				if (cache.TryGetValue(assembly.FullName, out var cached)) return cached;
				var resolved = resolver.Resolve(Redirect(assembly), null);
				if (resolved != null && modules.TryGetValue(resolved.ManifestModule.Location, out var physical)) resolved = physical.Assembly;
				return cache[assembly.FullName] = resolved;
			}
			IAssembly Redirect(IAssembly assembly) {
				foreach (XmlElement dependency in config.SelectNodes("//*[local-name()='dependentAssembly']")) {
					var identity = dependency.SelectSingleNode("*[local-name()='assemblyIdentity']") as XmlElement;
					var redirect = dependency.SelectSingleNode("*[local-name()='bindingRedirect']") as XmlElement;
					if (identity == null || redirect == null || !string.Equals(identity.GetAttribute("name"), assembly.Name.String, StringComparison.OrdinalIgnoreCase)) continue;
					var token = identity.GetAttribute("publicKeyToken");
					if (token.Length > 0 && !string.Equals(token, PublicKeyBase.ToPublicKeyToken(assembly.PublicKeyOrToken)?.ToString() ?? "null", StringComparison.OrdinalIgnoreCase)) continue;
					var culture = identity.GetAttribute("culture");
					if (culture.Length > 0 && culture != "neutral" && !string.Equals(culture, assembly.Culture.String, StringComparison.OrdinalIgnoreCase)) continue;
					var range = redirect.GetAttribute("oldVersion").Split('-');
					if (!Version.TryParse(range[0], out var low) || !Version.TryParse(range[range.Length-1], out var high) || assembly.Version < low || assembly.Version > high || !Version.TryParse(redirect.GetAttribute("newVersion"), out var version)) continue;
					return new AssemblyNameInfo(assembly.FullName) { Version = version };
				}
				return assembly;
			}
			public void Dispose() => resolver.Clear();
		}
	}
}
