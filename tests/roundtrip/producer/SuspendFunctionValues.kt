package roundtrip.suspendvalues

suspend fun addAsync(left: Int, right: Int): Int = left + right

fun makeBlock(): suspend () -> Int = { addAsync(20, 22) }

val storedBlock: suspend () -> Int = { addAsync(15, 15) }

class BlockHolder {
    val block: suspend () -> Int = { addAsync(100, 7) }
}

// Suspend function types erase to object + a carrier and therefore do not consume a delegate-family arity.
// Keeping this public makes dll2klib restore the 23-parameter shape for the separately compiled consumer.
suspend fun invokeWideSuspend23(
    block: suspend (Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int) -> Int
): Int = block(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23)
