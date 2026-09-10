using System;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
[assembly:Dotfuscator]
public class DotfuscatorAttribute:Attribute {}
class aaa { internal class bbb { public int Read()=>23; } }
public interface abc { int a(int value); }
public class Api:abc {
 private int z=17;
 public int a(int value)=>value+1;
 public string Open(string value)=>value+"!";
 public int Open(int value)=>value+2;
 public T Open<T>(T value)=>value;
 public static int add(int x,int y)=>x+y;
 public int Count {get;set;}
 public event Action Changed;
 public void Raise()=>Changed?.Invoke();
 public int Value;
 protected virtual int b()=>7;
 public int Invoke()=>b()+Hidden.Run();
 [DllImport("kernel32.dll",EntryPoint="GetCurrentProcessId")]
 public static extern uint ProcessIdentity();
}
class Hidden {
 [MethodImpl(MethodImplOptions.NoInlining)] static int a()=>11;
 [MethodImpl(MethodImplOptions.NoInlining)] static int a(int value)=>value;
 [MethodImpl(MethodImplOptions.NoInlining)] static int QxVjKpLrZtWn()=>0;
 [MethodImpl(MethodImplOptions.NoInlining)] static int GetHTTPResponseAsync()=>0;
 [MethodImpl(MethodImplOptions.NoInlining)] static int add()=>0;
 public static int Run()=>a()+a(0)+QxVjKpLrZtWn()+GetHTTPResponseAsync()+add();
}
