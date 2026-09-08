// Nominal CLR delegate import: a BCL-style delegate whose Invoke takes `object` (SendOrPostCallback shape), a base
// virtual using it, and two same-shape delegate identities that must remain distinguishable in Kotlin.
namespace Delegobj {
    public delegate void PostCb(object state);
    public delegate void PostCbTwin(object state);
    public delegate void RecursiveCb(RecursiveCb next);
    public delegate void ArityCb(object state);
    public delegate void ArityCb<T>(T state);
    public static class DelegateContainer {
        public delegate void NestedCb(object state);
    }
    public class Ctx {
        public virtual void Post(PostCb cb, object state) { cb(state); }
    }
    public static class SameShape {
        public static string Pick(PostCb cb) { cb("first"); return "post"; }
        public static string Pick(PostCbTwin cb) { cb("second"); return "twin"; }
    }
}
