using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using dnlib.DotNet;
using SourceNameMapping;

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
            MethodContractFamilies graph = null;
            MethodContractFamilies Graph() => graph ??= FamilyReview.Build(directory, Load);
            string Relative(ModuleDef module) => InputPaths.Relative(directory, loaded.Single(p => p.Value == module).Key);
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
                // One reviewed family member selects the contract. Verify that
                // seed before expanding from authoritative metadata, never from
                // an editable member list in the JSON.
                var requestedFamilies = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var seed in inventory.Methods.Where(e => e.ContractFamily != null && !string.IsNullOrWhiteSpace(e.NewName)).ToArray()) {
                    try {
                        var module = Load(seed.Module);
                        if (module.ResolveToken(Token(seed.Token)) is not MethodDef method || method.FullName != seed.Signature || method.Name != seed.OriginalName ||
                            module.Assembly?.FullName != seed.Identity || !Guid.TryParse(seed.Mvid, out var mvid) || module.Mvid != mvid ||
                            !StringComparer.OrdinalIgnoreCase.Equals(moduleHashes[module], seed.Sha256)) throw new InvalidDataException("Stale family seed identity/token/signature.");
                        var family = Graph().Get(method);
                        if (family == null || family.Id != seed.ContractFamily || family.Blockers.Length != 0) throw new InvalidDataException("Stale or unsupported contract family: " + string.Join("; ", family?.Blockers ?? Array.Empty<string>()));
                        if (requestedFamilies.TryGetValue(family.Id, out var requested) && requested != seed.NewName) throw new InvalidDataException("Conflicting names requested for one contract family.");
                        requestedFamilies[family.Id] = seed.NewName;
                        foreach (var member in family.Methods) {
                            string path = Relative(member.Module), token = "0x" + member.MDToken.Raw.ToString("X8");
                            var matches = inventory.Methods.Where(e => InputPaths.Comparer.Equals(e.Module, path) && e.Token == token).ToArray();
                            if (matches.Length > 1) throw new InvalidDataException("Duplicate family review member.");
                            var edit = matches.SingleOrDefault();
                            if (edit == null) {
                                edit = FamilyReview.Entry(member, path, moduleHashes[(ModuleDefMD)member.Module], family);
                                inventory.Methods.Add(edit);
                            }
                            if (edit.ContractFamily != null && edit.ContractFamily != family.Id || !string.IsNullOrWhiteSpace(edit.NewName) && edit.NewName != seed.NewName)
                                throw new InvalidDataException("Conflicting family member edit.");
                            edit.ContractFamily = family.Id; edit.NewName = seed.NewName;
                        }
                    } catch (Exception e) { result.Errors.Add(new(seed.Module, seed.Token, e.Message)); }
                }
                var seen = new HashSet<string>(InputPaths.Comparer);
                var edits = inventory.Methods.Select(e => (Entry: e, Kind: "Method")).Concat(inventory.Types.Select(e => (Entry: e, Kind: "Type")))
                    .Concat(inventory.Fields.Select(e => (Entry: e, Kind: "Field")));
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
                        } else if (item.Kind == "Field") {
                            if (member is not FieldDef field || FieldAliasScope.Blocker(field) != null || edit.ContractFamily != null || edit.ParameterNames.Length != 0)
                                throw new InvalidDataException("Unsupported field alias: " + (member is FieldDef f ? FieldAliasScope.Blocker(f) : "expected field token"));
                        } else {
                            if (method == null) throw new InvalidDataException("Expected a method token.");
                            string blocker = edit.ContractFamily == null ? MethodReview.Blocker(method) : MethodContractFamilies.MethodBlocker(method);
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
                        if (edit.ContractFamily != null) row.SetAttributeValue("ContractFamily", edit.ContractFamily);
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
            var contractRows = new Dictionary<MethodDef, XElement>();
            foreach (var pair in moduleRows) foreach (var row in pair.Value.Elements().Where(r => r.Attribute("ContractFamily") != null)) {
                try {
                    if (row.Name != "Method" || loaded[pair.Key].ResolveToken(Token((string)row.Attribute("Token"))) is not MethodDef method) throw new InvalidDataException("Expected contract method.");
                    var family = Graph().Get(method);
                    if (family == null || family.Id != (string)row.Attribute("ContractFamily") || family.Blockers.Length != 0) throw new InvalidDataException("Stale or unsupported contract family.");
                    contractRows.Add(method, row);
                } catch (Exception e) { result.Errors.Add(new((string)pair.Value.Attribute("Path"), (string)row.Attribute("Token"), e.Message)); }
            }
            foreach (var family in contractRows.Keys.Select(m => Graph().Get(m)).Distinct().OrderBy(f => f.Id, StringComparer.Ordinal)) {
                if (family.Methods.Any(m => !contractRows.ContainsKey(m))) { result.Errors.Add(new(Relative(family.Methods[0].Module), null, "Incomplete contract family " + family.Id)); continue; }
                var rows = family.Methods.Select(m => contractRows[m]).ToArray();
                var edits = rows.Where(proposed.ContainsKey).ToArray();
                if (edits.Length != 0) {
                    var requests = edits.Select(r => proposed[r]).Distinct(StringComparer.Ordinal).ToArray();
                    if (edits.Length != rows.Length || requests.Length != 1) { result.Errors.Add(new(null, null, "Contract family edits must be atomic.")); continue; }
                    var used = new HashSet<string>(family.Methods.Select(m => m.Module).Distinct().SelectMany(Reserved), StringComparer.Ordinal);
                    foreach (var module in family.Methods.Select(m => m.Module).Distinct()) {
                        var moduleRow = moduleRows[Resolve(directory, Relative(module))];
                        used.UnionWith(moduleRow.Elements().Where(r => !rows.Contains(r)).Select(r => proposed.TryGetValue(r, out var name) ? name : (string)r.Attribute("NewName")).Where(n => n != null));
                        used.UnionWith(moduleRow.Descendants("Parameter").Select(r => parameterProposals.TryGetValue(r, out var name) ? name : (string)r.Attribute("NewName")).Where(n => n != null));
                    }
                    string requested = requests[0], applied = requested; int suffix = 2;
                    while (!used.Add(applied)) applied = requested + suffix++.ToString(CultureInfo.InvariantCulture);
                    foreach (var row in rows) {
                        result.Changes.Add(new((string)row.Parent.Attribute("Path"), (string)row.Attribute("Token"), (string)row.Attribute("NewName"), requested, applied) { Kind = "ContractMethod" });
                        row.SetAttributeValue("NewName", applied); proposed.Remove(row);
                    }
                }
                if (rows.Any(r => r.Attribute("NewName") == null) || rows.Select(r => (string)r.Attribute("NewName")).Distinct(StringComparer.Ordinal).Count() != 1)
                    result.Errors.Add(new(null, null, "Contract family aliases differ."));
            }
            foreach (var pair in moduleRows) {
                var module = loaded[pair.Key]; var moduleRow = pair.Value;
                string relative = (string)moduleRow.Attribute("Path");
                var tokens = new HashSet<uint>();
                var used = Reserved(module);
                var parameterAliases = new HashSet<string>(StringComparer.Ordinal);
                var sharedNames = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var row in moduleRow.Elements()) {
                    try {
                        uint token = Token((string)row.Attribute("Token"));
                        if (!tokens.Add(token)) throw new InvalidDataException("Duplicate base-map token.");
                        if (module.ResolveToken(token) is not IMemberDef member || member.Name != (string)row.Attribute("ExpectedName") || member.FullName != (string)row.Attribute("Signature")) throw new InvalidDataException("Token/name/signature mismatch.");
                        if (row.Name == "Type") {
                            if (member is not TypeDef t || t.IsGlobalModuleType || t.HasGenericParameters || row.HasElements) throw new InvalidDataException("Unsupported type alias.");
                        } else if (row.Name == "Method") {
                            if (member is not MethodDef method || (contractRows.ContainsKey(method) ? MethodContractFamilies.MethodBlocker(method) : MethodReview.Blocker(method)) != null) throw new InvalidDataException("Unsupported method alias.");
                            var positions = new HashSet<int>();
                            var paramsUsed = new HashSet<string>(method.Parameters.Select(p => p.Name), StringComparer.Ordinal);
                            paramsUsed.UnionWith(method.GenericParameters.Select(p => p.Name.String));
                            for (var type = method.DeclaringType; type != null; type = type.DeclaringType) paramsUsed.UnionWith(type.GenericParameters.Select(p => p.Name.String));
                            var originalParameterNames = new HashSet<string>(paramsUsed, StringComparer.Ordinal);
                            paramsUsed.UnionWith(moduleRow.Elements().Where(r => r.Name != "Field" && !proposed.ContainsKey(r)).Select(r => (string)r.Attribute("NewName")).Where(n => n != null));
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
                        } else if (row.Name == "Field") {
                            if (member is not FieldDef field || FieldAliasScope.Blocker(field) != null || row.HasElements || row.Attribute("ContractFamily") != null)
                                throw new InvalidDataException("Unsupported field alias.");
                            if (!proposed.ContainsKey(row) && !Identifier((string)row.Attribute("NewName"))) throw new InvalidDataException("Invalid field alias.");
                        } else throw new InvalidDataException("Expected Type, Method or Field row.");
                        if (row.Name == "Field") continue; // Owner-scoped validation follows module-wide method/type allocation.
                        if (!proposed.ContainsKey(row)) {
                            string alias = (string)row.Attribute("NewName");
                            string family = (string)row.Attribute("ContractFamily");
                            if (alias != null && (!Identifier(alias) || !used.Add(alias) && (family == null || !sharedNames.TryGetValue(alias, out var previous) || previous != family))) throw new InvalidDataException("Invalid/colliding existing alias: " + alias);
                            if (alias != null && family != null) sharedNames[alias] = family;
                            if (alias == null && !row.HasElements) throw new InvalidDataException("Empty alias row.");
                        }
                    } catch (Exception e) { result.Errors.Add(new(relative, (string)row.Attribute("Token"), e.Message)); }
                }
                foreach (string alias in parameterAliases) {
                    if (moduleRow.Elements().Any(r => r.Name != "Field" && !proposed.ContainsKey(r) && (string)r.Attribute("NewName") == alias)) result.Errors.Add(new(relative, null, "Parameter/member alias collision: " + alias));
                    used.Add(alias);
                }
                var suggestions = new HashSet<string>(proposed.Where(p => p.Key.Parent == moduleRow).Select(p => p.Value), StringComparer.Ordinal);
                foreach (var edit in proposed.Where(p => p.Key.Parent == moduleRow && p.Key.Name != "Field").OrderBy(p => Token((string)p.Key.Attribute("Token")))) {
                    string name = edit.Value;
                    int suffix = 2;
                    if (!used.Add(name)) do { name = edit.Value + suffix++.ToString(CultureInfo.InvariantCulture); } while (suggestions.Contains(name) || !used.Add(name));
                    result.Changes.Add(new(relative, (string)edit.Key.Attribute("Token"), (string)edit.Key.Attribute("NewName"), edit.Value, name) { Kind = edit.Key.Name.LocalName });
                    edit.Key.SetAttributeValue("NewName", name);
                }
            }
            if (result.Errors.Count == 0 && moduleRows.Values.Any(r => r.Elements("Field").Any())) {
                // Load the complete input tree for inherited/derived reservations.
                // Same-identity compatibility copies remain distinct metadata objects.
                foreach (string file in FamilyReview.Files(directory)) {
                    try { Load(InputPaths.Relative(directory, file)); } catch (BadImageFormatException) { }
                }
                var scope = new FieldAliasScope(loaded.Values);
                var aliases = new Dictionary<IMemberDef, string>();
                var fields = new Dictionary<FieldDef, XElement>();
                foreach (var pair in moduleRows) foreach (var row in pair.Value.Elements()) {
                    var member = (IMemberDef)loaded[pair.Key].ResolveToken(Token((string)row.Attribute("Token")));
                    if (member is FieldDef field) fields.Add(field, row);
                    if (row.Attribute("NewName") != null && !(member is FieldDef && proposed.ContainsKey(row))) aliases[member] = (string)row.Attribute("NewName");
                }
                string Alias(IMemberDef member) => aliases.TryGetValue(member, out var name) ? name : null;
                foreach (var pair in fields.OrderBy(p => Relative(p.Key.Module), StringComparer.Ordinal).ThenBy(p => p.Key.MDToken.Raw)) {
                    var row = pair.Value;
                    if (!proposed.TryGetValue(row, out string requested)) continue;
                    var used = scope.Reserved(pair.Key, Alias);
                    var relatedTypes = scope.Types(pair.Key);
                    var suggestions = new HashSet<string>(fields.Where(p => relatedTypes.Contains(p.Key.DeclaringType) && proposed.ContainsKey(p.Value)).Select(p => proposed[p.Value]), StringComparer.Ordinal);
                    string name = requested; int suffix = 2;
                    if (used.Contains(name)) do { name = requested + suffix++.ToString(CultureInfo.InvariantCulture); } while (used.Contains(name) || suggestions.Contains(name));
                    result.Changes.Add(new(Relative(pair.Key.Module), (string)row.Attribute("Token"), (string)row.Attribute("NewName"), requested, name) { Kind = "Field" });
                    row.SetAttributeValue("NewName", name); aliases[pair.Key] = name;
                }
                foreach (var pair in fields) if (scope.Reserved(pair.Key, Alias).Contains(Alias(pair.Key)))
                    result.Errors.Add(new(Relative(pair.Key.Module), (string)pair.Value.Attribute("Token"), "Field alias collides in declaration/inheritance scope: " + Alias(pair.Key)));
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
