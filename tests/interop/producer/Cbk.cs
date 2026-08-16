// Producer source for the migrated il-cbk case. A custom .NET delegate + a BCL Action parameter — a Kotlin lambda
// binds to BOTH (the backend builds the specific delegate from the call-site signature). Own namespace.
namespace Cbk {
    public delegate string Transform(int x);
    public delegate string GenericTransform<T>(T x);
    public delegate object GenericResult<T>(T x);
    public interface IMarker { }
    public sealed class Marker : IMarker { }
    public sealed class Constrained<T> where T : IMarker {
        public Constrained(T value) { Value = value; }
        public T Value { get; }
    }
    public delegate object ConstrainedResult<T>(Constrained<T> x) where T : IMarker;
    public interface IBox<T> { }
    public sealed class StringBox : IBox<string> { }
    public delegate object DependentResult<T, U>(T x) where T : IBox<U>;
    public interface ICallbackEngine {
        string Apply(int v, Transform t) => "default:" + t(v);
        string Describe(int v) => "default-ref:" + v;
    }
    public interface IGenericCallbackEngine<T> {
        string Apply(T value, GenericTransform<T> transform) => "generic-default:" + transform(value);
        string Describe(T value) => "generic-ref:" + value;
    }
    public static class GenericCallbacks {
        public static string Use<T>(GenericTransform<T> transform, T value) => transform(value);
        public static object UseResult<T>(GenericResult<T> transform, T value) => transform(value);
        public static object UseConstrained<T>(ConstrainedResult<T> transform, Constrained<T> value)
            where T : IMarker => transform(value);
        public static object UseDependent<T, U>(DependentResult<T, U> transform, T value)
            where T : IBox<U> => transform(value);
    }
    public class Engine : ICallbackEngine {
        public string Apply(int v, Transform t) => "=" + t(v);
        public void Run(System.Action a) { a(); }
    }
}

// Void-to-value delegate adaptation shapes. Each of these declares an `Invoke` that RETURNS while the Kotlin
// lambda filling it is Unit-valued, which is the mismatch bir2cir reconciles with an adapter (#400 §7): arity 0
// and arity 2, a GENERIC OWNER whose parameter is constrained, plus the transpose — a value-returning lambda
// meeting a `void` Invoke, where no value has to be produced and the construction is merely retargeted.
namespace CbkUnit {
    using Cbk;
    public delegate object NullaryResult();
    public delegate object BinaryResult<T>(T first, string second);
    public delegate void IntSink(int value);
    public sealed class ConstrainedHost<T> where T : IMarker {
        public object Use(ConstrainedResult<T> transform, Constrained<T> value) => transform(value);
    }
    // A BYREF-LIKE Invoke parameter. `Action<Span<int>>` is a legal delegate because the family's parameter carries
    // the `allows ref struct` anti-constraint, so anything standing for that parameter must admit it too.
    public delegate object SpanResult(Span<int> value);
    public static class UnitCallbacks {
        public static object UseNullary(NullaryResult transform) => transform();
        public static object UseBinary<T>(BinaryResult<T> transform, T first, string second)
            => transform(first, second);
        public static string UseSink(IntSink sink) { sink(7); return "sunk"; }
        public static object UseSpan(SpanResult transform, int[] values) => transform(values.AsSpan());
        public static int SpanTotal(Span<int> values) { var total = 0; foreach (var v in values) total += v; return total; }
    }
}
