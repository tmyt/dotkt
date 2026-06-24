// kotlinx.coroutines on the CLR coroutine model: `delay` -> Task.Delay (a suspension point inside the existing
// suspend-fun CPS machinery), `runBlocking { … }` drives a trivial block synchronously (GetAwaiter().GetResult()).
// Pure BCL (System.Threading.Tasks) — no DotKt.Runtime needed for these.
import kotlinx.coroutines.delay
import kotlinx.coroutines.runBlocking

suspend fun compute(n: Int): Int {
    delay(1)              // kotlinx.coroutines.delay -> await Task.Delay
    return n * n
}

suspend fun total(): Int {
    val a = compute(3)    // direct suspend call (await compute's Task)
    val b = compute(4)
    return a + b          // 9 + 16
}

fun main() {
    println(runBlocking { total() })   // 25
}
