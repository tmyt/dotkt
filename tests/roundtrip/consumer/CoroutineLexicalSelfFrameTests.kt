package roundtriptests.coroutinelexicalself

import NUnit.Framework.TestAttribute
import kotlin.coroutines.Continuation
import kotlin.coroutines.EmptyCoroutineContext
import roundtrip.coroutinelexicalself.LexicalCompletion
import roundtrip.coroutinelexicalself.LexicalInlineOwner
import roundtrip.coroutinelexicalself.forwardLexicalResult
import roundtrip.coroutinelexicalself.resumeLexicalCompletion

private fun <T> inlineImportedContinuation(value: Continuation<T>): Continuation<T> {
    val owner = LexicalInlineOwner(value)
    var calls = 0
    check(owner.aliasValue { calls++ } === value)
    val read = owner.closureValue { calls++ }
    check(calls == 1)
    val result = read()
    check(result === value && calls == 2)
    return result
}

class CoroutineLexicalSelfFrameTests {
    @TestAttribute
    fun importedContinuationValuesRetainContextAndResultDispatch() {
        val value = Any()
        val references = LexicalCompletion<Any>()
        val referenceSlot = references.asContinuation()
        check((referenceSlot as Any) === references)
        check(referenceSlot.context === EmptyCoroutineContext)
        check(references.aliasedContext() === EmptyCoroutineContext)
        resumeLexicalCompletion(referenceSlot, forwardLexicalResult(Result.success(value)))
        check(references.result!!.getOrThrow() === value && references.resumes == 1)
        val error = IllegalStateException("imported lexical failure")
        resumeLexicalCompletion(referenceSlot, forwardLexicalResult(Result.failure(error)))
        check(references.result!!.exceptionOrNull() === error && references.resumes == 2)
        val values = LexicalCompletion<Int>()
        resumeLexicalCompletion(values.asContinuation(), forwardLexicalResult(Result.success(59)))
        check(values.result!!.getOrThrow() == 59 && values.resumes == 1)
        val nullable = LexicalCompletion<Int?>()
        resumeLexicalCompletion(nullable.asContinuation(), forwardLexicalResult(Result.success(null)))
        check(nullable.result!!.isSuccess && nullable.result!!.getOrThrow() == null)
        resumeLexicalCompletion(nullable.asContinuation(), forwardLexicalResult(Result.success(61)))
        check(nullable.result!!.getOrThrow() == 61 && nullable.resumes == 2)
        val owner = LexicalInlineOwner(Result.success(67))
        var calls = 0
        check(owner.aliasValue { calls++ }.getOrThrow() == 67)
        val deferred = owner.closureValue { calls++ }
        check(calls == 1 && deferred().getOrThrow() == 67 && calls == 2)
        check(inlineImportedContinuation(referenceSlot) === referenceSlot)
        check(inlineImportedContinuation(values.asContinuation()) === values)
        val star: Continuation<*> = references
        check(star.context === EmptyCoroutineContext)
        star.resumeWith(Result.failure(error))
        check(references.result!!.exceptionOrNull() === error && references.resumes == 3)
    }
}
