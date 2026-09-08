using System.Linq;
using dnlib.DotNet;
class Emit {
    static void Main(string[] args) {
        using var module = ModuleDefMD.Load(args[0]);
        foreach (string name in new[] { "Imported", "Managed" }) {
            var type = module.Types.Single(t => t.Name == name);
            var property = type.Properties.Single(p => p.Name == (name == "Imported" ? "Lookup" : "Number"));
            foreach (var method in new[] { property.GetMethod, property.SetMethod }) {
                method.SemanticsAttributes = 0;
                method.IsSpecialName = false;
            }
            type.Properties.Remove(property);
        }
        module.Write(args[1]);
        using var contract = ModuleDefMD.Load(args[2]);
        var imported = contract.Types.Single(t => t.Name == "IImported");
        imported.CustomAttributes.Remove(imported.CustomAttributes.Single(a => a.TypeFullName == "System.Reflection.DefaultMemberAttribute"));
        contract.Write(args[3]);
    }
}
