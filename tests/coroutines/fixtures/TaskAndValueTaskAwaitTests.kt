// feature fixture — the .NET Task family + generalized-awaitable (GetAwaiter) reverse-bridge cases that need only BCL
// types (no C# runtime.cs companion). taskfam pins the same-name .NET arity family (`Task` + `Task<T>`); valueawait
// proves the `await` generalization for a NON-Task BCL awaitable (`ValueTask<T>`). Each former case's `main` +
// stdout-golden becomes one @TestAttribute method preserving every value 1:1 (`// <expected>`).
//
// Coverage preserved (old case -> method):
//   il-taskfam    -> taskfam_sameNameArityFamily         (docs/dotkt-semantics.md §8d: Task`` / Task`1` coexist)
//   il-valueawait -> valueawait_valueTaskAwaiterFastPath  (#10: ValueTaskAwaiter<T> sync fast path, no .AsTask())
//
// (il-extawait — the extension-GetAwaiter await case that ships a C# runtime.cs (MyLib.MyOp) — is NOT migrated
// here; a co-compiled C# awaitable can't be referenced from the coroutines ktproj, so it is flagged for the
// tests/interop C#-producer lane.)
//
// Top-level names use the descriptive `taskAwaitValueTask`/`TaskAwaitValueTask` stem; taskfam has no top-level
// decls (its body is inline in the method).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import System.Threading.Tasks.Task
import System.Threading.Tasks.Task1
import System.Threading.Tasks.TaskCompletionSource1
import System.Threading.Tasks.ValueTask1
import dotkt.support.blockOn

// ---- il-valueawait -------------------------------------------------------------------------------------------
suspend fun taskAwaitValueTaskAwait(): Int {
    val vt = ValueTask1<Int>(41)   // a synchronously-completed ValueTask<Int>
    return vt.await() + 1          // 42 — ValueTaskAwaiter<Int> fast path (IsCompleted true)
}

class TaskAndValueTaskAwaitTests {
    @TestAttribute
    fun sameNameArityFamily() {
        // non-generic Task: implicit companion statics + instance members
        val t: Task = Task.Delay(10)
        t.Wait()
        assertEquals(true, t.IsCompleted)   // former golden: plain=True
        // Task<T>: the generic:Task1[T] cross-ref (tcs.Task) + a generic instance member (Result)
        val tcs = TaskCompletionSource1<Int>()
        tcs.SetResult(42)
        val g: Task1<Int> = tcs.Task
        assertEquals(42, g.Result)          // former golden: generic=42
    }

    @TestAttribute
    fun valueTaskAwaiterFastPath() {
        assertEquals(42, blockOn { taskAwaitValueTaskAwait() })   // 42
    }
}
