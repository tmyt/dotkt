using System;
namespace NominalFunctionSlots;
public delegate object SpanResult(Span<int> values);
public delegate Span<int> SpanTransform(Span<int> values);
public delegate Span<int> SpanFactory();
public delegate int Sink<in T>(T value);
public delegate T Write<out T>(ref int value);
public delegate ref int Address<in T>(T key);
public delegate Step<Func<T>> Step<out T>(Action<T> read);
public delegate Chain<T, K> Chain<out T, K>(Action<T> read) where K : T;
public abstract class CallbackHost {
    public abstract int Apply(Func<int, int> callback, int value);
}
public static class Callbacks {
    static readonly Func<object, int> boxedInput = value => (int)value + 1;
    public static Func<object, int> BoxedInput() => boxedInput;
    public static bool SameReference(Func<string, int> compatible) => ReferenceEquals(boxedInput, compatible);
    public static Func<int, Func<object, int>> Factory() => a => b => a + (int)b;
    public static Func<int, Func<int, int>> FactoryField;
    public static int ApplyFactory(Func<int, Func<int, int>> factory) => factory(40)(2);
    public static Func<Func<int,int>,int> Consumer() => f => f(8);
    public static int ApplyConsumer(Func<Func<object,int>,int> f) => f(boxedInput);
    public static int ApplyTyped<T>(Func<T, int> callback, T value) => callback(value);
    public static Sink<object> NativeSink() => value => 2 * (int)value;
    public static int ApplySink(Sink<int> sink) => sink(3);
    public static Write<int> Writer() => (ref int value) => ++value;
    public static bool WritesOriginal(Write<object> writer) {
        int value = 41;
        return (int)writer(ref value) == 42 && value == 42;
    }
    static int cell;
    static ref int SelectCell(object key) { cell = (int)key; return ref cell; }
    public static Address<object> AddressOf() => SelectCell;
    public static int WriteThroughResult(Address<int> address) {
        ref int result = ref address(21);
        result *= 2;
        return cell;
    }
    static Step<T> StepFrom<T>(T value) => read => { read(value); return StepFrom<Func<T>>(() => value); };
    public static Step<int> Steps() => StepFrom(14);
    public static Chain<int, int> ChainSource() => read => { read(42); return ChainSource(); };
    public static int ReadChain(Chain<object, int> chain) {
        int result = 0; _ = chain(value => result = (int)value); return result;
    }
    public static int ReadSteps(Step<object> step) {
        int total = 0;
        var next = step(value => total += (int)value);
        var third = next(read => total += (int)read());
        _ = third(read => total += (int)read()());
        return total;
    }
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
