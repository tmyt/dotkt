package roundtrip.consumer.selectedbounds

import NUnit.Framework.TestAttribute
import RoundtripDependentNative.JoinedSlot
import roundtrip.selectedbounds.*

class ImportedFactory : JointFactory<Concrete>, NominalFactory<Concrete> {
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
        }
        val local = LocalFactory()
        val imported = ImportedFactory()
        checkViews(local, local)
        checkViews(imported, imported)
    }
}
