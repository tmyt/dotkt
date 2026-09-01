using System.Runtime.InteropServices;

namespace ClrPInvoke;

public static class NativeMethods
{
    [DllImport("dotkt_pinvoke_probe", EntryPoint = "add_i32", CallingConvention = CallingConvention.Cdecl)]
    public static extern int Add(int left, int right);

    [DllImport("dotkt_pinvoke_probe", EntryPoint = "increment_i32")]
    public static extern void Increment(ref int value);
}

[AttributeUsage(AttributeTargets.Class)]
public sealed class ProjectionProbeAttribute : Attribute
{
    public ProjectionProbeAttribute(int Value) { }
    public int Value;
    public int[] Values { get; set; } = [];
}

[AttributeUsage(AttributeTargets.Class)]
public sealed class OverloadProbeAttribute : Attribute
{
    public OverloadProbeAttribute(int Value) { }
    public OverloadProbeAttribute() { }
    public int Value;
}
