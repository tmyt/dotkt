// .NET base-class interop battery (batch IntropB) — migrates the facadegen-driven cases/il-netbase* onto the
// in-process NUnit suite. The old cases were driven by a pre-authored facadegen `.meta` (EXCMETA = a scan of
// System.Exception/System.Console); the equivalent in-process form is a plain `import System.Exception` — the
// tests/il .ktproj runs the facadegen scan-imports pipeline, so the raw .NET Exception view (PascalCase
// `.Message`, the virtual property to override) injects at compile. Each old case's `main` + stdout-golden
// becomes one @TestAttribute method preserving every asserted value 1:1 (see the `// <expected>` comments).
//
// Coverage preserved (old case -> method):
//   il-netbase  -> netbase_inheritNetBaseClass     inherit System.Exception: base-ctor call + inherited .Message + own field
//   il-netbase2 -> netbase2_overrideNetVirtual     override System.Exception's VIRTUAL .Message, dispatched via the .NET base type
//
// Top-level names are family-prefixed with `IntropB` (one assembly = one namespace) to avoid clashing with
// sibling batteries and the stdlib.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import System.Exception

// il-netbase : inherit a real .NET base class — base ctor call + SetParent + inherited .NET member.
class IntropBNetBaseAppError(val code: Int) : Exception("app error")

// il-netbase2 : override a .NET base class's VIRTUAL member, dispatched polymorphically through the .NET base type.
open class IntropBNetBase2AppError(val code: Int) : Exception("base msg") {
    override val Message: String get() = "AppError #$code"
}
class IntropBNetBase2FatalError(code: Int) : IntropBNetBase2AppError(code)

// Takes the .NET base type; the override dispatches virtually.
fun intropBNetBase2Describe(e: Exception): String = e.Message

class MigratedIntropBNetBaseTests {
    // il-netbase: inherited System.Exception.Message (a .NET property) + own field.
    @TestAttribute
    fun netbase_inheritNetBaseClass() {
        val e = IntropBNetBaseAppError(7)
        assertEquals("app error", e.Message)   // app error   (inherited System.Exception.Message)
        assertEquals(7, e.code)                // 7           (own field)
    }

    // il-netbase2: the overridden .Message dispatches virtually through the .NET base type.
    @TestAttribute
    fun netbase2_overrideNetVirtual() {
        assertEquals("AppError #7", intropBNetBase2Describe(IntropBNetBase2AppError(7)))    // AppError #7
        assertEquals("AppError #21", intropBNetBase2Describe(IntropBNetBase2FatalError(21))) // AppError #21
    }
}
