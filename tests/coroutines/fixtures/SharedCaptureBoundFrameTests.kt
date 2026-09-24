package sharedcaptureboundframe

import NUnit.Framework.TestAttribute

open class CaptureBox<T>(val items: List<T?>)
private inline fun visit(block: () -> Unit) { block() }

private fun <T, U : CaptureBox<T>> capture(value: U, early: Boolean): U {
    var current = value
    visit { if (early) return current }
    current = value
    return current
}

private class CaptureHost<X> {
    fun <U : CaptureBox<X>> capture(initial: U, replacement: U, early: Boolean): U {
        var current = initial
        visit { if (early) return current }
        current = replacement
        return current
    }
}

class SharedCaptureBoundFrameTests {
    @TestAttribute
    fun cellBoundsUseTheirOwnTypeFrame() {
        val strings = CaptureBox<String>(listOf(null, "ok"))
        check(capture(strings, false) === strings)
        val empty = CaptureBox<String>(emptyList())
        check(capture(empty, true) === empty)
        val integers = CaptureBox<Int>(listOf(null, 17))
        check(capture(integers, false) === integers)
    }

    @TestAttribute
    fun ownerAndMethodParametersRemainDistinctInCellBounds() {
        val first = CaptureBox<String>(listOf("first"))
        val second = CaptureBox<String>(listOf(null, "second"))
        val host = CaptureHost<String>()
        check(host.capture(first, second, true) === first)
        check(host.capture(first, second, false) === second)
    }
}
