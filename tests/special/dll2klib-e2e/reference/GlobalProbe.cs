public sealed class GlobalProbe
{
    public int Value => 1;
}

// Together with Probe.StructConstraintBox, exercise owner constraints from two package workers.
public sealed class GlobalStructOwner<T> where T : struct
{
    public T? Optional(T value) => value;
}

public static class GlobalWidgetExtensions
{
    public static int GlobalBump(this Probe.Widget widget, int value) => widget.Add(value) + 2;
}

// Both package workers index the same CLR base during the serial/parallel KLIB comparison.
namespace OverrideProjection.First
{
    public abstract class Base { public abstract string? Describe(); }
    public sealed class Derived : Base { public override string Describe() => ""; }
}
namespace OverrideProjection.Second
{
    public sealed class Derived : First.Base { public override string Describe() => ""; }
}
