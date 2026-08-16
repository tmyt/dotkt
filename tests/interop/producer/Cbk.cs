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
