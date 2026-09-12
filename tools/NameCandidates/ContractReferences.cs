using System.Security.Cryptography;
using System.Text.Json;
using dnlib.DotNet;

namespace De4dot.NameCandidates;

// Explicit metadata-only dependencies. They never become editable inputs, and
// no GAC, runtime framework substitution or directory-order fallback is used.
public sealed class ContractReferences : IAssemblyResolver, IDisposable {
    public sealed record Entry(string Path, string Identity, string Mvid, string Sha256);
    public sealed class Manifest {
        public int Format { get; set; } = 1;
        public Entry[] Assemblies { get; set; } = Array.Empty<Entry>();
    }
    readonly Dictionary<string, ModuleDefMD> assemblies = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> hashes = new(StringComparer.Ordinal);
    public ModuleContext Context { get; }
    public string ManifestSha256 { get; }
    public ContractReferences(string manifestPath) {
        Context = new ModuleContext(this) { Resolver = new Resolver(this) };
        if (manifestPath == null) return;
        byte[] manifestBytes = File.ReadAllBytes(manifestPath);
        ManifestSha256 = Convert.ToHexString(SHA256.HashData(manifestBytes));
        var manifest = JsonSerializer.Deserialize<Manifest>(manifestBytes);
        if (manifest?.Format != 1 || manifest.Assemblies == null) throw new InvalidDataException("Expected contract reference manifest Format=1.");
        try {
            foreach (var entry in manifest.Assemblies) {
                string path = System.IO.Path.GetFullPath(entry.Path, System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(manifestPath)));
                byte[] bytes = File.ReadAllBytes(path);
                string hash = Convert.ToHexString(SHA256.HashData(bytes));
                if (!StringComparer.OrdinalIgnoreCase.Equals(hash, entry.Sha256)) throw new InvalidDataException("Stale contract reference hash: " + path);
                var module = ModuleDefMD.Load(bytes, Context);
                if (module.Assembly?.FullName != entry.Identity || !Guid.TryParse(entry.Mvid, out var mvid) || module.Mvid != mvid) {
                    module.Dispose(); throw new InvalidDataException("Contract reference identity/MVID mismatch: " + path);
                }
                if (assemblies.ContainsKey(entry.Identity)) {
                    module.Dispose();
                    if (hashes[entry.Identity] != hash) throw new InvalidDataException("Conflicting explicit contract reference identity: " + entry.Identity);
                } else {
                    assemblies.Add(entry.Identity, module); hashes.Add(entry.Identity, hash);
                }
            }
        } catch { Dispose(); throw; }
    }
    public AssemblyDef Resolve(IAssembly assembly, ModuleDef sourceModule) =>
        assembly != null && assemblies.TryGetValue(assembly.FullName, out var module) ? module.Assembly : null;
    public void Dispose() { foreach (var module in assemblies.Values) module.Dispose(); assemblies.Clear(); }
}
