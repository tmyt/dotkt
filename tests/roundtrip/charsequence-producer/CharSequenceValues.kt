class RoundtripCharSequence(val text: String) : CharSequence {
    override val length: Int get() = text.length
    override fun get(index: Int): Char = text[index]
    override fun subSequence(startIndex: Int, endIndex: Int): CharSequence =
        RoundtripCharSequence(text.substring(startIndex, endIndex))
}

private val roundtripStringValue = StringBuilder("cross").append("-assembly").toString()
fun roundtripOriginalString(): String = roundtripStringValue
fun roundtripStringAsSequence(): CharSequence = roundtripStringValue
fun roundtripCustomSequence(): CharSequence = RoundtripCharSequence("custom")
fun roundtripNullSequence(): CharSequence? = null

interface RoundtripSequenceBound<T : CharSequence>
interface RoundtripSequenceSink<T>
class RoundtripConcreteSequenceSink : RoundtripSequenceSink<CharSequence>

// Run in the polymorphic assembly: the called producer uses native String slots.
fun roundtripCheckSnapshotBoundary() {
    val concrete = RoundtripCharSequence("content")
    val value: CharSequence = concrete
    val expected = concrete.toString()
    check(roundtripSnapshotLength(value) == expected.length)
    check(roundtripSnapshotLength(concrete) == expected.length)
    check(roundtripSnapshotLength("str") == 3)
    val restored: CharSequence = roundtripSnapshotValue(value)
    check(restored.length == expected.length)
    check(restored[0] == expected[0])
    check(roundtripSnapshotNullable(null) == null)
    check(roundtripSnapshotNullable(value)!!.length == expected.length)
    var evaluations = 0
    fun source(): CharSequence { evaluations++; return value }
    check(roundtripSnapshotLength(source()) == expected.length)
    check(evaluations == 1)
}
