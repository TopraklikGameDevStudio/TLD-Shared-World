using System.Reflection.Metadata; using System.Reflection.PortableExecutable;
// refcheck <mod.dll> <refsDir> : list member refs of mod into game assemblies that don't exist (name+signature) in refsDir
var mod=new PEReader(File.OpenRead(args[0])).GetMetadataReader();
var mp=new SigProv(mod);
var targets=new Dictionary<string,(MetadataReader md,Dictionary<string,HashSet<string>> members)>();
(MetadataReader, Dictionary<string,HashSet<string>>) Load(string asm){
  if(targets.TryGetValue(asm,out var t)) return t;
  var p=Path.Combine(args[1],asm+".dll"); if(!File.Exists(p)) {targets[asm]=(null,null); return (null,null);}
  var md=new PEReader(File.OpenRead(p)).GetMetadataReader(); var sp=new SigProv(md); var d=new Dictionary<string,HashSet<string>>();
  foreach(var th in md.TypeDefinitions){var td=md.GetTypeDefinition(th); var key=Full(md,th); var set=new HashSet<string>();
   foreach(var mh in td.GetMethods()){var m=md.GetMethodDefinition(mh);var s=m.DecodeSignature(sp,null);set.Add(md.GetString(m.Name)+"("+string.Join(",",s.ParameterTypes)+")"+s.ReturnType);}
   foreach(var fh in td.GetFields()){var f=md.GetFieldDefinition(fh);set.Add("F:"+md.GetString(f.Name)+":"+f.DecodeSignature(sp,null));}
   d[key]=set;}
  targets[asm]=(md,d); return (md,d);}
string Full(MetadataReader md,TypeDefinitionHandle h){var t=md.GetTypeDefinition(h);var n=md.GetString(t.Name);var dt=t.GetDeclaringType();if(!dt.IsNil)return Full(md,dt)+"/"+n;return md.GetString(t.Namespace)+"."+n;}
(string asm,string type) Resolve(TypeReferenceHandle h){var tr=mod.GetTypeReference(h);var n=mod.GetString(tr.Name);
  if(tr.ResolutionScope.Kind==HandleKind.TypeReference){var (a,pt)=Resolve((TypeReferenceHandle)tr.ResolutionScope);return (a,pt+"/"+n);}
  if(tr.ResolutionScope.Kind==HandleKind.AssemblyReference){var ar=mod.GetAssemblyReference((AssemblyReferenceHandle)tr.ResolutionScope);return (mod.GetString(ar.Name),mod.GetString(tr.Namespace)+"."+n);}
  return ("?",n);}
var missing=new SortedSet<string>(); var missingTypes=new SortedSet<string>();
foreach(var trh in mod.TypeReferences){var (a,t)=Resolve(trh); var (md,d)=Load(a); if(md==null) continue; if(!d.ContainsKey(t)) missingTypes.Add(a+" "+t);}
foreach(var mrh in mod.MemberReferences){var mr=mod.GetMemberReference(mrh); string asm,type;
  if(mr.Parent.Kind==HandleKind.TypeReference){(asm,type)=Resolve((TypeReferenceHandle)mr.Parent);}
  else if(mr.Parent.Kind==HandleKind.TypeSpecification){var s=mod.GetTypeSpecification((TypeSpecificationHandle)mr.Parent).DecodeSignature(mp,null); continue;}
  else continue;
  var (md,d)=Load(asm); if(md==null) continue; var name=mod.GetString(mr.Name); string key;
  if(mr.GetKind()==MemberReferenceKind.Method){var s=mr.DecodeMethodSignature(mp,null);key=name+"("+string.Join(",",s.ParameterTypes)+")"+s.ReturnType;}
  else key="F:"+name+":"+mr.DecodeFieldSignature(mp,null);
  if(!d.TryGetValue(type,out var set)){missing.Add($"[{asm}] {type} :: {key}  (TYPE MISSING)");continue;}
  if(!set.Contains(key)) missing.Add($"[{asm}] {type} :: {key}");}
foreach(var t in missingTypes) Console.WriteLine("TYPE "+t); foreach(var m in missing) Console.WriteLine(m);
class SigProv:ISignatureTypeProvider<string,object>{MetadataReader md;public SigProv(MetadataReader m){md=m;}
public string GetArrayType(string e,ArrayShape s)=>e+"[,]";public string GetByReferenceType(string e)=>"ref "+e;public string GetFunctionPointerType(MethodSignature<string> s)=>"fnptr";
public string GetGenericInstantiation(string g,System.Collections.Immutable.ImmutableArray<string> a)=>g+"<"+string.Join(",",a)+">";public string GetGenericMethodParameter(object c,int i)=>"!!"+i;public string GetGenericTypeParameter(object c,int i)=>"!"+i;
public string GetModifiedType(string m,string u,bool r)=>u;public string GetPinnedType(string e)=>e;public string GetPointerType(string e)=>e+"*";public string GetPrimitiveType(PrimitiveTypeCode t)=>t.ToString();
public string GetSZArrayType(string e)=>e+"[]";
public string GetTypeFromDefinition(MetadataReader r,TypeDefinitionHandle h,byte k){var t=r.GetTypeDefinition(h);var dt=t.GetDeclaringType();var n=r.GetString(t.Name);if(!dt.IsNil)return GetTypeFromDefinition(r,dt,k)+"/"+n;return r.GetString(t.Namespace)+"."+n;}
public string GetTypeFromReference(MetadataReader r,TypeReferenceHandle h,byte k){var t=r.GetTypeReference(h);var n=r.GetString(t.Name);if(t.ResolutionScope.Kind==HandleKind.TypeReference)return GetTypeFromReference(r,(TypeReferenceHandle)t.ResolutionScope,k)+"/"+n;return r.GetString(t.Namespace)+"."+n;}
public string GetTypeFromSpecification(MetadataReader r,object c,TypeSpecificationHandle h,byte k)=>r.GetTypeSpecification(h).DecodeSignature(this,c);}
