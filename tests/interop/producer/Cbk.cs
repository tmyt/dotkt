// Producer source for the migrated il-cbk case. A custom .NET delegate + a BCL Action parameter — a Kotlin lambda
// binds to BOTH (the backend builds the specific delegate from the call-site signature). Own namespace.
namespace Cbk {
    public delegate string Transform(int x);
    public class Engine {
        public string Apply(int v, Transform t) => "=" + t(v);
        public void Run(System.Action a) { a(); }
    }
}
