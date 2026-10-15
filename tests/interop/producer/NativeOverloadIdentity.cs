namespace NativeOverloadIdentity;

public static class Selection
{
    public static string Last;
}

public interface ISlot<T>
{
    T Echo<U>(T value, U marker) where U : class { Selection.Last = "owner"; return value; }
    string Echo<U>(int value, U marker) where U : class { Selection.Last = "int"; return "int-overload"; }
    T Plain(T value) => value;
    string Plain(int value) => "plain-int-overload";
    T Optional<U>(T value, string marker = "owner-default") where U : class => value;
    string Optional<U>(int value, string marker = "int-default") where U : class => marker;
}

public interface IReverseSlot<T>
{
    string Echo<U>(int value, U marker) where U : class { Selection.Last = "reverse-int"; return "reverse-int-overload"; }
    T Echo<U>(T value, U marker) where U : class { Selection.Last = "reverse-owner"; return value; }
    string Plain(int value) => "reverse-plain-int-overload";
    T Plain(T value) => value;
    string Optional<U>(int value, string marker = "reverse-int-default") where U : class => marker;
    T Optional<U>(T value, string marker = "reverse-owner-default") where U : class => value;
}

public class ClassSlot<T>
{
    public T Echo<U>(T value, U marker) where U : class => value;
    public string Echo<U>(int value, U marker) where U : class => "class-int-overload";
}

public interface IIndexedSlot<T>
{
    T this[T value] => value;
    string this[int value] => "int-indexer";
}

public interface IReverseIndexedSlot<T>
{
    string this[int value] => "reverse-int-indexer";
    T this[T value] => value;
}

public interface ICompletedSlot<T>
{
    T Mirror(T value);
    T GenericMirror<U>(T value, U marker) where U : class;
}

internal interface IHiddenCompletedSlot : ICompletedSlot<string>
{
    string ICompletedSlot<string>.Mirror(string value) => value;
    string ICompletedSlot<string>.GenericMirror<U>(string value, U marker) => value;
}

public sealed class CompletedSlot : IHiddenCompletedSlot { }
