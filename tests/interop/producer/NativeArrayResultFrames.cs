namespace NativeArrayResultFrames;

public sealed class Owner<T>
{
    private readonly T[] values;
    public Owner(T value) => values = new[] { value };
    public T[] Values => values;
    public T[] Read() => values;
    public U[] Echo<U>(U[] input) => input;
}

public static class Boundary
{
    public static T[] Create<T>(T value) => new[] { value };
    public static bool Same<T>(T[] left, T[] right) => ReferenceEquals(left, right);
    public static T Element<T>(T[] values) => values[0];
}
