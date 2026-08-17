public sealed class GlobalProbe
{
    public int Value => 1;
}

public static class GlobalWidgetExtensions
{
    public static int GlobalBump(this Probe.Widget widget, int value) => widget.Add(value) + 2;
}
