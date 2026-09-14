package genericmutablecapture

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import kotlin.coroutines.*

private fun <T> capturedResult(value: T): T {
    var outcome: Result<T>? = null
    val continuation = object : Continuation<T> {
        override val context: CoroutineContext get() = EmptyCoroutineContext
        override fun resumeWith(result: Result<T>) { outcome = result }
    }
    continuation.resume(value)
    return outcome!!.getOrThrow()
}

private class CaptureOwner<A, B>(val firstValue: A, val secondValue: B) {
    fun <T> exchange(initial: T, next: T): T {
        var outcome: T = initial
        var first: A = firstValue
        var second: B = secondValue
        class Local<U>(val other: U) {
            fun update(value: T): T {
                check(other != null)
                first = firstValue
                second = secondValue
                val before = outcome
                outcome = value
                return before
            }
        }
        val left = Local(17)
        val right = Local("right")
        check(left.update(next) == initial)
        check(right.update(initial) == next)
        check(left.update(next) == initial)
        check(first == firstValue && second == secondValue)
        return outcome
    }
}

private class NestedCaptureOwner<A, B>(val first: A, val second: B) {
    fun <T> exchange(initial: T, next: T): T {
        var current = initial
        class Local<U>(val other: U) {
            fun update(): T {
                val writer = object {
                    fun write() {
                        check(first != null && second != null && other != null)
                        current = next
                    }
                }
                writer.write()
                return current
            }
        }
        return Local(19).update()
    }
}

private class DistinctCaptureOwner<A>(val value: A) {
    fun exchange(): Int {
        var outer = value
        class Local<U>(val initial: U) {
            fun update(next: U): U {
                var inner = initial
                val writer = object {
                    fun write() { outer = value; inner = next }
                }
                writer.write()
                check(outer == value)
                return inner
            }
        }
        return Local(1).update(2)
    }
}

// Public bound: private-interface bound calls have a separate known Star-lowering defect.
interface CaptureBound<T> { fun get(): T }
class StringCaptureBound(val value: String) : CaptureBound<String> {
    override fun get() = value
}

fun <T, U : CaptureBound<T>> capturedBound(value: U): T {
    var current = value
    val writer = object { fun write(next: U) { current = next } }
    writer.write(value)
    return current.get()
}

private fun <T> shadowedCapture(seed: T, next: T): T {
    var current = seed
    class Local<T>(val own: T) { fun write() { check(own != null); current = next } }
    Local(1).write()
    return current
}

private fun <T> localFunctionCaptures(seed: T, next: T): T {
    var outer = seed
    fun writeOuter() { outer = next }
    val caller = object { fun invoke() { writeOuter() } }
    caller.invoke()
    check(outer == next)
    fun <U> local(value: U): U {
        var inner = value
        fun nested() {
            val writer = object { fun write() { inner = value } }
            writer.write()
        }
        nested()
        return inner
    }
    return local(outer)
}

private fun <T> liftedElementCell(seed: T, next: T): T {
    class Holder(val value: T)
    var current = Holder(seed)
    val writer = object { fun write() { current = Holder(next) } }
    writer.write()
    return current.value
}

private class PairCaptureOwner<A>(val first: A) {
    fun <T> exchange(seed: T, next: T): Pair<A, T> {
        var current = first to seed
        val writer = object { fun write() { current = first to next } }
        writer.write()
        return current
    }
}

private class LocalReferenceOwner<A, B>(val first: A, val second: B) {
    fun <T> verify(value: T) {
        class Local<U>(val own: U) {
            fun read(): Triple<A, B, T> = Triple(first, second, value)
        }
        val left: Local<Int> = Local(1)
        val list: MutableList<Local<String>> = mutableListOf(Local("right"))
        val pair: Pair<Local<Int>, Local<String>> = left to list[0]
        check(pair.first.own == 1 && pair.second.own == "right")
        check(pair.first.read() == Triple(first, second, value))
        check(pair.second.read() == Triple(first, second, value))
    }
}

private inline fun <T : Comparable<T>> inlineBoundCell(seed: T, next: T): T {
    var current = seed
    run {
        val writer = object { fun write() { current = next } }
        writer.write()
    }
    check(current.compareTo(next) == 0)
    return current
}

private fun <T : Comparable<T>> boundCell(seed: T, next: T): T {
    var current = seed
    val writer = object { fun write() { current = next } }
    writer.write()
    check(current.compareTo(next) == 0)
    return current
}

class GenericMutableCaptureTests {
    @TestAttribute
    fun anonymousContinuationWritesGenericResultCell() {
        assertEquals("ok", capturedResult("ok"))
        assertEquals(42, capturedResult(42))
        assertEquals(null, capturedResult<String?>(null))
    }

    @TestAttribute
    fun localClassesShareCellsAcrossDistinctGenericFrames() {
        assertEquals(43, CaptureOwner("owner", true).exchange(42, 43))
        assertEquals("next", CaptureOwner(3, "second").exchange("value", "next"))
    }

    @TestAttribute
    fun nestedAnonymousClassWritesEnclosingMethodCell() {
        assertEquals(2, NestedCaptureOwner("owner", true).exchange(1, 2))
    }

    @TestAttribute
    fun equalPositionsInDistinctDeclarationsKeepSeparateCells() {
        assertEquals(2, DistinctCaptureOwner("owner").exchange())
    }

    @TestAttribute
    fun capturedTypeParameterPreservesDependentBound() {
        assertEquals("bound", capturedBound(StringCaptureBound("bound")))
    }

    @TestAttribute
    fun shadowedParameterNamesRetainDistinctDeclarations() {
        assertEquals("after", shadowedCapture("before", "after"))
    }

    @TestAttribute
    fun localFunctionAndAnonymousBoundariesShareCells() {
        assertEquals("after", localFunctionCaptures("before", "after"))
        assertEquals(2, localFunctionCaptures(1, 2))
    }

    @TestAttribute
    fun cellElementPreservesConstructedTypeArguments() {
        assertEquals("after", liftedElementCell("before", "after"))
        assertEquals("owner" to 2, PairCaptureOwner("owner").exchange(1, 2))
    }

    @TestAttribute
    fun localClassReferencesRetainCapturedArgumentsWithoutCells() {
        LocalReferenceOwner("owner", true).verify(42)
    }

    @TestAttribute
    fun recursiveBoundsSurviveInlineAndAnonymousCaptures() {
        assertEquals(2, boundCell(1, 2))
        assertEquals("after", boundCell("before", "after"))
        assertEquals(2, inlineBoundCell(1, 2))
        assertEquals("after", inlineBoundCell("before", "after"))
    }
}
