// clr/taskinterop/: the CLR platform Task bridge (design note §5). Lives under libraries/stdlib/clr/,
// so all three stdlib builds compile it (collect_stdlib_sources in lib.sh feeds klib/ref/rt alike);
// frontend resolution for consumers rides kotc's kotlin.clr injection seam (bundle-6 P2).
//
// The stdlib Task-family alias classes (bundle-6 P1, names locked by
// docs/design-coroutine-cold-core-task-bridge.md §11): `Task0` binds the non-generic
// `System.Threading.Tasks.Task`, `Task<T>` binds `System.Threading.Tasks.Task`1` (bir2cir derives the
// arity from the usage shape: bare -> clr:Task, generic -> clrg:Task[T]), `TaskCompletionSource<T>`
// binds `System.Threading.Tasks.TaskCompletionSource`1` — the RootContinuation bridge sink (§2/§7:
// the public `Task<T>` bridge of an exported suspend fun completes a TCS through a RootContinuation).
// Standard alias-class rules: members with a 1:1 BCL equivalent carry @ClrIntrinsic/@ClrProperty and a
// filler TODO body (pure metadata, never invoked — substituted at app-emit). Deliberately MINIMAL: the
// `await` lowering (P4) targets the BCL awaiter directly in CIR and needs no members here.
//
// NOTE (interop, P4): a value typed by facadegen's `import System.Threading.Tasks.Task1` is a DIFFERENT
// frontend symbol than kotlin.clr.Task even though both lower to the same BCL type — unifying them so
// `someClrApi().await()` resolves is the facadegen/bir2cir P4 wiring (design note §5), not a stdlib concern.

package kotlin.clr

/** The non-generic `System.Threading.Tasks.Task` (a hot .NET computation with no result value). */
@ClrTypeAlias("System.Threading.Tasks.Task")
public class Task0 {
    @ClrProperty(READ, "IsCompleted")
    public val isCompleted: Boolean get() = TODO("clr binding should be implemented")
}

/** The generic `System.Threading.Tasks.Task<TResult>` (a hot .NET computation yielding [T]). */
@ClrTypeAlias("System.Threading.Tasks.Task")
public class Task<T> {
    @ClrProperty(READ, "IsCompleted")
    public val isCompleted: Boolean get() = TODO("clr binding should be implemented")

    /** `Task<TResult>.Result` — BLOCKS until completion; faults surface as `AggregateException` (BCL semantics). */
    @ClrProperty(READ, "Result")
    public val result: T get() = TODO("clr binding should be implemented")
}

/** `System.Threading.Tasks.TaskCompletionSource<TResult>` — the sink the coroutine Task bridge completes. */
@ClrTypeAlias("System.Threading.Tasks.TaskCompletionSource")
public class TaskCompletionSource<T> {
    @ClrProperty(READ, "Task")
    public val task: Task<T> get() = TODO("clr binding should be implemented")

    @ClrIntrinsic("TrySetResult")
    public fun trySetResult(result: T): Boolean = TODO("clr binding should be implemented")

    @ClrIntrinsic("TrySetException")
    public fun trySetException(exception: Throwable): Boolean = TODO("clr binding should be implemented")
}
