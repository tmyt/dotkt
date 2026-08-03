package roundtrip.suspendvalues

suspend fun addAsync(left: Int, right: Int): Int = left + right

fun makeBlock(): suspend () -> Int = { addAsync(20, 22) }

val storedBlock: suspend () -> Int = { addAsync(15, 15) }

class BlockHolder {
    val block: suspend () -> Int = { addAsync(100, 7) }
}
