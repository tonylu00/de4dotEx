using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml;
using de4dot.code;
using de4dot.code.deobfuscators;
using de4dot.code.renamer.asmmodules;
using dnlib.DotNet;
using dnlib.DotNet.Writer;
using FileAttributes = System.IO.FileAttributes;

namespace de4dot.cui {
	/// <summary>Stages a complete application tree and binds consumers before symbol changes.</summary>
	sealed class BatchProcessor {
		readonly FilesDeobfuscator.Options options;
		readonly IDeobfuscatorContext context;
		readonly Func<IList<IDeobfuscator>> createDeobfuscators;
		readonly Action<IEnumerable<IObfuscatedFile>> deobfuscate, rename;
		static readonly StringComparison PathComparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

		public BatchProcessor(FilesDeobfuscator.Options options, IDeobfuscatorContext context,
			Func<IList<IDeobfuscator>> createDeobfuscators, Action<IEnumerable<IObfuscatedFile>> deobfuscate,
			Action<IEnumerable<IObfuscatedFile>> rename) {
			this.options = options;
			this.context = context;
			this.createDeobfuscators = createDeobfuscators;
			this.deobfuscate = deobfuscate;
			this.rename = rename;
		}

		public void Run() {
			int initialErrors = Logger.Instance.NumErrors;
			if (options.Files.Count != 0 || options.SearchDirs.Count != 0 || options.OneFileAtATime || options.DetectObfuscators)
				throw new UserException("--batch cannot be combined with individual files, -r, -d or one-file-at-a-time mode.");
			if (string.IsNullOrWhiteSpace(options.BatchOutput))
				throw new UserException("--batch requires --batch-output naming a new folder.");
			string root = FullDirectory(options.BatchRoot), output = FullDirectory(options.BatchOutput);
			if (!Directory.Exists(root) || Inside(root, output) || Inside(output, root))
				throw new UserException("Batch input must exist and input/output trees must not overlap.");
			if (Directory.Exists(output) || File.Exists(output))
				throw new UserException("Batch output already exists: " + output);
			CheckAncestors(root);
			CheckAncestors(Path.GetDirectoryName(output));
			var paths = Tree(root).ToList();
			var parent = Path.GetDirectoryName(output);
			Directory.CreateDirectory(parent);
			var work = Path.Combine(parent, ".de4dot-" + Guid.NewGuid().ToString("N"));
			var snapshot = Path.Combine(work, "input");
			var result = Path.Combine(work, "output");
			var files = new List<IObfuscatedFile>();
			bool published = false;
			try {
				CopyTree(root, snapshot, paths);
				CopyTree(snapshot, result, paths.Select(p => Path.Combine(snapshot, p.Substring(root.Length + 1))).ToList());
				TheAssemblyResolver.Instance.AddSearchDirectory(snapshot);
				foreach (var path in paths.Where(p => !Directory.Exists(p)).OrderBy(p => p, StringComparer.Ordinal)) {
					var ext = Path.GetExtension(path);
					if (!ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)) continue;
					var relative = path.Substring(root.Length + 1);
					var fileOptions = new ObfuscatedFile.Options {
						Filename = Path.Combine(snapshot, relative), NewFilename = Path.Combine(result, relative),
						ControlFlowDeobfuscation = options.ControlFlowDeobfuscation,
						KeepObfuscatorTypes = true, RenamerFlags = options.RenamerFlags,
						MetadataFlags = options.MetadataFlags | MetadataFlags.PreserveAll,
						StringDecrypterType = options.DefaultStringDecrypterType ?? DecrypterType.Default,
					};
					fileOptions.StringDecrypterMethods.AddRange(options.DefaultStringDecrypterMethods);
					var file = new ObfuscatedFile(fileOptions, options.ModuleContext, options.AssemblyClientFactory);
					try {
						file.DeobfuscatorContext = context;
						file.Load(createDeobfuscators());
						files.Add(file);
					}
					catch (BadImageFormatException) { file.Dispose(); Logger.n("Copying native or invalid PE: {0}", relative); }
					catch { file.Dispose(); throw; }
				}
				ConfigureBindings(files, snapshot);
				var metadataRepaired = new HashSet<ModuleDef>();
				foreach (var file in files) {
					int repaired = CoreLibraryAttributeReferences.Repair(file.ModuleDefMD);
					if (repaired == 0) continue;
					metadataRepaired.Add(file.ModuleDefMD);
					Logger.w("Repaired {0} missing self-scoped core-library attribute types in {1}", repaired, file.Filename);
				}
				var protectedFiles = files.Where(f => f.Deobfuscator.Type != "un").ToList();
				deobfuscate(protectedFiles);
				var graph = new Modules(context);
				foreach (var file in files) graph.Add(new de4dot.code.renamer.asmmodules.Module(file));
				graph.Initialize();
				if (Logger.Instance.NumErrors != initialErrors)
					throw new UserException("Batch reference resolution reported errors; output has not been published.");
				var modifiable = new HashSet<ModuleDef>(protectedFiles.Select(f => f.ModuleDefMD));
				var boundaryNames = new Dictionary<MethodDef, UTF8String>();
				foreach (var group in graph.InitializeVirtualMembers().GetAllGroups()) {
					if (!group.Methods.Any(m => !m.Owner.HasModule || !modifiable.Contains(m.MethodDef.Module))) continue;
					foreach (var method in group.Methods.Where(m => modifiable.Contains(m.MethodDef.Module)))
						boundaryNames[method.MethodDef] = method.MethodDef.Name;
				}
				var reflectionReferences = new ReflectionTypeReferences(graph);
				rename(protectedFiles);
				// Unprotected overrides/interfaces keep their declarations. Keep the matching
				// protected slots stable, then update every captured caller to the final name.
				foreach (var pair in boundaryNames) pair.Key.Name = pair.Value;
				int referenceOnly = 0;
				foreach (var module in graph.TheModules) {
					bool changed = ApplyReferences(module) | reflectionReferences.Apply(module.ModuleDefMD) | metadataRepaired.Contains(module.ModuleDefMD);
					bool cleaned = protectedFiles.Contains(module.ObfuscatedFile);
					if (!cleaned && !changed) continue;
					if (!cleaned) { referenceOnly++; Logger.n("Updating dependency references only: {0}", module.Filename); }
					File.SetAttributes(module.ObfuscatedFile.NewFilename, FileAttributes.Normal);
					module.ObfuscatedFile.Save();
				}
				foreach (var file in files) file.Dispose();
				files.Clear();
				if (Logger.Instance.NumErrors != initialErrors)
					throw new UserException("Batch processing reported errors; output has not been published.");
				Directory.Move(result, output);
				published = true;
				Logger.n("Batch complete: {0} protected, {1} reference-only, {2} managed copied unchanged. Output: {3}",
					protectedFiles.Count, referenceOnly, graph.TheModules.Count - protectedFiles.Count - referenceOnly, output);
			}
			finally {
				(context.GetData(BatchAssemblyContexts.ContextKey) as BatchAssemblyContexts)?.Dispose();
				context.ClearData(BatchAssemblyContexts.ContextKey);
				context.ClearData(BatchAssemblyBindings.ContextKey);
				foreach (var file in files) file.Dispose();
				if (published) {
					// Only our new, validated staging tree can be removed.
					CheckAncestors(work);
					foreach (var path in Tree(work).Where(File.Exists))
						File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
					Directory.Delete(work, true);
				}
				else Logger.w("Batch failed; unpublished staging files retained at {0}", work);
			}
		}

		void ConfigureBindings(List<IObfuscatedFile> files, string snapshot) {
			if (options.BatchBindings.Count == 0 && options.AssemblyContexts == null) return;
			var comparer = Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
			var modules = files.ToDictionary(f => Path.GetFullPath(f.ModuleDefMD.Location), f => f.ModuleDefMD, comparer);
			var contexts = new BatchAssemblyContexts(modules.Values, options.ModuleContext.AssemblyResolver);
			context.SetData(BatchAssemblyContexts.ContextKey, contexts);
			if (options.AssemblyContexts != null) ConfigureContexts(contexts, snapshot, modules);
			var bindings = new BatchAssemblyBindings(contexts);
			foreach (var binding in options.BatchBindings) {
				int separator = binding.IndexOf('=');
				if (separator <= 0 || separator == binding.Length - 1)
					throw new UserException("Expected --batch-binding source=dependency: " + binding);
				var source = BindingModule(binding.Substring(0, separator), snapshot, modules);
				var dependency = BindingModule(binding.Substring(separator + 1), snapshot, modules);
				if (dependency.Assembly == null || !source.GetAssemblyRefs().Any(a => a.FullName == dependency.Assembly.FullName))
					throw new UserException("Batch binding dependency must exactly match an assembly reference: " + binding);
				bindings.Add(source, dependency);
			}
			context.SetData(BatchAssemblyBindings.ContextKey, bindings);
			foreach (var file in files)
				file.ModuleDefMD.Context = new ModuleContext(bindings);
		}

		void ConfigureContexts(BatchAssemblyContexts contexts, string snapshot, Dictionary<string, ModuleDefMD> modules) {
			var manifest = Path.GetFullPath(options.AssemblyContexts);
			var root = FullDirectory(options.BatchRoot);
			var document = new XmlDocument { XmlResolver = null };
			using (var reader = XmlReader.Create(manifest, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) document.Load(reader);
			if (document.DocumentElement?.Name != "AssemblyContexts") throw new UserException("Expected an AssemblyContexts manifest.");
			foreach (XmlNode node in document.DocumentElement.ChildNodes) {
				if (!(node is XmlElement entry)) continue;
				if (entry.Name != "Context") throw new UserException("Unknown assembly context element: " + entry.Name);
				string ReadPath(string name, bool optional = false) {
					var value = entry.GetAttribute(name);
					if (string.IsNullOrWhiteSpace(value)) {
						if (optional) return null;
						throw new UserException("Assembly context requires " + name);
					}
					return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(manifest), value));
				}
				string Remap(string path) => path != null && Inside(path, root) ? Path.Combine(snapshot, path.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar)) : path;
				var source = ReadPath("Source");
				var directory = Remap(ReadPath("Directory"));
				var config = Remap(ReadPath("Config", true));
				if (!Inside(source, root) || !modules.TryGetValue(Remap(source), out var module)) throw new UserException("Assembly context Source must name a managed batch input: " + source);
				if (!Directory.Exists(directory) || config != null && !File.Exists(config)) throw new UserException("Assembly context directory/config does not exist: " + directory);
				contexts.Add(module, directory, config);
			}
		}

		static ModuleDefMD BindingModule(string relative, string snapshot, Dictionary<string, ModuleDefMD> modules) {
			var path = Path.GetFullPath(Path.Combine(snapshot, relative));
			if (Path.IsPathRooted(relative) || !Inside(path, snapshot) || !modules.TryGetValue(path, out var module))
				throw new UserException("Batch binding must name a managed input file inside the batch folder: " + relative);
			return module;
		}

		static bool ApplyReferences(de4dot.code.renamer.asmmodules.Module module) {
			bool changed = false;
			foreach (var pair in module.TypeRefsToRename) {
				if (pair.reference.Name == pair.definition.Name && pair.reference.Namespace == pair.definition.Namespace) continue;
				pair.reference.Name = pair.definition.Name;
				pair.reference.Namespace = pair.definition.Namespace;
				changed = true;
			}
			foreach (var pair in module.MethodRefsToRename) {
				if (pair.reference.Name == pair.definition.Name) continue;
				pair.reference.Name = pair.definition.Name; changed = true;
			}
			foreach (var pair in module.FieldRefsToRename) {
				if (pair.reference.Name == pair.definition.Name) continue;
				pair.reference.Name = pair.definition.Name; changed = true;
			}
			foreach (var pair in module.CustomAttributeFieldRefs.Concat(module.CustomAttributePropertyRefs)) {
				var arg = pair.cattr.NamedArguments[pair.index];
				if (arg.Name == pair.reference.Name) continue;
				arg.Name = pair.reference.Name; changed = true;
			}
			return changed;
		}

		static string FullDirectory(string path) {
			var full = Path.GetFullPath(path);
			if (full == Path.GetPathRoot(full)) throw new UserException("Choose an application folder, not a filesystem root.");
			return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		}
		static bool Inside(string path, string root) => path.Equals(root, PathComparison) || path.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);
		static void CheckAncestors(string path) {
			for (var dir = new DirectoryInfo(path); dir != null; dir = dir.Parent)
				if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0)
					throw new UserException("Batch paths cannot traverse links: " + dir.FullName);
		}
		static IEnumerable<string> Tree(string root) {
			foreach (var item in new DirectoryInfo(root).GetFileSystemInfos().OrderBy(i => i.Name, StringComparer.Ordinal)) {
				if ((item.Attributes & FileAttributes.ReparsePoint) != 0) throw new UserException("Batch input contains a link: " + item.FullName);
				yield return item.FullName;
				if ((item.Attributes & FileAttributes.Directory) != 0)
					foreach (var child in Tree(item.FullName)) yield return child;
			}
		}
		static void CopyTree(string source, string destination, List<string> paths) {
			Directory.CreateDirectory(destination);
			foreach (var path in paths.Where(Directory.Exists)) Directory.CreateDirectory(Path.Combine(destination, path.Substring(source.Length + 1)));
			Parallel.ForEach(paths.Where(File.Exists), new ParallelOptions { MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount) },
				path => File.Copy(path, Path.Combine(destination, path.Substring(source.Length + 1))));
		}
	}
}
