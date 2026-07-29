// Producer source for the migrated il-delegobj case (#1). A BCL-style delegate whose Invoke takes `object`
// (SendOrPostCallback shape) + a base with a virtual taking that delegate (SynchronizationContext.Post shape):
// dll2klib surfaces the delegate as a function type `(Any?) -> Unit` so the natural Kotlin override matches. Own ns.
namespace Delegobj {
    public delegate void PostCb(object state);
    public class Ctx {
        public virtual void Post(PostCb cb, object state) { cb(state); }
    }
}
