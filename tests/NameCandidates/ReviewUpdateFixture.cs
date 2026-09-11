using System.Text.Json;
using System.Xml.Linq;
using De4dot.NameCandidates;

static class ReviewUpdateFixture {
    public static void Run(string root, string inputs, string seed) {
        void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
        var inventory = MethodReview.Scan(inputs);
        var a = inventory.Methods.Single(m => m.OriginalName == "a");
        var c = inventory.Methods.Single(m => m.OriginalName == "c_1");
        string basePath = Path.Combine(root, "update-base.xml");
        var doc = XDocument.Load(seed);
        var module = doc.Root.Element("Module");
        module.Element("Method").Add(new XElement("Parameter", new XAttribute("Sequence", 1), new XAttribute("ExpectedName", "count"), new XAttribute("NewName", "itemCount")));
        module.Add(new XElement("Type", new XAttribute("Token", "0x02000002"), new XAttribute("ExpectedName", "Manager"), new XAttribute("Signature", "Example.Manager"), new XAttribute("NewName", "ReadableManager")));
        module.Add(new XElement("Method", new XAttribute("Token", c.Token), new XAttribute("ExpectedName", c.OriginalName), new XAttribute("Signature", c.Signature), new XAttribute("NewName", "Compute")));
        doc.Save(basePath);
        string originalBase = File.ReadAllText(basePath);
        a.NewName = "Compute";
        string reviewed = Path.Combine(root, "update-review.json"), output = Path.Combine(root, "updated.xml");
        File.WriteAllText(reviewed, JsonSerializer.Serialize(inventory));
        Require(ReviewMapUpdate.Run(reviewed, basePath, output, Path.Combine(root, "update-result.json")), "Update failed");
        var updated = XDocument.Load(output);
        var changed = updated.Descendants("Method").Single(m => (string)m.Attribute("Token") == a.Token);
        Require((string)changed.Attribute("NewName") == "Compute2", "Merged alias collision not repaired");
        Require((string)changed.Element("Parameter").Attribute("NewName") == "itemCount", "Parameter alias lost");
        Require(XNode.DeepEquals(doc.Descendants("Type").Single(), updated.Descendants("Type").Single()), "Type alias changed");
        Require(XNode.DeepEquals(doc.Descendants("Method").Single(m => (string)m.Attribute("Token") == c.Token), updated.Descendants("Method").Single(m => (string)m.Attribute("Token") == c.Token)), "Untouched method changed");
        Require(File.ReadAllText(basePath) == originalBase, "Base map overwritten");
        string merged = Path.Combine(root, "merged-generated.xml");
        Require(ReviewMapUpdate.Run(seed, basePath, merged, Path.Combine(root, "merged-generated.json"), true), "Generated-map merge failed");
        Require(XDocument.Load(merged).Descendants("Parameter").Count() == 1 && XDocument.Load(merged).Descendants("Type").Count() == 1, "Generated-map merge dropped seed rows");
        Require(ReviewMapUpdate.Run(null, output, null, Path.Combine(root, "check-result.json")), "Result preflight failed");
        inventory.Methods.Single(m => m.OriginalName == "b").NewName = "Dispatch";
        inventory.Methods.Single(m => m.OriginalName == "d_1").NewName = "not-an-identifier";
        File.WriteAllText(reviewed, JsonSerializer.Serialize(inventory));
        string failed = Path.Combine(root, "failed-update.xml"), report = Path.Combine(root, "failed-update.json");
        Require(!ReviewMapUpdate.Run(reviewed, basePath, failed, report) && !File.Exists(failed), "Invalid batch published");
        using var errors = JsonDocument.Parse(File.ReadAllText(report));
        Require(errors.RootElement.GetProperty("Errors").GetArrayLength() == 2, "Preflight did not collect both errors");
        // A failed base-map check also identifies the member instead of exporting.
        var invalid = XDocument.Load(basePath);
        invalid.Descendants("Method").First().SetAttributeValue("Signature", "stale");
        string invalidPath = Path.Combine(root, "stale-base.xml"); invalid.Save(invalidPath);
        Require(!ReviewMapUpdate.Run(null, invalidPath, null, Path.Combine(root, "stale-base-result.json")), "Stale base accepted");
        Console.WriteLine("PASS executable map merge/preflight, existing alias replacement, collision repair, type/parameter preservation, aggregated errors and no partial map.");
    }
}
