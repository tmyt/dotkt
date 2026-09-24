package sharedcaptureboundframe

import NUnit.Framework.TestAttribute

open class CaptureBox<T>(val items: List<T?>)
private inline fun visit(block: () -> Unit) { block() }
private fun interface CaptureAction { fun run() }
private inline fun defer(crossinline block: () -> Unit): () -> Unit = { block() }
private inline fun deferSam(crossinline block: () -> Unit): CaptureAction = CaptureAction { block() }

private fun <T, U : CaptureBox<T>> deferred(initial: U, replacement: U): U {
    var current = initial
    val action = deferSam { current = replacement }
    check(current === initial)
    action.run()
    check(current === replacement)
    val function = defer { current = initial }
    function()
    return current
}

private fun <T, U : CaptureBox<T>> capture(value: U, early: Boolean): U {
    var current = value
    visit { if (early) return current }
    current = value
    return current
}

private class CaptureHost<X> {
    fun <U : CaptureBox<X>> deferred(initial: U, replacement: U): U {
        var current = initial
        val function = defer { current = replacement }
        check(current === initial)
        function()
        return current
    }
    fun <U : CaptureBox<X>> capture(initial: U, replacement: U, early: Boolean): U {
        var current = initial
        visit { if (early) return current }
        current = replacement
        return current
    }
}

class SharedCaptureBoundFrameTests {
    @TestAttribute
    fun materializedSamAndClosureRetainCellBounds() {
        val first = CaptureBox<String>(listOf(null, "first"))
        val second = CaptureBox<String>(listOf("second"))
        check(deferred(first, second) === first)
        check(CaptureHost<String>().deferred(first, second) === second)
        val integer = CaptureBox<Int>(listOf(null, 1))
        check(deferred(integer, CaptureBox<Int>(listOf(2))) === integer)
    }

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
