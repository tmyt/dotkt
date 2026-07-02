// Same-name .NET arity family (docs/dotkt-semantics.md §8d): the non-generic `Task` and the generic
// `Task<TResult>` (Kotlin name `Task1`) COEXIST in one file — facadegen emits arity-qualified tokens
// (`System.Threading.Tasks.Task`1` / Kotlin `Task1`) so the injector no longer last-wins-overwrites the
// ClassId, and `generic:Task1[T]` cross-refs (tcs.Task) resolve to the arity-1 definition. This surface
// sits directly under the suspend=Task<T> ABI (every .NET *Async API).
import System.Threading.Tasks.Task
import System.Threading.Tasks.Task1
import System.Threading.Tasks.TaskCompletionSource1

fun main() {
    // non-generic Task: implicit companion statics + instance members
    val t: Task = Task.Delay(10)
    t.Wait()
    println("plain=" + t.IsCompleted)
    // Task<T>: the generic:Task1[T] cross-ref (tcs.Task) + a generic instance member (Result)
    val tcs = TaskCompletionSource1<Int>()
    tcs.SetResult(42)
    val g: Task1<Int> = tcs.Task
    println("generic=" + g.Result)
}
