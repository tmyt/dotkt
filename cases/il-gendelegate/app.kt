// A Kotlin lambda passed to a GENERIC BCL delegate ctor param over a USER type (task #140 / P3).
// `System.Threading.ThreadLocal<Box>` takes a `System.Func<Box>` value-factory; `System.Progress<Box>`
// takes a `System.Action<Box>` handler. Because `Box` is a same-assembly TypeBuilder, the constructed
// `ThreadLocal<Box>`/`Progress<Box>` is a TypeBuilderInstantiation whose ctor param is resolved on the
// OPEN definition (`Func<T>`/`Action<T>`). ilemit must substitute the instantiation's concrete arg
// (T -> Box) so the lambda is materialized as `System.Func`1<Box>`/`System.Action`1<Box>` (the target
// BCL delegate) — NOT the internal `DotKt.Runtime.CompilerServices.KFunc`1<Box>` (ilverify StackUnexpected).
import System.Threading.ThreadLocal
import System.Progress
class Box(val n: Int)
fun main() {
    val tl = ThreadLocal<Box>({ Box(42) })   // Func<T> — return-position type-var substitution
    println(tl.Value.n)                       // 42
    var seen = 0
    val pr = Progress<Box>({ b: Box -> seen = b.n })  // Action<T> — input-position type-var substitution
    println(pr != null)                       // true
}
