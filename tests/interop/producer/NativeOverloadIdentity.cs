namespace NativeOverloadIdentity;

public interface ISlot<T>
{
    T Echo<U>(T value, U marker) where U : class => value;
    string Echo<U>(int value, U marker) where U : class => "int-overload";
}

public interface IReverseSlot<T>
{
    string Echo<U>(int value, U marker) where U : class => "reverse-int-overload";
    T Echo<U>(T value, U marker) where U : class => value;
}
