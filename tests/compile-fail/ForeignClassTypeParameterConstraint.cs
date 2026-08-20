namespace ClassConstraintInterop;

public interface Sink<T>
{
    string Accept(T value);
}

public sealed class NotSink
{
}

public sealed class Box<T> where T : Sink<string>
{
}
