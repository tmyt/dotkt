package existentialcapture

import NUnit.Framework.TestAttribute
import kotlin.coroutines.*
import kotlin.clr.ClrField

private class FieldOwner<A>(private val ownerValue: A) {
    lateinit var label: String
    @ClrField var hits: Int = 0
    fun <T> run(value: T): String {
        label = "L"
        fun selected(): suspend (T) -> String = { item ->
            hits++
            "$label:$ownerValue:$item"
        }
        var output = ""
        val work: suspend () -> Unit = { output = selected()(value) }
        work.startCoroutine(object : Continuation<Unit> {
            override val context: CoroutineContext get() = EmptyCoroutineContext
            override fun resumeWith(result: Result<Unit>) { result.getOrThrow() }
        })
        check(hits == 1)
        return output
    }
}

private fun interface ValueReader<T> { fun read(): T }
private class CaptureGate {
    private var pending: Continuation<Unit>? = null
    suspend fun pause() = suspendCoroutine<Unit> { pending = it }
    fun release() { val next = pending!!; pending = null; next.resume(Unit) }
}
private class OuterOwner<A>(val outer: A) {
    inner class InnerOwner<B>(val inner: B) {
        fun <C> create(item: C, useReference: Boolean, gate: CaptureGate): suspend () -> Triple<A, B, C> {
            fun selected(): suspend () -> Triple<A, B, C> = {
                gate.pause()
                Triple(outer, inner, item)
            }
            val factory: () -> (suspend () -> Triple<A, B, C>) = ::selected
            return if (useReference) factory() else selected()
        }
    }
}
private class NestedOwner<T>(val value: T) {
    fun <U> createExtension(other: U): suspend String.() -> Pair<T, U> {
        fun selected(): suspend String.() -> Pair<T, U> = {
            check(this.length == 3)
            class Local(val marker: String) { fun own(): String = this.marker }
            check(Local("inner").own() == "inner")
            Pair(value, other)
        }
        return selected()
    }
    fun <U> create(other: U): suspend () -> Pair<T, U> {
        fun selected(): suspend () -> Pair<T, U> = {
            val ordinary = { value }
            val reader = ValueReader { value }
            val nested: suspend () -> T = { value }
            check(ordinary() == value)
            check(reader.read() == value)
            Pair(nested(), other)
        }
        return selected()
    }
}
class ExistentialSuspendCaptureTests {
    @TestAttribute
    fun capturesKeepLexicalReceiversAndStorageAcrossSuspension() {
        check(FieldOwner("owner").run(42) == "L:owner:42")
        var completed: Pair<String, Int>? = null
        val work = NestedOwner("owner").create(42)
        work.startCoroutine(object : Continuation<Pair<String, Int>> {
            override val context: CoroutineContext get() = EmptyCoroutineContext
            override fun resumeWith(result: Result<Pair<String, Int>>) { completed = result.getOrThrow() }
        })
        check(completed == Pair("owner", 42))
        var extensionResult: Pair<Int, String>? = null
        val extension = NestedOwner(17).createExtension("other")
        extension.startCoroutine("ext", object : Continuation<Pair<Int, String>> {
            override val context: CoroutineContext get() = EmptyCoroutineContext
            override fun resumeWith(result: Result<Pair<Int, String>>) { extensionResult = result.getOrThrow() }
        })
        check(extensionResult == Pair(17, "other"))
        for (useReference in listOf(false, true)) {
            var nestedResult: Triple<String, Int, Boolean>? = null
            val gate = CaptureGate()
            OuterOwner("outer").InnerOwner(29).create(true, useReference, gate).startCoroutine(
                object : Continuation<Triple<String, Int, Boolean>> {
                    override val context: CoroutineContext get() = EmptyCoroutineContext
                    override fun resumeWith(result: Result<Triple<String, Int, Boolean>>) {
                        nestedResult = result.getOrThrow()
                    }
                }
            )
            check(nestedResult == null)
            gate.release()
            check(nestedResult == Triple("outer", 29, true))
        }
    }
}

