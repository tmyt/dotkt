package roundtriptests.collectionarrayidentity

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import roundtrip.collectionarrayidentity.*

class CollectionArrayIdentityTests {
    @TestAttribute
    fun arrayAndVarargMetadataRetainReadOnlyElementTypes() {
        val values: Array<List<String>> = arrayOf(listOf("first"))
        val returned: Array<List<String>> = collectionArrayIdentity(values)
        assertTrue(returned === values)
        assertEquals("first", returned[0][0])
        assertEquals(1, collectionVarargSize(listOf("second")))
        val holder = CollectionArrays(values)
        assertTrue(holder.values === values)
        val replacement: Array<List<String>> = arrayOf(listOf("replacement"))
        holder.values = replacement
        assertTrue(holder.values === replacement)
    }
}
