@file:OptIn(ExperimentalUnsignedTypes::class)

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals

private fun assertArrayExhausted(iterator: Iterator<*>, next: () -> Any?) {
    repeat(2) {
        check(!iterator.hasNext())
        var threw = false
        try { next() } catch (_: NoSuchElementException) { threw = true }
        check(threw && !iterator.hasNext())
        threw = false
        try { iterator.next() } catch (_: NoSuchElementException) { threw = true }
        check(threw && !iterator.hasNext())
    }
}

private fun <T> assertSingleArrayElement(iterator: Iterator<T>, expected: T, next: () -> T) {
    check(iterator.hasNext() && iterator.hasNext())
    check(next() == expected)
    assertArrayExhausted(iterator, next)
}

private fun assertInvalidFill(
    fill: (Int, Int) -> Unit, snapshot: () -> String, initial: String,
    from: Int, to: Int, bounds: Boolean
) {
    var threw = false
    try { fill(from, to) }
    catch (e: IndexOutOfBoundsException) {
        assertEquals(true, bounds)
        assertEquals("fromIndex: $from, toIndex: $to, size: 3", e.message)
        threw = true
    }
    catch (e: IllegalArgumentException) {
        assertEquals(false, bounds)
        assertEquals("fromIndex: $from > toIndex: $to", e.message)
        threw = true
    }
    assertEquals(true, threw)
    assertEquals(initial, snapshot())
}

private fun assertFillBounds(
    fill: (Int, Int) -> Unit,
    snapshot: () -> String,
    initial: String,
    partial: String,
    full: String,
    fillDefault: () -> Unit
) {
    assertInvalidFill(fill, snapshot, initial, -1, 2, true)
    assertInvalidFill(fill, snapshot, initial, 0, 4, true)
    assertInvalidFill(fill, snapshot, initial, -1, -2, true)
    assertInvalidFill(fill, snapshot, initial, 4, 4, true)
    assertInvalidFill(fill, snapshot, initial, 2, 1, false)
    assertInvalidFill(fill, snapshot, initial, 0, -1, false)
    fill(0, 0)
    fill(3, 3)
    check(snapshot() == initial)
    fill(1, 2)
    check(snapshot() == partial)
    fillDefault()
    check(snapshot() == full)
}

class ArrayBoundaryTests {
    @TestAttribute fun emptyIteratorsKeepTheirCursorAfterEveryFailedNext() {
        val generic = emptyArray<Int>().iterator()
        assertArrayExhausted(generic) { generic.next() }
        val bytes = byteArrayOf().iterator()
        assertArrayExhausted(bytes) { bytes.nextByte() }
        val chars = charArrayOf().iterator()
        assertArrayExhausted(chars) { chars.nextChar() }
        val shorts = shortArrayOf().iterator()
        assertArrayExhausted(shorts) { shorts.nextShort() }
        val ints = intArrayOf().iterator()
        assertArrayExhausted(ints) { ints.nextInt() }
        val longs = longArrayOf().iterator()
        assertArrayExhausted(longs) { longs.nextLong() }
        val floats = floatArrayOf().iterator()
        assertArrayExhausted(floats) { floats.nextFloat() }
        val doubles = doubleArrayOf().iterator()
        assertArrayExhausted(doubles) { doubles.nextDouble() }
        val booleans = booleanArrayOf().iterator()
        assertArrayExhausted(booleans) { booleans.nextBoolean() }
        val unsigned = uintArrayOf().iterator()
        assertArrayExhausted(unsigned) { unsigned.next() }
    }

    @TestAttribute fun populatedPrimitiveIteratorsRemainLiveAndThenExhaust() {
        val b = byteArrayOf(0); val bi = b.iterator(); b[0] = 7
        assertSingleArrayElement(bi, 7.toByte()) { bi.nextByte() }
        val c = charArrayOf('a'); val ci = c.iterator(); c[0] = 'z'
        assertSingleArrayElement(ci, 'z') { ci.nextChar() }
        val s = shortArrayOf(0); val si = s.iterator(); s[0] = 7
        assertSingleArrayElement(si, 7.toShort()) { si.nextShort() }
        val i = intArrayOf(0); val ii = i.iterator(); i[0] = 7
        assertSingleArrayElement(ii, 7) { ii.nextInt() }
        val l = longArrayOf(0); val li = l.iterator(); l[0] = 7
        assertSingleArrayElement(li, 7L) { li.nextLong() }
        val f = floatArrayOf(0f); val fi = f.iterator(); f[0] = 7.5f
        assertSingleArrayElement(fi, 7.5f) { fi.nextFloat() }
        val d = doubleArrayOf(0.0); val di = d.iterator(); d[0] = 7.5
        assertSingleArrayElement(di, 7.5) { di.nextDouble() }
        val z = booleanArrayOf(false); val zi = z.iterator(); z[0] = true
        assertSingleArrayElement(zi, true) { zi.nextBoolean() }
    }

    @TestAttribute fun genericIteratorsRetainValueReferenceAndNullableFrames() {
        fun <T> verify(array: Array<T>, value: T) {
            val iterator = array.iterator()
            array[0] = value
            assertSingleArrayElement(iterator, value) { iterator.next() }
        }
        verify(arrayOf(0), 7)
        verify(arrayOf("a"), "changed")
        verify(arrayOf<Int?>(7), null)
        verify(arrayOf<Int?>(null), 7)
    }

    @TestAttribute fun fillUsesSharedBoundsBeforeWritingForEveryArrayKind() {
        val a = arrayOf("a", "a", "a")
        assertFillBounds({ f, t -> a.fill("b", f, t) }, { a.joinToString() }, "a, a, a", "a, b, a", "b, b, b") { a.fill("b") }
        val b = byteArrayOf(0, 0, 0)
        assertFillBounds({ f, t -> b.fill(7, f, t) }, { b.joinToString() }, "0, 0, 0", "0, 7, 0", "7, 7, 7") { b.fill(7) }
        val c = charArrayOf('a', 'a', 'a')
        assertFillBounds({ f, t -> c.fill('b', f, t) }, { c.joinToString() }, "a, a, a", "a, b, a", "b, b, b") { c.fill('b') }
        val s = shortArrayOf(0, 0, 0)
        assertFillBounds({ f, t -> s.fill(7, f, t) }, { s.joinToString() }, "0, 0, 0", "0, 7, 0", "7, 7, 7") { s.fill(7) }
        val i = intArrayOf(0, 0, 0)
        assertFillBounds({ f, t -> i.fill(7, f, t) }, { i.joinToString() }, "0, 0, 0", "0, 7, 0", "7, 7, 7") { i.fill(7) }
        val l = longArrayOf(0, 0, 0)
        assertFillBounds({ f, t -> l.fill(7, f, t) }, { l.joinToString() }, "0, 0, 0", "0, 7, 0", "7, 7, 7") { l.fill(7) }
        val f = floatArrayOf(0f, 0f, 0f)
        assertFillBounds({ from, to -> f.fill(7.5f, from, to) },
            { f.joinToString { if (it == 0f) "zero" else if (it == 7.5f) "filled" else "unexpected" } },
            "zero, zero, zero", "zero, filled, zero", "filled, filled, filled") { f.fill(7.5f) }
        val d = doubleArrayOf(0.0, 0.0, 0.0)
        assertFillBounds({ from, to -> d.fill(7.5, from, to) },
            { d.joinToString { if (it == 0.0) "zero" else if (it == 7.5) "filled" else "unexpected" } },
            "zero, zero, zero", "zero, filled, zero", "filled, filled, filled") { d.fill(7.5) }
        val z = booleanArrayOf(false, false, false)
        assertFillBounds({ from, to -> z.fill(true, from, to) },
            { z.joinToString { if (it) "true" else "false" } },
            "false, false, false", "false, true, false", "true, true, true") { z.fill(true) }
    }

    @TestAttribute fun genericValueAndNullableFillsKeepTheirStorageAndAllowEmptyRanges() {
        fun <T> fillOne(array: Array<T>, value: T) {
            array.fill(value)
            check(array[0] == value)
            array.fill(value, 1, 1)
            check(array[0] == value)
        }
        fillOne(arrayOf(0), 7)
        fillOne(arrayOf<Int?>(7), null)
        emptyArray<Int>().fill(7)
        byteArrayOf().fill(7); shortArrayOf().fill(7); intArrayOf().fill(7)
        longArrayOf().fill(7); floatArrayOf().fill(7f); doubleArrayOf().fill(7.0)
        charArrayOf().fill('a'); booleanArrayOf().fill(true)
    }
}
