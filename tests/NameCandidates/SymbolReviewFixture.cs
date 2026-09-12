using System.Text.Json;
using System.Xml.Linq;
using De4dot.NameCandidates;

static class SymbolReviewFixture {
    public static void Run(string root, string inputs, string seed) {
        void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
        string basePath = Path.Combine(root, "symbol-base.xml");
        var doc = XDocument.Load(seed);
        doc.Root.Element("Module").Element("Method").Add(new XElement("Parameter", new XAttribute("Sequence", 1), new XAttribute("ExpectedName", "count"), new XAttribute("NewName", "itemCount")));
        doc.Save(basePath);
        string original = File.ReadAllText(basePath);
        var inventory = MethodReview.Scan(inputs);
        var types = MethodReview.Scan(inputs, typesOnly: true);
        Require(types.Methods.Count == 0 && types.Types.Any(t => t.MappingBlocker == "global-module-type"), "Type inventory did not expose blockers");
        var type = types.Types.Single(t => t.OriginalName == "Manager");
        type.NewName = "itemCount";
        inventory.Types.Add(type);
        var method = inventory.Methods.Single(m => m.OriginalName == "a");
        Require(method.ParameterNames.Single().Sequence == 1 && method.ParameterNames.Single().HasMetadata, "Parameter inventory sequence/provenance");
        // Keep the existing method alias while changing only its parameter.
        method.ParameterNames.Single().NewName = "count";
        string review = Path.Combine(root, "symbols.json"), output = Path.Combine(root, "symbols.xml");
        File.WriteAllText(review, JsonSerializer.Serialize(inventory));
        Require(ReviewMapUpdate.Run(review, basePath, output, Path.Combine(root, "symbols-result.json")), "Mixed edit failed");
        var mapped = XDocument.Load(output);
        Require((string)mapped.Descendants("Type").Single().Attribute("NewName") == "itemCount", "Type alias did not reuse a replaced parameter alias");
        Require(!mapped.Descendants("Parameter").Any(), "Restoring the original parameter name must remove the override, not produce count2");
        Require((string)mapped.Descendants("Method").Single().Attribute("NewName") == (string)doc.Descendants("Method").Single().Attribute("NewName"), "Parameter-only edit changed method name");
        Require(ReviewMapUpdate.Run(null, output, null, Path.Combine(root, "symbols-check.json")), "Mixed map preflight failed");
        string fromReview = Path.Combine(root, "symbols-standalone.xml");
        MethodReview.WriteMap(review, inputs, fromReview);
        Require(XDocument.Load(fromReview).Descendants("Type").Count() == 1 && !XDocument.Load(fromReview).Descendants("Method").Any(), "An unchanged parameter must not create an empty method map row");
        // A collision with a different mapped member still needs allocation.
        method.ParameterNames.Single().NewName = "itemCount";
        File.WriteAllText(review, JsonSerializer.Serialize(inventory));
        string collision = Path.Combine(root, "symbols-parameter-collision.xml");
        Require(ReviewMapUpdate.Run(review, output, collision, Path.Combine(root, "symbols-parameter-collision.json")), "Genuine parameter/member collision failed");
        var colliding = XDocument.Load(collision);
        Require((string)colliding.Descendants("Parameter").Single().Attribute("NewName") != (string)colliding.Descendants("Type").Single().Attribute("NewName"), "Genuine parameter/member collision was accepted");
        method.ParameterNames.Single().NewName = "count";
        method.ParameterNames.Single().Sequence = 99;
        var global = types.Types.Single(t => t.MappingBlocker == "global-module-type");
        global.NewName = "ModuleAlias"; inventory.Types.Add(global);
        File.WriteAllText(review, JsonSerializer.Serialize(inventory));
        string failed = Path.Combine(root, "symbols-failed.xml"), errors = Path.Combine(root, "symbols-errors.json");
        Require(!ReviewMapUpdate.Run(review, basePath, failed, errors) && !File.Exists(failed), "Invalid symbol edits published");
        using var report = JsonDocument.Parse(File.ReadAllText(errors));
        Require(report.RootElement.GetProperty("Errors").GetArrayLength() == 2, "Symbol preflight did not aggregate errors");
        Require(File.ReadAllText(basePath) == original, "Base map changed");
        Console.WriteLine("PASS original parameter preservation/restoration, genuine collision repair, mixed/parameter-only maps, stale sequences, unsupported types, aggregate errors and no partial output.");
    }
}
