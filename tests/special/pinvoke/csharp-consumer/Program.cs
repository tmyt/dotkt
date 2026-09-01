if (NativeMethodsKt.add(20, 22) != 42)
    throw new InvalidOperationException("C# could not invoke the DotKt-declared primitive P/Invoke method");
var value = 9;
NativeMethodsKt.increment(ref value);
if (value != 10)
    throw new InvalidOperationException("C# could not invoke the DotKt-declared byref P/Invoke method");
if (NativeMethodsKt.observedLastError(2345) != 2345)
    throw new InvalidOperationException("C# did not observe the native last-error value through the DotKt declaration");
if (NativeMethodsKt.echoIntPtr(new IntPtr(123)) != new IntPtr(123))
    throw new InvalidOperationException("C# could not invoke the DotKt-declared IntPtr P/Invoke method");
if (NativeMethodsKt.echoUIntPtr(new UIntPtr(456)) != new UIntPtr(456))
    throw new InvalidOperationException("C# could not invoke the DotKt-declared UIntPtr P/Invoke method");
Console.WriteLine("C# consumes DotKt P/Invoke: OK");
