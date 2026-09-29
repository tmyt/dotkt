import NUnit.Framework.TestAttribute
import kotlin.coroutines.*
import roundtrip.covarianttaskresults.*

private class TaskResultOuter { class Nested }
private interface LocalAnyTaskResult<T> {
    suspend fun read(value: T, pause: suspend () -> Unit): Any
}
private class ConsumerStringTask : AnyTaskResult<TaskResultOuter.Nested>, StringTaskResult<TaskResultOuter.Nested>,
    LocalAnyTaskResult<TaskResultOuter.Nested> {
    override suspend fun read(value: TaskResultOuter.Nested, pause: suspend () -> Unit): String {
        pause()
        return "consumer"
    }
    override suspend fun read(value: TaskResultOuter.Nested, tag: String): String = tag
}
private class LocalGenericStringTask<T> : LocalAnyTaskResult<T>, AnyTaskResult<T>, StringTaskResult<T> {
    override suspend fun read(value: T, pause: suspend () -> Unit): String {
        pause()
        return "generic"
    }
    override suspend fun read(value: T, tag: String): String = tag
}
private class InheritedStringTask<T> : ProducerStringTask<T>(), LocalAnyTaskResult<T>
private class StringArgumentTask : AnyTaskResult<String>, StringTaskResult<String> {
    override suspend fun read(value: String, pause: suspend () -> Unit): String { pause(); return value }
    override suspend fun read(value: String, tag: String): String = tag
}
private class IntArgumentTask : AnyTaskResult<Int>, StringTaskResult<Int> {
    override suspend fun read(value: Int, pause: suspend () -> Unit): String { pause(); return "$value" }
    override suspend fun read(value: Int, tag: String): String = tag
}
private class ConstrainedMethodTask<T> : MethodTaskResult<T> {
    override suspend fun <U : T> read(value: U, pause: suspend () -> Unit): String { pause(); return "method" }
}
private class DefaultTaskBody<T> : DefaultStringTask<T>
private class ConsumerReferenceTask : ReferenceTaskResult<TaskResultOuter.Nested> {
    override suspend fun read(value: TaskResultOuter.Nested, pause: suspend () -> Unit): TaskResultDerived {
        pause()
        return TaskResultDerived("reference")
    }
}

private fun checkCovariantTask(expected: String, block: suspend (suspend () -> Unit) -> String) {
    var pending: Continuation<Unit>? = null
    var completed = false
    var actual = ""
    val action: suspend () -> String = { block { suspendCoroutine<Unit> { pending = it } } }
    action.startCoroutine(object : Continuation<String> {
        override val context: CoroutineContext = EmptyCoroutineContext
        override fun resumeWith(result: Result<String>) { actual = result.getOrThrow(); completed = true }
    })
    check(!completed)
    val continuation = pending
    check(continuation != null)
    continuation.resume(Unit)
    check(completed)
    check(actual == expected)
}

class CovariantTaskResultTests {
    @TestAttribute
    fun exactInterfaceBridgesPreserveTheNarrowTaskBody() {
        val value = TaskResultOuter.Nested()
        val producer = ProducerStringTask<TaskResultOuter.Nested>()
        val consumer = ConsumerStringTask()
        val generic = LocalGenericStringTask<TaskResultOuter.Nested>()
        val inherited = InheritedStringTask<TaskResultOuter.Nested>()
        checkCovariantTask("producer") { pause -> producer.read(value, pause) }
        checkCovariantTask("consumer") { pause -> consumer.read(value, pause) }
        checkCovariantTask("generic") { pause -> generic.read(value, pause) }
        checkCovariantTask("producer") { pause -> inherited.read(value, pause) }
        val wide: List<AnyTaskResult<TaskResultOuter.Nested>> = listOf(producer, consumer, generic, inherited)
        val narrow: List<StringTaskResult<TaskResultOuter.Nested>> = listOf(producer, consumer, generic, inherited)
        val expected = listOf("producer", "consumer", "generic", "producer")
        for (i in expected.indices) {
            checkCovariantTask(expected[i]) { pause -> wide[i].read(value, pause) as String }
            checkCovariantTask(expected[i]) { pause -> narrow[i].read(value, pause) }
            checkCovariantTask("overload") { pause ->
                pause()
                wide[i].read(value, "overload") as String
            }
        }
        checkCovariantTask("consumer") { pause -> (consumer as LocalAnyTaskResult<TaskResultOuter.Nested>).read(value, pause) as String }
        checkCovariantTask("generic") { pause -> (generic as LocalAnyTaskResult<TaskResultOuter.Nested>).read(value, pause) as String }
        checkCovariantTask("producer") { pause -> (inherited as LocalAnyTaskResult<TaskResultOuter.Nested>).read(value, pause) as String }
        val reference = ConsumerReferenceTask()
        checkCovariantTask("reference") { pause -> reference.read(value, pause).text }
        checkCovariantTask("reference") { pause -> (reference as ReferenceTaskResult<TaskResultOuter.Nested>).read(value, pause).text }
        val string = StringArgumentTask()
        checkCovariantTask("string") { pause -> string.read("string", pause) }
        checkCovariantTask("string") { pause -> (string as AnyTaskResult<String>).read("string", pause) as String }
        val int = IntArgumentTask()
        checkCovariantTask("42") { pause -> int.read(42, pause) }
        checkCovariantTask("42") { pause -> (int as AnyTaskResult<Int>).read(42, pause) as String }
        val method = ConstrainedMethodTask<TaskResultBase>()
        checkCovariantTask("method") { pause -> method.read(TaskResultDerived("argument"), pause) }
        checkCovariantTask("method") { pause -> (method as MethodTaskResult<TaskResultBase>).read(TaskResultDerived("argument"), pause) as String }
        val default = DefaultTaskBody<TaskResultOuter.Nested>()
        checkCovariantTask("default") { pause -> default.read(value, pause) }
        checkCovariantTask("default") { pause -> (default as AnyTaskResult<TaskResultOuter.Nested>).read(value, pause) as String }
    }
}
