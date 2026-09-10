using System;
class Derived:Api {protected override int b()=>13;}
class Caller {
 static int Main() {
  var x=new Derived();
  if(ReflectionReader.Read(new Api())!=17) throw new Exception("Unprocessed dependency reflection broke");
  if(ReflectionReader.ReadNested()!=23) throw new Exception("Nested assembly-qualified reflection broke");
  if(x.Open(2)!=4 || x.Open("a")!="a!" || x.Open<double>(2.5)!=2.5 || ((abc)x).a(3)!=4 || Api.add(2,3)!=5 || x.Invoke()!=24 || Api.ProcessIdentity()==0) throw new Exception("External API behavior changed");
  x.Count=9; x.Value=3; bool fired=false; x.Changed+=()=>fired=true; x.Raise();
  if(x.Count!=9 || x.Value!=3 || !fired) throw new Exception("Property, field or event changed");
  Console.WriteLine("PASS external unmodified SDK caller: overloads, generics, short API names, virtual dispatch, PInvoke, properties, fields and events"); return 0;
 }
}
