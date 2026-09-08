using System.IO;
using System.Linq;
using dnlib.DotNet;
class Emitter {
    static string Name(string type, string field) {
        if (type == "Collision") {
            if (field == "First" || field == "Second") return "Value";
            if (field == "CallName") return "Call";
            if (field == "SameAsType") return "Collision";
        }
        if (type == "GenericBox`1" && (field == "First" || field == "Second")) return "a";
        return field;
    }
    static void Main(string[] args) {
        if (args[0] == "--verify") {
            using var module = ModuleDefMD.Load(args[1]);
            foreach (var type in module.GetTypes()) {
                var names = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
                names.Add(type.Name.String.Split('`')[0]);
                foreach (var method in type.Methods) names.Add(method.Name);
                foreach (var property in type.Properties) names.Add(property.Name);
                foreach (var evt in type.Events) names.Add(evt.Name);
                foreach (var parameter in type.GenericParameters) names.Add(parameter.Name);
                foreach (var nested in type.NestedTypes) names.Add(nested.Name.String.Split('`')[0]);
                foreach (var field in type.Fields)
                    if (!names.Add(field.Name))
                        throw new System.Exception("Processed metadata still contains a field collision: " + field.FullName);
            }
            return;
        }
        using var library = ModuleDefMD.Load(args[0]);
        using var client = ModuleDefMD.Load(args[1]);
        foreach (var member in client.GetMemberRefs().Concat(library.GetMemberRefs())) {
            if (member.IsFieldRef)
                member.Name = Name(member.DeclaringType.Name, member.Name);
        }
        foreach (var module in new[] { library, client })
            foreach (var type in module.GetTypes())
                foreach (var method in type.Methods.Where(m => m.HasBody))
                    foreach (var instruction in method.Body.Instructions)
                        if (instruction.Operand is MemberRef member && member.IsFieldRef)
                            member.Name = Name(member.DeclaringType.Name, member.Name);
        foreach (var type in library.GetTypes())
            foreach (var field in type.Fields) field.Name = Name(type.Name, field.Name);
        library.Write(Path.Combine(args[2], "FieldCollisionLibrary.dll"));
        client.Write(Path.Combine(args[2], "FieldCollisionClient.exe"));
    }
}




