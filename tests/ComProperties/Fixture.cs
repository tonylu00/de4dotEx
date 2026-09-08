using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
[ComImport, Guid("113188F8-34F7-44DE-A765-1E98AB5BCE02"), ClassInterface(ClassInterfaceType.None)]
public class Imported : IImported {
    [IndexerName("Lookup")] public extern string this[int key] {
        [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType=MethodCodeType.Runtime)] get;
        [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType=MethodCodeType.Runtime)] set;
    }
    public extern int Existing { [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType=MethodCodeType.Runtime)] get; }
}
public interface IManaged { int Number { get; set; } }
public class Managed : IManaged { public int Number { get; set; } }
class Program {
    static int Main(string[] args) {
        bool emitted = args.Length != 0;
        var type = typeof(Imported);
        if (!type.IsImport || type.GUID != new Guid("113188F8-34F7-44DE-A765-1E98AB5BCE02") ||
            type.GetProperty("Existing") == null || (type.GetProperty("Lookup") == null) != emitted) throw new Exception("Imported shape changed");
        foreach (string name in new[] { "get_Lookup", "set_Lookup" }) {
            var method = type.GetMethod(name);
            if (method == null || method.GetParameters()[0].ParameterType != typeof(int) || !method.IsVirtual ||
                method.GetMethodBody() != null || (method.GetMethodImplementationFlags() & MethodImplAttributes.InternalCall) == 0)
                throw new Exception("Imported method contract changed");
        }
        IManaged value = new Managed(); value.Number = 73;
        if (value.Number != 73 || typeof(IImported).GetProperty("Lookup").GetIndexParameters().Length != 1)
            throw new Exception("Dispatch or interface metadata changed");
        if (args.Length > 1 && typeof(Managed).GetProperty("Number") == null) throw new Exception("Managed restoration skipped");
        Console.WriteLine("PASS: COM method shape, existing properties and managed dispatch");
        return 0;
    }
}
