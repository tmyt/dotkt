#nullable enable

namespace SlotTableInterop;

public interface IOverloaded<T>
{
    int Measure(string? value);
    int Measure(T? value);
}

public class ConstraintBase;

public interface IEntrySlot<T> { int Take(T value); }
public class EntrySlotBase<T> { public int Clash(T value) => 2; }

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
