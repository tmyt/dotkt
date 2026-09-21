#nullable enable
namespace InheritedNrtProbe;
public interface IValue<T> { T Value { get; } }
public class Base<T> where T : class
{
    public System.Tuple<T, string?> Value => new(default!, null);
}
public class Mix : Base<System.Tuple<string, string>>, IValue<int>
{
    int IValue<int>.Value => 17;
}
