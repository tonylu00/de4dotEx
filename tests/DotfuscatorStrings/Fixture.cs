using System;
public class DotfuscatorAttribute : Attribute { }
class Fixture {
 public static string Decode(string text, int value) {
  char[] chars=text.ToCharArray();
  int key=unchecked((int)((nint)(1234567+value)+(nint)7+(nint)31+(nint)13));
  for(int i=0;i<chars.Length;i++) {
   char c=chars[i];
   byte high=unchecked((byte)((c & 255)^key++));
   byte low=unchecked((byte)((c >> 8)^key++));
   chars[i]=(char)((high<<8)|low);
  }
  return string.Intern(new string(chars));
 }
 public static string[] Values() { /*VALUES*/ }
 static int Main() {
  string[] expected={"{0}.signature","certificate:{0}","\0\u4e2d\ud83d\ude00\uffff","","validation succeeded:{0}"};
  string[] actual=Values();
  for(int i=0;i<expected.Length;i++) if(actual[i]!=expected[i]) throw new Exception("String mismatch at "+i);
  Console.WriteLine("PASS: readable strings, UTF-16 code units, empty strings and overflowing keys");
  return 0;
 }
}
