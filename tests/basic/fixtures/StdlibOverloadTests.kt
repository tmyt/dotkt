import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsNull as assertNull

private fun <T : Comparable<T>> genericArrayMaximum(values: Array<T>): T = values.max()

private class StdlibClosedIterableRange(
    override val start: Int,
    override val endInclusive: Int,
) : ClosedRange<Int>, Iterable<Int> {
    override fun iterator(): Iterator<Int> = (start..endInclusive).iterator()
}

private class StdlibOpenIterableRange(
    override val start: Int,
    override val endExclusive: Int,
) : OpenEndRange<Int>, Iterable<Int> {
    override fun iterator(): Iterator<Int> = (start..<endExclusive).iterator()
}

private fun <T, R> stdlibClosedMembership(range: R, value: T?): Boolean
    where T : Comparable<T>, R : ClosedRange<T>, R : Iterable<T> = value in range

private fun <T, R> stdlibOpenMembership(range: R, value: T?): Boolean
    where T : Comparable<T>, R : OpenEndRange<T>, R : Iterable<T> = value in range

class StdlibOverloadTests {
    @TestAttribute
    fun floatingArrayAggregatesKeepTheirSelectedOverload() {
        val floats = arrayOf(3.5f, -2.0f, 1.0f)
        val doubles = arrayOf(3.5, -2.0, 1.0)
        assertEquals(3.5f, floats.max())
        assertEquals(-2.0f, floats.min())
        assertEquals(3.5f, floats.maxOrNull())
        assertEquals(-2.0f, floats.minOrNull())
        assertEquals(3.5, doubles.max())
        assertEquals(-2.0, doubles.min())
        assertEquals(3.5, doubles.maxOrNull())
        assertEquals(-2.0, doubles.minOrNull())
        assertNull(emptyArray<Float>().maxOrNull())
        assertNull(emptyArray<Float>().minOrNull())
        assertNull(emptyArray<Double>().maxOrNull())
        assertNull(emptyArray<Double>().minOrNull())
        assertEquals(3.5f, floatArrayOf(-2.0f, 3.5f).max())
        assertEquals(-2.0f, floatArrayOf(-2.0f, 3.5f).min())
        assertEquals(3.5, doubleArrayOf(-2.0, 3.5).max())
        assertEquals(-2.0, doubleArrayOf(-2.0, 3.5).min())
        assertEquals(3.5f, genericArrayMaximum(floats))
        assertEquals("z", genericArrayMaximum(arrayOf("a", "z")))
    }

    @TestAttribute
    fun collectionOverloadsDistinguishSequencesFromIterables() {
        val values = mutableListOf(1)
        values += sequenceOf(2, 3)
        values += listOf(4)
        assertEquals("1,2,3,4", values.joinToString(","))
        values -= sequenceOf(2)
        values -= listOf(4)
        assertEquals("1,3", values.joinToString(","))
        check(values.addAll(sequenceOf(5, 6, 7)))
        check(values.removeAll(sequenceOf(3, 5)))
        check(values.retainAll(sequenceOf(1, 6)))
        assertEquals("1,6", values.joinToString(","))
        assertEquals("1,6,7", (values + sequenceOf(7)).joinToString(","))
        assertEquals("1", (values - sequenceOf(6)).joinToString(","))
        assertEquals("1,6,8", (values + listOf(8)).joinToString(","))
        assertEquals("6", (values - listOf(1)).joinToString(","))
        assertEquals("1,2,3", (sequenceOf(1, 2) + sequenceOf(3)).joinToString(","))
        assertEquals("1,3", (sequenceOf(1, 2, 3) - sequenceOf(2)).joinToString(","))
        assertEquals("1,2,4", (sequenceOf(1, 2) + listOf(4)).joinToString(","))
        assertEquals("2,3", (sequenceOf(1, 2, 3) - listOf(1)).joinToString(","))
        val iterable: Iterable<Int> = listOf(1)
        assertEquals("1,2", (iterable + sequenceOf(2)).joinToString(","))
        assertEquals("1,2,3", (setOf(1, 2) + sequenceOf(3)).joinToString(","))
        assertEquals("2", (setOf(1, 2) - sequenceOf(1)).joinToString(","))
    }

    @TestAttribute
    fun mapOverloadsKeepSequencePairsAndDestinationIdentity() {
        val map = mutableMapOf("a" to 1)
        map.putAll(sequenceOf("b" to 2))
        map.putAll(listOf("c" to 3))
        map += sequenceOf("d" to 4)
        map += listOf("e" to 5)
        assertEquals(5, map.size)
        val copied = sequenceOf("a" to 9, "f" to 6).toMap()
        assertEquals(9, copied["a"])
        val destination = mutableMapOf<String, Int>()
        check(sequenceOf("b" to 2).toMap(destination) === destination)
        assertEquals(2, destination["b"])
        assertEquals(7, (map + sequenceOf("g" to 7))["g"])
        assertEquals(8, (map + listOf("h" to 8))["h"])
        assertEquals(4, (map - sequenceOf("a")).size)
        assertEquals(4, (map - listOf("b")).size)
        map -= sequenceOf("a", "b")
        map -= listOf("c")
        assertEquals(2, map.size)
        assertEquals(4, map["d"])
    }

    @TestAttribute
    fun genericRangeMembershipUsesItsDeclaredBounds() {
        val closed: ClosedRange<String> = "a".."m"
        val open: OpenEndRange<String> = "a"..<"m"
        check("a" in closed && "m" in closed && "z" !in closed)
        check("a" in open && "m" !in open && "z" !in open)
        val closedIterable = StdlibClosedIterableRange(1, 3)
        val openIterable = StdlibOpenIterableRange(1, 3)
        val absent: Int? = null
        val last: Int? = 3
        check(last in closedIterable && last !in openIterable)
        check(absent !in closedIterable && absent !in openIterable)
        check(stdlibClosedMembership(closedIterable, last))
        check(!stdlibOpenMembership(openIterable, last))
        check(!stdlibClosedMembership(closedIterable, absent))
        check(!stdlibOpenMembership(openIterable, absent))
    }
}
