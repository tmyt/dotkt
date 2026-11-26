package roundtrip.consumer.selectedbounds

import NUnit.Framework.TestAttribute
import RoundtripDependentNative.JoinedSlot
import roundtrip.selectedbounds.*

class ImportedFactory : JointFactory<Concrete>, NominalFactory<Concrete> {
    override fun <A : Concrete> make(): Any = Product<A>()
    override fun <A : Concrete> select(value: A): A = value
}

class FacetFactory : LeftFactory<Concrete>, RightFactory<Concrete>, DefaultFactory<Concrete> {
    override fun <A : Concrete> make(): Any = Product<A>()
}

class SelectedMethodBoundsTests {
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
        check(joint.make<Item>() == "generic" && parent.make<Item>() == "generic")
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
