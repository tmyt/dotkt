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
// (N3-deep) The sibling `WhenAll<T>(params Task<T>[]): Task<T[]>` now EXECUTES too. Its `vararg` param was reaching
// the frontend as `vararg:generic:Task1[TResult]`; the GENERIC .NET-method value-parameter builder did not strip the
// `vararg:` prefix (unlike the non-generic paths), so the param fell to `coneOf`'s else -> `Any?`. The vararg overload
// then surfaced as `WhenAll(tasks: Any?)`, whose `clrMethodShape` is "Object", so ilemit's `ResolveGenericMethod`
// matched no real `params Task<T>[]` overload ("Sequence contains no elements"). Stripping `vararg:` -> a real
// `vararg tasks: Task1<TResult>` (shape "array") binds the `params Task<TResult>[]` overload.
import System.Threading.Tasks.Task

fun main() {
    val a = Task.FromResult(10)
    val b = Task.FromResult(20)

    val any = Task.WhenAny(a, b)
    any.Wait()
    println(any.Result.Result)        // 10

    // WhenAll over a vararg of Task<Int> -> Task<Int[]>; unwrap the array and sum it.
    val all = Task.WhenAll(Task.FromResult(1), Task.FromResult(2), Task.FromResult(3))
    all.Wait()
    val r = all.Result                // Int[]
    println(r[0] + r[1] + r[2])       // 6
}
