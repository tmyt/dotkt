// Producer source for the migrated il-delegatearg case. A delegate type used as a .NET CONSTRUCTOR param and as a
// .NET METHOD param — the façade erases the delegate param, so the backend recovers the real delegate type from the
// ctor/method and builds that specific delegate. Own namespace.
namespace Delegatearg {
    public delegate int Transform(int x);
    public class Box {
        private readonly Transform _f;
        public Box(Transform f) { _f = f; }                                  // delegate as CTOR param
        public int Apply(int x) => _f(x);
        public int Run(Transform g) => g(10);                                // delegate as METHOD param
    }
}
