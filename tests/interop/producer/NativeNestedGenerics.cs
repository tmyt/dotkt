using System;

namespace NativeNestedGenerics;

public sealed class Box<T>
{
    public T Value;
    public Box(T value) { Value = value; }
}

public static class Api
{
    public static bool ReplaceAliased(ref Box<string> first, ref Box<string> second)
    {
        first = new("first");
        bool same = ReferenceEquals(first, second);
        second = new("second");
        return same && ReferenceEquals(first, second);
    }
    public static Box<string> ReplaceCallback(ref Box<string> value, Action callback)
    {
        value = new("native");
        callback();
        return value;
    }
    public static Box<Box<string>> EchoBox(Box<Box<string>> value) => value;
    public static Box<Box<T>> EchoGeneric<T>(Box<Box<T>> value) => value;
    public static Tuple<Tuple<string, string>, string> EchoTuple(
        Tuple<Tuple<string, string>, string> value) => value;
    public static Tuple<Tuple<string, string>, string> MakeTuple() =>
        new(new("native-left", "native-right"), "native-outer");
    public static bool ExactBox(object value) => value.GetType() == typeof(Box<Box<string>>);
    public static bool ExactTuple(object value) =>
        value.GetType() == typeof(Tuple<Tuple<string, string>, string>);
}
