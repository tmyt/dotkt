// #345 consumer half: the receiver bound and member declaration come from a projected reference KLIB. The bound at
// `Int?` physically closes to ReferencedBoundSink<object>, so the constrained call must box its Int argument into the
// substituted slot. The Any? twin proves an ordinary reference already inhabiting object is left untouched.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert
import roundtrip.clrrefparameters.ReferenceBox
import roundtrip.constrainedbound.ReferencedAnyBoundSink
import roundtrip.constrainedbound.ReferencedBoundSink
import roundtrip.constrainedbound.ReferencedIntBoundSink
import roundtrip.constrainedbound.PendingItems
import roundtrip.constrainedbound.ResultSource.ResultItem
import roundtrip.constrainedbound.ResultSource.Tagged
import roundtrip.constrainedbound.RelatedItemSlot
import roundtrip.constrainedbound.MarkedItemSlot
import roundtrip.constrainedbound.MarkedSelector
import roundtrip.constrainedbound.NativeChoice
import roundtrip.constrainedbound.LastNativeChoice
import roundtrip.constrainedbound.localNativeChoice
import roundtrip.constrainedbound.localBoundedNativeChoice
import roundtrip.constrainedbound.OwnerSelector
import roundtrip.constrainedbound.OwnerTag
import roundtrip.constrainedbound.OtherSelector
import roundtrip.constrainedbound.ConcreteMarkedItem
import roundtrip.constrainedbound.NativeFacetHolder
import roundtrip.constrainedbound.JoinedFactory
import roundtrip.constrainedbound.JoinedParent
import roundtrip.constrainedbound.makeNativeJoined
import roundtrip.constrainedbound.keepMarker
import RoundtripDependentNative.IMarker
import roundtrip.constrainedbound.CapturedBoundOwner
import RoundtripDependentNative.Base
import RoundtripDependentNative.Family.IMark
import roundtrip.constrainedbound.MarkedChoice
import roundtrip.constrainedbound.MarkedWriter
import roundtrip.constrainedbound.absentMarked
import roundtrip.constrainedbound.pickMarked
import roundtrip.constrainedbound.AbstractMarkedSlot
import roundtrip.constrainedbound.MarkedItemOwner
import roundtrip.constrainedbound.JointOuter
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
import GenericValueInterop.IMarkedRecord
import GenericValueInterop.IMarkedRecordTag
import GenericValueInterop.IRichProjectedMark
import GenericValueInterop.MarkedStreamReceiver
import GenericValueInterop.NativeStreamBase
import GenericValueInterop.SharedNativeStream
import GenericValueInterop.TwoMarkedTags
import GenericValueInterop.ForeignFirstSelector
import kotlin.clr.byref
import kotlin.clr.ClrRef
import GenericValueInterop.NativeBoxApi
import GenericValueInterop.NativeResultProbe
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

private class ScopedNativeSelector<T> : RoundtripDependentNative.Selector() {
    fun callback(other: ScopedNativeSelector<Int>, item: NativeChoice<*>) = {
        var count = 0
        other.Visit<NativeChoice<*>>(item, byref(count)) === item && count == 1
    }
}

private fun <Y> callFixedPair(chooser: LastNativeChoice, first: NativeChoice<*>, second: Y): Y
    where Y : Base, Y : IMarker<String> = chooser.Pair<NativeChoice<*>, Y>(first, second)

private class NestedChoice : LastNativeChoice() {
    override fun <T> Choose(first: T, last: T, visits: ClrRef<Int>): T where T : Base, T : IMarker<String> = first
    inner class Caller {
        fun base(first: NativeChoice<*>, last: NativeChoice<*>): Boolean {
            var visits = 0
            return super@NestedChoice.Choose<NativeChoice<*>>(first, last, byref(visits)) === last && visits == 2
        }
    }
}

class ConstrainedNullableBoundTests {
    private class TaggedItem<T>(val payload: T) : PendingItems.Item(), OwnerTag<String>, Mark
    private class LastTagged : OwnerSelector<String>(), OtherSelector<String> {
        override fun <A> select(left: A, right: A): A where A : PendingItems.Item, A : OwnerTag<String>, A : Mark = right
        fun fromBase(a: TaggedItem<*>, b: TaggedItem<*>) = super.select(a, b)
    }
    private class ClientItem<T>(val payload: T) : PendingItems.Item(), Mark
    private class Cell(@kotlin.clr.ClrField var item: ClientItem<*>)
    private fun <A> swapLocations(a: ClrRef<A>, b: ClrRef<A>) where A : PendingItems.Item, A : Mark =
        roundtrip.clrrefparameters.swapReferenced(a, b)
    private class ClientWriter : MarkedWriter() {
        var calls = 0
        override fun <A> write(left: ClrRef<A>, right: ClrRef<A>, value: A, fail: Boolean): A
            where A : PendingItems.Item, A : Mark {
            calls++
            return super.write(left, right, value, fail)
        }
    }

    @TestAttribute
    fun projectedManagedReferencesPreserveLocationsThroughDispatch() {
        check(LocationCaller("marker").checkLocations(MarkedStream<Int>(), MarkedStream<Int>(),
            MarkedStream<String>(), MarkedStream<String>(), "marker", 42) == 42)
        val first = ClientItem(1)
        val second = ClientItem("two")
        val cell = Cell(first)
        val client = ClientWriter()
        val writer: MarkedWriter = client
        check(writer.write<ClientItem<*>>(byref(cell.item), byref(cell.item), second, false) === second)
        check(cell.item === second)
        var local: ClientItem<*> = second
        try {
            writer.write<ClientItem<*>>(byref(local), byref(cell.item), first, true)
            error("did not throw")
        } catch (e: IllegalStateException) {
            check(e.message == "after write")
        }
        check(local === first && cell.item === second && client.calls == 2)
        val elements = arrayOf<ClientItem<*>>(first, first)
        check(writer.writeElement(elements, 1, second) === second)
        check(elements[0] === first && elements[1] === second && client.calls == 3)
        val other = Cell(first)
        swapLocations<ClientItem<*>>(byref(cell.item), byref(other.item))
        check(cell.item === first && other.item === second)
        val before = ReferenceBox<ClientItem<*>>(first)
        val after = ReferenceBox<ClientItem<*>>(second)
        var nested = before
        check(writer.exchange<ClientItem<*>>(byref(nested)) { after } === before && nested === after)
        val concrete = ConcreteMarkedItem()
        var span = System.Span<ConcreteMarkedItem>(arrayOf(concrete, concrete))
        var shorter = System.Span<ConcreteMarkedItem>(arrayOf(concrete))
        check(writer.replaceSpan<ConcreteMarkedItem, ClientItem<*>>(byref(span), byref(shorter), second) == 2)
        check(span.Length == 1)
    }
    private class FirstSelector : MarkedSelector() {
        override fun <A> selectMarked(left: A, right: A): A where A : PendingItems.Item, A : Mark = left
        fun callBase(left: ClientItem<Long>, right: ClientItem<Long>) = super.selectMarked(left, right)
        fun callProjectedBase(left: ClientItem<*>, right: ClientItem<*>) = super.selectMarked(left, right)
    }
    private class Selection<A> where A : PendingItems.Item, A : Mark {
        fun choose(left: A, right: A) = pickMarked(left, right, false)
    }
    private class MethodSlot<A> where A : PendingItems.Item, A : Mark {
        fun newSlot() = MarkedItemSlot<A>()
        fun fromClosures(selector: MarkedSelector, x: A) = selector.identity<A>()(selector.later(x)()())
        fun pick(selector: MarkedSelector, first: A, last: A): A = selector.selectMarked(first, last)
        fun fromInterface(selector: MarkedChoice, first: A, last: A): A = selector.selectMarked(first, last)
        fun absent(): A? = absentMarked<A>()
    }
    private fun <A> chooseThroughSlot(left: A, right: A): A where A : PendingItems.Item, A : Mark =
        Selection<A>().choose(left, right)
    private open class MarkedStream<T> : MemoryStream(), IProjectedMark
    private class LocationCaller<C>(private val marker: C) {
        fun <M> checkLocations(first: MarkedStream<*>, next: MarkedStream<*>, second: MarkedStream<*>,
                               otherNext: MarkedStream<*>, expected: C, result: M): M {
            val probe = NativeResultProbe()
            val other = NativeResultProbe()
            var visits = 0
            while (visits < 3) {
                var current by byref(probe.Location<MarkedStream<*>>(first))
                if (++visits == 1) continue
                current = next
                if (probe.Read(0) === next) break
            }
            check(visits == 2)
            var live by byref(probe.Location<MarkedStream<*>>(first))
            var otherLive by byref(other.Location<MarkedStream<*>>(second))
            check(live === first && otherLive === second)
            otherLive = otherNext
            NativeResultProbe.Replace<MarkedStream<*>>(byref(live), next)
            check(probe.Read(0) === next && other.Read(0) === otherNext)
            check(marker == expected)
            return result
        }
    }
    @TestAttribute
    fun projectedNativeSpanRetainsItsElementTypeAcrossTheConsumer() {
        check(checkProjectedSpan(MarkedStream<Int>(), MarkedStream<Int>(), false) == 3)
        check(checkProjectedSpan(MarkedStream<Int>(), MarkedStream<Int>(), true) == 2)
        check(lengthThroughBlock(MarkedStream<Int>(), false) == 2)
        check(lengthThroughBlock(MarkedStream<Int>(), true) == 19)
    }
    private fun <T> spanLength(values: kotlin.clr.Span<T>): Int = NativeResultProbe.Size(values)
    private fun lengthThroughBlock(stream: MarkedStream<*>, stop: Boolean): Int {
        val saved = run {
            val s = NativeResultProbe().Slice(stream)
            if (stop) return 19
            s
        }
        return spanLength(saved)
    }
    private fun checkProjectedSpan(stream: MarkedStream<*>, replacement: MarkedStream<*>, stop: Boolean): Int {
        val probe = NativeResultProbe()
        check(NativeResultProbe.Size(probe.Slice<MarkedStream<*>>(stream)) == 2)
        val saved = probe.Slice<MarkedStream<*>>(stream)
        val alias = saved
        check(spanLength(alias) == 2)
        var step = 0
        probe.WriteSecond(++step, probe.Slice<MarkedStream<*>>(stream.also { check(++step == 2) }),
            if (stop) return step else ++step, replacement)
        check(probe.Read(1) === replacement)
        return step
    }
    private class LongStream : MarkedStream<Long>()
    private class NativeRecord<A>(var item: A) : IMarkedRecord<A> where A : MemoryStream, A : IProjectedMark {
        override fun Read(): A = item
        override fun Write(item: A) { this.item = item }
    }
    private class NativeTag<A> : IMarkedRecordTag<A> where A : MemoryStream, A : IProjectedMark
    private class IndependentNativeTag<A,B> : IMarkedRecordTag<A>
        where A : MemoryStream, A : IProjectedMark, B : MemoryStream, B : IProjectedMark
    private class TagFactory<A> where A : MemoryStream, A : IProjectedMark {
        fun tag() = NativeTag<A>()
    }
    private class PartialStream<X,Y> : SharedNativeStream<X>()
    private class IntStream<T> : SharedNativeStream<Int>(), Mark
    private fun <A> fromIntStream(value: A): A where A : SharedNativeStream<Int>, A : Mark = value
    private class RichStream<T> : MemoryStream(), IRichProjectedMark
    private class LinkedSlot<A, B : A> where A : MemoryStream, A : IProjectedMark { var value: B? = null }
    private class RichMethodSlot<A> where A : MemoryStream, A : IRichProjectedMark {
        fun choose(first: A, second: A) = lastNative<A>(first, second)
        fun witness() = NativeBoxApi.JointInterfaceWitness<A>()
    }
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
            val other = StreamSlot<MarkedStream<Long>, MarkedStream<Long>>(MarkedStream<Long>())
            check(other.Receiver<A>(value, byref(n)) === other && n == 44)
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
        fun dependent(first: A, second: A): A = NativeBoxApi.DependentMarkedStream<A, A>(first, second)
        fun independentType(first: A, second: A, independent: B) =
            NativeBoxApi.IndependentType<A, A, B>(first, second, independent)
    }

    interface ChildMark : IMark<String>
    private class Child<T> : Base(), ChildMark, IMarker<String>
    private class MarkedChild<T> : RoundtripDependentNative.MarkedBase(), ChildMark
    private fun <A> viaMarkedBase(value: A): Any where A : RoundtripDependentNative.MarkedBase, A : IMark<String> =
        roundtrip.constrainedbound.NativeJoined<A>()
    private class LocalHolder<T> where T : Base, T : IMark<String> { var item: T? = null }
    private class Forward<T> where T : Base, T : ChildMark {
        fun local() = LocalHolder<T>()
        fun imported() = NativeFacetHolder<T>()
    }

    private class JoinedForward<T> where T : Base, T : ChildMark, T : IMarker<String> {
        fun factory() = JoinedFactory<T>()
    }
    private class JointSelection<A> : RoundtripDependentNative.IKey<RoundtripDependentNative.IJoined<A>>
        where A : Base, A : IMark<String>, A : IMarker<String> {
        fun select(value: A) = value
    }
    private fun <A> selectJoined(value: A): A where A : Base, A : IMark<String>, A : IMarker<String> =
        JointSelection<A>().select(value)
    private fun <A> makeJoined(): Any where A : Base, A : IMark<String>, A : IMarker<String> =
        JointSelection<A>()
    private fun <A> forwardNativeJoined(): Any where A : Base, A : ChildMark, A : IMarker<String> =
        makeNativeJoined<A>()
    private fun <R : IMarker<String>> forwardMarker(value: R): R = keepMarker<String, R>(value)

    private interface JoinedSurface {
        fun <A> make(value: A): Any where A : Base, A : IMark<String>, A : IMarker<String>
    }
    private class JoinedOverride : JoinedParent(), JoinedSurface {
        override fun <A> make(value: A): Any where A : Base, A : IMark<String>, A : IMarker<String> =
            super<JoinedParent>.make(value)
    }

    @TestAttribute
    fun forwardedFacetSelectsTheSameNestedNativeInterfaceAcrossAssemblies() {
        val value = Child<Int>()
        check(forwardMarker(value) === value)
        val through = Forward<Child<*>>()
        val local = through.local()
        val imported = through.imported()
        val direct = NativeFacetHolder<Child<*>>()
        local.item = value
        imported.item = value
        direct.item = value
        check(local.item === value && imported.item === value && direct.item === value)
        val outer = CapturedBoundOwner<String>()
        check(outer.Entry<Child<*>>(value).item === value)
        val joined = JoinedForward<Child<*>>().factory().create()
        joined.item = value
        check(joined.item === value)
        check(selectJoined<Child<*>>(value) === value)
        check(makeJoined<Child<*>>() is RoundtripDependentNative.IKey<*>)
        check(forwardNativeJoined<Child<*>>() is RoundtripDependentNative.IJoined<*>)
        val implementation = JoinedOverride()
        val fromBase: JoinedParent = implementation
        val fromInterface: JoinedSurface = implementation
        check(fromBase.make<Child<*>>(value) is RoundtripDependentNative.IJoined<*>)
        check(fromInterface.make<Child<*>>(value) is RoundtripDependentNative.IJoined<*>)
        check(viaMarkedBase<MarkedChild<*>>(MarkedChild<Int>()) is RoundtripDependentNative.IJoined<*>)
    }

    private fun checkFixedVirtualDispatch() {
        val chooser = LastNativeChoice()
        val nativeChooser: RoundtripDependentNative.Selector = chooser
        val a = NativeChoice("first")
        val b = NativeChoice("last")
        var order = ""
        fun receiver(): LastNativeChoice { order += "r"; return chooser }
        fun operand(label: String, x: NativeChoice<*>): NativeChoice<*> { order += label; return x }
        check(receiver().Choose<NativeChoice<*>>(operand("first", a), operand("last", b)) === b)
        check(order == "rfirstlast")
        check(chooser.Choose<NativeChoice<String>>(a, b) === b)
        check(nativeChooser.Choose<NativeChoice<*>>(a, b) === b)
        check(callFixedPair<NativeChoice<*>>(chooser, a, b) === b)
        val key: RoundtripDependentNative.IKey<*> = RoundtripDependentNative.StringKey()
        val projected: NativeChoice<*> = a
        check(chooser.Capture(projected, key) === key)
        var capturedVisits = 0
        check(chooser.Capture(projected, key, byref(capturedVisits)) === key && capturedVisits == 2)
        val callbackOwner = ScopedNativeSelector<String>()
        val callbackTarget = ScopedNativeSelector<Int>()
        check(callbackOwner.callback(callbackTarget, b)())
        check(callbackOwner.Visits == 0 && callbackTarget.Visits == 1)
        var calls = 0
        check(nativeChooser.Apply<NativeChoice<*>>(a, System.Action { calls++ }) === a)
        check(nativeChooser.Apply<NativeChoice<*>>(b, System.Action1<Int> { calls += it }) === b)
        check(calls == 43)
        check(localNativeChoice(chooser, a, b) === b)
        check(localBoundedNativeChoice(chooser, a, b) === b)
        check(chooser.base(a, b) === a)
        var visits = 0
        check(chooser.Choose<NativeChoice<*>>(a, b, byref(visits)) === b && visits == 2)
        check(nativeChooser.Choose<NativeChoice<*>>(a, b, byref(visits)) === b && visits == 4)
        check(chooser.baseRef(a, b))
        check(NestedChoice().Caller().base(a, b))
        val other = LastNativeChoice()
        check(chooser.visitOther(other, a) && chooser.Visits == 0 && other.Visits == 1)
    }

    @TestAttribute
    fun separateClassAndInterfaceBoundsAcceptHeterogeneousProjectedValues() {
        checkFixedVirtualDispatch()
        val number = ClientItem(42L)
        val text = ClientItem("text")
        val concrete = object : AbstractMarkedSlot<ClientItem<String>>(text) {}
        check(concrete.value === text)
        val slot = MarkedItemSlot<ClientItem<*>>()
        val methods = MethodSlot<ClientItem<*>>()
        check(chooseThroughSlot(ClientItem(0L), number) === number)
        check(chooseThroughSlot<ClientItem<*>>(number, text) === text)
        val imported = methods.newSlot()
        imported.item = text
        check(imported.item === text)
        check(methods.fromClosures(MarkedSelector(), number) === number)
        check(methods.pick(MarkedSelector(), number, text) === text && methods.absent() == null)
        check(methods.pick(FirstSelector(), number, text) === number)
        check(methods.fromInterface(FirstSelector(), text, number) === text)
        check(FirstSelector().callBase(ClientItem(1L), number) === number)
        val selector: MarkedSelector = FirstSelector()
        val choice: MarkedChoice = selector
        check(selector.selectMarked<ClientItem<*>>(number, text) === number)
        check(choice.selectMarked<ClientItem<*>>(text, number) === text)
        val select: (ClientItem<*>, ClientItem<*>) -> ClientItem<*> = selector::selectMarked
        check(select(text, number) === text)
        check(FirstSelector().callProjectedBase(number, text) === text)
        val firstTag = TaggedItem(1)
        val lastTag = TaggedItem("last")
        val tagged = LastTagged()
        val typedOwner: OwnerSelector<String> = tagged
        check(typedOwner.select<TaggedItem<*>>(firstTag, lastTag) === lastTag)
        val otherOwner: OtherSelector<String> = tagged
        check(otherOwner.select<TaggedItem<*>>(firstTag, lastTag) === lastTag)
        check(tagged.fromBase(firstTag, lastTag) === firstTag)
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
        val record = NativeRecord<MarkedStream<*>>(first)
        check(NativeBoxApi.ReplaceRecord<MarkedStream<*>>(record, second) === second && record.item === second)
        check(NativeBoxApi.HasRecordTag<MarkedStream<*>>(NativeTag<MarkedStream<*>>()))
        check(NativeBoxApi.HasRecordTag<MarkedStream<*>>(TagFactory<MarkedStream<*>>().tag()))
        check(NativeBoxApi.HasRecordTag<MarkedStream<Long>>(NativeTag<MarkedStream<Long>>()))
        check(NativeBoxApi.HasRecordTag<PartialStream<String,*>>(NativeTag<PartialStream<String,*>>()))
        check(NativeBoxApi.HasRecordTag<MarkedStream<Long>>(IndependentNativeTag<MarkedStream<Long>,MarkedStream<*>>()))
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
        check(streams.dependent(LongStream(), first) === first)
        val independent = StreamSlot<MarkedStream<*>, ExactStream>(first)
        check(independent.independentType(LongStream(), first, DerivedStream()) == "ExactStream")
        val linked = LinkedSlot<MarkedStream<*>, MarkedStream<*>>()
        linked.value = second
        check(linked.value === second)
        val partial = PartialStream<String, Int>()
        check(NativeBaseSlot<PartialStream<String, *>, MarkedStream<*>>().roundtrip(partial) === partial)
        val localStream = IntStream<String>()
        check(fromIntStream<IntStream<*>>(localStream) === localStream)
        val rich = RichMethodSlot<RichStream<*>>()
        val value = RichStream<Long>()
        check(rich.choose(value, value) === value && rich.witness() == "IRichProjectedMark")
        val outer: MarkedItemOwner<*> = MarkedItemOwner("outer")
        val inner = outer.Slot<ClientItem<*>>(number)
        inner.item = text
        check(inner.item === text && inner.ownerValue() == "outer" && inner.number() == 42)
        val joint = JointOuter<ClientItem<*>>(number)
        val child = joint.Inner<ClientItem<*>>(text)
        check(child.owner() === joint && child.outerValue() === number && child.item === text)
        val concreteOuter = JointOuter(text)
        check(concreteOuter.Inner<ClientItem<*>>(number).outerValue() === text)
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
