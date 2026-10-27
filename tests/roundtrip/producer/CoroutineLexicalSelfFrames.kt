package roundtrip.coroutinelexicalself

import kotlin.coroutines.Continuation
import kotlin.coroutines.CoroutineContext
import kotlin.coroutines.EmptyCoroutineContext

class LexicalCompletion<T> : Continuation<T> {
    override val context: CoroutineContext get() = EmptyCoroutineContext
    var result: Result<T>? = null
    var resumes: Int = 0
    override fun resumeWith(result: Result<T>) {
        this.result = result
        resumes++
    }
    fun asContinuation(): Continuation<T> = this
    fun aliasedContext(): CoroutineContext {
        val self = this
        return self.context
    }
}

fun <T> forwardLexicalResult(result: Result<T>): Result<T> = result
fun <T> resumeLexicalCompletion(completion: Continuation<T>, result: Result<T>) {
    completion.resumeWith(result)
}
