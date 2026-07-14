// #19: a bare Kotlin lambda `{ ... }` into a .NET member that overloads on DELEGATE-typed params must resolve
// (regression guard). `Thread({...})` overloads `ThreadStart` (`() -> Unit`) / `ParameterizedThreadStart`
// (`(Any?) -> Unit`); `Task.Run({...})` overloads `Action` (`() -> Unit`) / `Func<T>` (`() -> T`). A no-arrow
// `{ ... }` has ambiguous arity/return, so BOTH used to be an overload-resolution ambiguity. facadegen marks the
// Pareto-dominated (wider / value-returning) sibling `lowPriority`; kotc stamps
// `@kotlin.internal.LowPriorityInOverloadResolution` on it, so the bare lambda binds the PREFERRED sibling
// (ThreadStart / Action) with no ambiguity — while an explicit `{ x -> ... }` still reaches the wider one.
import System.Threading.Thread
import System.Threading.Tasks.Task

fun main() {
    val t = Thread({ println("x") })   // bare lambda -> ThreadStart (was: ambiguity)
    t.Start()
    t.Join()
    val task = Task.Run({ println("y") })   // bare lambda -> Action (was: ambiguity vs Func<T>)
    task.Wait()
    println("done")
}
