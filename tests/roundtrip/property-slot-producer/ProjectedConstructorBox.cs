#nullable enable
namespace ProjectedConstructionInterop;

public sealed class NonNullBox<T> where T : class
{
    public T Value;
    public NonNullBox(T value) { Value = value; }
}

public interface IMarker { }
public sealed class MarkerValue<T> : IMarker { }
public sealed class ConstrainedBox<T> where T : IMarker
{
    public T Value;
    public ConstrainedBox(T value) { Value = value; }
}

#nullable disable
public sealed class ObliviousBox<T>
{
    public T Value;
    public ObliviousBox(T value) { Value = value; }
}
