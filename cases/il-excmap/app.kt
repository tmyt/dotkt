// bundle-6 ④ fix #5 + #2: a Kotlin IndexOutOfBoundsException catch covers BOTH .NET out-of-range types
// (List -> ArgumentOutOfRangeException, array -> IndexOutOfRangeException); printStackTrace resolves through the
// override chain to the kotlin.Throwable rule-3 helper on any subclass receiver (no NRE).
fun main() {
    val list = listOf(1, 2, 3)
    try { println(list[10]) } catch (e: IndexOutOfBoundsException) { println("caught-list") }
    val arr = intArrayOf(1, 2, 3)
    try { println(arr[10]) } catch (e: IndexOutOfBoundsException) { println("caught-arr") }
    try { throw RuntimeException("boom") } catch (e: Exception) { (e as Exception).printStackTrace(); println("pst-ok") }
    try { list[99] } catch (e: RuntimeException) { println("caught-super") }
}
