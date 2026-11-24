// #345 producer half: the consumer imports this interface and its implementations through the emitted DLL/KLIB.
package roundtrip.constrainedbound

import System.Collections.Generic.List as ClrList
import RoundtripDependentNative.Base
import RoundtripDependentNative.IMarker
import RoundtripDependentNative.Selector
import kotlin.clr.ClrRef
import kotlin.clr.byref
import roundtrip.clrrefparameters.ReferenceBox

class NativeChoice<T>(val value: T) : Base(), IMarker<String>
open class LastNativeChoice : Selector() {
    override fun <X,Y> Capture(value: X, key: RoundtripDependentNative.IKey<Y>): RoundtripDependentNative.IKey<Y>
        where X : Base, X : IMarker<String> = key
    override fun <X,Y> Capture(value: X, key: RoundtripDependentNative.IKey<Y>, visits: ClrRef<Int>): RoundtripDependentNative.IKey<Y>
        where X : Base, X : IMarker<String> {
        visits.value += 2
        return key
    }
    override fun <X,Y> Pair(first: X, second: Y): Y
        where X : Base, X : IMarker<String>, Y : Base, Y : IMarker<String> = second
    protected override fun <T> Visit(item: T, count: ClrRef<Int>): T where T : Base, T : IMarker<String> {
        var visits = count.value
        val result = super.Visit<T>(item, byref(visits))
        count.value = visits
        return result
    }
    override fun <T> Choose(first: T, last: T): T where T : Base, T : IMarker<String> = last
    override fun <T> Choose(first: T, last: T, visits: ClrRef<Int>): T where T : Base, T : IMarker<String> {
        visits.value += 2
        return last
    }
    fun base(a: NativeChoice<*>, b: NativeChoice<*>) = super.Choose<NativeChoice<*>>(a, b)
    fun baseRef(a: NativeChoice<*>, b: NativeChoice<*>): Boolean {
        var visits = 0
        return super.Choose<NativeChoice<*>>(a, b, byref(visits)) === a && visits == 1
    }
    fun visitOther(other: LastNativeChoice, a: NativeChoice<*>): Boolean {
        var visits = 0
        return other.Visit<NativeChoice<*>>(a, byref(visits)) === a && visits == 1
    }
}
fun localNativeChoice(s: LastNativeChoice, a: NativeChoice<*>, b: NativeChoice<*>) = s.Choose<NativeChoice<*>>(a, b)
fun <S : LastNativeChoice> localBoundedNativeChoice(s: S, a: NativeChoice<*>, b: NativeChoice<*>) =
    s.Choose<NativeChoice<*>>(a, b)

interface UsableBound { val name: String }
internal interface HiddenBound : UsableBound
open class VisibleBoundBase : HiddenBound { override val name get() = "base" }
fun <T : VisibleBoundBase> keepBound(value: T): T = value
open class ScopedBoundOwner {
    protected interface State
    open class Item : State
}

interface ReferencedBoundSink<T> { fun accept(x: T): String }

class ReferencedIntBoundSink : ReferencedBoundSink<Int?> {
    override fun accept(x: Int?): String = "i:" + (x?.toString() ?: "none")
}

class ReferencedAnyBoundSink : ReferencedBoundSink<Any?> {
    override fun accept(x: Any?): String = "a:" + (x?.toString() ?: "none")
}

class ResultSource {
    interface Mark
    open class Tagged<out K> : PendingItems.Item(), Mark
    class ResultItem<T>(val result: T) : Tagged<String>()
}

class ConcreteMarkedItem : ResultSource.Tagged<String>()

class NativeFacetHolder<T> where T : RoundtripDependentNative.Base, T : RoundtripDependentNative.Family.IMark<String> {
    var item: T? = null
}

class NativeJoined<T> : RoundtripDependentNative.IJoined<T>
    where T : RoundtripDependentNative.Base, T : RoundtripDependentNative.Family.IMark<String>,
          T : RoundtripDependentNative.IMarker<String> { var item: T? = null }

class JoinedValue<T>(val item: T) : RoundtripDependentNative.IJoinedValue<T>
    where T : RoundtripDependentNative.ILeft, T : RoundtripDependentNative.IRight

abstract class JoinedFactoryContract {
    abstract fun <A> make(): Any where A : Base, A : RoundtripDependentNative.ILeft, A : RoundtripDependentNative.IRight
}
abstract class DependentJoinedContract<K> where K : Base, K : RoundtripDependentNative.ILeft, K : RoundtripDependentNative.IRight {
    abstract fun <A : K> make(): Any
}
interface JointProof : RoundtripDependentNative.ILeft, RoundtripDependentNative.IRight

fun <A> makeNativeJoined(): Any where A : RoundtripDependentNative.Base, A : RoundtripDependentNative.Family.IMark<String>,
                                      A : RoundtripDependentNative.IMarker<String> = NativeJoined<A>()

fun <K, R : IMarker<K>> keepMarker(value: R): R = value

open class JoinedParent {
    open fun <A> make(value: A): Any where A : Base, A : RoundtripDependentNative.Family.IMark<String>,
                                          A : IMarker<String> = makeNativeJoined<A>()
}

class JoinedFactory<T> where T : RoundtripDependentNative.Base, T : RoundtripDependentNative.Family.IMark<String>,
                             T : RoundtripDependentNative.IMarker<String> {
    fun create() = NativeJoined<T>()
}

class FixedSlot<A> : RoundtripDependentNative.FixedSlotBase<A>() where A : Base, A : RoundtripDependentNative.Family.IMark<String>
class FixedSlotFactory<A> where A : Base, A : RoundtripDependentNative.Family.IMark<String>, A : IMarker<String> {
    fun usesJoint() = FixedSlot<A>().UsesJoint()
}

class CapturedBoundOwner<X> {
    inner class Entry<T>(val item: T) where T : RoundtripDependentNative.Base, T : RoundtripDependentNative.Family.IMark<X>
}
fun exposeCapturedBound(entry: CapturedBoundOwner<*>.Entry<*>): Any = entry

class ItemSlot<A : PendingItems.Item> {
    var item: A? = null
}

class RelatedItemSlot<B : A, A : PendingItems.Item> { var item: B? = null }

interface MarkedItemSink<A> where A : PendingItems.Item, A : ResultSource.Mark { fun accept(value: A): A }
fun <A> pickMarked(left: A, right: A, first: Boolean): A where A : PendingItems.Item, A : ResultSource.Mark =
    if (first) left else right
interface MarkedChoice {
    fun <A> selectMarked(left: A, right: A): A where A : PendingItems.Item, A : ResultSource.Mark
}
open class MarkedWriter {
    fun <A> writeElement(xs: Array<A>, index: Int, value: A): A where A : PendingItems.Item, A : ResultSource.Mark =
        write(byref(xs[index]), byref(xs[index]), value, false)
    fun <A> exchange(slot: ClrRef<ReferenceBox<A>>, next: () -> ReferenceBox<A>)
        where A : PendingItems.Item, A : ResultSource.Mark = exchangeBox(slot, next)
    open fun <A, B> replaceSpan(slot: ClrRef<System.Span<A>>, other: ClrRef<System.Span<A>>, tag: B): Int
        where A : PendingItems.Item, A : ResultSource.Mark, B : PendingItems.Item, B : ResultSource.Mark {
        val length = slot.value.Length
        slot.value = other.value
        return length
    }
    open fun <A> write(left: ClrRef<A>, right: ClrRef<A>, value: A, fail: Boolean): A
        where A : PendingItems.Item, A : ResultSource.Mark {
        left.value = value
        if (fail) throw IllegalStateException("after write")
        return right.value
    }
}
private fun <A> exchangeBox(slot: ClrRef<ReferenceBox<A>>, next: () -> ReferenceBox<A>): ReferenceBox<A> {
    val previous = slot.value
    slot.value = next()
    return previous
}
interface OwnerTag<P>
open class OwnerSelector<P> {
    open fun <A> select(left: A, right: A): A where A : PendingItems.Item, A : OwnerTag<P>, A : ResultSource.Mark = left
}
interface OtherSelector<P> {
    fun <A> select(left: A, right: A): A where A : PendingItems.Item, A : ResultSource.Mark, A : OwnerTag<P>
}
open class MarkedSelector : MarkedChoice {
    open fun <A> later(x: A): () -> () -> A where A : PendingItems.Item, A : ResultSource.Mark = { { x } }
    open fun <A> identity(): (A) -> A where A : PendingItems.Item, A : ResultSource.Mark = { it }
    override fun <A> selectMarked(left: A, right: A): A where A : PendingItems.Item, A : ResultSource.Mark =
        pickMarked(left, right, false)
}
fun <A> absentMarked(): A? where A : PendingItems.Item, A : ResultSource.Mark = null
class MarkedItemSlot<A> : MarkedItemSink<A> where A : PendingItems.Item, A : ResultSource.Mark {
    var item: A? = null
    override fun accept(value: A): A = value
    fun duplicate(): MarkedItemSlot<A> {
        val copy = MarkedItemSlot<A>()
        copy.item = item
        return copy
    }
    fun <B> deferred(label: B): () -> () -> Pair<B, A?> = { { label to item } }
}
abstract class AbstractMarkedSlot<A>(val value: A) where A : PendingItems.Item, A : ResultSource.Mark
class JointOuter<A>(private val value: A) where A : PendingItems.Item, A : ResultSource.Mark {
    private fun captured() = value
    inner class Inner<B>(val item: B) where B : PendingItems.Item, B : ResultSource.Mark {
        fun owner() = this@JointOuter
        fun outerValue() = captured()
    }
}
class MarkedItemOwner<X>(val outerValue: X) {
    private class Secret(val n: Int)
    inner class Slot<A>(var item: A) where A : PendingItems.Item, A : ResultSource.Mark {
        fun ownerValue(): X = outerValue
        fun number(): Int = Secret(42).n
    }
}

fun <A : PendingItems.Item?> keepItem(value: A?): A? = value

fun checkLocalMethodBounds(value: ResultSource.ResultItem<*>) {
    check(marked(value) === value)
    check(dependent<ResultSource.Tagged<*>, ResultSource.ResultItem<*>>(value, value) === value)
    check(nullableOperand(value, null) === value)
    check(boxed<ResultSource.ResultItem<*>>() == null)
}

fun localRelatedItem(value: ResultSource.ResultItem<*>): Boolean {
    val slot = RelatedItemSlot<ResultSource.ResultItem<*>, ResultSource.ResultItem<*>>()
    slot.item = value
    return slot.item === value
}
private fun <A> marked(value: A): A where A : PendingItems.Item, A : ResultSource.Mark = value
private fun <A : PendingItems.Item, B : A> dependent(first: A, second: B): A = second
private fun <A : PendingItems.Item> nullableOperand(value: A, xs: ClrList<A?>?): A = value
private fun <A : PendingItems.Item> boxed(): ItemSlot<A>? = null

class PendingItems<A : PendingItems.Item> {
    abstract class Item
    private val items = ArrayList<A>()
    fun put(item: A) { items.add(item) }
    fun get(index: Int): A = items[index]
    fun <T : Item> select(value: T): T = value
    fun <T : Item> projected(value: T, xs: ClrList<T?>?): T = value
}

fun localItemResult(): Long {
    val slot = ItemSlot<ResultSource.ResultItem<Long>>()
    slot.item = ResultSource.ResultItem(17L)
    val projected: ResultSource.ResultItem<*> = slot.item!!
    check(keepItem(projected) === projected)
    return slot.item!!.result
}

class StreamChunk<T>(val data: T) : System.IO.MemoryStream()
private fun <T : System.IO.Stream> streamIdentity(value: T): Any = value
fun <V : System.IO.Stream> forwardStream(make: () -> V): Any = streamIdentity<V>(make())
fun <V : System.IO.Stream> deferredStream(make: () -> V): () -> Any = { streamIdentity<V>(make()) }
class StreamFactory<V : System.IO.Stream>(private val make: () -> V) {
    fun read(): Any = forwardStream(make)
}
class ChunkList<A : System.IO.MemoryStream>(val items: ClrList<A>)
fun firstChunk(items: ClrList<StreamChunk<*>>): StreamChunk<*> = ChunkList<StreamChunk<*>>(items).items[0]
