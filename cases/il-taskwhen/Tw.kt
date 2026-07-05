// N3 regression (facadegen `Map` short-circuit): `Task.WhenAny` returns a DOUBLE-NESTED generic —
// `WhenAny<T>(Task<T>, Task<T>): Task<Task<T>>`. facadegen's `Map` short-circuited on
// `t.FullName == self.FullName`, but an OPEN constructed generic that references a type parameter
// (`Task<T>` nested inside `Task<Task<T>>`) has a NULL `FullName`, so `null == null` matched and the arg
// was replaced by the ENCLOSING type's name -> the return surfaced as `Task1[Task1]` (raw inner, no arg).
// Guarding the compare with `t.FullName != null` (mirroring the twin at Program.cs:643) recurses into the
// arg -> `Task1[Task1[TResult]]`, so `any.Result.Result` (unwrap the outer then the inner Task<Int>)
// resolves. With the pre-fix facadegen this file FAILS to compile: the inner `.Result` is unresolved
// because the raw inner `Task1` carries no type argument.
//
// (The sibling `WhenAll<T>(IEnumerable<Task<T>>): Task<T[]>` — the `IEnumerable[Task1[T]]` param /
// `Task1[array:T]` return — now surfaces correctly too, but its E2E is blocked downstream on a kotc/bir2cir
// generic-static instantiation gap, tracked separately; only the surface belongs to facadegen.)
import System.Threading.Tasks.Task

fun main() {
    val a = Task.FromResult(10)
    val b = Task.FromResult(20)

    val any = Task.WhenAny(a, b)
    any.Wait()
    println(any.Result.Result)        // 10
}
