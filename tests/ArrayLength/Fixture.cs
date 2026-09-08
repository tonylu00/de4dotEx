using System;
using System.Text;
using System.Linq;
public class DotfuscatorAttribute : Attribute { }
class Fixture {
 static byte[] Copy(string text) {
  byte[] bytes = new byte[text.Length * 2];
  Buffer.BlockCopy(text.ToCharArray(), 0, bytes, 0, bytes.Length);
  return bytes;
 }
 static int Dynamic(int length) { var array = new byte[length]; return array.Length; }
 static int Partial(int length) { var array = new byte[length & 15]; return array.Length; }
 static int Known() { var array = new byte[17]; return array.Length; }
 static int Main() {
  try {
   foreach (string value in new[] { "", "one", "different", "\u4e2d\ud83d\ude00" })
    if (!Copy(value).SequenceEqual(Encoding.Unicode.GetBytes(value))) throw new Exception("BlockCopy count changed");
   foreach (int length in new[] { 0, 1, 17, 31, 500001 })
    if (Dynamic(length) != length || Partial(length) != (length & 15)) throw new Exception("Array length changed");
   if (Known() != 17) throw new Exception("Known array length changed");
   Console.WriteLine("PASS: runtime, partially known and constant array lengths; complete UTF-16 copy");
   return 0;
  } catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
 }
}
