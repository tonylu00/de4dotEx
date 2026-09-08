using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
				rename(protectedFiles);
				// Unprotected overrides/interfaces keep their declarations. Keep the matching
				// protected slots stable, then update every captured caller to the final name.
				foreach (var pair in boundaryNames) pair.Key.Name = pair.Value;
				int referenceOnly = 0;
				foreach (var module in graph.TheModules) {
					bool changed = ApplyReferences(module);
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
