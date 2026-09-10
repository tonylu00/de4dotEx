using System.Reflection;
public static class ReflectionReader {
 public static int Read(object value) => (int)value.GetType().GetField("z", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(value);
}
