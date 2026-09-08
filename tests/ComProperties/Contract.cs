using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
[ComImport, Guid("113188F8-34F7-44DE-A765-1E98AB5BCE01"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IImported {
    [IndexerName("Lookup")] string this[int key] { get; set; }
}
