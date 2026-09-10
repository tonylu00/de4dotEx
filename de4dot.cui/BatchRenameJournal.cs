using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using dnlib.DotNet;
using de4dot.code;

namespace de4dot.cui {
 // Capture before deobfuscation. Read the saved image when reporting: writer tokens
 // and removed definitions must never be confused with the input metadata.
 sealed class BatchRenameJournal {
  public const string Filename = "de4dot-rename-map.xml";
  sealed class Symbol {
   public string Kind, Name, FullName, Namespace;
   public uint Token;
   public IMemberDef Definition;
   public ParamDef Parameter;
   public ushort Sequence;
  }
  sealed class Entry {
   public IObfuscatedFile File;
   public string Path, Hash, Mvid;
   public List<Symbol> Symbols = new List<Symbol>();
  }
  readonly List<Entry> entries = new List<Entry>();
  public BatchRenameJournal(IEnumerable<IObfuscatedFile> files, string root) {
   foreach(var file in files) {
    var e = new Entry {File=file, Path=file.Filename.Substring(root.Length+1), Hash=Hash(file.Filename), Mvid=file.ModuleDefMD.Mvid.ToString()};
    foreach(var t in file.ModuleDefMD.GetTypes()) {
     Add(e,"Type",t,t.Namespace);
     foreach(var m in t.Methods) {
      Add(e,"Method",m);
      foreach(var p in m.ParamDefs) e.Symbols.Add(new Symbol {Kind="Parameter",Definition=m,Parameter=p,Token=m.MDToken.Raw,Sequence=p.Sequence,Name=p.Name,FullName=m.FullName});
     }
     foreach(var f in t.Fields) Add(e,"Field",f);
     foreach(var p in t.Properties) Add(e,"Property",p);
     foreach(var v in t.Events) Add(e,"Event",v);
    }
    entries.Add(e);
   }
  }
  static void Add(Entry e,string kind,IMemberDef d,string ns=null) => e.Symbols.Add(new Symbol {Kind=kind,Definition=d,Token=d.MDToken.Raw,Name=d.Name,FullName=d.FullName,Namespace=ns});
  static string Hash(string path) { using(var s=File.OpenRead(path)) using(var h=SHA256.Create()) return BitConverter.ToString(h.ComputeHash(s)).Replace("-",""); }
  static void Text(XElement e,string key,string text) {
   text=text??"";
   // Obfuscated metadata can contain NUL and other characters forbidden in XML.
   e.SetAttributeValue(key+"Utf16",Convert.ToBase64String(Encoding.Unicode.GetBytes(text)));
   try { XmlConvert.VerifyXmlChars(text); e.SetAttributeValue(key,text); } catch(XmlException) { }
  }
  public void Save(string root,bool preserveApi) {
   var doc=new XElement("De4dotRenameMap",new XAttribute("Version",1),new XAttribute("PreservePublicApi",preserveApi),new XAttribute("Purpose","Audit and SDK migration; tokens are scoped by module path and hash"));
   foreach(var e in entries) {
    var output=Path.Combine(root,e.Path);
    using(var saved=ModuleDefMD.Load(output)) {
     var node=new XElement("Module",new XAttribute("Path",e.Path),new XAttribute("InputMvid",e.Mvid),new XAttribute("OutputMvid",saved.Mvid),new XAttribute("InputSha256",e.Hash),new XAttribute("OutputSha256",Hash(output)));
     foreach(var s in e.Symbols) {
      var d=s.Definition;
      bool removed = d is TypeDef td ? td.Module != e.File.ModuleDefMD : d.DeclaringType == null || d.DeclaringType.Module != e.File.ModuleDefMD;
      string name=s.Parameter==null ? (string)d.Name : (string)s.Parameter.Name;
      string ns=d is TypeDef type ? (string)type.Namespace : null;
      if(!removed && name==s.Name && d.FullName==s.FullName && ns==s.Namespace) continue;
      var row=new XElement(s.Kind,new XAttribute("InputToken","0x"+s.Token.ToString("X8")),new XAttribute("Status",removed?"Removed":"Changed"));
      Text(row,"OldName",s.Name); Text(row,"OldSignature",s.FullName);
      if(s.Parameter!=null) row.SetAttributeValue("Sequence",s.Sequence);
      if(!removed) {
       uint token=d.MDToken.Raw;
       var read=saved.ResolveToken(token) as IMemberDef;
       if(read==null || read.FullName!=d.FullName) throw new UserException("Rename journal could not verify saved token: "+e.Path+" "+token.ToString("X8"));
       if(s.Parameter!=null && !((MethodDef)read).ParamDefs.Any(p=>p.Sequence==s.Sequence && p.Name==name)) throw new UserException("Rename journal parameter verification failed: "+e.Path);
       row.SetAttributeValue("OutputToken","0x"+token.ToString("X8")); Text(row,"NewName",name); Text(row,"NewSignature",read.FullName);
       if(s.Namespace!=null) { Text(row,"OldNamespace",s.Namespace); Text(row,"NewNamespace",ns); }
      }
      node.Add(row);
     }
     doc.Add(node);
    }
   }
   new XDocument(doc).Save(Path.Combine(root,Filename));
  }
 }
}
