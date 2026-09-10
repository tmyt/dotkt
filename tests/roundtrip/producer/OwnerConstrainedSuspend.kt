package constrainedsuspend

open class ReferencedSuspendSink<in T> {
    open suspend fun <R : T> render(value: R, pause: suspend () -> Unit): String {
        pause()
        val captured = { value.toString() }
        return captured()
    }

    suspend fun <R : T> echo(value: R, pause: suspend () -> Unit): R {
        pause()
        return value
    }
}

open class ReferencedSuspendAnimal
class ReferencedSuspendDog : ReferencedSuspendAnimal() {
    override fun toString(): String = "dog"
}
