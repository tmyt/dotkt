package roundtrip.consumer.selectedbounds

import NUnit.Framework.TestAttribute
import RoundtripDependentNative.JoinedSlot
import RoundtripDependentNative.Base
import RoundtripDependentNative.ILeft
import RoundtripDependentNative.IRight
import System.Collections.Generic.Queue
import GenericValueInterop.InheritedSuspendTaskApi
import kotlin.clr.ClrRef
import kotlin.clr.byref
import kotlin.coroutines.*
import roundtrip.inheritedsuspendcovariance.IntGate
import roundtrip.selectedbounds.*

class ImportedFactory : JointFactory<Concrete>, NominalFactory<Concrete> {
    override fun <A : Concrete> make(): Any = Product<A>()
    override fun <A : Concrete> select(value: A): A = value
}

class FacetFactory : LeftFactory<Concrete>, RightFactory<Concrete>, DefaultFactory<Concrete> {
    override fun <A : Concrete> make(): Any = Product<A>()
}

private fun <A : Concrete> takeNullable(parent: GenericParent<*>, queue: Queue<A?>): Any = parent.take<A>(queue)

private class OrdinarySuspendFactory<K, P> : SuspendFactory<K>, ReverseStorageFactory<K, P>
    where K : Base, K : IRight, K : ILeft {
    override suspend fun <A : K> make(): Any = Product<A>()
    override fun replace(target: ClrRef<P>, input: P) { target.value = input }
}

private class SplitSuspendFactory<K>(val value: K) : SuspendLeft<K>, SuspendRight<K>
    where K : Base, K : ILeft, K : IRight {
    override suspend fun <A : K> make(): Any = value
}

private class SuspendChild<C>(val ownerValue: C, val gate: IntGate) :
    SuspendParent(), SuspendLeft<Concrete>, SuspendRight<Concrete> where C : Base, C : ILeft {
    var calls = 0
    override suspend fun <A : Concrete> make(): Any {
        val captured = ownerValue
        val delta = gate.await()
        check(captured === ownerValue)
        calls += delta
        return Product<A>()
    }
}

interface SuspendEcho<K> where K : Base, K : ILeft, K : IRight {
    suspend fun echo(value: K): K
}

class StoredEcho<K>(val stored: K, val gate: IntGate) : SuspendEcho<K>
    where K : Base, K : ILeft, K : IRight {
    override suspend fun echo(value: K): K {
        gate.await()
        check(value === stored)
        return value
    }
}

class SelectedMethodBoundsTests {
    @TestAttribute
    fun covariantSuspendBridgeRetainsItsReceiverAcrossSuspension() {
        val item = Item()
        val gate = IntGate()
        val view: SuspendEcho<Item> = StoredEcho(item, gate)
        var result: Item? = null
        val invoke: suspend () -> Item = { view.echo(item) }
        invoke.startCoroutine(object : Continuation<Item> {
            override val context: CoroutineContext = EmptyCoroutineContext
            override fun resumeWith(value: Result<Item>) { result = value.getOrThrow() }
        })
        check(result == null)
        gate.resume(1)
        check(result === item)
    }

    private class Both : GenericValueInterop.IJoinedPicker {
        override suspend fun <A : Item> pick(value: Item): Any = "first"
        override suspend fun <A : OtherItem> pick(value: OtherItem): Any = "second"
        override fun <A : Item> write(value: Item, condition: Boolean, target: ClrRef<Int>) {
            if (condition) target.value += 1
        }
        override fun <A : OtherItem> write(value: OtherItem, condition: Boolean, target: ClrRef<Int>) {
            if (condition) target.value += 2
        }
    }
    private class Overloaded : OverloadedPicker<Concrete> {
        override suspend fun <A : Concrete> pick(value: Concrete, source: Iterable<Int>): Any = "iterable"
        override suspend fun <A : Concrete> pick(value: Concrete, source: Sequence<Int>): Any = "sequence"
    }
    private class Single : SuspendPicker<Concrete> {
        override suspend fun <A : Concrete> pick(value: Concrete): Any = "single"
        override fun <A : Concrete> write(value: Concrete, condition: Boolean, target: ClrRef<Int>) = Unit
    }
    private class OverloadedChild : OverloadedPickerBase<Concrete>() {
        override suspend fun <A : Concrete> pick(value: Concrete, source: Iterable<Int>): Any = value
        override suspend fun <A : Concrete> pick(value: Concrete, source: Sequence<Int>): Any = source
    }
    private suspend fun <K> via(picker: SuspendPicker<K>, value: K): Any where K : Base, K : ILeft, K : IRight =
        picker.pick<K>(value)

    @TestAttribute
    @Suppress("UNCHECKED_CAST")
    fun eachConstructionOfTheSameSuspendDeclarationRetainsItsOverride() {
        val both = Both()
        val first: SuspendPicker<Item> = both
        val second: SuspendPicker<OtherItem> = both
        val operand: Any = "value"
        var sum = 0
        first.write<Item>(Item(), operand is String, byref(sum))
        second.write<OtherItem>(OtherItem(), operand !is Int, byref(sum))
        check(sum == 3)
        var completed = false
        val calls: suspend () -> Unit = {
            check(both.pick<Item>(Item()) == "first" && both.pick<OtherItem>(OtherItem()) == "second")
            check(first.pick<Item>(Item()) == "first" && second.pick<OtherItem>(OtherItem()) == "second")
            check(via(first, Item()) == "first" && via(second, OtherItem()) == "second")
            check(pickThrough(first, Item()) == "first" && pickThrough(second, OtherItem()) == "second")
            check(pickThrough(Single() as SuspendPicker<OtherItem>, OtherItem()) == "single")
            check(pickStar(Single()) == "single")
            val overloaded: OverloadedPicker<Concrete> = Overloaded()
            check(overloaded.pick<Concrete>(Concrete(), listOf(1)) == "iterable")
            check(overloaded.pick<Concrete>(Concrete(), sequenceOf(1)) == "sequence")
            val base: OverloadedPickerBase<Concrete> = OverloadedChild()
            val item = Concrete()
            val sequence = sequenceOf(3)
            check(base.pick<Concrete>(item, listOf(1)) === item)
            check(base.pick<Concrete>(item, sequence) === sequence)
            var order = ""
            check(run { order += "r"; first }.pick<Item>(run { order += "a"; Item() }) == "first")
            check(order == "ra")
        }
        calls.startCoroutine(object : Continuation<Unit> {
            override val context = EmptyCoroutineContext
            override fun resumeWith(result: Result<Unit>) { result.getOrThrow(); completed = true }
        })
        check(completed)
        check(InheritedSuspendTaskApi.Pick<Item>(first, Item()) == "first")
        check(InheritedSuspendTaskApi.Pick<OtherItem>(second, OtherItem()) == "second")
    }

    @TestAttribute
    fun suspendOverrideRetainsTheColdAndTaskSlots() {
        val gate = IntGate()
        val child = SuspendChild(Item(), gate)
        val factory: SuspendFactory<Concrete> = child
        val left: SuspendLeft<Concrete> = child
        val right: SuspendRight<Concrete> = child
        val exact: SuspendFactory<Concrete> = OrdinarySuspendFactory<Concrete, String>()
        val ordinaryTask = InheritedSuspendTaskApi.ThroughConstrainedTask(exact)
        check(JoinedSlot.IsValue<Item>(ordinaryTask.Result) && !JoinedSlot.IsValue<Concrete>(ordinaryTask.Result))
        var matched = false
        val cold: suspend () -> Any = {
            check(JoinedSlot.IsValue<Item>(exact.make<Item>()))
            val projected = OrdinarySuspendFactory<Leaf<*>, String>()
            val face: SuspendFactory<Leaf<*>> = projected
            val reverse: ReverseStorageFactory<Leaf<*>, String> = projected
            check(JoinedSlot.IsValue<Leaf<*>>(face.make<Leaf<*>>()) && JoinedSlot.IsValue<Leaf<*>>(reverse.make<Leaf<*>>()))
            var result = "before"
            reverse.replace(byref(result), "after")
            check(result == "after")
            val leaf = Leaf<String>()
            val split = SplitSuspendFactory<Leaf<*>>(leaf)
            val leftOnly: SuspendLeft<Leaf<*>> = split
            val rightOnly: SuspendRight<Leaf<*>> = split
            check(leftOnly.make<Leaf<*>>() === leaf && rightOnly.make<Leaf<*>>() === leaf)
            check(JoinedSlot.IsValue<Item>(left.make<Item>()))
            right.make<Item>()
        }
        cold.startCoroutine(object : Continuation<Any> {
            override val context: CoroutineContext get() = EmptyCoroutineContext
            override fun resumeWith(result: Result<Any>) { matched = JoinedSlot.IsValue<Item>(result.getOrThrow()) }
        })
        check(!matched && child.calls == 0)
        gate.resume(1)
        check(!matched && child.calls == 1)
        gate.resume(1)
        check(matched && child.calls == 2)
        val task = InheritedSuspendTaskApi.ThroughConstrainedTask(factory)
        check(!task.IsCompleted)
        gate.resume(2)
        check(task.IsCompleted && JoinedSlot.IsValue<Item>(task.Result) && child.calls == 4)
    }

    @TestAttribute
    fun eitherSelectedSlotKeepsTheExactArgument() {
        fun checkViews(nominal: NominalFactory<Concrete>, joint: JointFactory<Concrete>) {
            val first = nominal.make<Item>()
            val second = joint.make<Item>()
            check(JoinedSlot.IsValue<Item>(first) && JoinedSlot.IsValue<Item>(second))
            check(!JoinedSlot.IsValue<Concrete>(first) && !JoinedSlot.IsValue<Concrete>(second))
            check(JoinedSlot.IsValue<Leaf<*>>(nominal.make<Leaf<*>>()))
            check(JoinedSlot.IsValue<Leaf<*>>(joint.make<Leaf<*>>()))
            val item = Item()
            check(nominal.select(item) === item && joint.select(item) === item)
        }
        val local = LocalFactory()
        val imported = ImportedFactory()
        checkViews(local, local)
        checkViews(imported, imported)
        val parent: GenericParent<*> = GenericParent<String>()
        val joint: JointFactory<Concrete> = parent
        check(JoinedSlot.IsValue<Item>(joint.make<Item>()) && JoinedSlot.IsValue<Item>(parent.make<Item>()))
        val item = Item()
        check(joint.select(item) === item && parent.select(item) === item)
        val queue = Queue<Item?>()
        check(takeNullable<Item>(parent, queue) === queue)
        val factory = FacetFactory()
        val left: LeftFactory<Concrete> = factory
        val right: RightFactory<Concrete> = factory
        val fallback: DefaultFactory<Concrete> = factory
        check(JoinedSlot.IsValue<Item>(left.make<Item>()))
        check(JoinedSlot.IsValue<Item>(right.make<Item>()))
        check(JoinedSlot.IsValue<Item>(fallback.make<Item>()))
        check(JoinedSlot.IsValue<Leaf<*>>(fallback.make<Leaf<*>>()))
    }
}
