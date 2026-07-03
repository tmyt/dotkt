// Cross-assembly call to a PUBLIC static on a GENERIC stdlib type: `kotlin.Result`1<T>::success`/`failure`
// are static generic methods living in DotKt.Stdlib, invoked from THIS app assembly. ilemit must anchor the
// call onto the constructed `Result`1<object>` instantiation (mirroring the stdlib's own emitted IL), NOT the
// open `Result`1` typedef — the latter is an invalid memberref → runtime TypeLoadException. Exercises
// AnchorOpenGenericOwnerStatic's external-reflection branch.
fun main() {
    val ok: Result<Int> = Result.success(42)
    println(ok.getOrNull())              // 42
    println(ok.isSuccess)                // true

    val bad: Result<Int> = Result.failure(RuntimeException("boom"))
    println(bad.isFailure)               // true
    println(bad.exceptionOrNull()?.message)  // boom

    val s: Result<String> = Result.success("hi")
    println(s.getOrNull())               // hi
}
