// feature fixture — il-inlmatsetcap: §4.4ii ref-cell write-through — a MATERIALIZED (non-suspend newClosure) carrier that
// MUTATES a captured enclosing var must write through so the mutation is visible after the inline call. All top-level
// declarations use the descriptive `materializedCapture`/`MaterializedCapture` stem so their simple names are UNIQUE across this
// assembly (bir2cir's cold-core suspend lowering keys top-level suspend funs by simple name). Driven by the shared
// `dotkt.support.blockOn` harness; the former `main` + golden -> one @TestAttribute method (value 1:1).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import kotlin.coroutines.Continuation
import kotlin.coroutines.resume
import kotlin.coroutines.intrinsics.suspendCoroutineUninterceptedOrReturn
import kotlin.coroutines.intrinsics.COROUTINE_SUSPENDED
import dotkt.support.blockOn

suspend inline fun <T> materializedCaptureSuspend(crossinline block: (Continuation<T>) -> Unit): T =
    suspendCoroutineUninterceptedOrReturn { uCont -> block(uCont); COROUTINE_SUSPENDED }

suspend fun materializedCaptureWrite(): Int {
    var acc = 0
    return materializedCaptureSuspend { cont -> acc += 10; cont.resume(acc) }
}

private class MaterializedConstructedBox<T>(val continuation: Continuation<T>)

private suspend inline fun <T> materializedConstructedSuspend(
    crossinline block: (MaterializedConstructedBox<T>) -> Unit
): T = suspendCoroutineUninterceptedOrReturn { uCont ->
    val box = MaterializedConstructedBox<T>(uCont)
    block(box)
    COROUTINE_SUSPENDED
}

private class MaterializedConstructedOwner<T>(private val value: T) {
    suspend fun awaitList(): List<T> = materializedConstructedSuspend<List<T>> { box ->
        box.continuation.resume(listOf(value))
    }
}

private class MaterializedMixedBox<A, B>(val continuation: Continuation<A>, val tag: B)

private suspend inline fun <A, B> materializedMixedSuspend(
    tag: B,
    crossinline block: (MaterializedMixedBox<A, B>) -> Unit
): A = suspendCoroutineUninterceptedOrReturn { uCont ->
    block(MaterializedMixedBox<A, B>(uCont, tag))
    COROUTINE_SUSPENDED
}

private class MaterializedMixedOwner<T>(private val value: T) {
    suspend fun awaitTaggedList(): List<T> = materializedMixedSuspend<List<T>, T>(value) { box ->
        box.continuation.resume(listOf(box.tag))
    }
}

private class MaterializedOverloadedSlot<T> {
    val selected: Int
    constructor(value: T) { selected = 1 }
    constructor(value: Int) { selected = 2 }
}

private suspend inline fun <T> materializedSelectedConstructor(value: T): Int =
    suspendCoroutineUninterceptedOrReturn { continuation ->
        continuation.resume(MaterializedOverloadedSlot<T>(value).selected)
        COROUTINE_SUSPENDED
    }

class MaterializedLambdaCaptureTests {
    @TestAttribute
    fun refCellWriteThroughMaterializedCarrier() {
        assertEquals(10, blockOn { materializedCaptureWrite() })   // 10 — ref-cell write-through through the materialized carrier
    }

    @TestAttribute
    fun constructedSpecializationKeepsTheOwnersExactGenericFrame() {
        assertEquals(listOf("OK"), blockOn { MaterializedConstructedOwner("OK").awaitList() })
        assertEquals(Unit, blockOn {
            materializedConstructedSuspend<Unit> { it.continuation.resume(Unit) }
        })
        assertEquals(42, blockOn {
            materializedConstructedSuspend<Int> { it.continuation.resume(42) }
        })
        assertEquals("closed", blockOn {
            materializedConstructedSuspend<String> { it.continuation.resume("closed") }
        })
        assertEquals(1, blockOn { materializedSelectedConstructor(42) })
        assertEquals(1, blockOn { materializedSelectedConstructor("value") })
        assertEquals(2, MaterializedOverloadedSlot<Int>(42).selected)
    }

    @TestAttribute
    fun constructedAndDirectSpecializationsShareTheExactOwnerFrame() {
        assertEquals(listOf("OK"), blockOn { MaterializedMixedOwner("OK").awaitTaggedList() })
    }
}
