namespace DependentConstructionInterop;

public interface IMarker<Tag> { }

public sealed class NativeOwner<Tag, T> where T : IMarker<Tag>
{
    public T Echo(T value) => value;
}

public sealed class EnumerableOwner<Tag, T> where T : System.Collections.Generic.IEnumerable<Tag> { }

public readonly struct NativeNumber(int number) : IMarker<int>
{
    public int Number => number;
}
