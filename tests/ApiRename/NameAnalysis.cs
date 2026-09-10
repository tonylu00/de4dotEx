using System;
using de4dot.code.renamer;

class NameAnalysisTests {
 static void Main() {
  foreach(var name in new[] { "IsDeviceTesterLicensed", "GetHTTPResponseAsync", "LoadX509Certificate", "ComputeSHA256Hash", "EncodeUTF8Text", "get_Item", "IDisposable.Dispose", "add", "Run", "Open", "ReadXmlFile" })
   Check(name, MethodNameAnalysis.Assessment.Meaningful);
  foreach(var name in new[] { "QxVjKpLrZtWn", "a1B2c3D4e5F6", "ab92cdef8292abcf7392abcd" })
   Check(name, MethodNameAnalysis.Assessment.Obfuscated);
  foreach(var name in new[] { "a", "ab", "nrf", "KNX", "DptTranslator", "Hydrothermalization", "读取配置", "<Execute>b__0", "1GetValue" })
   Check(name, MethodNameAnalysis.Assessment.Unknown);
  if(string.Join("|",MethodNameAnalysis.Tokenize("GetHTTPResponseAsync")) != "Get|HTTP|Response|Async") throw new Exception("Acronym tokenization");
  var names = new[] { "GetETS", "SaveETS", "ReadHydronicData", "QxVjKpLrZtWn" };
  var literals = new[] { "Hydronic data is available", "QxVjKpLrZtWn QxVjKpLrZtWn" };
  var learned = MethodNameAnalysis.Learn(names, literals);
  if(!learned.Contains("ETS") || !learned.Contains("Hydronic") || learned.Contains("Qx")) throw new Exception("Vocabulary evidence");
  if(MethodNameAnalysis.Analyze("ETS",learned)!=MethodNameAnalysis.Assessment.Meaningful) throw new Exception("Learned acronym");
  if(MethodNameAnalysis.Analyze("Hydronic",learned)!=MethodNameAnalysis.Assessment.Meaningful) throw new Exception("Name plus prose");
  if(MethodNameAnalysis.Analyze("a",learned)!=MethodNameAnalysis.Assessment.Unknown) throw new Exception("Short name contaminated");
  if(MethodNameAnalysis.Analyze("ETS")!=MethodNameAnalysis.Assessment.Unknown) throw new Exception("Vocabulary leaked between batches");
  if(!learned.SetEquals(MethodNameAnalysis.Learn(new[] {"QxVjKpLrZtWn", "ReadHydronicData", "SaveETS", "GetETS"},literals))) throw new Exception("Order dependent vocabulary");
  if(MethodNameAnalysis.Learn(new[] {"GetETS", "GetETS"},new string[0]).Contains("ETS")) throw new Exception("Repeated overload taught vocabulary");
  Console.WriteLine("Lexical method-name assessment: PASS");
 }
 static void Check(string name, MethodNameAnalysis.Assessment expected) {
  var actual=MethodNameAnalysis.Analyze(name);
  if(actual!=expected) throw new Exception(name+": expected "+expected+", got "+actual);
 }
}
