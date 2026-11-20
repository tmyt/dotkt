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
import GenericValueInterop.NativeBoxApi
import System.IO.MemoryStream

private fun <T : ReferencedBoundSink<Int?>> useReferencedNullableValueBound(t: T): String = t.accept(2)
private fun <T : ReferencedBoundSink<Any?>> useReferencedObjectBound(t: T): String = t.accept("y")

private class ForeignBoundHolder<T : VisibleBoundBase>(val element: T) {
    fun read(): String = publicBoundName(keepBound(element))
}
private fun <T : UsableBound> publicBoundName(value: T): String = value.name
private class ScopedBoundConsumer : ScopedBoundOwner() {
    private fun <T : State> keep(value: T): T = value
    fun <T : ScopedBoundOwner.Item> forward(value: T): T = keep(value)
}

class ConstrainedNullableBoundTests {
    private class ClientItem<T>(val payload: T) : PendingItems.Item(), Mark
    private class MarkedStream<T> : MemoryStream(), IProjectedMark
    private class StreamSlot<A>(var item: A) where A : MemoryStream, A : IProjectedMark {
        fun keep(value: A): A = NativeBoxApi.KeepMarkedStream<A>(value)
    }

    @TestAttribute
    fun separateClassAndInterfaceBoundsAcceptHeterogeneousProjectedValues() {
        val number = ClientItem(42L)
        val text = ClientItem("text")
        val slot = MarkedItemSlot<ClientItem<*>>()
        slot.item = number
        check(slot.item === number)
        slot.item = text
        check(slot.item === text)
        val any: Any = slot
        check(any is MarkedItemSlot<*> && any::class == MarkedItemSlot::class)
        val exact = MarkedItemSlot<ClientItem<Long>>()
        exact.item = number
        check(exact.item === number)
        val first = MarkedStream<Long>()
        val second = MarkedStream<String>()
        val streams = StreamSlot<MarkedStream<*>>(first)
        check(streams.item === first && streams.keep(first) === first)
        streams.item = second
        check(streams.item === second && streams.keep(second) === second)
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
