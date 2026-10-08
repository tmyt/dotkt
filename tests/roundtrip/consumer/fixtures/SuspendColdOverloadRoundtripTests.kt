import NUnit.Framework.TestAttribute
import kotlin.coroutines.*
import roundtrip.suspendcoldoverloads.SelectionGate
import roundtrip.suspendcoldoverloads.selectCold
import roundtrip.suspendcoldoverloads.selectGenericCold

private fun <T> verifyColdSelection(
    gate: SelectionGate,
    label: String,
    expected: T,
    operation: suspend () -> T,
) {
    var completed = false
    var actual: T? = null
    operation.startCoroutine(object : Continuation<T> {
        override val context: CoroutineContext = EmptyCoroutineContext
        override fun resumeWith(result: Result<T>) {
            actual = result.getOrThrow()
            completed = true
        }
    })
    check(!completed && gate.trace == label)
    val continuation = gate.pending ?: error("selected overload did not suspend")
    gate.pending = null
    continuation.resume("resumed")
    check(completed && actual == expected)
}

class SuspendColdOverloadRoundtripTests {
    @TestAttribute
    fun erasedCollectionReceiverSelectsItsColdDeclaration() {
        val gate = SelectionGate()
        verifyColdSelection(gate, "collection", "C:resumed:2") {
            listOf("first", "second").selectCold(gate)
        }
    }

    @TestAttribute
    fun nativeArrayReceiverSelectsItsColdDeclaration() {
        val gate = SelectionGate()
        verifyColdSelection(gate, "array", "A:resumed:1") {
            arrayOf("first").selectCold(gate)
        }
    }

    @TestAttribute
    fun genericErasedCollectionReceiverKeepsItsMethodFrame() {
        val gate = SelectionGate()
        verifyColdSelection(gate, "generic-collection", 42) {
            listOf(42, 43).selectGenericCold(gate)
        }
    }

    @TestAttribute
    fun genericArrayReceiverKeepsItsMethodFrame() {
        val gate = SelectionGate()
        verifyColdSelection(gate, "generic-array", "first") {
            arrayOf("first", "second").selectGenericCold(gate)
        }
    }
}
