using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using dnlib.DotNet;

namespace De4dot.NameCandidates;

public static class SourceMapWriter {
    internal sealed record ReferenceNames(string Hash, Dictionary<uint, string> Names);
    internal static Dictionary<string, ReferenceNames> ReadReferenceNames(byte[] mapBytes, string referenceRoot) {
        var settings = new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null };
        using var stream = new MemoryStream(mapBytes, false);
        using var reader = System.Xml.XmlReader.Create(stream, settings);
        var map = XDocument.Load(reader).Root;
        if (map?.Name != "SourceNameMap" || (string)map.Attribute("Version") != "1" ||
            !string.Equals(Root((string)map.Attribute("InputDirectory")), Root(referenceRoot), InputPaths.Comparison))
            throw new InvalidDataException("Reference source map has a different input tree or format.");
        var result = new Dictionary<string, ReferenceNames>(InputPaths.Comparer);
        foreach (var row in map.Elements("Module")) {
            string relative = (string)row.Attribute("Path") ?? throw new InvalidDataException("Missing reference module path.");
            string path = Path.GetFullPath(Path.Combine(referenceRoot, relative));
            if (Path.IsPathRooted(relative) || !path.StartsWith(Root(referenceRoot) + Path.DirectorySeparatorChar, InputPaths.Comparison))
                throw new InvalidDataException("Reference module leaves input tree.");
            for (string current = path; current != null; current = Path.GetDirectoryName(current)) {
                if ((File.GetAttributes(current) & System.IO.FileAttributes.ReparsePoint) != 0) throw new IOException("Linked reference input is not supported.");
                if (string.Equals(current, Root(referenceRoot), InputPaths.Comparison)) break;
            }
            byte[] bytes = File.ReadAllBytes(path);
            using var module = ModuleDefMD.Load(bytes);
            if (!string.Equals((string)row.Attribute("Sha256"), Convert.ToHexString(SHA256.HashData(bytes)), StringComparison.OrdinalIgnoreCase) ||
                !Guid.TryParse((string)row.Attribute("Mvid"), out var mvid) || mvid != module.Mvid)
                throw new InvalidDataException("Stale reference source map module.");
            var names = new Dictionary<uint, string>();
            if (!result.TryAdd(InputPaths.Relative(referenceRoot, path), new ReferenceNames(Convert.ToHexString(SHA256.HashData(bytes)), names))) throw new InvalidDataException("Duplicate reference module.");
            foreach (var entry in row.Elements("Method").Where(e => e.Attribute("NewName") != null)) {
                string token = (string)entry.Attribute("Token") ?? "";
                if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) token = token.Substring(2);
                uint raw = uint.Parse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                string name = (string)entry.Attribute("NewName");
                if (!Identifier(name) || module.ResolveToken(raw) is not MethodDef method ||
                    method.Name.String != (string)entry.Attribute("ExpectedName") || method.FullName != (string)entry.Attribute("Signature"))
                    throw new InvalidDataException("Invalid reference method alias or definition.");
                if (!names.TryAdd(raw, name)) throw new InvalidDataException("Duplicate reference method alias.");
            }
        }
        return result;
    }
    static bool Identifier(string name) => name != null && Regex.IsMatch(name, @"\A[A-Za-z_][A-Za-z0-9_]*\z");
    static string Root(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    public static void Write(string reportPath, string targetRoot, string outputPath) {
        using var document = JsonDocument.Parse(File.ReadAllText(reportPath));
        if (document.RootElement.GetProperty("Format").GetInt32() != 1) throw new InvalidDataException("Unsupported candidate report format.");
        var report = document.RootElement.Deserialize<Report>() ?? throw new InvalidDataException("Missing report.");
        targetRoot = Root(targetRoot);
        if (!string.Equals(targetRoot, Root(report.TargetRoot), InputPaths.Comparison)) throw new InvalidDataException("Target tree differs from the candidate report.");
        var root = new XElement("SourceNameMap", new XAttribute("Version", "1"), new XAttribute("InputDirectory", targetRoot));
        foreach (var group in report.Candidates.GroupBy(c => c.Module, InputPaths.Comparer).OrderBy(g => g.Key, StringComparer.Ordinal)) {
            if (Path.IsPathRooted(group.Key)) throw new InvalidDataException("Expected a relative module path.");
            string path = Path.GetFullPath(Path.Combine(targetRoot, group.Key));
            if (!path.StartsWith(targetRoot + Path.DirectorySeparatorChar, InputPaths.Comparison)) throw new InvalidDataException("Module leaves target tree.");
            for (string current = path; current != null; current = Path.GetDirectoryName(current)) {
                if ((File.GetAttributes(current) & System.IO.FileAttributes.ReparsePoint) != 0) throw new IOException("Linked target input is not supported.");
                if (string.Equals(current, targetRoot, InputPaths.Comparison)) break;
            }
            byte[] bytes = File.ReadAllBytes(path);
            string hash = Convert.ToHexString(SHA256.HashData(bytes));
            using var module = ModuleDefMD.Load(bytes);
            var row = new XElement("Module", new XAttribute("Path", group.Key), new XAttribute("Mvid", module.Mvid), new XAttribute("Sha256", hash));
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var type in module.GetTypes()) {
                used.UnionWith((type.Namespace.String ?? "").Split('.'));
                used.Add(type.Name.String.Split('`')[0]);
                used.UnionWith(type.GenericParameters.Select(p => p.Name.String));
                used.UnionWith(type.Methods.Select(m => m.Name.String));
                used.UnionWith(type.Methods.SelectMany(m => m.Parameters).Select(p => p.Name));
                used.UnionWith(type.Methods.SelectMany(m => m.GenericParameters).Select(p => p.Name.String));
                used.UnionWith(type.Fields.Select(f => f.Name.String));
                used.UnionWith(type.Properties.Select(p => p.Name.String));
                used.UnionWith(type.Events.Select(e => e.Name.String));
            }
            // Reserve all suggestions before allocating suffixes, so collision
            // repair cannot take a different candidate's unmodified name.
            var suggestions = new HashSet<string>(group.Select(c => c.SuggestedName), StringComparer.Ordinal);
            var seen = new HashSet<uint>();
            foreach (var candidate in group.OrderBy(c => c.TargetToken, StringComparer.Ordinal)) {
                if (!string.Equals(candidate.TargetHash, hash, StringComparison.OrdinalIgnoreCase) ||
                    !Guid.TryParse(candidate.TargetMvid, out var mvid) || mvid != module.Mvid || candidate.TargetIdentity != module.Assembly?.FullName)
                    throw new InvalidDataException("Stale candidate module: " + group.Key);
                string token = candidate.TargetToken;
                if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) token = token.Substring(2);
                uint raw = uint.Parse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                if (!seen.Add(raw)) throw new InvalidDataException("Duplicate candidate token.");
                if (!(module.ResolveToken(raw) is MethodDef method) || method.FullName != candidate.Target)
                    throw new InvalidDataException("Candidate token/signature mismatch.");
                string blocker = MethodReview.Blocker(method);
                if (blocker != null)
                    throw new InvalidDataException($"Unsupported source-map method: {group.Key} token 0x{raw:X8} {method.FullName}: {blocker}.");
                if (!Identifier(candidate.SuggestedName)) throw new InvalidDataException("Invalid suggested source identifier.");
                string name = candidate.SuggestedName;
                if (!used.Add(name)) {
                    int suffix = 2;
                    do { name = candidate.SuggestedName + suffix++.ToString(CultureInfo.InvariantCulture); }
                    while (suggestions.Contains(name) || !used.Add(name));
                }
                row.Add(new XElement("Method", new XAttribute("Token", "0x" + raw.ToString("X8")),
                    new XAttribute("ExpectedName", method.Name), new XAttribute("Signature", method.FullName), new XAttribute("NewName", name)));
            }
            root.Add(row);
        }
        // Validate every input first, and never overwrite an existing artifact.
        using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write);
        new XDocument(root).Save(output);
    }
}
