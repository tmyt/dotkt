// DotKt coroutine core — the CLR forms of the kotlin.coroutines stdlib package, shared across assemblies so a
// compiled `suspend fun`, the user assembly, and `dotktx.coroutines` (compiled upstream kotlinx-coroutines-core)
// all bind to the SAME Continuation/Result/CoroutineContext types (cross-assembly identity — see memory
// dotkt-naming-and-runtime-split, dotktx-coroutines-path-b). The compiler maps the `kotlin.coroutines.*` fqnames
// onto these types and emits suspend-fun state machines as classes implementing `Continuation<T>`.
//
// Path B / B2-as-generalization (docs/design-coroutines-clr.md §13a): the internal lowered form is
// Continuation-passing; the DEFAULT public CLR surface stays `Task<T>` via the `Future` sink here
// ("Continuation can be regarded as Task" — its sink is a TaskCompletionSource). Shape proven by the Phase 1 PoC.
using System;
using System.Threading;
using System.Threading.Tasks;

namespace DotKt.Coroutines
{
    /// kotlin.Result<T> — a success value or a failure exception. Carried by Continuation.ResumeWith.
    /// Kept a plain struct (no boxing of the success value into a sentinel wrapper as on JVM).
    public readonly struct Result<T>
    {
        readonly T _value;
        readonly Exception _ex;
        Result(T v, Exception e) { _value = v; _ex = e; }
        public static Result<T> Success(T v) => new Result<T>(v, null);
        public static Result<T> Failure(Exception e) => new Result<T>(default, e);
        public bool IsFailure => _ex != null;
        public Exception ExceptionOrNull => _ex;
        public T GetOrThrow() { if (_ex != null) throw _ex; return _value; }
    }

    /// kotlin.coroutines.CoroutineContext. Minimal for now (Element/Key/dispatcher land in later phases — the
    /// dispatcher set is part of dotktx.coroutines' CLR actuals). `EmptyCoroutineContext` is the unit element.
    public interface CoroutineContext { }

    public sealed class EmptyCoroutineContext : CoroutineContext
    {
        public static readonly EmptyCoroutineContext Instance = new EmptyCoroutineContext();
    }

    /// kotlin.coroutines.Continuation<T>. INVARIANT on the CLR: the JVM declares `in T` but erases it; invariance
    /// is the CLR-safe choice (upstream's contravariant assignments are rare — revisit only if a real case needs it).
    /// The compiler-generated state machine implements this; `ResumeWith` re-enters the machine's label switch.
    public interface Continuation<T>
    {
        CoroutineContext Context { get; }
        void ResumeWith(Result<T> result);
    }

    public static class Intrinsics
    {
        /// kotlin.coroutines.intrinsics.COROUTINE_SUSPENDED — the sentinel a suspension point returns (by ===
        /// reference identity) when it actually suspends rather than completing synchronously.
        public static readonly object COROUTINE_SUSPENDED = new object();
    }

    /// The boundary sinks between the Continuation core and the CLR `Task` world.
    public static class Builders
    {
        /// future{}: run a coroutine to a Task<T>. `start` kicks the state machine with a root continuation whose
        /// ResumeWith completes the TCS (normal→SetResult, OperationCanceled→SetCanceled, other→SetException).
        /// This is the default public surface that makes a `suspend fun` appear as `Task<T>` from C#/F#.
        public static Task<T> Future<T>(CoroutineContext ctx, Action<Continuation<T>> start)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            try { start(new Root<T>(ctx ?? EmptyCoroutineContext.Instance, tcs)); }
            catch (Exception e) { tcs.TrySetException(e); }
            return tcs.Task;
        }

        sealed class Root<T> : Continuation<T>
        {
            readonly TaskCompletionSource<T> _tcs;
            public CoroutineContext Context { get; }
            public Root(CoroutineContext c, TaskCompletionSource<T> t) { Context = c; _tcs = t; }
            public void ResumeWith(Result<T> r)
            {
                if (r.IsFailure)
                {
                    if (r.ExceptionOrNull is OperationCanceledException) _tcs.TrySetCanceled();
                    else _tcs.TrySetException(r.ExceptionOrNull);
                }
                else _tcs.TrySetResult(r.GetOrThrow());
            }
        }

        /// runBlocking: drive a coroutine to completion on the calling thread (a real event loop replaces this
        /// blocking GetResult in Phase 4, once dispatchers exist).
        public static T RunBlocking<T>(Action<Continuation<T>> start) =>
            Future<T>(EmptyCoroutineContext.Instance, start).GetAwaiter().GetResult();
    }
}
