import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.AreSame as assertSame

private var subjectIdentityTrace = ""
private fun subjectIdentityEffect(tag: String, value: Int): Int {
    subjectIdentityTrace += tag
    return value
}

private fun ordinaryMutableUnit(flag: Boolean): Any = if (flag) {
    var value = 0
    if (flag) { value = 1 } else { value = 2 }
} else Unit

private fun ordinaryMutableBranch(flag: Boolean): Any = if (flag) {
    var value = subjectIdentityEffect("i", 1)
    if (flag) { value = 2; subjectIdentityEffect("t", value) } else { value = 3; subjectIdentityEffect("f", value) }
} else {
    var value = 0
    if (flag) { value = 4 } else { value = 5 }
}

private fun ordinaryWhenBranch(mode: Int): Int = if (mode >= 0) {
    var value = subjectIdentityEffect("v", 10)
    when (mode) {
        0 -> { value += 1; value }
        else -> { value += 2; value }
    }
} else -1

class LocalSubjectIdentityTests {
    @TestAttribute
    fun mutableDeclarationsKeepTheirWritesInIfAndWhenTails() {
        subjectIdentityTrace = ""
        assertSame(Unit, ordinaryMutableUnit(true))
        assertSame(Unit, ordinaryMutableUnit(false))
        assertEquals(2, ordinaryMutableBranch(true))
        assertSame(Unit, ordinaryMutableBranch(false))
        assertEquals(11, ordinaryWhenBranch(0))
        assertEquals(12, ordinaryWhenBranch(1))
        assertEquals("itvv", subjectIdentityTrace)
    }

    @TestAttribute
    fun ordinaryImmutableLocalsAndNestedCapturesKeepScopeIdentity() {
        subjectIdentityTrace = ""
        fun choose(flag: Boolean): Int = if (flag) {
            val value = subjectIdentityEffect("a", 6)
            if (value == 6) { value + 1 } else { -1 }
        } else {
            var value = subjectIdentityEffect("b", 8)
            if (value == 8) {
                val update = { value += 2 }
                update()
                val nested = if (value == 10) {
                    val value = subjectIdentityEffect("c", 20)
                    if (value == 20) value + 1 else -1
                } else -1
                value + nested
            } else -1
        }
        assertEquals(7, choose(true))
        assertEquals(31, choose(false))
        assertEquals("abc", subjectIdentityTrace)
    }

    @TestAttribute
    fun realWhenSubjectsAreEvaluatedOnce() {
        subjectIdentityTrace = ""
        fun choose(mode: Int): Int = when (subjectIdentityEffect("s", mode)) {
            0 -> 10
            1 -> 11
            else -> 12
        }
        assertEquals(10, choose(0))
        assertEquals(11, choose(1))
        assertEquals(12, choose(2))
        val named = when (val value = subjectIdentityEffect("n", 7)) {
            0 -> 0
            else -> value + 1
        }
        assertEquals(8, named)
        fun shadowed(n: Int): Int = when (val value = n) {
            0 -> 0
            else -> {
                val value = 20
                if (value == 20) value + 1 else -1
            }
        }
        assertEquals(21, shadowed(7))
        assertEquals(0, shadowed(0))
        val closures = when (val value = 7) {
            0 -> 0
            else -> {
                val captured = { value + 1 }
                val parameter = { value: Int -> value + 1 }
                captured() + parameter(20)
            }
        }
        assertEquals(29, closures)
        assertEquals("sssn", subjectIdentityTrace)
    }

    @TestAttribute
    fun safeCallAndElvisPreserveNullableValuesAndEvaluationOrder() {
        subjectIdentityTrace = ""
        fun receiver(present: Boolean): String? {
            subjectIdentityTrace += "r"
            return if (present) "abc" else null
        }
        fun length(present: Boolean): Int = receiver(present)?.length ?: subjectIdentityEffect("f", 9)
        assertEquals(3, length(true))
        assertEquals(9, length(false))
        val present: Int? = receiver(true)?.length
        val missing: Int? = receiver(false)?.length
        assertEquals(3, present)
        assertEquals(null, missing)
        assertEquals("rrfrr", subjectIdentityTrace)
    }
}
