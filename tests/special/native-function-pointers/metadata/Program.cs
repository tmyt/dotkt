using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

// Independent ECMA decoder: deliberately does not link the compiler's TypeNode,
// signature vocabulary, reflection adapters or signature comparison routines.
using var referenceStream = File.OpenRead(args[0]);
using var referencePe = new PEReader(referenceStream);
var reference = referencePe.GetMetadataReader();
using var consumerStream = File.OpenRead(args[1]);
using var consumerPe = new PEReader(consumerStream);
var consumer = consumerPe.GetMetadataReader();
var decoder = new TypeText();
var declarations = new Dictionary<(string Owner, string Name), string>();
foreach (var handle in reference.TypeDefinitions)
{
    var type = reference.GetTypeDefinition(handle);
    var owner = decoder.GetTypeFromDefinition(reference, handle, 0);
    foreach (var methodHandle in type.GetMethods())
    {
        var method = reference.GetMethodDefinition(methodHandle);
        declarations[(owner, reference.GetString(method.Name))] = TypeText.Method(method.DecodeSignature(decoder, reference));
    }
    foreach (var fieldHandle in type.GetFields())
    {
        var field = reference.GetFieldDefinition(fieldHandle);
        declarations[(owner, reference.GetString(field.Name))] = field.DecodeSignature(decoder, reference);
    }
}

var seen = new HashSet<string>();
foreach (var handle in consumer.MemberReferences)
{
    var member = consumer.GetMemberReference(handle);
    var owner = member.Parent.Kind switch {
        HandleKind.TypeReference => decoder.GetTypeFromReference(consumer, (TypeReferenceHandle)member.Parent, 0),
        HandleKind.TypeSpecification => decoder.GetTypeFromSpecification(consumer, consumer, (TypeSpecificationHandle)member.Parent, 0),
        _ => "",
    };
    if (!owner.StartsWith("NativePointers.", StringComparison.Ordinal)) continue;
    var head = owner.Split('<')[0];
    var name = consumer.GetString(member.Name);
    var actual = member.GetKind() == MemberReferenceKind.Field ? member.DecodeFieldSignature(decoder, consumer)
        : TypeText.Method(member.DecodeMethodSignature(decoder, consumer));
    if (!declarations.TryGetValue((head, name), out var expected) || actual != expected)
        throw new Exception($"Native MemberRef changed: {owner}.{name}\nexpected {expected}\nactual   {actual}");
    seen.Add(head + "." + name);
}
foreach (var name in new[] { "Echo", "Get", "Call", "Value", "get_Property", "set_Property", "NullableSignature",
    "GetCdecl", "EchoCdecl", "CallCdecl", "GetArray", "EchoArray", "CallArray", "Array", "GetModified", "Modified", "GetNested", "NestedEcho", "UnitValue", "UnitVoid", "Rectangular", "DataPointer", "RectangularPointers", "Wide", "NullableValue", "EchoNullable" })
    if (!seen.Contains("NativePointers.Api." + name)) throw new Exception("Native MemberRef not exercised: " + name);
if (!seen.Contains("NativePointers.GenericApi`1.Echo")) throw new Exception("Generic native MemberRef not exercised");
foreach (var name in new[] { "Value", "get_Property", "set_Property" })
    if (!seen.Contains("NativePointers.GenericApi`1." + name)) throw new Exception("Generic native field/property not exercised: " + name);
if (!seen.Contains("NativePointers.NullableApi`1.Value")) throw new Exception("Generic CLR Nullable signature not exercised");
if (!seen.Contains("NativePointers.AnnotatedApi`1.Value")) throw new Exception("Generic NRT signature not exercised");
var annotated = consumer.MethodDefinitions.Select(consumer.GetMethodDefinition)
    .Single(method => consumer.GetString(method.Name) == "annotatedGeneric").DecodeSignature(decoder, consumer);
if (annotated.ReturnType != declarations[("NativePointers.Api", "Value")])
    throw new Exception("NRT changed the closed native generic ABI to a nullable value ABI");
Console.WriteLine("Independent native MemberRef metadata comparison: PASS");

sealed class TypeText : ISignatureTypeProvider<string, MetadataReader>
{
    public static string Method(MethodSignature<string> signature) =>
        $"method[{signature.Header.RawValue},{signature.GenericParameterCount},{signature.RequiredParameterCount}]({string.Join(";", signature.ParameterTypes)})->{signature.ReturnType}";
    public string GetFunctionPointerType(MethodSignature<string> signature) => "fp:" + Method(signature);
    public string GetArrayType(string element, ArrayShape shape) =>
        $"array[{shape.Rank};{string.Join(",", shape.Sizes)};{string.Join(",", shape.LowerBounds)}]({element})";
    public string GetSZArrayType(string element) => $"vector({element})";
    public string GetByReferenceType(string element) => $"ref({element})";
    public string GetPointerType(string element) => $"ptr({element})";
    public string GetPinnedType(string element) => $"pinned({element})";
    public string GetModifiedType(string modifier, string element, bool required) => $"{(required ? "req" : "opt")}({modifier};{element})";
    public string GetPrimitiveType(PrimitiveTypeCode code) => code.ToString();
    public string GetGenericMethodParameter(MetadataReader context, int index) => "!!" + index;
    public string GetGenericTypeParameter(MetadataReader context, int index) => "!" + index;
    public string GetGenericInstantiation(string definition, ImmutableArray<string> arguments) => definition + "<" + string.Join(";", arguments) + ">";
    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte kind)
    {
        var type = reader.GetTypeDefinition(handle);
        return type.GetDeclaringType().IsNil ? Name(reader.GetString(type.Namespace), reader.GetString(type.Name))
            : GetTypeFromDefinition(reader, type.GetDeclaringType(), kind) + "+" + reader.GetString(type.Name);
    }
    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte kind)
    {
        var type = reader.GetTypeReference(handle);
        return type.ResolutionScope.Kind == HandleKind.TypeReference
            ? GetTypeFromReference(reader, (TypeReferenceHandle)type.ResolutionScope, kind) + "+" + reader.GetString(type.Name)
            : Name(reader.GetString(type.Namespace), reader.GetString(type.Name));
    }
    public string GetTypeFromSpecification(MetadataReader reader, MetadataReader context, TypeSpecificationHandle handle, byte kind) => reader.GetTypeSpecification(handle).DecodeSignature(this, reader);
    static string Name(string ns, string name) => ns.Length == 0 ? name : ns + "." + name;
}
