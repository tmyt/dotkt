import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import kotlin.coroutines.*
import constrainedsuspend.ReferencedSuspendSink
import constrainedsuspend.ReferencedSuspendAnimal
import constrainedsuspend.ReferencedSuspendDog

class ConsumerSuspendSink : ReferencedSuspendSink<ReferencedSuspendAnimal>() {
    override suspend fun <R : ReferencedSuspendAnimal> render(value: R, pause: suspend () -> Unit): String {
        pause()
        val captured = { "consumer:$value" }
        return captured()
    }
}

private fun referencedSuspendRun(expected: String, body: suspend (suspend () -> Unit) -> String) {
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

class OwnerConstrainedSuspendRoundtripTests {
    @TestAttribute
    fun referencedSuspendBoundsAndOverridesRetainTheirFrames() {
        val strings: ReferencedSuspendSink<String> = ReferencedSuspendSink<Any>()
        referencedSuspendRun("text") { pause -> strings.render("text", pause) }
        referencedSuspendRun("text") { pause -> strings.echo("text", pause) }
        val integers: ReferencedSuspendSink<Int> = ReferencedSuspendSink<Any>()
        referencedSuspendRun("42") { pause -> integers.echo(42, pause).toString() }
        val animals: ReferencedSuspendSink<ReferencedSuspendDog> = ConsumerSuspendSink()
        referencedSuspendRun("consumer:dog") { pause -> animals.render(ReferencedSuspendDog(), pause) }
    }
}
