package roundtrip.selectedbounds

import RoundtripDependentNative.Base
import RoundtripDependentNative.IJoinedValue
import RoundtripDependentNative.ILeft
import RoundtripDependentNative.IRight
import System.Collections.Generic.Queue
import kotlin.clr.ClrRef

interface NominalFactory<K : Base> {
    fun <A : K> make(): Any
    fun <A : K> select(value: A): A = error("expected the overriding implementation")
}
interface JointFactory<K> where K : Base, K : ILeft, K : IRight {
    fun <A : K> make(): Any
    fun <A : K> select(value: A): A
}
interface SuspendFactory<K> where K : Base, K : ILeft, K : IRight {
    suspend fun <A : K> make(): Any
}
interface ReverseStorageFactory<K, P> where K : Base, K : IRight, K : ILeft {
    suspend fun <A : K> make(): Any
    fun replace(target: ClrRef<P>, input: P)
}
interface OverloadedPicker<K> where K : Base, K : ILeft, K : IRight {
    suspend fun <A : K> pick(value: K, source: Iterable<Int>): Any
    suspend fun <A : K> pick(value: K, source: Sequence<Int>): Any
}
abstract class OverloadedPickerBase<K> where K : Base, K : ILeft, K : IRight {
    abstract suspend fun <A : K> pick(value: K, source: Iterable<Int>): Any
    abstract suspend fun <A : K> pick(value: K, source: Sequence<Int>): Any
}
class LocalOverloadedPicker : OverloadedPicker<Concrete> {
    override suspend fun <A : Concrete> pick(value: Concrete, source: Iterable<Int>): Any = "iterable"
    override suspend fun <A : Concrete> pick(value: Concrete, source: Sequence<Int>): Any = "sequence"
}
interface SuspendPicker<K> where K : Base, K : ILeft, K : IRight {
    suspend fun <A : K> pick(value: K): Any
    fun <A : K> write(value: K, condition: Boolean, target: ClrRef<Int>)
}
// This call is compiled before the foreign interface joining two constructions exists.
suspend fun <K> pickThrough(picker: SuspendPicker<K>, value: K): Any where K : Base, K : ILeft, K : IRight =
    picker.pick<K>(value)
suspend fun pickStar(picker: SuspendPicker<Concrete>): Any =
    picker.pick<Leaf<*>>(Concrete())
class OtherItem : Concrete()
interface SuspendLeft<K> where K : Base, K : ILeft { suspend fun <A : K> make(): Any }
interface SuspendRight<K> where K : Base, K : IRight { suspend fun <A : K> make(): Any }
open class SuspendParent : SuspendFactory<Concrete> {
    override suspend fun <A : Concrete> make(): Any = Product<A>()
}
open class Concrete : Base(), ILeft, IRight
class Item : Concrete()
class Leaf<T> : Concrete()
interface Tagged { val tag: Int }
class TaggedLeaf<T>(override val tag: Int) : Tagged
interface TaggedIdentity<B : Tagged> { fun <A : B> keep(value: A): A }
class Product<T> : IJoinedValue<T> where T : Base, T : ILeft, T : IRight
interface DeferredNativeFactory {
    fun <A> later(): (A?) -> Any where A : Base, A : roundtrip.constrainedbound.JointProof
}
class DeferredOwner<A> where A : Base, A : roundtrip.constrainedbound.JointProof {
    fun make(factory: DeferredNativeFactory): Any = factory.later<A>()(null)
}
class NativeFactory : JointFactory<Concrete>, DeferredNativeFactory {
    override fun <A> later(): (A?) -> Any where A : Base, A : roundtrip.constrainedbound.JointProof =
        { RoundtripDependentNative.ReferenceProduct<A>() }
    override fun <A : Concrete> make(): Any = RoundtripDependentNative.NativeProduct<A>()
    override fun <A : Concrete> select(value: A): A {
        val deferred = { RoundtripDependentNative.NativeProduct<A>(value) }
        return deferred().Value
    }
}
interface LocalDefault : NominalFactory<Concrete>, JointFactory<Concrete> {
    override fun <A : Concrete> make(): Any = Product<A>()
    override fun <A : Concrete> select(value: A): A = value
}
class LocalFactory : LocalDefault

interface NullableFactory<K> where K : Base, K : ILeft, K : IRight {
    fun <A : K> take(queue: Queue<A?>): Any
}
open class GenericParent<C> : JointFactory<Concrete>, NullableFactory<Concrete> {
    override fun <A : Concrete> make(): Any = Product<A>()
    override fun <A : Concrete> select(value: A): A = value
    override fun <A : Concrete> take(queue: Queue<A?>): Any = queue
}

interface LeftFactory<K> where K : Base, K : ILeft { fun <A : K> make(): Any }
interface RightFactory<K> where K : Base, K : IRight { fun <A : K> make(): Any }
interface DefaultFactory<K : ILeft> { fun <A : K> make(): Any = "default" }
