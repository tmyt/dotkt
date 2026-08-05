// .NET base-class interop battery (feature fixture). The Interop consumer loads System.Exception from its
// reference KLIB, including the PascalCase virtual Message property.
//
// Coverage preserved (old case -> method):
//   il-netbase  -> netbase_inheritNetBaseClass     inherit System.Exception: base-ctor call + inherited .Message + own field
//   il-netbase2 -> netbase2_overrideNetVirtual     override System.Exception's VIRTUAL .Message, dispatched via the .NET base type
//
// Top-level names are family-prefixed with `BclInheritance` (one assembly = one namespace) to avoid clashing with
// sibling batteries and the stdlib.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import System.Exception
import System.InvalidOperationException

// il-netbase : inherit a real .NET base class — base ctor call + SetParent + inherited .NET member.
class BclInheritanceNetBaseAppError(val code: Int) : Exception("app error")

// il-netbase2 : override a .NET base class's VIRTUAL member, dispatched polymorphically through the .NET base type.
open class BclInheritanceNetBase2AppError(val code: Int) : Exception("base msg") {
    override val Message: String get() = "AppError #$code"
}
class BclInheritanceNetBase2FatalError(code: Int) : BclInheritanceNetBase2AppError(code)

// Takes the .NET base type; the override dispatches virtually.
fun bclInheritanceNetBase2Describe(e: Exception): String = e.Message

class BclInheritanceTests {
    // il-netbase: inherited System.Exception.Message (a .NET property) + own field.
    @TestAttribute
    fun inheritNetBaseClass() {
        val e = BclInheritanceNetBaseAppError(7)
        assertEquals("app error", e.Message)   // app error   (inherited System.Exception.Message)
        assertEquals(7, e.code)                // 7           (own field)
    }

    // il-netbase2: the overridden .Message dispatches virtually through the .NET base type.
    @TestAttribute
    fun overrideNetVirtual() {
        assertEquals("AppError #7", bclInheritanceNetBase2Describe(BclInheritanceNetBase2AppError(7)))    // AppError #7
        assertEquals("AppError #21", bclInheritanceNetBase2Describe(BclInheritanceNetBase2FatalError(21))) // AppError #21
    }

    @TestAttribute
    fun rawClrExceptionHierarchyIsThrowable() {
        val root = try {
            throw Exception("clr root")
        } catch (e: Exception) {
            e.Message
        }
        assertEquals("clr root", root)

        val derived = try {
            throw InvalidOperationException("clr derived")
        } catch (e: InvalidOperationException) {
            e.Message
        }
        assertEquals("clr derived", derived)
    }
}
