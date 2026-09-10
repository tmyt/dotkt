import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import kotlin.coroutines.*

open class OwnerSuspendSink<in T> {
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

open class OwnerSuspendAnimal
class OwnerSuspendDog : OwnerSuspendAnimal() {
    override fun toString(): String = "dog"
}

class OwnerSuspendAnimalSink : OwnerSuspendSink<OwnerSuspendAnimal>() {
    override suspend fun <R : OwnerSuspendAnimal> render(value: R, pause: suspend () -> Unit): String {
        pause()
        val captured = { "derived:$value" }
        return captured()
    }
}

class OwnerSuspendGenericSink<in T> : OwnerSuspendSink<T>() {
    override suspend fun <R : T> render(value: R, pause: suspend () -> Unit): String {
        pause()
        return "generic:$value"
    }
}

// Resume only after startCoroutine returns, so every call crosses a real suspension boundary.
private fun ownerSuspendRun(expected: String, body: suspend (suspend () -> Unit) -> String) {
    var actual = "pending"
    var pending: Continuation<Unit>? = null
    val pause: suspend () -> Unit = { suspendCoroutine<Unit> { pending = it } }
    val action: suspend () -> String = { body(pause) }
    action.startCoroutine(object : Continuation<String> {
        override val context: CoroutineContext = EmptyCoroutineContext
        override fun resumeWith(result: Result<String>) { actual = result.getOrThrow() }
    })
    assertEquals("pending", actual)
    pending!!.resume(Unit)
    assertEquals(expected, actual)
}

class OwnerConstrainedSuspendTests {
    @TestAttribute
    fun ownerAndMethodFramesSurviveSuspensionAndOverrides() {
        val strings: OwnerSuspendSink<String> = OwnerSuspendSink<Any>()
        ownerSuspendRun("text") { pause -> strings.render("text", pause) }
        ownerSuspendRun("text") { pause -> strings.echo("text", pause) }
        val integers: OwnerSuspendSink<Int> = OwnerSuspendSink<Any>()
        ownerSuspendRun("42") { pause -> integers.render(42, pause) }
        ownerSuspendRun("43") { pause -> integers.echo(43, pause).toString() }
        val animals: OwnerSuspendSink<OwnerSuspendDog> = OwnerSuspendAnimalSink()
        ownerSuspendRun("derived:dog") { pause -> animals.render(OwnerSuspendDog(), pause) }
        val generic: OwnerSuspendSink<Int> = OwnerSuspendGenericSink<Any>()
        ownerSuspendRun("generic:44") { pause -> generic.render(44, pause) }
    }
}
