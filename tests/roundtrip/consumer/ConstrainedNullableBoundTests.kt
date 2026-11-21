// #345 consumer half: the receiver bound and member declaration come from a projected reference KLIB. The bound at
// `Int?` physically closes to ReferencedBoundSink<object>, so the constrained call must box its Int argument into the
// substituted slot. The Any? twin proves an ordinary reference already inhabiting object is left untouched.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert
import roundtrip.constrainedbound.ReferencedAnyBoundSink
import roundtrip.constrainedbound.ReferencedBoundSink
import roundtrip.constrainedbound.ReferencedIntBoundSink
import roundtrip.constrainedbound.PendingItems
import roundtrip.constrainedbound.ResultSource.ResultItem
import roundtrip.constrainedbound.ResultSource.Tagged
import roundtrip.constrainedbound.RelatedItemSlot
import roundtrip.constrainedbound.MarkedItemSlot
import roundtrip.constrainedbound.MarkedSelector
import roundtrip.constrainedbound.ConcreteMarkedItem
import roundtrip.constrainedbound.MarkedChoice
import roundtrip.constrainedbound.absentMarked
import roundtrip.constrainedbound.AbstractMarkedSlot
import roundtrip.constrainedbound.MarkedItemOwner
import roundtrip.constrainedbound.ResultSource.Mark
import roundtrip.constrainedbound.localRelatedItem
import roundtrip.constrainedbound.localItemResult
import roundtrip.constrainedbound.keepItem
import roundtrip.constrainedbound.checkLocalMethodBounds
import roundtrip.constrainedbound.StreamChunk
import roundtrip.constrainedbound.firstChunk
import roundtrip.constrainedbound.UsableBound
import roundtrip.constrainedbound.keepBound
import roundtrip.constrainedbound.ScopedBoundOwner
import roundtrip.constrainedbound.VisibleBoundBase
import System.Collections.Generic.List as ClrList
import GenericValueInterop.IProjectedMark
import GenericValueInterop.IRichProjectedMark
import GenericValueInterop.MarkedStreamReceiver
import GenericValueInterop.NativeStreamBase
import GenericValueInterop.TwoMarkedTags
import GenericValueInterop.ForeignFirstSelector
import kotlin.clr.byref
import kotlin.clr.ClrRef
import GenericValueInterop.NativeBoxApi
import System.IO.MemoryStream

private fun <T : ReferencedBoundSink<Int?>> useReferencedNullableValueBound(t: T): String = t.accept(2)
private fun <T : ReferencedBoundSink<Any?>> useReferencedObjectBound(t: T): String = t.accept("y")

private class ForeignBoundHolder<T : VisibleBoundBase>(val element: T) {
    fun read(): String = publicBoundName(keepBound(element))
}
private fun <T : UsableBound> publicBoundName(value: T): String = value.name
private fun <T> lastNative(first: T, second: T): T where T : MemoryStream, T : IProjectedMark =
    NativeBoxApi.LastMarked<T>(first, second)
private fun <X, Y> secondNative(first: X, second: Y): Y
    where X : MemoryStream, X : IProjectedMark, Y : MemoryStream, Y : IProjectedMark =
    NativeBoxApi.SecondMarkedStream<X, Y>(first, second)
private class ScopedBoundConsumer : ScopedBoundOwner() {
    private fun <T : State> keep(value: T): T = value
    fun <T : ScopedBoundOwner.Item> forward(value: T): T = keep(value)
}

class ConstrainedNullableBoundTests {
    private class ClientItem<T>(val payload: T) : PendingItems.Item(), Mark
    private class FirstSelector : MarkedSelector() {
        override fun <A> selectMarked(left: A, right: A): A where A : PendingItems.Item, A : Mark = left
        fun callBase(left: ClientItem<Long>, right: ClientItem<Long>) = super.selectMarked(left, right)
    }
    private class MethodSlot<A> where A : PendingItems.Item, A : Mark {
        fun pick(selector: MarkedSelector, first: A, last: A): A = selector.selectMarked(first, last)
        fun fromInterface(selector: MarkedChoice, first: A, last: A): A = selector.selectMarked(first, last)
        fun absent(): A? = absentMarked<A>()
    }
    private class MarkedStream<T> : MemoryStream(), IProjectedMark
    private open class ExactStream : MemoryStream(), IProjectedMark
    private class DerivedStream : ExactStream()
    private class SharedBaseStream<T> : ExactStream(), IRichProjectedMark
    private class RichSlot<A> : NativeStreamBase<A>() where A : MemoryStream, A : IRichProjectedMark {
        fun roundtrip(value: A) = FromNative(value)
    }
    private class NativeBaseSlot<A, B> : NativeStreamBase<A>()
        where A : MemoryStream, A : IProjectedMark, B : MemoryStream, B : IProjectedMark {
        var other: B? = null
        fun roundtrip(value: A) = FromNative(value)
    }
    private class StreamSlot<A, B>(var item: A) : MarkedStreamReceiver()
        where A : MemoryStream, A : IProjectedMark, B : MemoryStream, B : IProjectedMark {
        override fun <T> Source(item: T): String where T : MemoryStream, T : IProjectedMark = "derived"
        override fun <T> SourceRef(item: T, n: ClrRef<Int>): Int where T : MemoryStream, T : IProjectedMark = -1
        fun source(value: A): String {
            var n = 0
            check(super.SourceRef<A>(value, byref(n)) == 42 && n == 42)
            check(Inherited<A>(value, byref(n)) == 43 && n == 43)
            return super.Source<A>(value)
        }
        fun keep(value: A): A = MarkedStreamReceiver().Keep<A>(value)
        fun interfaceWitness() = NativeBoxApi.InterfaceWitness<A>()
        fun last(first: A, second: A): A = lastNative<A>(first, second)
        fun <T> choose(first: A, second: T): T where T : MemoryStream, T : IProjectedMark =
            secondNative<A, T>(first, second)
        fun bothScopes(first: A, second: B): B = choose<B>(first, second)
        fun keepAliased(value: A): A {
            var n = 0
            val result = MarkedStreamReceiver().WriteAliased<A>(value, byref(n), byref(n))
            check(n == 42)
            return result
        }
        fun second(first: A, second: B): B = NativeBoxApi.SecondMarkedStream<A, B>(first, second)
        fun firstType(first: A, second: B) = NativeBoxApi.FirstMarkedType<A, B>(first, second, TwoMarkedTags())
        fun dependent(value: A): A = NativeBoxApi.DependentMarkedStream<A, A>(value, value)
    }

    @TestAttribute
    fun separateClassAndInterfaceBoundsAcceptHeterogeneousProjectedValues() {
        val number = ClientItem(42L)
        val text = ClientItem("text")
        val concrete = object : AbstractMarkedSlot<ClientItem<String>>(text) {}
        check(concrete.value === text)
        val slot = MarkedItemSlot<ClientItem<*>>()
        val methods = MethodSlot<ClientItem<*>>()
        check(methods.pick(MarkedSelector(), number, text) === text && methods.absent() == null)
        check(methods.pick(FirstSelector(), number, text) === number)
        check(methods.fromInterface(FirstSelector(), text, number) === text)
        check(FirstSelector().callBase(ClientItem(1L), number) === number)
        val foreign = ForeignFirstSelector()
        check(methods.fromInterface(foreign, text, number) === text)
        val exactItem = ConcreteMarkedItem()
        check(ForeignFirstSelector.FromExact(foreign, exactItem, ConcreteMarkedItem()) === exactItem)
        slot.item = number
        check(slot.item === number)
        check(slot.accept(text) === text && slot.duplicate().item === number)
        val captured = slot.deferred("held")()()
        check(captured.first == "held" && captured.second === number)
        slot.item = text
        check(slot.item === text)
        val any: Any = slot
        check(any is MarkedItemSlot<*> && any::class == MarkedItemSlot::class)
        val exact = MarkedItemSlot<ClientItem<Long>>()
        exact.item = number
        check(exact.item === number)
        val first = MarkedStream<Long>()
        val second = MarkedStream<String>()
        val streams = StreamSlot<MarkedStream<*>, MarkedStream<*>>(first)
        check(streams.source(first) == "base")
        check(streams.item === first && streams.keep(first) === first)
        streams.item = second
        check(streams.item === second && streams.keep(second) === second)
        check(streams.second(first, second) === second)
        val mixed = StreamSlot<ExactStream, MarkedStream<*>>(ExactStream())
        check(mixed.firstType(DerivedStream(), second) == "ExactStream")
        check(mixed.interfaceWitness() == "ExactStream")
        check(streams.interfaceWitness() == "IProjectedMark" && streams.last(first, second) === second)
        check(streams.bothScopes(first, second) === second)
        val native = NativeBaseSlot<ExactStream, MarkedStream<*>>()
        native.other = first
        val exactStream = ExactStream()
        check(native.roundtrip(exactStream) === exactStream && native.other === first)
        val shared = SharedBaseStream<Int>()
        check(RichSlot<SharedBaseStream<*>>().roundtrip(shared) === shared)
        native.other = second
        check(native.other === second)
        check(streams.dependent(second) === second)
        val outer: MarkedItemOwner<*> = MarkedItemOwner("outer")
        val inner = outer.Slot<ClientItem<*>>(number)
        inner.item = text
        check(inner.item === text && inner.ownerValue() == "outer" && inner.number() == 42)
        check(streams.keepAliased(second) === second)
    }

    @TestAttribute
    fun constructedAndStarredSubclassesSatisfyTheClassBound() {
        check(localItemResult() == 17L)
        check(ForeignBoundHolder(VisibleBoundBase()).read() == "base")
        val scoped = ScopedBoundOwner.Item()
        check(ScopedBoundConsumer().forward(scoped) === scoped)
        val queue = PendingItems<ResultItem<*>>()
        val numeric = ResultItem(42L)
        val text = ResultItem("text")
        val projected: ResultItem<*> = text
        check(keepItem(projected) === text && queue.select(projected).result == "text")
        check(keepItem<ResultItem<*>>(null) == null)
        checkLocalMethodBounds(projected)
        check(localRelatedItem(projected))
        val related = RelatedItemSlot<ResultItem<*>, Tagged<*>>()
        related.item = numeric
        check(related.item === numeric)
        related.item = text
        check(related.item === text)
        val projectedQueue: PendingItems<*> = queue
        check(projectedQueue.projected(projected, null) === text)
        queue.put(numeric)
        queue.put(text)
        check(queue.get(0) === numeric && queue.get(0).result == 42L)
        check(queue.get(1) === text && queue.get(1).result == "text")
        val exact = ClrList<StreamChunk<Long>>()
        val chunk = StreamChunk(42L)
        exact.Add(chunk)
        @Suppress("UNCHECKED_CAST")
        check(firstChunk(exact as ClrList<StreamChunk<*>>) === chunk)
    }

    @TestAttribute
    fun constrainedCallUsesReferencedSubstitutedErasedParameterSlot() {
        ClassicAssert.AreEqual("i:2", useReferencedNullableValueBound(ReferencedIntBoundSink()))
        ClassicAssert.AreEqual("a:y", useReferencedObjectBound(ReferencedAnyBoundSink()))
    }
}
