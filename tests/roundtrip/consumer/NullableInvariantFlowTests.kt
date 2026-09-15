package roundtriptests.nullableinvariantflow

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import roundtrip.nullableinvariantflow.*

class NullableInvariantFlowTests {
    @TestAttribute
    fun separateProducerFilesSelectTheOriginalAbstractOverload() {
        val receiver = StringReceiver()
        assertEquals("box:selected", invokeBoxReceiver(receiver, Box("selected")))
        assertEquals("value:selected", invokeValueReceiver(receiver, "selected"))
        invokeAcceptReceiver(receiver, Box("accepted"))
        assertEquals("accepted", receiver.last)
    }

    @TestAttribute
    fun typeAndMethodBoundsRetainTheirKotlinGenericArguments() {
        val box = Box<String?>("bounded")
        val owner = Bound(box)
        assertTrue(owner.box === box)
        assertEquals("bounded", owner.box.value)
        assertEquals("bounded", readBound(box))
    }

    @TestAttribute
    fun inheritedPropertySlotsShareTheirImplementation() {
        val value = KeyImpl("key")
        val child: KeyChild<String> = value
        val root: KeyRoot<String> = value
        assertEquals("key", value.key)
        assertEquals("key", child.key)
        assertEquals("key", root.key)
        assertEquals(1, root.marker)
        assertEquals(1, readLocalMarker(root))
        val integerRoot: KeyRoot<Int> = KeyImpl(23)
        assertEquals(23, integerRoot.key)
    }

    @TestAttribute
    fun directAndInterfaceOverridesPreserveExistingObjectIdentity() {
        val box = Box<String?>(null)
        val direct = StringExchange()
        val throughInterface: Exchange<String> = direct
        assertTrue(direct.exchange(box) === box)
        assertTrue(throughInterface.exchange(box) === box)
        assertTrue(sameModule(box) === box)
        assertTrue(StringExchange().exchange(Box<String?>(null)).value == null)
        val alias = throughInterface.exchange(box)
        alias.value = "changed"
        assertEquals("changed", box.value)
        box.value = null
        assertTrue(alias.value == null)
    }

    @TestAttribute
    fun genericFunctionsPreserveAliasesAndNullablePayloads() {
        val strings = Box<String?>(null)
        val alias = identity(nullableIdentity<String>(strings))
        assertTrue(alias === strings)
        replace<String>(alias, "present")
        assertEquals("present", strings.value)
        replace<String>(strings, null)
        assertTrue(alias.value == null)
        assertEquals("created", create<String>("created").value)
        assertTrue(create<String>(null).value == null)
        assertEquals(42, create<Int>(42).value)
        assertTrue(create<Int>(null).value == null)
    }

    @TestAttribute
    fun ordinaryInvariantValuesRetainTypedReadsAndWrites() {
        val integers = Box(10)
        val alias = identity(integers)
        assertTrue(alias === integers)
        alias.value = alias.value + 7
        assertEquals(17, integers.value)
        val strings = Box("before")
        identity(strings).value = "after"
        assertEquals("after", strings.value)
    }

    @TestAttribute
    fun nestedGenericAndArrayStorageKeepTheSameValues() {
        val inner = create<String>("before")
        val outer = Box(inner)
        val array = arrayOf(outer)
        assertTrue(identity(array[0]).value === inner)
        replace<String>(array[0].value, "after")
        assertEquals("after", outer.value.value)
        val replacement = create<String>(null)
        array[0].value = replacement
        assertTrue(outer.value === replacement)
        assertTrue(outer.value.value == null)
    }
}
