using System.Reflection;
using System.Runtime.Loader;

AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(args[1]));
var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(args[0]));
var facade = assembly.GetType("kotlin.SourceArrayProbeKt", throwOnError: true)!;
object? Call(string name, object value) => facade.GetMethod(name, BindingFlags.Public | BindingFlags.Static)!
    .Invoke(null, new[] { value });
void Require(bool condition, string reason)
{
    if (!condition) throw new InvalidOperationException(reason);
}

var arrays = new (string Method, Array Value)[] {
    ("sourceByteArray", new sbyte[] { -7 }), ("sourceShortArray", new short[] { -7 }),
    ("sourceIntArray", new[] { -7 }), ("sourceLongArray", new long[] { -7 }),
    ("sourceFloatArray", new[] { 1.5f }), ("sourceDoubleArray", new[] { 1.5 }),
    ("sourceCharArray", new[] { 'a' }), ("sourceBooleanArray", new[] { true }),
    ("sourceUByteArray", new byte[] { 255 }), ("sourceUShortArray", new ushort[] { 65535 }),
    ("sourceUIntArray", new[] { uint.MaxValue }), ("sourceULongArray", new[] { ulong.MaxValue }),
    ("sourceGenericStringArray", new[] { "array" }),
};
foreach (var (method, value) in arrays)
    Require(ReferenceEquals(Call(method, value), value), method + " copied or changed the native array");

var chars = new[] { 'a', 'b', 'c' };
Require((int)Call("sourceArrayNativeLength", chars)! == 3, "native Length");
Require(ReferenceEquals(Call("sourceArrayBound", chars), chars), "expression generic bound");
Require(ReferenceEquals(Call("sourceArrayExplicitCast", chars), chars), "explicit native-base cast");
Require((string)Call("sourceArrayOverload", chars)! == "array", "array overload");
Require((string)Call("sourceObjectOverload", new object())! == "object", "ordinary object overload");
var clone = (char[])Call("sourceArrayClone", chars)!;
Require(!ReferenceEquals(clone, chars) && clone.SequenceEqual(chars), "native Clone");
Call("sourceArrayClear", chars);
Require(chars.All(value => value == '\0') && clone[0] == 'a', "native Clear or independent clone");
var derived = assembly.GetType("kotlin.SourceArrayDerived", throwOnError: true)!;
Require(Activator.CreateInstance(derived, nonPublic: true) is not null, "declaration generic bound");
Console.WriteLine("source-array-runtime-ok");
