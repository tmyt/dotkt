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
        public bool IsSuccess => _ex == null;            // kotlin.Result.isSuccess
        public T Value => _value;                        // the success value (read on the success branch)
        public Exception ExceptionOrNull => _ex;         // kotlin.Result.exceptionOrNull()
        public T GetOrThrow() { if (_ex != null) throw _ex; return _value; }
    }

    /// kotlin.coroutines.CoroutineContext — the indexed set of Elements (the kotlin stdlib algebra, mirrored in C#):
    /// get(key)/plus/minusKey/fold, with EmptyCoroutineContext the unit and CombinedContext the cons cell. (T3.)
    public interface CoroutineContext
    {
        E Get<E>(Key<E> key) where E : class, Element;
        R Fold<R>(R initial, Func<R, Element, R> operation);
        CoroutineContext Plus(CoroutineContext context);
        CoroutineContext MinusKey(IKey key);
    }

    /// CoroutineContext.Key<E> erased to a non-generic marker (for minusKey / an element's own key).
    public interface IKey { }
    /// CoroutineContext.Key<E> — the typed lookup key for an element of type E.
    public interface Key<E> : IKey where E : class, Element { }
    /// CoroutineContext.Element — a single-entry context that knows its own Key.
    public interface Element : CoroutineContext { IKey Key { get; } }

    /// AbstractCoroutineContextElement — the base most user Elements extend (default get/fold/plus/minusKey).
    public abstract class AbstractElement : Element
    {
        public IKey Key { get; }
        protected AbstractElement(IKey key) { Key = key; }
        public E Get<E>(Key<E> key) where E : class, Element => ReferenceEquals(Key, key) ? (E)(object)this : null;
        public R Fold<R>(R initial, Func<R, Element, R> op) => op(initial, this);
        public CoroutineContext Plus(CoroutineContext context) => Contexts.Plus(this, context);
        public CoroutineContext MinusKey(IKey key) => ReferenceEquals(Key, key) ? EmptyCoroutineContext.Instance : (CoroutineContext)this;
    }

    public sealed class EmptyCoroutineContext : CoroutineContext
    {
        public static readonly EmptyCoroutineContext Instance = new EmptyCoroutineContext();
        EmptyCoroutineContext() { }
        public E Get<E>(Key<E> key) where E : class, Element => null;
        public R Fold<R>(R initial, Func<R, Element, R> op) => initial;
        public CoroutineContext Plus(CoroutineContext context) => context;
        public CoroutineContext MinusKey(IKey key) => this;
    }

    /// The cons cell: a left context plus one more element (the standard kotlin representation).
    public sealed class CombinedContext : CoroutineContext
    {
        readonly CoroutineContext _left; readonly Element _element;
        public CombinedContext(CoroutineContext left, Element element) { _left = left; _element = element; }
        public E Get<E>(Key<E> key) where E : class, Element
        {
            CoroutineContext cur = this;
            while (true)
            {
                if (cur is CombinedContext cc) { var e = cc._element.Get(key); if (e != null) return e; cur = cc._left; }
                else return cur.Get(key);
            }
        }
        public R Fold<R>(R initial, Func<R, Element, R> op) => op(_left.Fold(initial, op), _element);
        public CoroutineContext Plus(CoroutineContext context) => Contexts.Plus(this, context);
        public CoroutineContext MinusKey(IKey key)
        {
            if (ReferenceEquals(_element.Key, key)) return _left;
            var nl = _left.MinusKey(key);
            if (ReferenceEquals(nl, _left)) return this;
            return ReferenceEquals(nl, EmptyCoroutineContext.Instance) ? (CoroutineContext)_element : new CombinedContext(nl, _element);
        }
    }

    public static class Contexts
    {
        public static CoroutineContext Plus(CoroutineContext left, CoroutineContext right) =>
            ReferenceEquals(right, EmptyCoroutineContext.Instance) ? left
            : right.Fold(left, (acc, el) =>
            {
                var removed = acc.MinusKey(el.Key);
                return ReferenceEquals(removed, EmptyCoroutineContext.Instance) ? (CoroutineContext)el : new CombinedContext(removed, el);
            });
    }

    /// kotlin.coroutines.Continuation<T>. INVARIANT on the CLR: the JVM declares `in T` but erases it; invariance
    /// is the CLR-safe choice (upstream's contravariant assignments are rare — revisit only if a real case needs it).
    /// The compiler-generated state machine implements this; `ResumeWith` re-enters the machine's label switch.
    public interface Continuation<T>
    {
        CoroutineContext Context { get; }
        void ResumeWith(Result<T> result);
    }

    /// Adapts the object-typed state machine to a typed `Continuation<T>` for the raw
    /// `suspendCoroutineUninterceptedOrReturn { c -> ... }` intrinsic (the block receives `Continuation<T>`, but the
    /// SM is `Continuation<object>`). Boxes on resume. Confines the reified-T friction to this one hop.
    public sealed class TypedCont<T> : Continuation<T>
    {
        readonly Continuation<object> _raw;
        public TypedCont(Continuation<object> raw) { _raw = raw; }
        public CoroutineContext Context => _raw.Context;
        public void ResumeWith(Result<T> r) =>
            _raw.ResumeWith(r.IsFailure ? Result<object>.Failure(r.ExceptionOrNull) : Result<object>.Success(r.GetOrThrow()));
    }

    /// kotlin.coroutines.{resume,resumeWithException} — the resume API over `Continuation<T>`, hiding Result from
    /// emitted user code (the compiler maps the stdlib extension funs onto these).
    public static class Continuations
    {
        public static void Resume<T>(Continuation<T> c, T value) => c.ResumeWith(Result<T>.Success(value));
        public static void ResumeWithException<T>(Continuation<T> c, Exception e) => c.ResumeWith(Result<T>.Failure(e));
    }

    /// A test completion: a `Continuation<int>` that captures the resumed value and lets a caller block for it
    /// (used to observe `startCoroutine` standalone, without a real dispatcher/runBlocking).
    public sealed class CaptureI : Continuation<int>
    {
        readonly System.Threading.Tasks.TaskCompletionSource<int> _tcs = new System.Threading.Tasks.TaskCompletionSource<int>();
        public CoroutineContext Context => EmptyCoroutineContext.Instance;
        public void ResumeWith(Result<int> r) { if (r.IsFailure) _tcs.SetException(r.ExceptionOrNull); else _tcs.SetResult(r.GetOrThrow()); }
        public int Await() => _tcs.Task.GetAwaiter().GetResult();
    }

    /// kotlinx.coroutines.Channel<T> over System.Threading.Channels (T8). suspend send/receive map to awaiting
    /// the Task forms; capacity<=0 -> unbounded. (A genuine kotlinx Channel has more — close semantics, fan-out,
    /// rendezvous — but this is the CLR-native core: produce/consume across the Task ABI.)
    public sealed class Chan<T>
    {
        readonly System.Threading.Channels.Channel<T> _ch;
        public Chan(int capacity) =>
            _ch = capacity <= 0
                ? System.Threading.Channels.Channel.CreateUnbounded<T>()
                : System.Threading.Channels.Channel.CreateBounded<T>(capacity);
        public Task<int> SendAsync(T v) => _ch.Writer.WriteAsync(v).AsTask().ContinueWith(_ => 0);
        public Task<T> ReceiveAsync() => _ch.Reader.ReadAsync().AsTask();
        public void Close() => _ch.Writer.Complete();
    }

    /// kotlin.Unit as a real type — needed when Unit is a generic TYPE ARGUMENT (Continuation<Unit>, Result<Unit>,
    /// Deferred<Unit>): a CLR generic arg can't be System.Void, so it erases to this singleton. (In return/statement
    /// position Unit still lowers to `void`.) See T7 / docs §13r.
    public sealed class Unit
    {
        public static readonly Unit Instance = new Unit();
        Unit() { }
        public override string ToString() => "kotlin.Unit";
    }

    /// kotlinx.coroutines.CancellableContinuation<T> — a Continuation<T> with cancellation hooks. v1: forwards
    /// resume to the underlying continuation; cancel/invokeOnCancellation are minimal (real cancellation lands with
    /// the dispatcher work). `c.resume(v)` rides the kotlin.coroutines.resume extension (Continuations.Resume).
    public sealed class CancellableCont<T> : Continuation<T>
    {
        readonly Continuation<T> _inner;
        Action<Exception> _onCancel;
        public CancellableCont(Continuation<T> inner) { _inner = inner; }
        public CoroutineContext Context => _inner.Context;
        public void ResumeWith(Result<T> result) => _inner.ResumeWith(result);
        public bool IsActive => true;
        public void Cancel(Exception cause) { _onCancel?.Invoke(cause); }
        public void InvokeOnCancellation(Action<Exception> handler) { _onCancel = handler; }
    }

    public static class Intrinsics
    {
        /// kotlin.coroutines.intrinsics.COROUTINE_SUSPENDED — the sentinel a suspension point returns (by ===
        /// reference identity) when it actually suspends rather than completing synchronously.
        public static readonly object COROUTINE_SUSPENDED = new object();
    }

    /// The boundary sinks between the Continuation core and the CLR `Task` world.
    ///
    /// A compiler-generated state machine implements `Continuation<object>` (Any? internally — it is resumed with
    /// heterogeneous results across its suspension points). The TYPED public result (`Task<T>`) is recovered only
    /// at the boundary, by the root continuation casting object→T. This mirrors the JVM where every coroutine is
    /// `Continuation<Any?>` and reified-T friction is confined to the boundary.
    public static class Builders
    {
        /// future{}: run a coroutine to a Task<T>. `start` kicks the state machine with a root continuation whose
        /// ResumeWith completes the TCS (normal→SetResult, OperationCanceled→SetCanceled, other→SetException).
        /// This is the default public surface that makes a `suspend fun` appear as `Task<T>` from C#/F#.
        public static Task<T> Future<T>(CoroutineContext ctx, Action<Continuation<object>> start)
        {
            var root = new Root<T>(ctx ?? EmptyCoroutineContext.Instance);
            try { start(root); }
            catch (Exception e) { root.ResumeWith(Result<object>.Failure(e)); }
            return root.Task;
        }

        /// The Task-sink root continuation. The compiler-generated kickoff builds the state machine, sets its
        /// completion to a `NewRoot<T>()`, drives `ResumeWith(success(null))`, and returns `root.Task` — no IL
        /// closure required. `T` is the suspend fun's result type; the coroutine drives as `Continuation<object>`
        /// and the root casts object→T at the boundary.
        public sealed class Root<T> : Continuation<object>
        {
            readonly TaskCompletionSource<T> _tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            public CoroutineContext Context { get; }
            public Root(CoroutineContext c) { Context = c; }
            public Task<T> Task => _tcs.Task;
            public void ResumeWith(Result<object> r)
            {
                if (r.IsFailure)
                {
                    if (r.ExceptionOrNull is OperationCanceledException) _tcs.TrySetCanceled();
                    else _tcs.TrySetException(r.ExceptionOrNull);
                }
                else _tcs.TrySetResult((T)r.GetOrThrow());
            }
        }

        /// Build a fresh Task-sink root (used by the emitted kickoff).
        public static Root<T> NewRoot<T>(CoroutineContext ctx) => new Root<T>(ctx ?? EmptyCoroutineContext.Instance);

        /// The Unit-result Task sink: a `suspend fun … : Unit` surfaces as a non-generic `Task`.
        public sealed class RootUnit : Continuation<object>
        {
            readonly TaskCompletionSource _tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            public CoroutineContext Context { get; }
            public RootUnit(CoroutineContext c) { Context = c; }
            public Task Task => _tcs.Task;
            public void ResumeWith(Result<object> r)
            {
                if (r.IsFailure)
                {
                    if (r.ExceptionOrNull is OperationCanceledException) _tcs.TrySetCanceled();
                    else _tcs.TrySetException(r.ExceptionOrNull);
                }
                else _tcs.TrySetResult();
            }
        }

        public static RootUnit NewRootUnit(CoroutineContext ctx) => new RootUnit(ctx ?? EmptyCoroutineContext.Instance);

        /// runBlocking: drive a coroutine to completion on the calling thread (a real event loop replaces this
        /// blocking GetResult in Phase 4, once dispatchers exist).
        public static T RunBlocking<T>(Action<Continuation<object>> start) =>
            Future<T>(EmptyCoroutineContext.Instance, start).GetAwaiter().GetResult();

        /// The leaf .NET-Task suspension, callable from a state machine's InvokeSuspend: register `cont` to be
        /// resumed when `task` completes (boxing the result to object), then the machine returns COROUTINE_SUSPENDED.
        /// Encapsulates the awaiter + completion closure that is awkward to emit in raw IL. (This is `await(Task)`
        /// expressed on the Continuation core; the genuine intrinsic-based form arrives with Phase 2.)
        public static void AwaitOnto<T>(Task<T> task, Continuation<object> cont)
        {
            task.GetAwaiter().OnCompleted(() =>
                cont.ResumeWith(task.IsFaulted
                    ? Result<object>.Failure(Unwrap(task.Exception))
                    : Result<object>.Success(task.Result)));
        }

        /// Unit-result overload (a non-generic Task suspension, e.g. `delay`).
        public static void AwaitOnto(Task task, Continuation<object> cont)
        {
            task.GetAwaiter().OnCompleted(() =>
                cont.ResumeWith(task.IsFaulted
                    ? Result<object>.Failure(Unwrap(task.Exception))
                    : Result<object>.Success(null)));
        }

        static Exception Unwrap(AggregateException ae) =>
            ae != null && ae.InnerExceptions.Count == 1 ? ae.InnerException : ae;

        /// Kotlin-facing await leaf: register a typed continuation to be resumed when `task` completes. Used by a
        /// `suspend fun await(t): T = suspendCoroutineUninterceptedOrReturn { c -> onComplete(t, c); COROUTINE_SUSPENDED }`.
        public static void OnComplete<T>(Task<T> task, Continuation<T> cont)
        {
            task.GetAwaiter().OnCompleted(() =>
            {
                if (task.IsFaulted) Continuations.ResumeWithException(cont, Unwrap(task.Exception));
                else Continuations.Resume(cont, task.Result);
            });
        }

        /// Monomorphic Int convenience for facades that can't yet call a generic .NET method (Phase 2 sample).
        public static void OnCompleteInt(Task<int> task, Continuation<int> cont) => OnComplete(task, cont);

        /// Register a callback to run with the Task's result when it completes (lets Kotlin do the resume itself,
        /// e.g. `onCompleteCb(task) { v -> c.resume(v) }`). Exercises the Kotlin-side resume API (A2).
        public static void OnCompleteCbInt(Task<int> task, Action<int> cb) =>
            task.GetAwaiter().OnCompleted(() => cb(task.Result));

        /// `(suspend ()->T).startCoroutine(completion)` — start the block (its kickoff Task) and route its result
        /// into the supplied completion continuation (normal→resume, throw→resumeWithException). Eager start.
        public static void StartCoroutine<T>(Func<Task<T>> block, Continuation<T> completion)
        {
            Task<T> t;
            try { t = block(); } catch (Exception e) { Continuations.ResumeWithException(completion, e); return; }
            CompleteOnto(t, completion);
        }

        /// `(suspend R.()->T).startCoroutine(receiver, completion)` — the receiver-lambda overload.
        public static void StartCoroutineR<R, T>(Func<R, Task<T>> block, R receiver, Continuation<T> completion)
        {
            Task<T> t;
            try { t = block(receiver); } catch (Exception e) { Continuations.ResumeWithException(completion, e); return; }
            CompleteOnto(t, completion);
        }

        // Route a Task's outcome into a completion (normal→resume, throw→resumeWithException).
        static void CompleteOnto<T>(Task<T> t, Continuation<T> completion) =>
            t.GetAwaiter().OnCompleted(() =>
            {
                if (t.IsFaulted) Continuations.ResumeWithException(completion, Unwrap(t.Exception));
                else Continuations.Resume(completion, t.Result);
            });
    }
}
