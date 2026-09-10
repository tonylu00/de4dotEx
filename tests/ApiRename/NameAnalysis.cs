using System;
using de4dot.code.renamer;

class NameAnalysisTests {
 static void Main() {
  foreach(var name in new[] { "GetHTTPResponseAsync", "LoadX509Certificate", "ComputeSHA256Hash", "EncodeUTF8Text", "get_Item", "IDisposable.Dispose", "add", "Run", "Open", "ReadXmlFile" })
   Check(name, MethodNameAnalysis.Assessment.Meaningful);
  foreach(var name in new[] { "QxVjKpLrZtWn", "a1B2c3D4e5F6", "ab92cdef8292abcf7392abcd" })
   Check(name, MethodNameAnalysis.Assessment.Obfuscated);
  foreach(var name in new[] { "a", "ab", "nrf", "KNX", "DptTranslator", "Hydrothermalization", "读取配置", "<Execute>b__0", "1GetValue" })
   Check(name, MethodNameAnalysis.Assessment.Unknown);
  if(string.Join("|",MethodNameAnalysis.Tokenize("GetHTTPResponseAsync")) != "Get|HTTP|Response|Async") throw new Exception("Acronym tokenization");
  Console.WriteLine("Lexical method-name assessment: PASS");
 }
 static void Check(string name, MethodNameAnalysis.Assessment expected) {
  var actual=MethodNameAnalysis.Analyze(name);
  if(actual!=expected) throw new Exception(name+": expected "+expected+", got "+actual);
 }
}
