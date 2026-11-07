using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NativePointers;

public static unsafe class Api
{
    public static delegate* managed<int, int> Value;
    public static delegate* managed<int, int> Property { get; set; }
    public static delegate* managed<int, int>[]? Array;
    public static delegate* managed<int, int>* Nested;
    public static delegate* managed<string?, string> NullableSignature;
    public static delegate* managed<int, int> Get() => &Twice;
    public static delegate* managed<int, int> Echo(delegate* managed<int, int> p) => p;
    public static int Call(delegate* managed<int, int> p, int value) => p(value);
    public static delegate* unmanaged[Cdecl]<int, int> GetCdecl() => &NativeTwice;
    public static delegate* unmanaged[Cdecl]<int, int> EchoCdecl(delegate* unmanaged[Cdecl]<int, int> p) => p;
    public static int CallCdecl(delegate* unmanaged[Cdecl]<int, int> p, int value) => p(value);
    public static delegate* unmanaged[Cdecl, SuppressGCTransition]<int, int> Modified(
        delegate* unmanaged[Cdecl, SuppressGCTransition]<int, int> p) => p;
    public static delegate* managed<delegate* unmanaged[Stdcall]<int, int>, int> NestedEcho(
        delegate* managed<delegate* unmanaged[Stdcall]<int, int>, int> p) => p;
    static int Twice(int value) => value * 2;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int NativeTwice(int value) => value * 2;
}

public unsafe class GenericApi<T>
{
    public delegate* managed<T, U, U> Echo<U>(delegate* managed<T, U, U> p) => p;
    public static delegate* managed<T, T> Value;
}
