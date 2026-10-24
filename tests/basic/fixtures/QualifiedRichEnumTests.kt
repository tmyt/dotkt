package privateenumprobe

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import publicenumprobe.State as PublicState

private enum class State(val score: Int) {
    ONE(1), TWO(2);

    fun ownName(): String = name
    fun ownOrdinal(): Int = ordinal
}

class NestedEnumOwner {
    enum class State(val score: Int) { ONE(1), TWO(2) }
}

fun privateName(): String = State.ONE.name
private fun privateOrdinal(value: State): Int = value.ordinal
fun publicName(): String = PublicState.ONE.name
fun publicOrdinal(value: PublicState): Int = value.ordinal
fun nestedName(): String = NestedEnumOwner.State.ONE.name
fun nestedOrdinal(value: NestedEnumOwner.State): Int = value.ordinal

class QualifiedRichEnumTests {
    @TestAttribute
    fun declarationOwnedNameAndOrdinalAcrossPackagesAndNesting() {
        assertEquals("ONE", privateName())
        assertEquals(1, privateOrdinal(State.TWO))
        assertEquals("TWO", State.TWO.ownName())
        assertEquals(1, State.TWO.ownOrdinal())
        assertEquals("ONE", publicName())
        assertEquals(1, publicOrdinal(PublicState.TWO))
        assertEquals("ONE", nestedName())
        assertEquals(1, nestedOrdinal(NestedEnumOwner.State.TWO))
        assertEquals("TWO", PublicState.valueOf("TWO").name)
        assertEquals(1, State.values()[1].ordinal)
    }
}
