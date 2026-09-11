import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.AreSame as assertSame

interface InheritedNameStringSlot { fun read(): String }
interface InheritedNameValueSlot<T> { fun read(): T }
open class InheritedNameStringBody {
    @kotlin.clr.ClrName("ReadString")
    fun read(): String = "named"
}
class InheritedNameString : InheritedNameStringBody(), InheritedNameStringSlot, InheritedNameValueSlot<String>
abstract class InheritedNameAbstract : InheritedNameStringBody(), InheritedNameStringSlot
class InheritedNameConcrete : InheritedNameAbstract()

open class InheritedNameOwnerBody<T>(private val value: T) {
    @kotlin.clr.ClrName("ReadValue")
    fun read(): T = value
}
class InheritedNameStringOwner : InheritedNameOwnerBody<String>("generic"), InheritedNameValueSlot<String>
class InheritedNameIntOwner : InheritedNameOwnerBody<Int>(42), InheritedNameValueSlot<Int>

interface InheritedNameMethodSlot { fun <T> identity(value: T): T }
open class InheritedNameMethodBody {
    @kotlin.clr.ClrName("Identity")
    fun <T> identity(value: T): T = value
}
class InheritedNameMethod : InheritedNameMethodBody(), InheritedNameMethodSlot

interface InheritedNameBoundSlot<T> { fun <R : T> identity(value: R): R }
open class InheritedNameBoundBody<T> {
    @kotlin.clr.ClrName("BoundIdentity")
    fun <R : T> identity(value: R): R = value
}
class InheritedNameBound : InheritedNameBoundBody<CharSequence>(), InheritedNameBoundSlot<CharSequence>

interface InheritedNameStringArgument { fun read(value: String): String }
interface InheritedNameIntArgument { fun read(value: Int): Int }
open class InheritedNameOverloadBody {
    @kotlin.clr.ClrName("ReadText")
    fun read(value: String): String = "text:$value"
    @kotlin.clr.ClrName("ReadNumber")
    fun read(value: Int): Int = value + 1
}
class InheritedNameOverloads : InheritedNameOverloadBody(), InheritedNameStringArgument, InheritedNameIntArgument

interface InheritedNameUnitSlot { fun read() }
open class InheritedNameUnitBody {
    @kotlin.clr.ClrName("ReadUnit")
    fun read() {}
}
class InheritedNameUnit : InheritedNameUnitBody(), InheritedNameUnitSlot, InheritedNameValueSlot<Unit>

class InheritedClrNameTests {
    @TestAttribute
    fun renamedFinalBodyFillsMultipleInterfaceSlots() {
        val body = InheritedNameString()
        val plain: InheritedNameStringSlot = body
        val generic: InheritedNameValueSlot<String> = body
        assertEquals("named", plain.read())
        assertEquals("named", generic.read())
        assertEquals("named", body.read())
    }

    @TestAttribute
    fun abstractIntermediateClassKeepsInheritedMapping() {
        val slot: InheritedNameStringSlot = InheritedNameConcrete()
        assertEquals("named", slot.read())
    }

    @TestAttribute
    fun constructedOwnerAndMethodFramesReachRenamedBodies() {
        val text: InheritedNameValueSlot<String> = InheritedNameStringOwner()
        val number: InheritedNameValueSlot<Int> = InheritedNameIntOwner()
        val method: InheritedNameMethodSlot = InheritedNameMethod()
        assertEquals("generic", text.read())
        assertEquals(42, number.read())
        assertEquals("method", method.identity("method"))
        assertEquals(43, method.identity(43))
        val bounded: InheritedNameBoundSlot<CharSequence> = InheritedNameBound()
        assertEquals("bounded", bounded.identity("bounded"))
    }

    @TestAttribute
    fun selectedOverloadsKeepSeparatePhysicalTargets() {
        val body = InheritedNameOverloads()
        val text: InheritedNameStringArgument = body
        val number: InheritedNameIntArgument = body
        assertEquals("text:argument", text.read("argument"))
        assertEquals(43, number.read(42))
    }

    @TestAttribute
    fun emptyUnitBodySupportsVoidAndValueSlots() {
        val body = InheritedNameUnit()
        val plain: InheritedNameUnitSlot = body
        val generic: InheritedNameValueSlot<Unit> = body
        assertSame(Unit, plain.read())
        assertSame(Unit, generic.read())
    }
}
