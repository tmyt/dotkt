using roundtrip.genericvalueinterop;
using System.Collections.Generic;

namespace GenericValueInterop;

public static class NativeBoxApi
{
    private static Box<string> text = new("initial");
    private static Box<int> number = new(17);
    private static Box<Box<string>> nested = new(new Box<string>("nested initial"));
    private static IReadOnlyList<string> names = new List<string> { "initial" };
    private static Box<string>[] boxes = { new("initial") };
    public static ref Box<string> TextReference() => ref text;
    public static ref Box<int> NumberReference() => ref number;
    public static ref Box<Box<string>> NestedReference() => ref nested;
    public static ref Box<string> EchoReference(ref Box<string> value) => ref value;
    public static Box<string> ReadText() => text;
    public static Box<int> ReadNumber() => number;
    public static Box<Box<string>> ReadNested() => nested;
    public static void ResetText(string value) => text = new Box<string>(value);
    public static ref IReadOnlyList<string> NamesReference() => ref names;
    public static IReadOnlyList<string> CreateNames(string value) => new List<string> { value };
    public static IReadOnlyList<string> ReadNames() => names;
    public static string ReadFirstName() => names[0];
    public static void ReplaceNamesThroughKotlin(IReadOnlyList<string> replacement) =>
        GenericValueInteropKt.replaceNamesRef(ref names, replacement);
    public static ref Box<string>[] BoxesReference() => ref boxes;
    public static Box<string>[] CreateBoxes(string value) => new[] { new Box<string>(value) };
    public static Box<string>[] ReadBoxes() => boxes;

    public static bool ReplaceAliased(ref Box<string> first, ref Box<string> second)
    {
        first = new Box<string>("first");
        bool observedFirstWrite = object.ReferenceEquals(first, second);
        second = new Box<string>("second");
        return observedFirstWrite && object.ReferenceEquals(first, second);
    }
}

public sealed class NativeSlot<T>
{
    private T value;
    public NativeSlot(T value) => this.value = value;
    public ref T Reference() => ref value;
    public void Replace(T replacement) => value = replacement;
}
