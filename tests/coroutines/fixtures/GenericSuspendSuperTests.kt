import NUnit.Framework.TestAttribute
import kotlin.coroutines.Continuation
import kotlin.coroutines.EmptyCoroutineContext
import kotlin.coroutines.resume
import kotlin.coroutines.startCoroutine
import kotlin.coroutines.suspendCoroutine

private fun genericSuperRunImmediately(block: suspend () -> Unit) {
    var completed = false
    block.startCoroutine(object : Continuation<Unit> {
        override val context = EmptyCoroutineContext
        override fun resumeWith(result: Result<Unit>) {
            result.getOrThrow()
            completed = true
        }
    })
    check(completed)
}

private open class GenericSuperBase<T>(private val value: T) {
    protected open suspend fun read(): T = value
    protected open suspend fun <R> echo(value: R): R = value
}

private class GenericSuperChild<T>(value: T) : GenericSuperBase<T>(value) {
    override suspend fun read(): T = super.read()
    suspend fun result(): T = read()
    suspend fun <R> methodResult(value: R): R = super.echo(value)
}

private open class GenericSuperPairBase<A, B> {
    protected open suspend fun select(first: A, second: B): B = second
}

private class GenericSuperPairChild<X, Y> : GenericSuperPairBase<Y, X>() {
    suspend fun selected(first: Y, second: X): X = super.select(first, second)
}

private class GenericSuperGate<T> {
    private var pending: Continuation<T>? = null
    suspend fun pause(): T = suspendCoroutine { pending = it }
    fun finish(value: T) {
        val continuation = pending!!
        pending = null
        continuation.resume(value)
    }
}

private open class GenericSuperPausedBase<T>(private val gate: GenericSuperGate<T>) {
    protected open suspend fun read(): T = gate.pause()
    protected open fun marker(): String = "base"
}

private class GenericSuperPausedChild<T>(gate: GenericSuperGate<T>) : GenericSuperPausedBase<T>(gate) {
    var trace = ""
    override fun marker(): String = "override"
    override suspend fun read(): T {
        trace += "before;"
        try {
            val value = super.read()
            // This nested try has no suspension: its receiver takes RewriteNoSpill rather than Rewrite.
            try {
                trace += super.marker() + ";"
            } finally {
                trace += "plain;"
            }
            trace += "after;"
            return value
        } finally {
            trace += super.marker()
        }
    }
    suspend fun result(): T = read()
}

class GenericSuspendSuperTests {
    @TestAttribute
    fun genericOwnersAndMethodFramesStayConstructed() = genericSuperRunImmediately {
        check(GenericSuperChild(17).result() == 17)
        check(GenericSuperChild("text").result() == "text")
        check(GenericSuperChild<Int?>(null).result() == null)
        check(GenericSuperChild("owner").methodResult(23) == 23)
        check(GenericSuperChild(11).methodResult("method") == "method")
    }

    @TestAttribute
    fun inheritedOwnerParametersKeepTheirOrder() = genericSuperRunImmediately {
        check(GenericSuperPairChild<Int, String>().selected("first", 31) == 31)
        check(GenericSuperPairChild<String, Int>().selected(31, "second") == "second")
        check(GenericSuperPairChild<Int?, String>().selected("first", null) == null)
    }

    @TestAttribute
    fun superCallsResumeOnceAndKeepNonvirtualFinallyDispatch() {
        val gate = GenericSuperGate<Int>()
        val child = GenericSuperPausedChild(gate)
        var completions = 0
        val block: suspend () -> Unit = { check(child.result() == 41) }
        block.startCoroutine(object : Continuation<Unit> {
            override val context = EmptyCoroutineContext
            override fun resumeWith(result: Result<Unit>) {
                result.getOrThrow()
                completions++
            }
        })
        check(completions == 0)
        check(child.trace == "before;")
        gate.finish(41)
        check(completions == 1)
        check(child.trace == "before;base;plain;after;base")
    }
}
