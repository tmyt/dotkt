#nullable enable

namespace SlotTableInterop;

public interface IOverloaded<T>
{
    int Measure(string? value);
    int Measure(T? value);
}

public class ConstraintBase;

public sealed class ConstraintLeaf : ConstraintBase;

public interface IConstrainedDefault<T> where T : ConstraintBase
{
    string Describe<U>() where U : T => "clr-default";
}

public interface IReturnBase
{
    object Read();
}

public interface IReturnDerived : IReturnBase
{
    new string Read();
}
