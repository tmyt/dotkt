package roundtrip.covarianttaskresults

import RoundtripDependentNative.Base
import RoundtripDependentNative.ILeft
import RoundtripDependentNative.IRight
import System.Threading.Tasks.Task1
import roundtrip.selectedbounds.Concrete

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
interface ProjectedResult<K> where K : Base, K : ILeft, K : IRight {
    suspend fun <A : K> read(value: K, gate: Task1<Int>): TaskResultBase
}
class ProjectedDerivedResult : ProjectedResult<Concrete> {
    override suspend fun <A : Concrete> read(value: Concrete, gate: Task1<Int>): TaskResultDerived {
        gate.await()
        return TaskResultDerived("projected")
    }
}
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
