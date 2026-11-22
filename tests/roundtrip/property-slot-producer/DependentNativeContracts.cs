namespace RoundtripDependentNative;

public interface IMarker<Tag> { }
public static class Family { public interface IMark<Tag> { } }
public interface IJoined<T> where T : Family.IMark<string>, IMarker<string> { }
public class Base { }
public class Selector
{
    public int Visits { get; private set; }
    protected virtual T Visit<T>(T item, ref int count) where T : Base, IMarker<string>
    { count = ++Visits; return item; }
    public virtual T Choose<T>(T first, T last) where T : Base, IMarker<string> => first;
    public virtual T Choose<T>(T first, T last, ref int visits) where T : Base, IMarker<string>
    { visits++; return first; }
}
public sealed class Owner<Tag, T> where T : IMarker<Tag> { }
