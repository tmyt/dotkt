// Generic .NET static factory (Task.FromResult<TResult>): the seam that lets Kotlin BUILD a Task<T> from a
// .NET generic static method. facadegen surfaces the generic `sfun` (type-param tokens); kotc's companion
// generic-static builder declares the method type parameter and resolves the return/param against it, so
// `Task.FromResult(42)` resolves as `Task.FromResult<Int>(42): Task<Int>` — the async-interop enabler.
import System.Threading.Tasks.Task
import System.Threading.Tasks.Task1

fun main() {
    val t: Task1<Int> = Task.FromResult(42)
    println(t.Result)
}
