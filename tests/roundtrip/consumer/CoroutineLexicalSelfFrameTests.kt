package roundtriptests.coroutinelexicalself

import NUnit.Framework.TestAttribute
import kotlin.coroutines.EmptyCoroutineContext
import roundtrip.coroutinelexicalself.LexicalCompletion
import roundtrip.coroutinelexicalself.forwardLexicalResult
import roundtrip.coroutinelexicalself.resumeLexicalCompletion

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
    }
}
