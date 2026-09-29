package roundtrip.covarianttaskresults

interface AnyTaskResult<T> {
    suspend fun read(value: T, pause: suspend () -> Unit): Any
    suspend fun read(value: T, tag: String): Any
}
interface StringTaskResult<T> {
    suspend fun read(value: T, pause: suspend () -> Unit): String
}
open class ProducerStringTask<T> : AnyTaskResult<T>, StringTaskResult<T> {
    override suspend fun read(value: T, pause: suspend () -> Unit): String {
        pause()
        return "producer"
    }
    override suspend fun read(value: T, tag: String): String = tag
}

open class TaskResultBase(val text: String)
class TaskResultDerived(text: String) : TaskResultBase(text)
interface ReferenceTaskResult<T> {
    suspend fun read(value: T, pause: suspend () -> Unit): TaskResultBase
}

interface MethodTaskResult<T> {
    suspend fun <U : T> read(value: U, pause: suspend () -> Unit): Any
}
interface DefaultStringTask<T> : AnyTaskResult<T>, StringTaskResult<T> {
    override suspend fun read(value: T, pause: suspend () -> Unit): String {
        pause()
        return "default"
    }
    override suspend fun read(value: T, tag: String): String = tag
}
