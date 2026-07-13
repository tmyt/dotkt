namespace Kfc {
    // A BCL-style delegate whose Invoke takes `object` (mirrors System.Threading.SendOrPostCallback(object state)).
    public delegate void PostCb(object state);
    // A base with a virtual taking that delegate (mirrors SynchronizationContext.Post(SendOrPostCallback, object)).
    public class Ctx {
        public virtual void Post(PostCb cb, object state) { cb(state); }
    }
}
