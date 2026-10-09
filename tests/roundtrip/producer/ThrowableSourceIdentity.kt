package roundtrip.throwablesource

fun acceptThrowable(error: Throwable): String? = error.message
fun echoException(error: Exception): Exception = error
fun echoRuntimeException(error: RuntimeException): RuntimeException = error
fun echoIllegalStateException(error: IllegalStateException): IllegalStateException = error
fun echoNullableThrowable(error: Throwable?): Throwable? = error
fun echoThrowableList(errors: List<Throwable>): List<Throwable> = errors
fun Throwable.sourceMessage(): String? = message

class ErrorHolder(var error: Throwable) {
    fun accept(error: Throwable): String? = error.message
}
