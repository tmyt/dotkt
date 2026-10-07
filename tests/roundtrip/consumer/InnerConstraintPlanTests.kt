package roundtriptests.innerconstraintplan

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import innerconstraint.*

class InnerConstraintPlanTests {
    @TestAttribute
    fun constructedOwnerBoundsAndIndependentConstraintsSurviveRoundtrip() {
        val exact = owner()
        assertTrue(exact.Direct<String, Nothing>(null).value == null)
        val star: Owner<*> = exact
        assertTrue(star.Direct<Int, Nothing>(null).value == null)
        val value = Value<Nothing>()
        val transitive = star.Transitive<Nothing, Value<Nothing>>(value)
        assertTrue(transitive.value === value)
        assertTrue(transitive.label() == "value")
    }
}
