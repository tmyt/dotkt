// kotlin/clr coroutine runtime (strategy B foundation).
//
// This is the CLR side of the "pure Kotlin coroutine" engine: a Continuation type and the
// Continuation <-> TaskCompletionSource bridge (the `future { }` reverse-bridge) that the ABI in
// docs/coroutine-abi.md specifies. The compiler's suspend lowering (D2.1) will lower a `suspend fun`
// into a state-machine class implementing `IContinuation<T>` and start it via `Future`.
//
// Until D2.1 lands, the production interop path remains strategy A (suspend -> C# async Task<T>).
// This runtime is the foundation B builds on; it is intentionally small and dependency-free.
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Kotlin.Coroutines
{
    /// kotlin.Result<T> — a success value or a failure exception.
    public readonly struct KResult<T>
    {
        public readonly T Value;
        public readonly Exception Failure;
        public bool IsFailure => Failure != null;
        private KResult(T v, Exception e) { Value = v; Failure = e; }
        public static KResult<T> Success(T v) => new KResult<T>(v, null);
        public static KResult<T> Fail(Exception e) => new KResult<T>(default, e);
    }

    /// Minimal CoroutineContext (dispatcher/Job slots come later; D2.6/D2.7).
    public sealed class CoroutineContext
    {
        public CancellationToken CancellationToken;
        public static readonly CoroutineContext Empty = new CoroutineContext();
    }

    /// kotlin.coroutines.Continuation<T>. The lowering-produced state machine implements this.
    public interface IContinuation<in T>
    {
        CoroutineContext Context { get; }
        void ResumeWith(KResult<object> result);
    }

    /// The sentinel returned by a suspension point that actually suspends.
    public static class Intrinsics
    {
        public static readonly object CoroutineSuspended = new object();
    }

    public static class CoroutineBuilders
    {
        /// Continuation <-> TaskCompletionSource bridge: expose a coroutine as a Task<T>.
        /// `start(rootContinuation)` kicks the state machine; the root continuation completes the TCS:
        ///   normal -> SetResult, exception -> SetException, cancellation -> SetCanceled.
        public static Task<T> Future<T>(CoroutineContext context, Action<IContinuation<T>> start)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var root = new RootContinuation<T>(context ?? CoroutineContext.Empty, tcs);
            try { start(root); }
            catch (Exception e) { tcs.TrySetException(e); }
            return tcs.Task;
        }

        private sealed class RootContinuation<T> : IContinuation<T>
        {
            private readonly TaskCompletionSource<T> _tcs;
            public CoroutineContext Context { get; }
            public RootContinuation(CoroutineContext ctx, TaskCompletionSource<T> tcs) { Context = ctx; _tcs = tcs; }

            public void ResumeWith(KResult<object> result)
            {
                if (result.IsFailure)
                {
                    if (result.Failure is OperationCanceledException) _tcs.TrySetCanceled();
                    else _tcs.TrySetException(result.Failure);
                }
                else _tcs.TrySetResult((T)result.Value);
            }
        }

        /// Run a coroutine to completion on the current thread (runBlocking).
        public static T RunBlocking<T>(Action<IContinuation<T>> start) =>
            Future<T>(CoroutineContext.Empty, start).GetAwaiter().GetResult();
    }
}
