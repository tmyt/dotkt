using System;
namespace NominalFunctionSlots;
public delegate object SpanResult(Span<int> values);
public delegate Span<int> SpanTransform(Span<int> values);
public delegate Span<int> SpanFactory();
public abstract class CallbackHost {
    public abstract int Apply(Func<int, int> callback, int value);
}
public static class Callbacks {
    public static int Apply(Func<int, int> callback, int value) => callback(value);
    public static Func<int, int> Identity(Func<int, int> callback) => callback;
    public static int Dispatch(CallbackHost host) => host.Apply(value => value + 2, 40);
    public static void Run(Action callback) => callback();
    public static object UseSpan(SpanResult callback, int[] values) => callback(values.AsSpan());
    public static int SpanTotal(Span<int> values) { int sum = 0; foreach (int value in values) sum += value; return sum; }
    public static int TransformSpan(SpanTransform callback, int[] values) => SpanTotal(callback(values.AsSpan()));
    public static Span<int> ArraySpan(int[] values) => values.AsSpan();
    public static int ProducedSpanTotal(SpanFactory callback) => SpanTotal(callback());
}
