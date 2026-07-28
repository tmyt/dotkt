// A suspend function PARAMETER of byref-like type, in a body that DOES suspend: the state machine's constructor
// writes it into an instance field, which the CLR refuses. The C# CS4012 mirror. The refusal does not depend on
// the body suspending — it is a rule about the declaration, and SuspendFreeParameterByRefLike.kt pins the
// suspension-free half of it. What a suspend function MAY hold is a byref-like LOCAL that no path reads after a
// resume (tests/coroutines/fixtures/ByRefLikeStorageTests.kt).
import System.Span

suspend fun cfParamTick(n: Int): Int = n + 1

suspend fun cfConsume(s: Span<Int>): Int {
    val t = cfParamTick(1)
    return s.Length + t
}

suspend fun main() {
    println(cfConsume(Span<Int>(arrayOf(1, 2))))
}
