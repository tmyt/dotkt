package roundtrip.consumer.selectedbounds

import NUnit.Framework.TestAttribute
import RoundtripDependentNative.JoinedSlot
import System.Collections.Generic.Queue
import GenericValueInterop.InheritedSuspendTaskApi
import kotlin.coroutines.*
import roundtrip.selectedbounds.*

class ImportedFactory : JointFactory<Concrete>, NominalFactory<Concrete> {
    override fun <A : Concrete> make(): Any = Product<A>()
    override fun <A : Concrete> select(value: A): A = value
}

class FacetFactory : LeftFactory<Concrete>, RightFactory<Concrete>, DefaultFactory<Concrete> {
    override fun <A : Concrete> make(): Any = Product<A>()
}

private fun <A : Concrete> takeNullable(parent: GenericParent<*>, queue: Queue<A?>): Any = parent.take<A>(queue)

private class SuspendChild : SuspendParent() {
    var calls = 0
    override suspend fun <A : Concrete> make(): Any {
        pause()
        calls++
        return Product<A>()
    }
}

private suspend fun pause(): Unit = suspendCoroutine { it.resume(Unit) }

class SelectedMethodBoundsTests {
    @TestAttribute
    fun suspendOverrideRetainsTheColdAndTaskSlots() {
        val child = SuspendChild()
        val factory: SuspendFactory<Concrete> = child
        var matched = false
        val cold: suspend () -> Any = { factory.make<Item>() }
        cold.startCoroutine(object : Continuation<Any> {
            override val context: CoroutineContext get() = EmptyCoroutineContext
            override fun resumeWith(result: Result<Any>) { matched = JoinedSlot.IsValue<Item>(result.getOrThrow()) }
        })
        check(matched && child.calls == 1)
        check(JoinedSlot.IsValue<Item>(InheritedSuspendTaskApi.ThroughConstrainedTask(factory)) && child.calls == 2)
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
