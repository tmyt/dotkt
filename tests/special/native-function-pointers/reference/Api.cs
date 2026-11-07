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
    public static delegate* managed<kotlin.Unit, kotlin.Unit> UnitValue;
    public static delegate* managed<void> UnitVoid;
    public static delegate* managed<int[,], int> Rectangular;
    public static delegate* managed<int*, void> DataPointer;
    public static delegate* managed<int, int>[,]? RectangularPointers;
    public static delegate* managed<int?, int?> NullableValue;
    public static delegate* managed<int, int, int, int, int, int, int, int, int, int, int, int,
        int, int, int, int, int, int, int, int, int, int, int, int> Wide;
    public static delegate* managed<int, int> Get() => &Twice;
    public static delegate* managed<int, int> Echo(delegate* managed<int, int> p) => p;
    public static delegate* managed<int?, int?> EchoNullable(delegate* managed<int?, int?> p) => p;
    public static int Call(delegate* managed<int, int> p, int value) => p(value);
    public static delegate* unmanaged[Cdecl]<int, int> GetCdecl() => &NativeTwice;
    public static delegate* unmanaged[Cdecl]<int, int> EchoCdecl(delegate* unmanaged[Cdecl]<int, int> p) => p;
    public static int CallCdecl(delegate* unmanaged[Cdecl]<int, int> p, int value) => p(value);
    public static delegate* managed<int, int>[] GetArray() => [&Twice];
    public static delegate* managed<int, int>[] EchoArray(delegate* managed<int, int>[] p) => p;
    public static int CallArray(delegate* managed<int, int>[] p, int value) => p[0](value);
    public static int CallOverride(CallbackBase value) => value.Echo(&Twice)(21);
    public static delegate* unmanaged[Cdecl, SuppressGCTransition]<int, int> GetModified() => &ModifiedTwice;
    public static delegate* unmanaged[Cdecl, SuppressGCTransition]<int, int> Modified(
        delegate* unmanaged[Cdecl, SuppressGCTransition]<int, int> p) => p;
    public static delegate* managed<delegate* unmanaged[Stdcall]<int, int>, int> NestedEcho(
        delegate* managed<delegate* unmanaged[Stdcall]<int, int>, int> p) => p;
    public static delegate* managed<delegate* unmanaged[Stdcall]<int, int>, int> GetNested() => &IgnoreNested;
    static int IgnoreNested(delegate* unmanaged[Stdcall]<int, int> p) => 42;
    static int Twice(int value) => value * 2;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int NativeTwice(int value) => value * 2;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl), typeof(CallConvSuppressGCTransition)])]
    static int ModifiedTwice(int value) => value * 2;
}

public unsafe class GenericApi<T>
{
    public delegate* managed<T, U, U> Echo<U>(delegate* managed<T, U, U> p) => p;
    public delegate* managed<T, T> Value;
    public delegate* managed<T, T> Property { get; set; }
}

public unsafe class CallbackBase
{
    public virtual delegate* managed<int, int> Echo(delegate* managed<int, int> value) => value;
}

public unsafe class NullableApi<T> where T : struct
{
    public delegate* managed<T?, T?> Value;
}

public unsafe class AnnotatedApi<T>
{
    public delegate* managed<T?, T?> Value;
}
