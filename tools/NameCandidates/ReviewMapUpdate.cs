using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using dnlib.DotNet;

namespace De4dot.NameCandidates;

// Portable preflight and transactional map editing; never executes assemblies.
public static class ReviewMapUpdate {
    public sealed record Diagnostic(string Module, string Token, string Message);
    public sealed record Change(string Module, string Token, string Before, string Requested, string Applied) {
        public string Kind { get; init; } = "Method";
        public int? ParameterSequence { get; init; }
    }
    public sealed class Result {
        public bool Success { get; set; }
        public string BaseMapSha256 { get; set; }
        public string ReviewSha256 { get; set; }
        public List<Diagnostic> Errors { get; } = new();
        public List<Change> Changes { get; } = new();
    }
    static bool Identifier(string name) => name != null && Regex.IsMatch(name,
        @"\A[\p{L}\p{Nl}_][\p{L}\p{Nl}\p{Nd}\p{Pc}\p{Mn}\p{Mc}_]*\z");
    static uint Token(string value) => uint.Parse(value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    static XDocument ReadMap(string path) {
        using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return XDocument.Load(reader);
    }
    static HashSet<string> Reserved(ModuleDef module) {
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in module.GetTypes()) {
            used.UnionWith((t.Namespace.String ?? "").Split('.'));
            used.Add(Regex.Replace(t.Name.String, @"`[0-9]+\z", ""));
            used.UnionWith(t.GenericParameters.Select(p => p.Name.String));
            used.UnionWith(t.Methods.SelectMany(m => m.GenericParameters).Select(p => p.Name.String));
            used.UnionWith(t.Methods.SelectMany(m => m.Parameters).Select(p => p.Name));
            used.UnionWith(t.Methods.Select(m => m.Name.String));
            used.UnionWith(t.Fields.Select(f => f.Name.String));
            used.UnionWith(t.Properties.Select(p => p.Name.String));
            used.UnionWith(t.Events.Select(e => e.Name.String));
        }
        return used;
    }
    static string Resolve(string root, string relative) {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) throw new InvalidDataException("Expected a relative module path.");
        string full = Path.GetFullPath(Path.Combine(root, relative));
        string prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, InputPaths.Comparison)) throw new InvalidDataException("Module leaves input root.");
        for (string p = full; p != null; p = Path.GetDirectoryName(p)) {
            if ((File.GetAttributes(p) & System.IO.FileAttributes.ReparsePoint) != 0) throw new IOException("Linked input is unsupported.");
            if (string.Equals(p, root, InputPaths.Comparison)) break;
        }
        return full;
    }
    public static bool Run(string reviewPath, string basePath, string outputPath, string reportPath, bool sourceMapChanges = false) {
        if (File.Exists(reportPath) || outputPath != null && File.Exists(outputPath)) throw new IOException("Choose new output and report paths.");
        if (outputPath != null && InputPaths.Comparer.Equals(Path.GetFullPath(outputPath), Path.GetFullPath(reportPath))) throw new IOException("Map and report paths must differ.");
        var result = new Result();
        var loaded = new Dictionary<string, ModuleDefMD>(InputPaths.Comparer);
        var moduleHashes = new Dictionary<ModuleDefMD, string>();
        XDocument doc = null;
        try {
            result.BaseMapSha256 = Hash(basePath);
            doc = ReadMap(basePath);
            var root = doc.Root;
            if (root?.Name != "SourceNameMap" || (string)root.Attribute("Version") != "1") throw new InvalidDataException("Expected SourceNameMap Version=1.");
            string directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath((string)root.Attribute("InputDirectory") ?? throw new InvalidDataException("Missing InputDirectory.")));
            ModuleDefMD Load(string relative) {
                string path = Resolve(directory, relative);
                if (!loaded.TryGetValue(path, out var module)) {
                    byte[] bytes = File.ReadAllBytes(path);
                    loaded.Add(path, module = ModuleDefMD.Load(bytes));
                    moduleHashes.Add(module, Convert.ToHexString(SHA256.HashData(bytes)));
                }
                return module;
            }
            var moduleRows = new Dictionary<string, XElement>(InputPaths.Comparer);
            foreach (var row in root.Elements()) {
                string relative = (string)row.Attribute("Path");
                try {
                    if (row.Name != "Module") throw new InvalidDataException("Expected Module row.");
                    var m = Load(relative);
                    if (!moduleRows.TryAdd(Resolve(directory, relative), row)) throw new InvalidDataException("Duplicate physical module.");
                    if (!Guid.TryParse((string)row.Attribute("Mvid"), out var id) || id != m.Mvid || !string.Equals(moduleHashes[m], (string)row.Attribute("Sha256"), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Stale module identity/hash.");
                } catch (Exception e) { result.Errors.Add(new(relative, null, e.Message)); }
            }
            var proposed = new Dictionary<XElement, string>();
            var parameterProposals = new Dictionary<XElement, string>();
            if (reviewPath != null) {
                result.ReviewSha256 = Hash(reviewPath);
                MethodReview.Inventory inventory;
                if (sourceMapChanges) {
                    var changes = ReadMap(reviewPath).Root;
                    if (changes?.Name != "SourceNameMap" || (string)changes.Attribute("Version") != "1") throw new InvalidDataException("Expected generated method SourceNameMap Version=1.");
                    inventory = new MethodReview.Inventory { TargetRoot = (string)changes.Attribute("InputDirectory") };
                    foreach (var moduleRow in changes.Elements()) {
                        if (moduleRow.Name != "Module") throw new InvalidDataException("Expected proposal Module row.");
                        string relative = (string)moduleRow.Attribute("Path");
                        foreach (var row in moduleRow.Elements()) {
                            if (row.Name != "Method" || row.HasElements || row.Attribute("NewName") == null) throw new InvalidDataException("Merge input requires generated method aliases only; existing base type/parameter aliases are preserved.");
                            inventory.Methods.Add(new MethodReview.Entry {
                                Module = relative, Mvid = (string)moduleRow.Attribute("Mvid"), Sha256 = (string)moduleRow.Attribute("Sha256"),
                                Identity = Load(relative).Assembly?.FullName, Token = (string)row.Attribute("Token"),
                                Signature = (string)row.Attribute("Signature"), OriginalName = (string)row.Attribute("ExpectedName"), NewName = (string)row.Attribute("NewName")
                            });
                        }
                    }
                } else inventory = JsonSerializer.Deserialize<MethodReview.Inventory>(File.ReadAllText(reviewPath));
                if (inventory == null || inventory.Kind != "MethodReview" && inventory.Kind != "SymbolReview" || inventory.Format != 1 || !InputPaths.Comparer.Equals(directory, Path.TrimEndingDirectorySeparator(Path.GetFullPath(inventory.TargetRoot)))) throw new InvalidDataException("Review format/input root mismatch.");
                var seen = new HashSet<string>(InputPaths.Comparer);
                var edits = inventory.Methods.Select(e => (Entry: e, Kind: "Method")).Concat(inventory.Types.Select(e => (Entry: e, Kind: "Type")));
                foreach (var item in edits.Where(e => !string.IsNullOrWhiteSpace(e.Entry.NewName) || e.Entry.ParameterNames.Any(p => !string.IsNullOrWhiteSpace(p.NewName)))) {
                    var edit = item.Entry;
                    try {
                        var m = Load(edit.Module);
                        string path = Resolve(directory, edit.Module);
                        uint token = Token(edit.Token);
                        if (!seen.Add(path + "|" + token.ToString("X8"))) throw new InvalidDataException("Duplicate review token.");
                        if (!string.Equals(moduleHashes[m], edit.Sha256, StringComparison.OrdinalIgnoreCase) || !Guid.TryParse(edit.Mvid, out var id) || id != m.Mvid || edit.Identity != m.Assembly?.FullName) throw new InvalidDataException("Stale review module identity/hash.");
                        if (m.ResolveToken(token) is not IMemberDef member || member.FullName != edit.Signature || member.Name != edit.OriginalName) throw new InvalidDataException("Review token/name/signature mismatch.");
                        var method = member as MethodDef;
                        if (item.Kind == "Type") {
                            if (member is not TypeDef t || t.IsGlobalModuleType || t.HasGenericParameters || edit.ParameterNames.Any(p => !string.IsNullOrWhiteSpace(p.NewName))) throw new InvalidDataException("Unsupported type alias.");
                        } else {
                            if (method == null) throw new InvalidDataException("Expected a method token.");
                            string blocker = MethodReview.Blocker(method);
                            if (blocker != null) throw new InvalidDataException(blocker);
                        }
                        bool rename = !string.IsNullOrWhiteSpace(edit.NewName);
                        if (rename && !Identifier(edit.NewName)) throw new InvalidDataException("Invalid source identifier.");
                        if (!moduleRows.TryGetValue(path, out var moduleRow)) {
                            moduleRow = new XElement("Module", new XAttribute("Path", InputPaths.Relative(directory, path)), new XAttribute("Mvid", m.Mvid), new XAttribute("Sha256", edit.Sha256));
                            moduleRows.Add(path, moduleRow); root.Add(moduleRow);
                        }
                        var matches = moduleRow.Elements(item.Kind).Where(r => Token((string)r.Attribute("Token")) == token).ToArray();
                        if (matches.Length > 1) throw new InvalidDataException("Duplicate base-map member token.");
                        var row = matches.SingleOrDefault();
                        if (row == null) {
                            row = new XElement(item.Kind, new XAttribute("Token", "0x" + token.ToString("X8")), new XAttribute("ExpectedName", member.Name), new XAttribute("Signature", member.FullName));
                            moduleRow.Add(row);
                        }
                        if (rename) proposed.Add(row, edit.NewName);
                        var positions = new HashSet<int>();
                        foreach (var p in edit.ParameterNames.Where(p => !string.IsNullOrWhiteSpace(p.NewName))) {
                            if (!positions.Add(p.Sequence)) throw new InvalidDataException("Duplicate parameter edit sequence.");
                            var definition = method.Parameters.SingleOrDefault(a => a.IsNormalMethodParameter && a.MethodSigIndex + 1 == p.Sequence);
                            if (definition?.ParamDef == null || definition.Name != p.OriginalName) throw new InvalidDataException("Parameter sequence/name mismatch or missing metadata.");
                            if (!Identifier(p.NewName)) throw new InvalidDataException("Invalid parameter identifier.");
                            var existing = row.Elements("Parameter").Where(e => (int)e.Attribute("Sequence") == p.Sequence).ToArray();
                            if (existing.Length > 1) throw new InvalidDataException("Duplicate base-map parameter.");
                            var parameter = existing.SingleOrDefault();
                            if (parameter == null) {
                                parameter = new XElement("Parameter", new XAttribute("Sequence", p.Sequence), new XAttribute("ExpectedName", definition.Name));
                                row.Add(parameter);
                            }
                            parameterProposals.Add(parameter, p.NewName);
                        }
                    } catch (Exception e) { result.Errors.Add(new(edit.Module, edit.Token, e.Message)); }
                }
            }
            foreach (var pair in moduleRows) {
                var module = loaded[pair.Key]; var moduleRow = pair.Value;
                string relative = (string)moduleRow.Attribute("Path");
                var tokens = new HashSet<uint>();
                var used = Reserved(module);
                var parameterAliases = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in moduleRow.Elements()) {
                    try {
                        uint token = Token((string)row.Attribute("Token"));
                        if (!tokens.Add(token)) throw new InvalidDataException("Duplicate base-map token.");
                        if (module.ResolveToken(token) is not IMemberDef member || member.Name != (string)row.Attribute("ExpectedName") || member.FullName != (string)row.Attribute("Signature")) throw new InvalidDataException("Token/name/signature mismatch.");
                        if (row.Name == "Type") {
                            if (member is not TypeDef t || t.IsGlobalModuleType || t.HasGenericParameters || row.HasElements) throw new InvalidDataException("Unsupported type alias.");
                        } else if (row.Name == "Method") {
                            if (member is not MethodDef method || MethodReview.Blocker(method) != null) throw new InvalidDataException("Unsupported method alias.");
                            var positions = new HashSet<int>();
                            var paramsUsed = new HashSet<string>(method.Parameters.Select(p => p.Name), StringComparer.Ordinal);
                            paramsUsed.UnionWith(method.GenericParameters.Select(p => p.Name.String));
                            for (var type = method.DeclaringType; type != null; type = type.DeclaringType) paramsUsed.UnionWith(type.GenericParameters.Select(p => p.Name.String));
                            var originalParameterNames = new HashSet<string>(paramsUsed, StringComparer.Ordinal);
                            // Unchanged aliases retain their names. Reserve all requested
                            // names before repairing collisions so one edit cannot steal another.
                            foreach (var p in row.Elements().Where(p => !parameterProposals.ContainsKey(p))) {
                                string alias = (string)p.Attribute("NewName");
                                if (alias != null) paramsUsed.Add(alias);
                            }
                            var parameterSuggestions = new HashSet<string>(row.Elements().Where(parameterProposals.ContainsKey).Select(p => parameterProposals[p]), StringComparer.Ordinal);
                            foreach (var p in row.Elements()) {
                                int sequence = int.Parse((string)p.Attribute("Sequence"), CultureInfo.InvariantCulture);
                                var parameter = method.Parameters.SingleOrDefault(a => a.IsNormalMethodParameter && a.MethodSigIndex + 1 == sequence);
                                string alias = (string)p.Attribute("NewName");
                                if (p.Name != "Parameter" || p.HasElements || !positions.Add(sequence) || parameter?.ParamDef == null || parameter.Name != (string)p.Attribute("ExpectedName")) throw new InvalidDataException("Invalid parameter alias at sequence " + sequence);
                                if (parameterProposals.TryGetValue(p, out string requested)) {
                                    string applied = requested;
                                    int suffix = 2;
                                    if (!paramsUsed.Add(applied)) do { applied = requested + suffix++.ToString(CultureInfo.InvariantCulture); } while (parameterSuggestions.Contains(applied) || !paramsUsed.Add(applied));
                                    result.Changes.Add(new(relative, (string)row.Attribute("Token"), alias, requested, applied) { Kind = "Parameter", ParameterSequence = sequence });
                                    p.SetAttributeValue("NewName", applied); alias = applied;
                                } else if (!Identifier(alias) || row.Elements().Count(e => !parameterProposals.ContainsKey(e) && (string)e.Attribute("NewName") == alias) != 1 || originalParameterNames.Contains(alias))
                                    throw new InvalidDataException("Invalid/colliding parameter alias at sequence " + sequence);
                                parameterAliases.Add(alias);
                            }
                        } else throw new InvalidDataException("Expected Type or Method row.");
                        if (!proposed.ContainsKey(row)) {
                            string alias = (string)row.Attribute("NewName");
                            if (alias != null && (!Identifier(alias) || !used.Add(alias))) throw new InvalidDataException("Invalid/colliding existing alias: " + alias);
                            if (alias == null && !row.HasElements) throw new InvalidDataException("Empty alias row.");
                        }
                    } catch (Exception e) { result.Errors.Add(new(relative, (string)row.Attribute("Token"), e.Message)); }
                }
                foreach (string alias in parameterAliases) {
                    if (moduleRow.Elements().Any(r => !proposed.ContainsKey(r) && (string)r.Attribute("NewName") == alias)) result.Errors.Add(new(relative, null, "Parameter/member alias collision: " + alias));
                    used.Add(alias);
                }
                var suggestions = new HashSet<string>(proposed.Where(p => p.Key.Parent == moduleRow).Select(p => p.Value), StringComparer.Ordinal);
                foreach (var edit in proposed.Where(p => p.Key.Parent == moduleRow).OrderBy(p => Token((string)p.Key.Attribute("Token")))) {
                    string name = edit.Value;
                    int suffix = 2;
                    if (!used.Add(name)) do { name = edit.Value + suffix++.ToString(CultureInfo.InvariantCulture); } while (suggestions.Contains(name) || !used.Add(name));
                    result.Changes.Add(new(relative, (string)edit.Key.Attribute("Token"), (string)edit.Key.Attribute("NewName"), edit.Value, name) { Kind = edit.Key.Name.LocalName });
                    edit.Key.SetAttributeValue("NewName", name);
                }
            }
            result.Success = result.Errors.Count == 0;
        } catch (Exception e) { result.Errors.Add(new(null, null, e.Message)); }
        finally { foreach (var module in loaded.Values) module.Dispose(); }
        // Publish no map when any entry fails. Always return the complete preflight report.
        if (result.Success && outputPath != null) {
            string stage = Path.GetFullPath(outputPath) + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                using (var stream = new FileStream(stage, FileMode.CreateNew, FileAccess.Write)) doc.Save(stream);
                File.Move(stage, outputPath, false);
            } catch (Exception e) { result.Success = false; result.Errors.Add(new(null, null, "Map publication failed: " + e.Message)); }
            finally { if (File.Exists(stage)) File.Delete(stage); }
        }
        using (var stream = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write)) JsonSerializer.Serialize(stream, result, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine($"{(result.Success ? "PASS" : "FAILED")}: {result.Changes.Count} reviewed edits; {result.Errors.Count} errors. Report: {reportPath}");
        return result.Success;
    }
}
