package kotc.bir

import kotlin.test.Test
import kotlin.test.assertEquals

class ConstructorKindTest {
    @Test
    fun sourceConstructorKindIncludesFalseAndLeavesPhysicalSynthesisAbsent() {
        for ((primary, suffix) in listOf(true to ",\"primary\":true", false to ",\"primary\":false", null to "")) {
            val constructor = BirConstructor(emptyList(), null, emptyList(), primary = primary)
            assertEquals("{\"params\":[],\"baseArgs\":null,\"body\":[]$suffix}", buildString { constructor.writeJson(this) })
        }
    }
}
