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
