// Prints the SHAPE of every KFunc`N / KAction`N type an assembly DEFINES (TypeDef rows), one line per type,
// sorted. `strings` cannot answer even the first question: a merely-REFERENCED type puts its name in the same
// #Strings heap, which is exactly the distinction #220 turns on — an app must reference the stdlib's canonical
// family, never define one. The line carries the whole ABI-relevant shape (attributes, generic parameter variance,
// the Invoke signature, the applied custom attributes), so comparing the two stdlib twins line-for-line compares
// what they actually expose rather than a set of names.
// Run as a file-based app: `dotnet run <this file> <assembly.dll>`.
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

if (args.Length != 1)
{
    Console.Error.WriteLine("usage: delegate-typedefs.cs <assembly.dll>");
    return 2;
}

using var stream = File.OpenRead(args[0]);
using var pe = new PEReader(stream, PEStreamOptions.PrefetchMetadata);
var md = pe.GetMetadataReader();

string TypeName(EntityHandle h)
{
    switch (h.Kind)
    {
        case HandleKind.TypeReference:
            var r = md.GetTypeReference((TypeReferenceHandle)h);
            var rns = md.GetString(r.Namespace);
            return rns.Length == 0 ? md.GetString(r.Name) : rns + "." + md.GetString(r.Name);
        case HandleKind.TypeDefinition:
            var d = md.GetTypeDefinition((TypeDefinitionHandle)h);
            var dns = md.GetString(d.Namespace);
            return dns.Length == 0 ? md.GetString(d.Name) : dns + "." + md.GetString(d.Name);
        default:
            return h.Kind.ToString();
    }
}

string AttributeNames(CustomAttributeHandleCollection attrs)
{
    var names = new List<string>();
    foreach (var h in attrs)
    {
        var ctor = md.GetCustomAttribute(h).Constructor;
        var owner = ctor.Kind switch
        {
            HandleKind.MemberReference => TypeName(md.GetMemberReference((MemberReferenceHandle)ctor).Parent),
            HandleKind.MethodDefinition => TypeName(md.GetMethodDefinition((MethodDefinitionHandle)ctor).GetDeclaringType()),
            _ => ctor.Kind.ToString(),
        };
        names.Add(owner);
    }
    names.Sort(StringComparer.Ordinal);
    return string.Join("+", names);
}

var lines = new List<string>();
foreach (var handle in md.TypeDefinitions)
{
    var def = md.GetTypeDefinition(handle);
    var name = md.GetString(def.Name);
    if (!name.StartsWith("KFunc`", StringComparison.Ordinal) && !name.StartsWith("KAction`", StringComparison.Ordinal))
        continue;
    var ns = md.GetString(def.Namespace);
    var full = ns.Length == 0 ? name : ns + "." + name;

    var gps = new List<string>();
    foreach (var gh in def.GetGenericParameters())
    {
        var gp = md.GetGenericParameter(gh);
        var variance = (gp.Attributes & GenericParameterAttributes.VarianceMask) switch
        {
            GenericParameterAttributes.Covariant => "out ",
            GenericParameterAttributes.Contravariant => "in ",
            _ => "",
        };
        gps.Add(variance + md.GetString(gp.Name));
    }

    var members = new List<string>();
    foreach (var mh in def.GetMethods())
    {
        var m = md.GetMethodDefinition(mh);
        var sig = m.DecodeSignature(new Provider(), default);
        members.Add($"{md.GetString(m.Name)}({string.Join(",", sig.ParameterTypes)}):{sig.ReturnType}" +
                    $"[{m.Attributes}|{m.ImplAttributes}]");
    }
    members.Sort(StringComparer.Ordinal);

    lines.Add($"{full}<{string.Join(",", gps)}> base={TypeName(def.BaseType)} attrs={def.Attributes} " +
              $"cattrs={AttributeNames(def.GetCustomAttributes())} members={string.Join(" ", members)}");
}
lines.Sort(StringComparer.Ordinal);
foreach (var line in lines) Console.WriteLine(line);
return 0;

sealed class Provider : ISignatureTypeProvider<string, object?>
{
    public string GetArrayType(string e, ArrayShape s) => e + "[]";
    public string GetByReferenceType(string e) => e + "&";
    public string GetFunctionPointerType(MethodSignature<string> s) => "fnptr";
    public string GetGenericInstantiation(string g, System.Collections.Immutable.ImmutableArray<string> a) => g + "<" + string.Join(",", a) + ">";
    public string GetGenericMethodParameter(object? c, int i) => "!!" + i;
    public string GetGenericTypeParameter(object? c, int i) => "!" + i;
    public string GetModifiedType(string m, string u, bool isRequired) => u;
    public string GetPinnedType(string e) => e;
    public string GetPointerType(string e) => e + "*";
    public string GetPrimitiveType(PrimitiveTypeCode c) => c.ToString();
    public string GetSZArrayType(string e) => e + "[]";
    public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte k)
    {
        var d = r.GetTypeDefinition(h);
        var ns = r.GetString(d.Namespace);
        return ns.Length == 0 ? r.GetString(d.Name) : ns + "." + r.GetString(d.Name);
    }
    public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte k)
    {
        var t = r.GetTypeReference(h);
        var ns = r.GetString(t.Namespace);
        return ns.Length == 0 ? r.GetString(t.Name) : ns + "." + r.GetString(t.Name);
    }
    public string GetTypeFromSpecification(MetadataReader r, object? c, TypeSpecificationHandle h, byte k) =>
        r.GetTypeSpecification(h).DecodeSignature(this, c);
}
