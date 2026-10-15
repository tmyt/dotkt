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
    string Echo<U>(int value, U marker) where U : class => "reverse-int-overload";
    T Echo<U>(T value, U marker) where U : class => value;
    string Plain(int value) => "reverse-plain-int-overload";
    T Plain(T value) => value;
}
