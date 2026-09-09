// Shared by dnSpy, de4dotEx and NETReactorSlayer. GPL-3.0-or-later.
// Keep the three copies identical; the regression runs every frontend.
#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;
using FileAttributes = System.IO.FileAttributes;

namespace CustomNames {
    internal static class BatchNameMap {
        static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
        public static bool TryRun(string[] args, out int exitCode) {
            exitCode = 0;
            if (!args.Any(a => a.StartsWith("--name-map", StringComparison.Ordinal))) return false;
            if (args.Length == 1 && args[0] == "--name-map-help") { Console.WriteLine("Custom batch names: --name-map-input DIR --name-map-export XML | --name-map XML (--name-map-preview | --name-map-output NEW_DIR) [--name-map-report NEW_XML]. Use a restored assembly tree; mappings bind Path + Mvid + Token + ExpectedName. Parameters use one-based signature Sequence. No deobfuscation or name guessing runs in this mode."); return true; }
            try { Run(args); }
            catch (Exception e) { Console.Error.WriteLine("Name map: " + e.Message); exitCode = 1; }
            return true;
        }
        static XDocument Read(string path) {
            using (var r = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) return XDocument.Load(r);
        }
        static string Required(XElement e, string attribute) {
            var value = (string)e.Attribute(attribute);
            if (string.IsNullOrEmpty(value)) throw new InvalidOperationException(e.Name + " requires " + attribute);
            return value;
        }
        static string Within(string root, string relative) {
            var full = Path.GetFullPath(Path.Combine(root, relative));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Path outside input tree: " + relative);
            return full;
        }
        static void NewFile(string path, XDocument document) {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) document.Save(stream);
        }
        static void Run(string[] args) {
            var options = new Dictionary<string,string>();
            for (int i = 0; i < args.Length; i++) {
                var key = args[i];
                if (key == "--name-map-preview") { options.Add(key, "true"); continue; }
                if (!new[] { "--name-map-input", "--name-map-output", "--name-map", "--name-map-export", "--name-map-report" }.Contains(key) || ++i == args.Length)
                    throw new InvalidOperationException("Custom-name mode accepts --name-map-input DIR, --name-map-export XML, --name-map XML, --name-map-preview, --name-map-report XML and --name-map-output NEW_DIR only.");
                options.Add(key, args[i]);
            }
            if (!options.ContainsKey("--name-map-input")) throw new InvalidOperationException("--name-map-input is required.");
            var root = Path.GetFullPath(options["--name-map-input"]).TrimEnd(Path.DirectorySeparatorChar);
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
            // Reject links before recursive enumeration/copying.
            var paths = new List<string>();
            Action<string> visit = null;
            visit = dir => { foreach (var path in Directory.GetFileSystemEntries(dir)) {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Links are not supported: " + path);
                paths.Add(path); if ((attributes & FileAttributes.Directory) != 0) visit(path);
            }};
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Input is a link.");
            visit(root);
            var modules = new Dictionary<string,ModuleDefMD>(Paths);
            using (var resolver = new ContextResolver(root, modules)) {
                try {
                    foreach (var path in paths.Where(File.Exists).OrderBy(p => p, Paths)) {
                        if (!new[] { ".dll", ".exe" }.Contains(Path.GetExtension(path).ToLowerInvariant())) continue;
                        try { modules.Add(path, ModuleDefMD.Load(path)); } catch (BadImageFormatException) { }
                    }
                    if (modules.Count == 0) throw new InvalidOperationException("No managed assemblies.");
                    foreach (var module in modules.Values) module.Context = new ModuleContext(resolver);
                    if (options.ContainsKey("--name-map-export")) {
                        if (options.ContainsKey("--name-map")) throw new InvalidOperationException("Export and apply are separate operations.");
                        NewFile(Path.GetFullPath(options["--name-map-export"]), Inventory(root, modules));
                        Console.WriteLine("Exported name inventory for " + modules.Count + " modules."); return;
                    }
                    if (!options.ContainsKey("--name-map")) throw new InvalidOperationException("Specify --name-map or --name-map-export.");
                    var doc = Read(options["--name-map"]);
                    if (doc.Root.Name != "NameMap" || (string)doc.Root.Attribute("Version") != "1") throw new InvalidOperationException("Expected NameMap Version=1.");
                    resolver.Bind(doc.Root);
                    var plan = new Plan(root, modules);
                    plan.Load(doc.Root);
                    plan.ValidateAndCapture();
                    var report = plan.Report();
                    if (options.ContainsKey("--name-map-report")) NewFile(Path.GetFullPath(options["--name-map-report"]), report);
                    if (options.ContainsKey("--name-map-preview")) { Console.WriteLine(report); return; }
                    if (!options.ContainsKey("--name-map-output")) throw new InvalidOperationException("Apply requires --name-map-output naming a new directory.");
                    var output = Path.GetFullPath(options["--name-map-output"]).TrimEnd(Path.DirectorySeparatorChar);
                    if (Directory.Exists(output) || File.Exists(output) || output.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || root.StartsWith(output + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Output must be a new, non-overlapping directory.");
                    plan.Apply();
                    Directory.CreateDirectory(Path.GetDirectoryName(output));
                    var stage = output + ".staging-" + Guid.NewGuid().ToString("N");
                    Directory.CreateDirectory(stage);
                    foreach (var path in paths) {
                        var target = Path.Combine(stage, path.Substring(root.Length + 1));
                        if (Directory.Exists(path)) Directory.CreateDirectory(target);
                        else { Directory.CreateDirectory(Path.GetDirectoryName(target)); File.Copy(path, target); }
                    }
                    foreach (var module in plan.Changed) {
                        if (!module.IsILOnly) throw new InvalidOperationException("Mapped changes affect a mixed-mode module: " + module.Location);
                        var target = Path.Combine(stage, module.Location.Substring(root.Length + 1));
                        File.SetAttributes(target, FileAttributes.Normal);
                        var writer = new ModuleWriterOptions(module);
                        writer.MetadataOptions.Flags |= MetadataFlags.PreserveAll;
                        module.Write(target, writer);
                    }
                    plan.ValidateSaved(stage, doc.Root);
                    Directory.Move(stage, output);
                    Console.WriteLine("Applied " + plan.Count + " names; wrote " + plan.Changed.Count + " modules. Output: " + output);
                } finally { foreach (var module in modules.Values) module.Dispose(); }
            }
        }
        static XDocument Inventory(string root, Dictionary<string,ModuleDefMD> modules) {
            var map=new XElement("NameMap",new XAttribute("Version",1));
            foreach(var pair in modules) {
                var module=new XElement("Module",new XAttribute("Path",pair.Key.Substring(root.Length+1)),new XAttribute("Mvid",pair.Value.Mvid));
                map.Add(module);
                foreach(var type in pair.Value.GetTypes().Where(t=>!t.IsGlobalModuleType)) {
                    module.Add(new XElement("Type",Token(type),new XAttribute("ExpectedName",type.Name),new XAttribute("NewName",""),new XAttribute("Signature",type.FullName)));
                    foreach(var method in type.Methods) {
                        var element=new XElement("Method",Token(method),new XAttribute("ExpectedName",method.Name),new XAttribute("NewName",""),new XAttribute("Signature",method.FullName));
                        module.Add(element);
                        foreach(var p in method.Parameters.Where(p=>!p.IsHiddenThisParameter && !p.IsReturnTypeParameter)) element.Add(new XElement("Parameter",new XAttribute("Sequence",p.MethodSigIndex+1),new XAttribute("ExpectedName",p.Name ?? ""),new XAttribute("NewName","")));
                    }
                }
            }
            return new XDocument(map);
        }
        static XAttribute Token(IMDTokenProvider value) { return new XAttribute("Token", "0x" + value.MDToken.Raw.ToString("X8")); }
        static bool Identifier(string name) {
            var value = name.Split('`')[0];
            return value.Length != 0 && (char.IsLetter(value[0]) || value[0] == '_') && value.All(c => char.IsLetterOrDigit(c) || c == '_');
        }
        sealed class Plan {
            readonly string root;
            readonly Dictionary<string,ModuleDefMD> modules;
            readonly Dictionary<IMemberDef,string> names = new Dictionary<IMemberDef,string>();
            readonly Dictionary<Parameter,string> parameters = new Dictionary<Parameter,string>();
            readonly List<Action> referenceUpdates = new List<Action>();
            readonly List<Tuple<ModuleDef,IMemberRef,IMemberDef>> referenceChecks = new List<Tuple<ModuleDef,IMemberRef,IMemberDef>>();
            readonly List<string> warnings = new List<string>();
            public readonly HashSet<ModuleDef> Changed = new HashSet<ModuleDef>();
            public int Count { get { return names.Count + parameters.Count; } }
            public Plan(string root, Dictionary<string,ModuleDefMD> modules) { this.root=root; this.modules=modules; }
            string Name(IMemberDef member) { return names.ContainsKey(member) ? names[member] : member.Name.String; }
            void Add(IMemberDef member, string value) {
                if (!Identifier(value)) throw new InvalidOperationException("Invalid identifier: " + value);
                if (names.ContainsKey(member) && names[member] != value) throw new InvalidOperationException("Conflicting names: " + member.FullName);
                names[member]=value;
            }
            public void Load(XElement map) {
                foreach (var element in map.Elements()) {
                    if (element.Name == "Binding") continue;
                    if (element.Name != "Module") throw new InvalidOperationException("Unknown map element: " + element.Name);
                    ModuleDefMD module;
                    if (!modules.TryGetValue(Within(root, Required(element,"Path")),out module)) throw new InvalidOperationException("Mapped module missing.");
                    if (Guid.Parse(Required(element,"Mvid")) != module.Mvid) throw new InvalidOperationException("Stale MVID: " + module.Location);
                    foreach (var item in element.Elements()) {
                        if (item.Name != "Type" && item.Name != "Method") throw new InvalidOperationException("Unknown symbol kind: " + item.Name);
                        var token = Required(item,"Token");
                        if (!token.StartsWith("0x",StringComparison.Ordinal)) throw new InvalidOperationException("Expected hexadecimal token.");
                        var member = module.ResolveToken(uint.Parse(token.Substring(2),NumberStyles.HexNumber)) as IMemberDef;
                        if (member == null || (item.Name == "Type" ? !(member is TypeDef) : !(member is MethodDef))) throw new InvalidOperationException("Wrong or missing token: " + token);
                        if (item.Attribute("ExpectedName") == null || (string)item.Attribute("ExpectedName") != member.Name.String) throw new InvalidOperationException("Stale name: " + member.FullName);
                        var value = (string)item.Attribute("NewName");
                        if (!string.IsNullOrEmpty(value) && value != member.Name.String) Add(member,value);
                        foreach (var parameter in item.Elements()) {
                            if (!(member is MethodDef) || parameter.Name != "Parameter") throw new InvalidOperationException("Parameters require a method.");
                            var method=(MethodDef)member;
                            int sequence=int.Parse(Required(parameter,"Sequence"),CultureInfo.InvariantCulture);
                            var p=method.Parameters.SingleOrDefault(x=>!x.IsHiddenThisParameter && !x.IsReturnTypeParameter && x.MethodSigIndex+1==sequence);
                            if (p == null || (string)parameter.Attribute("ExpectedName") != (p.Name ?? "")) throw new InvalidOperationException("Stale or missing parameter: " + method.FullName);
                            var newName=(string)parameter.Attribute("NewName");
                            if (string.IsNullOrEmpty(newName) || newName==p.Name) continue;
                            if (!Identifier(newName) || newName.Contains("`")) throw new InvalidOperationException("Invalid parameter identifier.");
                            if (parameters.ContainsKey(p)) throw new InvalidOperationException("Duplicate parameter mapping.");
                            parameters.Add(p,newName);
                        }
                    }
                }
            }
            IEnumerable<TypeDef> Ancestors(TypeDef type, HashSet<TypeDef> seen) {
                if (!seen.Add(type)) yield break;
                yield return type;
                foreach (var reference in type.Interfaces.Select(i=>i.Interface).Concat(type.BaseType==null ? new ITypeDefOrRef[0] : new[]{type.BaseType})) {
                    var ancestor=reference.ResolveTypeDef();
                    if(ancestor==null) throw new InvalidOperationException("Unresolved hierarchy: " + reference.FullName);
                    foreach(var t in Ancestors(ancestor,seen)) yield return t;
                }
            }
            public void ValidateAndCapture() {
                var types=modules.Values.SelectMany(m=>m.GetTypes()).ToArray();
                // Conservatively keep overloads sharing an implicit virtual name together.
                // Signature-only matching is insufficient for constructed generic interfaces.
                bool added;
                do {
                    int before=names.Count;
                    foreach(var type in types) {
                        var mapped=names.Keys.OfType<MethodDef>().Where(m=>m.IsVirtual).ToArray();
                        if(mapped.Length==0) break;
                        var methods=Ancestors(type,new HashSet<TypeDef>()).SelectMany(t=>t.Methods).Where(m=>m.IsVirtual).ToArray();
                        foreach(var target in mapped.Where(methods.Contains)) {
                            foreach(var related in methods.Where(m=>m.Name==target.Name)) {
                                if(!modules.Values.Contains(related.Module)) throw new InvalidOperationException("External virtual contract cannot be renamed: "+related.FullName);
                                Add(related,names[target]);
                            }
                        }
                        foreach(var method in type.Methods) foreach(var impl in method.Overrides) {
                            var declaration=impl.MethodDeclaration.ResolveMethodDef();
                            if(declaration==null) throw new InvalidOperationException("Unresolved override.");
                            // MethodImpl metadata binds explicit implementations independent of names.
                        }
                    }
                    added=names.Count!=before;
                } while(added);
                foreach(var pair in names) {
                    if(pair.Key is MethodDef method && (method.IsConstructor || method.IsRuntimeSpecialName || method.IsSpecialName || pair.Value.Contains("`"))) throw new InvalidOperationException("Accessor/runtime method names cannot be independently mapped: "+method.FullName);
                    if(pair.Key is TypeDef type) {
                        if(type.IsGlobalModuleType || type.Name.String.Substring(type.Name.String.IndexOf('`')<0 ? type.Name.String.Length : type.Name.String.IndexOf('`')) != pair.Value.Substring(pair.Value.IndexOf('`')<0 ? pair.Value.Length : pair.Value.IndexOf('`'))) throw new InvalidOperationException("Generic arity must be preserved.");
                        if(type.Module.Resources.Any(r=>r.Name.String.StartsWith(type.FullName+".",StringComparison.Ordinal))) throw new InvalidOperationException("Type has named resources requiring coordinated resource mapping: "+type.FullName);
                    }
                    Changed.Add(pair.Key.DeclaringType == null && pair.Key is TypeDef ? ((TypeDef)pair.Key).Module : pair.Key.DeclaringType.Module);
                }
                foreach(var type in types) {
                    var peers=type.DeclaringType==null ? type.Module.Types : type.DeclaringType.NestedTypes;
                    if(names.ContainsKey(type) && peers.Any(t=>t!=type && t.Namespace==type.Namespace && Name(t)==Name(type))) throw new InvalidOperationException("Type name collision: "+Name(type));
                    foreach(var method in type.Methods) {
                        if(names.ContainsKey(method) && type.Methods.Any(m=>m!=method && Name(m)==Name(method) && new SigComparer().Equals(m.MethodSig,method.MethodSig))) throw new InvalidOperationException("Method signature collision: "+Name(method));
                        if(names.ContainsKey(method) && (type.Fields.Any(f=>f.Name==Name(method)) || type.Properties.Any(p=>p.Name==Name(method)) || type.Events.Any(e=>e.Name==Name(method)) || type.NestedTypes.Any(t=>Name(t).Split('`')[0]==Name(method)) || Name(type).Split('`')[0]==Name(method))) throw new InvalidOperationException("Member name collision: "+Name(method));
                        if(names.ContainsKey(type) && !method.IsConstructor && Name(method)==Name(type).Split('`')[0]) throw new InvalidOperationException("Type/member name collision: "+Name(type));
                        foreach(var p in method.Parameters.Where(parameters.ContainsKey)) {
                            if(method.Parameters.Any(other=>other!=p && !other.IsHiddenThisParameter && (parameters.ContainsKey(other)?parameters[other]:other.Name)==parameters[p])) throw new InvalidOperationException("Parameter name collision: "+parameters[p]);
                            if(method.GenericParameters.Concat(type.GenericParameters).Any(g=>g.Name==parameters[p])) throw new InvalidOperationException("Parameter/type-parameter name collision: "+parameters[p]);
                            Changed.Add(method.Module);
                        }
                    }
                }
                var mappedTypeNames = new HashSet<string>(names.Keys.Select(m => m is TypeDef ? m.FullName : m.DeclaringType.FullName));
                // Check reflection literals in every module, including modules with no
                // static dependency on a renamed assembly, without decoding every body.
                var literals=new HashSet<string>(names.Keys.SelectMany(m=>new[]{m.Name.String,m.FullName,m.FullName.Replace('/', '+')}));
                foreach(var module in modules.Values) {
                    var reader=module.Metadata.USStream.CreateReader();
                    if(reader.Length==0) continue;
                    reader.Position=1;
                    while(reader.Position<reader.Length) {
                        uint size;
                        if(!reader.TryReadCompressedUInt32(out size) || size>reader.Length-reader.Position) throw new InvalidOperationException("Invalid user-string heap: "+module.Location);
                        if(size==0) continue;
                        var bytes=reader.ReadBytes((int)size);
                        var text=System.Text.Encoding.Unicode.GetString(bytes,0,bytes.Length-1);
                        if(literals.Contains(text) || mappedTypeNames.Any(n=>text.StartsWith(n+",",StringComparison.Ordinal))) warnings.Add("Review reflection/serialization string in "+module.Name+": "+text);
                    }
                }
                var affected=new HashSet<ModuleDef>(Changed);
                bool expanded;
                do {
                    int count=affected.Count;
                    var identities=new HashSet<string>(affected.Where(m=>m.Assembly!=null).Select(m=>m.Assembly.FullName));
                    foreach(var candidate in modules.Values.Where(m=>!affected.Contains(m)))
                        if(candidate.GetAssemblyRefs().Any(r=>identities.Contains(r.FullName))) affected.Add(candidate);
                    expanded=affected.Count!=count;
                } while(expanded);
                foreach(var module in affected) {
                    Console.WriteLine("Capturing references: "+module.Location.Substring(root.Length+1));
                    var finder = new MemberFinder().FindAll(module);
                    foreach(var reference in finder.TypeRefs.Keys) {
                        var target=reference.ResolveTypeDef();
                        if(target==null && mappedTypeNames.Contains(reference.FullName)) throw new InvalidOperationException("Unresolved mapped type reference: "+reference.FullName);
                        if(target!=null && names.ContainsKey(target)) { var name=names[target]; referenceUpdates.Add(()=>reference.Name=name); referenceChecks.Add(Tuple.Create<ModuleDef,IMemberRef,IMemberDef>(module,reference,target)); Changed.Add(module); }
                    }
                    foreach(var reference in finder.MemberRefs.Keys.Where(r=>r.IsMethodRef)) {
                        var target=reference.ResolveMethod();
                        if(target==null && names.Keys.OfType<MethodDef>().Any(m=>m.Name==reference.Name && m.DeclaringType==reference.DeclaringType.ResolveTypeDef())) throw new InvalidOperationException("Unresolved mapped method reference: "+reference.FullName);
                        if(target!=null && names.ContainsKey(target)) { var name=names[target]; referenceUpdates.Add(()=>reference.Name=name); referenceChecks.Add(Tuple.Create<ModuleDef,IMemberRef,IMemberDef>(module,reference,target)); Changed.Add(module); }
                    }
                }
                if(Changed.Any(m=>!m.IsILOnly)) throw new InvalidOperationException("Custom mapping affects mixed-mode code; cannot safely rewrite it.");
                if(warnings.Count!=0) throw new InvalidOperationException(string.Join(Environment.NewLine,warnings.Distinct())+Environment.NewLine+"String-bound names require a separate reviewed migration; no output published.");
            }
            public XDocument Report() {
                return new XDocument(new XElement("NameMapPlan", new XAttribute("Changes",Count), new XAttribute("Modules",Changed.Count),
                    names.Select(p=>new XElement("Rename",new XAttribute("Module", (p.Key is TypeDef ? ((TypeDef)p.Key).Module : p.Key.DeclaringType.Module).Location.Substring(root.Length+1)),new XAttribute("Before",p.Key.FullName),new XAttribute("After",p.Value),Token(p.Key))),
                    parameters.Select(p=>new XElement("Parameter",new XAttribute("Module",p.Key.Method.Module.Location.Substring(root.Length+1)),Token(p.Key.Method),new XAttribute("Method",p.Key.Method.FullName),new XAttribute("Sequence",p.Key.MethodSigIndex+1),new XAttribute("After",p.Value)))));
            }
            public void Apply() {
                foreach(var p in names) p.Key.Name=p.Value;
                foreach(var p in parameters) { p.Key.CreateParamDef(); p.Key.Name=p.Value; }
                foreach(var update in referenceUpdates) update();
            }
            public void ValidateSaved(string stage, XElement map) {
                var saved=new Dictionary<string,ModuleDefMD>(Paths);
                string SavedPath(ModuleDef module) { return Path.Combine(stage,module.Location.Substring(root.Length+1)); }
                ModuleDef Owner(IMemberDef member) { return member is TypeDef ? ((TypeDef)member).Module : member.DeclaringType.Module; }
                using(var resolver=new ContextResolver(stage,saved)) {
                    try {
                        foreach(var module in modules.Values) saved.Add(SavedPath(module),ModuleDefMD.Load(SavedPath(module)));
                        foreach(var module in saved.Values) module.Context=new ModuleContext(resolver);
                        resolver.Bind(map);
                        foreach(var pair in names) {
                            var actual=saved[SavedPath(Owner(pair.Key))].ResolveToken(pair.Key.MDToken.Raw) as IMemberDef;
                            if(actual==null || actual.Name.String!=pair.Value) throw new InvalidOperationException("Saved definition mismatch: "+pair.Key.FullName);
                        }
                        foreach(var pair in parameters) {
                            var method=(MethodDef)saved[SavedPath(pair.Key.Method.Module)].ResolveToken(pair.Key.Method.MDToken.Raw);
                            if(method.Parameters.Single(p=>p.MethodSigIndex==pair.Key.MethodSigIndex).Name!=pair.Value) throw new InvalidOperationException("Saved parameter mismatch.");
                        }
                        foreach(var group in referenceChecks.GroupBy(c=>c.Item1)) {
                            var finder=new MemberFinder().FindAll(saved[SavedPath(group.Key)]);
                            foreach(var check in group) {
                                IEnumerable<IMemberDef> targets;
                                if(check.Item2 is TypeRef) targets=finder.TypeRefs.Keys.Where(r=>r.FullName==check.Item2.FullName).Select(r=>(IMemberDef)r.ResolveTypeDef());
                                else targets=finder.MemberRefs.Keys.Where(r=>r.IsMethodRef && r.FullName==check.Item2.FullName).Select(r=>(IMemberDef)r.ResolveMethod());
                                if(!targets.Any(t=>t!=null && t.MDToken.Raw==check.Item3.MDToken.Raw && Paths.Equals(Owner(t).Location,SavedPath(Owner(check.Item3))))) throw new InvalidOperationException("Saved reference mismatch: "+check.Item2.FullName);
                            }
                        }
                    } finally { foreach(var module in saved.Values) module.Dispose(); }
                }
            }
        }
        sealed class ContextResolver : IAssemblyResolver, IDisposable {
            readonly string root;
            readonly Dictionary<string,ModuleDefMD> modules;
            readonly Dictionary<string,AssemblyDef> bindings=new Dictionary<string,AssemblyDef>(Paths);
            readonly AssemblyResolver fallback=new AssemblyResolver();
            public ContextResolver(string root,Dictionary<string,ModuleDefMD> modules) { this.root=root; this.modules=modules; fallback.EnableTypeDefCache=false; fallback.PostSearchPaths.Add(root); }
            public void Bind(XElement map) {
                foreach(var b in map.Elements("Binding")) {
                    var source=Within(root,Required(b,"Source")); var dependency=Within(root,Required(b,"Dependency"));
                    if(!modules.ContainsKey(source)||!modules.ContainsKey(dependency)) throw new InvalidOperationException("Binding module missing.");
                    var assembly=modules[dependency].Assembly;
                    if(assembly==null || !modules[source].GetAssemblyRefs().Any(a=>a.FullName==assembly.FullName)) throw new InvalidOperationException("Binding does not match source assembly reference.");
                    bindings.Add(source+"|"+assembly.FullName,assembly);
                }
            }
            public AssemblyDef Resolve(IAssembly assembly,ModuleDef source) {
                AssemblyDef bound;
                if(source!=null && bindings.TryGetValue(source.Location+"|"+assembly.FullName,out bound)) return bound;
                var candidates=modules.Values.Where(m=>m.Assembly!=null && m.Assembly.FullName==assembly.FullName).ToArray();
                if(source!=null) {
                    var directory=Path.GetDirectoryName(source.Location);
                    while(directory!=null && (Paths.Equals(directory,root)||directory.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))) {
                        var local=candidates.Where(m=>Paths.Equals(Path.GetDirectoryName(m.Location),directory)).ToArray();
                        if(local.Length==1) return local[0].Assembly;
                        if(local.Length>1) throw new InvalidOperationException("Ambiguous assembly; supply a Binding: "+assembly.FullName);
                        directory=Path.GetDirectoryName(directory);
                    }
                }
                if(candidates.Length==1) return candidates[0].Assembly;
                if(candidates.Length>1) throw new InvalidOperationException("Ambiguous assembly; supply a Binding: "+assembly.FullName);
                return fallback.Resolve(assembly,source);
            }
            public void Dispose() { fallback.Clear(); }
        }
    }
}
