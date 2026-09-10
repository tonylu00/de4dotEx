using System.Reflection;
public static class ReflectionReader {
 public static int ReadNested() { var type=System.Type.GetType("aaa+bbb, Fixture",true); return (int)type.GetMethod("Read").Invoke(System.Activator.CreateInstance(type,true),null); }
 public static int Read(object value) => (int)value.GetType().GetField("z", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(value);
}
