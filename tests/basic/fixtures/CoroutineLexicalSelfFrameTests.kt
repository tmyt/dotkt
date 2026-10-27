import NUnit.Framework.TestAttribute
import kotlin.coroutines.Continuation
import kotlin.coroutines.CoroutineContext
import kotlin.coroutines.EmptyCoroutineContext

private class LexicalInlineOwner<T>(val value: T) {
    inline fun aliasValue(touch: () -> Unit): T { touch(); val self = this; return self.value }
    inline fun closureValue(crossinline touch: () -> Unit): () -> T = { touch(); value }
}

private fun <T> inlineLexicalContinuation(value: Continuation<T>): Continuation<T> {
    val owner = LexicalInlineOwner(value)
    var calls = 0
    check(owner.aliasValue { calls++ } === value)
    val read = owner.closureValue { calls++ }
    check(calls == 1)
    val result = read()
    check(result === value && calls == 2)
    return result
}

private class LexicalPayload(private val value: Int) {
    override fun equals(other: Any?): Boolean = other is LexicalPayload && other.value == value
    override fun hashCode(): Int = value
    override fun toString(): String = "payload:$value"
}

private class LexicalResultOuter<A, B>(val first: Result<A>, val second: Result<B>) {
    val nullableStorage: A? = null

    inner class Inner<C>(val marker: C) {
        fun first(): Result<A> = this@LexicalResultOuter.first
        fun second(): Result<B> = this@LexicalResultOuter.second
        fun deferred(): () -> Result<A> = { this@LexicalResultOuter.first }
        fun aliased(): Result<B> {
            val owner = this@LexicalResultOuter
            return owner.second
        }
    }
}

class CoroutineLexicalSelfFrameTests {
    @TestAttribute
    fun resultBodiesPreserveSuccessFailureAndPayloadContracts() {
        val payload = LexicalPayload(37)
        val success = Result.success(payload)
        check(success.isSuccess && !success.isFailure)
        check(success.getOrNull() === payload && success.exceptionOrNull() == null)
        check(success == Result.success(LexicalPayload(37)))
        check(success.hashCode() == 37 && success.toString() == "Success(payload:37)")
        val read: (Result<Int>) -> Int? = Result<Int>::getOrNull
        val render: (Result<Int>) -> String = Result<Int>::toString
        val hash: (Result<Int>) -> Int = Result<Int>::hashCode
        check(read(Result.success(41)) == 41)
        check(render(Result.success(41)) == "Success(41)" && hash(Result.success(41)) == 41)
        val nullable = Result.success<Int?>(null)
        check(nullable.isSuccess && nullable.getOrNull() == null && nullable.getOrThrow() == null)
        check(nullable.hashCode() == 0 && nullable.toString() == "Success(null)")
        val error = IllegalStateException("lexical failure")
        val failure = Result.failure<Int>(error)
        check(failure.isFailure && !failure.isSuccess && read(failure) == null)
        check(failure.exceptionOrNull() === error && failure.hashCode() == error.hashCode())
        check(failure.toString() == "Failure($error)")
        check(failure == Result.failure<String>(error))
        val owner = LexicalInlineOwner(Result.success(61))
        var calls = 0
        check(owner.aliasValue { calls++ }.getOrThrow() == 61)
        val deferred = owner.closureValue { calls++ }
        check(calls == 1 && deferred().getOrThrow() == 61 && calls == 2)
        val completion = object : Continuation<Int> {
            override val context: CoroutineContext get() = EmptyCoroutineContext
            override fun resumeWith(result: Result<Int>) {}
        }
        check(inlineLexicalContinuation(completion) === completion)
    }

    @TestAttribute
    fun nestedAndCapturedLexicalReceiversKeepTheirFrames() {
        val first = LexicalPayload(43)
        val outer = LexicalResultOuter(Result.success(first), Result.success(47))
        val inner = outer.Inner("marker")
        check(inner.marker == "marker" && outer.nullableStorage == null)
        check(inner.first().getOrThrow() === first)
        check(inner.second().getOrThrow() == 47)
        check(inner.deferred()().getOrThrow() === first)
        check(inner.aliased().getOrThrow() == 47)
        val nullable = LexicalResultOuter(Result.success<Int?>(null), Result.success("text")).Inner(53)
        check(nullable.first().getOrThrow() == null)
        check(nullable.second().getOrThrow() == "text")
        check(nullable.deferred()().getOrThrow() == null)
        check(nullable.aliased().getOrThrow() == "text")
    }
}
