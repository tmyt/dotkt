// ContinuationInterceptor / intercepted() + a minimal dispatcher (T3c). An interceptor is a CoroutineContext
// element that can wrap a continuation so its resume runs on a chosen executor; `intercepted()` looks the
// interceptor up in the continuation's own context and applies it. This is the seam dispatchers plug into —
// buildable standalone (a synthetic recording dispatcher here), independent of compiling upstream.
namespace DotKt.Coroutines
{
    public interface ContinuationInterceptor : Element
    {
        Continuation<T> InterceptContinuation<T>(Continuation<T> continuation);
    }

    public static class Interceptors
    {
        /// ContinuationInterceptor.Key — the context key every interceptor registers under.
        public static readonly Key<ContinuationInterceptor> Key = new KeyImpl();
        sealed class KeyImpl : Key<ContinuationInterceptor> { }

        /// kotlin.coroutines.intercepted(): wrap the continuation via its context's interceptor (identity if none).
        public static Continuation<T> Intercepted<T>(Continuation<T> cont)
        {
            var ic = cont.Context.Get(Key);
            return ic != null ? ic.InterceptContinuation(cont) : cont;
        }
    }

    // ---- test scaffolding: a dispatcher that records each dispatched resume, plus a sink that carries it ----
    public static class Recorder { static int _n; public static void Bump() => _n++; public static int Count() => _n; }

    public sealed class RecordingDispatcher : AbstractElement, ContinuationInterceptor
    {
        public RecordingDispatcher() : base(Interceptors.Key) { }
        public Continuation<T> InterceptContinuation<T>(Continuation<T> cont) => new Dispatched<T>(cont);
        sealed class Dispatched<T> : Continuation<T>
        {
            readonly Continuation<T> _inner;
            public Dispatched(Continuation<T> inner) { _inner = inner; }
            public CoroutineContext Context => _inner.Context;
            public void ResumeWith(Result<T> r) { Recorder.Bump(); _inner.ResumeWith(r); }   // "dispatch" then forward
        }
    }

    /// A Continuation<int> whose context carries a RecordingDispatcher (so intercepted() finds it).
    public sealed class SinkI : Continuation<int>
    {
        public CoroutineContext Context { get; } = new RecordingDispatcher();
        public int Value { get; private set; }
        public void ResumeWith(Result<int> r) { Value = r.GetOrThrow(); }
    }
}
