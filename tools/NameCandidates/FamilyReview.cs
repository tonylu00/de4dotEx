using System.Security.Cryptography;
using System.Text.Json;
using dnlib.DotNet;
using SourceNameMapping;

namespace De4dot.NameCandidates;

public static class FamilyReview {
    internal static IEnumerable<string> Files(string input) => MethodReview.Files(input).Where(p =>
        Path.GetExtension(p).Equals(".dll", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(p).Equals(".exe", StringComparison.OrdinalIgnoreCase));
    internal static MethodContractFamilies Build(string root, Func<string, ModuleDefMD> load) {
        var paths = new Dictionary<ModuleDef, string>();
        foreach (string file in Files(root)) {
            try { paths[load(InputPaths.Relative(root, file))] = InputPaths.Relative(root, file); }
            catch (BadImageFormatException) { }
        }
        return new MethodContractFamilies(paths.Keys, m => paths.TryGetValue(m, out var path) ? path : m.Location);
    }
    internal static MethodReview.Entry Entry(MethodDef method, string path, string hash, MethodContractFamilies.Family family) => new() {
        Module = path, Identity = method.Module.Assembly?.FullName, Mvid = method.Module.Mvid.ToString(), Sha256 = hash,
        Type = method.DeclaringType.FullName, Token = "0x" + method.MDToken.Raw.ToString("X8"), Signature = method.FullName,
        OriginalName = method.Name, Reason = MethodReview.Reason(method.Name), NeedsReadableName = MethodReview.Reason(method.Name) != null,
        MappingBlocker = family.Blockers.Length == 0 ? null : string.Join("; ", family.Blockers), ContractFamily = family.Id,
        Parameters = method.Parameters.Where(p => p.IsNormalMethodParameter).Select(p => $"{p.MethodSigIndex + 1}: {p.Type} {p.Name}").ToArray(),
        ParameterNames = method.Parameters.Where(p => p.IsNormalMethodParameter).Select(p => new MethodReview.ParameterEdit {
            Sequence = p.MethodSigIndex + 1, OriginalName = p.Name, HasMetadata = p.ParamDef != null
        }).ToArray()
    };
    public static void Write(string input, string output, string contractReferences = null) {
        if (File.Exists(output)) throw new IOException("Choose a new family review file.");
        input = Path.GetFullPath(input);
        string root = File.Exists(input) ? Path.GetDirectoryName(input) : Path.TrimEndingDirectorySeparator(input);
        var modules = new Dictionary<ModuleDef, (string Path, string Hash)>();
        using var references = new ContractReferences(contractReferences);
        var inventory = new MethodReview.Inventory { Kind = "SymbolReview", TargetRoot = root, ContractReferenceSha256 = references.ManifestSha256 };
        try {
            foreach (string file in Files(input)) {
                byte[] bytes = File.ReadAllBytes(file);
                try { modules.Add(ModuleDefMD.Load(bytes, references.Context), (InputPaths.Relative(root, file), Convert.ToHexString(SHA256.HashData(bytes)))); }
                catch (BadImageFormatException) { inventory.Skips.Add(new Skip(InputPaths.Relative(root, file), "non-managed input")); }
            }
            var graph = new MethodContractFamilies(modules.Keys, m => modules.TryGetValue(m, out var row) ? row.Path : m.Location);
            foreach (var family in graph.Families) foreach (var method in family.Methods)
                inventory.Methods.Add(Entry(method, modules[method.Module].Path, modules[method.Module].Hash, family));
            using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write);
            JsonSerializer.Serialize(stream, inventory, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine($"Inventoried {graph.Families.Count()} contract families ({graph.Families.Count(f => f.Blockers.Length == 0)} editable), {inventory.Methods.Count} members. Set NewName on one member to request an atomic family update.");
        } finally { foreach (var module in modules.Keys) module.Dispose(); }
    }
}
