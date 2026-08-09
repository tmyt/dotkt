using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using System.Text.RegularExpressions;

if (args.Length != 2)
    throw new ArgumentException("usage: StaticExtensionInspector <producer.dll> <consumer.dll>");

var producerPath = Path.GetFullPath(args[0]);
var consumerPath = Path.GetFullPath(args[1]);
var producer = AssemblyLoadContext.Default.LoadFromAssemblyPath(producerPath);
var consumer = AssemblyLoadContext.Default.LoadFromAssemblyPath(consumerPath);

const string extensionAttribute = "System.Runtime.CompilerServices.ExtensionAttribute";
const string markerAttribute = "System.Runtime.CompilerServices.ExtensionMarkerAttribute";
var groupPattern = new Regex("^<G>\\$[0-9A-F]{32}(?:`[0-9]+)?$", RegexOptions.CultureInvariant);
var markerPattern = new Regex("^<M>\\$[0-9A-F]{32}$", RegexOptions.CultureInvariant);
var oneByteOpCodes = BuildOpCodes(twoByte: false);
var twoByteOpCodes = BuildOpCodes(twoByte: true);

var alpha = RequireContainer("CSharp14StaticExtensions.AlphaExtensions", "CSharp14StaticExtensions.Alpha");
var beta = RequireContainer("CSharp14StaticExtensions.BetaExtensions", "CSharp14StaticExtensions.Beta");
var generic = RequireContainer(
    "CSharp14StaticExtensions.GenericTargetExtensions",
    "CSharp14StaticExtensions.GenericTarget`1");

RequireImplementations(alpha.Container, "Answer", "get_Label", "get_Mutable", "set_Mutable", "Select");
RequireImplementations(beta.Container, "Answer");
RequireImplementations(generic.Container, "TypeName");

RequireProperty(alpha.Group, "Label", canWrite: false);
RequireProperty(alpha.Group, "Mutable", canWrite: true);
Require(alpha.Container.GetProperties(BindingFlags.Public | BindingFlags.Static).Length == 0,
    "implementation container unexpectedly owns Property rows");
Require(alpha.Group.GetMethods(BindingFlags.Public | BindingFlags.Static).Count(m => m.Name == "Select") == 2,
    "extension overload declarations were not preserved");

var implementationGeneric = generic.Container.GetMethod("TypeName", BindingFlags.Public | BindingFlags.Static)
    ?? throw new InvalidDataException("missing generic implementation method");
Require(implementationGeneric.IsGenericMethodDefinition, "generic extension implementation lost its method parameter");
var implementationT = implementationGeneric.GetGenericArguments().Single();
Require((implementationT.GenericParameterAttributes & GenericParameterAttributes.ReferenceTypeConstraint) != 0,
    "generic extension implementation lost its class constraint");
Require(implementationT.GetGenericParameterConstraints().Single().GetGenericTypeDefinition() == typeof(IComparable<>),
    "generic extension implementation lost IComparable<T> constraint");
Require(generic.Group.GetGenericArguments().Length == 1,
    "generic extension grouping type did not capture the extension block parameter");

var producerCalls = ReadCallTargets(consumer)
    .Where(m => m.DeclaringType?.Assembly == producer)
    .ToArray();
var expectedCalls = new[]
{
    "AlphaExtensions.set_Mutable",
    "AlphaExtensions.Answer",
    "BetaExtensions.Answer",
    "AlphaExtensions.get_Mutable",
    "AlphaExtensions.Select",
    "AlphaExtensions.get_Label",
    "GenericTargetExtensions.TypeName",
};
foreach (var expected in expectedCalls)
    Require(producerCalls.Any(m => $"{m.DeclaringType!.Name}.{m.Name}" == expected),
        $"consumer IL does not call implementation {expected}");
Require(producerCalls.Count(m => m.DeclaringType == alpha.Container && m.Name == "Select") == 2,
    "consumer IL did not bind both Select overloads to their implementation methods");
Require(producerCalls.All(m => m.DeclaringType is { IsNested: false }),
    "consumer IL calls a signature-only extension grouping member");

Console.WriteLine("C# 14 static extension ABI metadata and call targets OK");
return;

(Type Container, Type Group, Type Marker) RequireContainer(string containerName, string receiverName)
{
    var container = producer.GetType(containerName, throwOnError: true)!;
    Require(container.Attributes ==
        (TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit),
        $"{containerName} has unexpected TypeDef flags: {container.Attributes}");
    Require(HasAttribute(container, extensionAttribute), $"{containerName} lacks [Extension]");

    var groups = container.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
        .Where(t => groupPattern.IsMatch(t.Name))
        .ToArray();
    Require(groups.Length == 1, $"{containerName} does not have exactly one extension grouping type");
    var group = groups[0];
    Require(group.Attributes == (TypeAttributes.NestedPublic | TypeAttributes.Sealed | TypeAttributes.SpecialName),
        $"{group.FullName} has unexpected grouping TypeDef flags: {group.Attributes}");
    Require(HasAttribute(group, extensionAttribute), $"{group.FullName} lacks [Extension]");
    Require(group.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Length == 0,
        $"{group.FullName} unexpectedly has a constructor");

    var markers = group.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
        .Where(t => markerPattern.IsMatch(t.Name))
        .ToArray();
    Require(markers.Length == 1, $"{group.FullName} does not have exactly one receiver marker type");
    var marker = markers[0];
    Require(marker.Attributes ==
        (TypeAttributes.NestedPublic | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.SpecialName),
        $"{marker.FullName} has unexpected marker TypeDef flags: {marker.Attributes}");
    Require(marker.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Length == 0,
        $"{marker.FullName} unexpectedly has a constructor");
    var extensionMethod = marker.GetMethod("<Extension>$", BindingFlags.Public | BindingFlags.Static)
        ?? throw new InvalidDataException($"{marker.FullName} lacks <Extension>$");
    Require(extensionMethod.Attributes ==
        (MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig | MethodAttributes.SpecialName)
        && extensionMethod.ReturnType == typeof(void),
        $"{marker.FullName}.<Extension>$ has unexpected flags or return type");
    Require(HasAttribute(extensionMethod, "System.Runtime.CompilerServices.CompilerGeneratedAttribute"),
        $"{marker.FullName}.<Extension>$ lacks [CompilerGenerated]");
    var receiver = extensionMethod.GetParameters().Single().ParameterType;
    var receiverDefinition = receiver.IsGenericType ? receiver.GetGenericTypeDefinition() : receiver;
    Require(receiverDefinition.FullName == receiverName,
        $"{marker.FullName}.<Extension>$ receiver is {receiver}, expected {receiverName}");

    foreach (var declaration in group.GetMethods(BindingFlags.Public | BindingFlags.Static))
    {
        var expectedFlags = MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig;
        if (declaration.Name.StartsWith("get_", StringComparison.Ordinal)
            || declaration.Name.StartsWith("set_", StringComparison.Ordinal))
            expectedFlags |= MethodAttributes.SpecialName;
        Require(declaration.Attributes == expectedFlags,
            $"{group.FullName}.{declaration.Name} has unexpected MethodDef flags: {declaration.Attributes}");
        var observedMarker = MarkerName(declaration);
        Require(observedMarker == marker.Name,
            $"{group.FullName}.{declaration.Name} has ExtensionMarker target '{observedMarker}', expected '{marker.Name}'");
        var body = declaration.GetMethodBody()?.GetILAsByteArray();
        // Roslyn has emitted both `ldnull; throw` and `newobj <exception>; throw` for this released signature-only
        // declaration across .NET SDK builds. Debug sequence points may add nop instructions around either form. The
        // ABI invariant is that the declaration is an unconditional throw stub, not one compiler-version byte string.
        Require(IsSignatureThrowStub(body),
            $"{group.FullName}.{declaration.Name} is not a signature-only throw stub " +
            $"(IL={Convert.ToHexString(body ?? [])})");
    }
    return (container, group, marker);
}

void RequireImplementations(Type container, params string[] names)
{
    var methods = container.GetMethods(BindingFlags.Public | BindingFlags.Static);
    foreach (var name in names)
        Require(methods.Any(m => m.Name == name), $"{container.FullName} lacks implementation method {name}");
    foreach (var method in methods)
    {
        var expectedFlags = MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig;
        Require(method.Attributes == expectedFlags,
            $"{container.FullName}.{method.Name} has unexpected MethodDef flags: {method.Attributes}");
    }
    Require(methods.All(m => !HasAttribute(m, markerAttribute)),
        $"{container.FullName} implementation method incorrectly carries [ExtensionMarker]");
}

void RequireProperty(Type group, string name, bool canWrite)
{
    var property = group.GetProperty(name, BindingFlags.Public | BindingFlags.Static)
        ?? throw new InvalidDataException($"missing grouping property {group.FullName}.{name}");
    Require(property.CanRead && property.CanWrite == canWrite,
        $"grouping property {group.FullName}.{name} has the wrong accessor shape");
    var expectedMarker = group.GetNestedTypes().Single(t => markerPattern.IsMatch(t.Name)).Name;
    Require(MarkerName(property) == expectedMarker, $"grouping property {group.FullName}.{name} lacks marker");
    foreach (var accessor in property.GetAccessors(nonPublic: true))
        Require(MarkerName(accessor) == expectedMarker,
            $"grouping property accessor {group.FullName}.{accessor.Name} lacks marker");
}

string? MarkerName(MemberInfo member) => member.CustomAttributes
    .SingleOrDefault(a => a.AttributeType.FullName == markerAttribute)?
    .ConstructorArguments.Single().Value as string;

static bool HasAttribute(MemberInfo member, string fullName) =>
    member.CustomAttributes.Any(a => a.AttributeType.FullName == fullName);

static bool IsSignatureThrowStub(byte[]? body)
{
    if (body is null) return false;
    var offset = 0;
    while (offset < body.Length && body[offset] == 0x00) offset++; // nop
    if (offset < body.Length && body[offset] == 0x14)              // ldnull
        offset++;
    else if (offset + 5 <= body.Length && body[offset] == 0x73)   // newobj <4-byte metadata token>
        offset += 5;
    else
        return false;
    while (offset < body.Length && body[offset] == 0x00) offset++;
    if (offset >= body.Length || body[offset++] != 0x7A) return false; // throw
    while (offset < body.Length && body[offset] == 0x00) offset++;
    return offset == body.Length;
}

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidDataException(message);
}

IEnumerable<MethodBase> ReadCallTargets(Assembly assembly)
{
    foreach (var type in assembly.GetTypes())
    foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
    {
        var body = method.GetMethodBody();
        if (body is null)
            continue;
        var il = body.GetILAsByteArray()!;
        for (var offset = 0; offset < il.Length;)
        {
            var op = ReadOpCode(il, ref offset);
            if (op.OperandType == OperandType.InlineMethod)
            {
                var token = BitConverter.ToInt32(il, offset);
                if (op == OpCodes.Call || op == OpCodes.Callvirt)
                {
                    MethodBase? target = null;
                    try
                    {
                        target = method.Module.ResolveMethod(
                            token,
                            type.IsGenericType ? type.GetGenericArguments() : null,
                            method.IsGenericMethod ? method.GetGenericArguments() : null);
                    }
                    catch (ArgumentException)
                    {
                        // The fixture only asserts resolvable producer calls; unrelated framework tokens may remain unresolved.
                    }
                    if (target is not null)
                        yield return target;
                }
            }
            offset += OperandSize(op.OperandType, il, offset);
        }
    }
}

OpCode ReadOpCode(byte[] il, ref int offset)
{
    var first = il[offset++];
    if (first != 0xFE)
        return oneByteOpCodes[first];
    return twoByteOpCodes[il[offset++]];
}

static int OperandSize(OperandType type, byte[] il, int offset) => type switch
{
    OperandType.InlineNone => 0,
    OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
    OperandType.InlineVar => 2,
    OperandType.InlineI or OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineMethod
        or OperandType.InlineSig or OperandType.InlineString or OperandType.InlineTok or OperandType.InlineType
        or OperandType.ShortInlineR => 4,
    OperandType.InlineI8 or OperandType.InlineR => 8,
    OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, offset),
    _ => throw new InvalidDataException($"unsupported IL operand type {type}"),
};

static OpCode[] BuildOpCodes(bool twoByte)
{
    var table = new OpCode[256];
    foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
    {
        if (field.GetValue(null) is not OpCode op)
            continue;
        var value = unchecked((ushort)op.Value);
        if (twoByte == (value > byte.MaxValue))
            table[value & byte.MaxValue] = op;
    }
    return table;
}
