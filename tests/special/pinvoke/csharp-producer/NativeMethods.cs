using System.Runtime.InteropServices;

namespace ClrPInvoke;

public static class NativeMethods
{
    [DllImport("dotkt_pinvoke_probe", EntryPoint = "add_i32", CallingConvention = CallingConvention.Cdecl)]
    public static extern int Add(int left, int right);

    [DllImport("dotkt_pinvoke_probe", EntryPoint = "increment_i32")]
    public static extern void Increment(ref int value);
}
