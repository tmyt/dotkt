// #145: array fill(element, fromIndex, toIndex) must validate the range (Kotlin contract:
// IndexOutOfBoundsException for out-of-bounds, IllegalArgumentException for an inverted
// fromIndex > toIndex) instead of silently no-op'ing on an inverted range.
//
// Exercised via the generic Array<T>.fill path, which is app-callable; the 8 primitive-array
// fill actuals (IntArray/ByteArray/...) receive the identical checkFillRangeIndexes guard but
// are not app-callable here due to the pre-existing primitive-array-receiver resolution gap
// (the same gap that keeps the primitive copyInto actuals out of il-copyintoverlap).
fun main() {
    val a = arrayOf("a", "b", "c", "d", "e")
    a.fill("z", 1, 3)
    println(a.joinToString(","))   // a,z,z,d,e

    // Inverted range -> IllegalArgumentException.
    try { arrayOf("a", "b", "c").fill("z", 2, 1); println("no-throw") }
    catch (e: IllegalArgumentException) { println("iae") }

    // toIndex past the end -> IndexOutOfBoundsException.
    try { arrayOf("a", "b", "c").fill("z", 0, 5); println("no-throw") }
    catch (e: IndexOutOfBoundsException) { println("ioobe") }

    // Negative fromIndex -> IndexOutOfBoundsException.
    try { arrayOf("a", "b", "c").fill("z", -1, 2); println("no-throw") }
    catch (e: IndexOutOfBoundsException) { println("ioobe-neg") }

    // Valid full fill still works.
    val b = arrayOf(0, 0, 0)
    b.fill(4)
    println(b.joinToString(","))   // 4,4,4
}
