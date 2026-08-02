// An OMITTED `vararg` argument — Kotlin's empty array of the element type.
//
// A vararg is omissible without being optional: Kotlin forbids it a default expression, so it reaches neither half of
// the emitter's default fill. Keyed on `defaultValue`, both halves dropped the slot outright and the emitted call
// carried an argument vector one shorter than the declaration it named — `sumOf()` on `fun sumOf(vararg xs: Int)` was
// refused at CIL emission, not at run time.
//
// One case per arm that can fail on its own: the plain fill, the fill whose element type is the callee's open type
// parameter (a different type frame), the fill's POSITION among supplied arguments, and the inline splice (a separate
// emitter, which failed with its own diagnostic). Every method also asserts the SUPPLIED form of the same call so a
// fix that swallowed real arguments would not pass.
//
// The .NET half of the same family (a projected `params` parameter, and the overload the frontend selects for
// `Console.WriteLine("x")` because of one) lives in tests/interop/consumer/fixtures/BclConsoleWriteTests.kt.
//
// Top-level names are family-prefixed with `varargOmission` (one assembly = one namespace).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals

fun varargOmissionSumOf(vararg xs: Int): Int = xs.sum()
fun varargOmissionNames(vararg xs: String): String = xs.joinToString(",")
fun <T> varargOmissionCountOf(vararg xs: T): Int = xs.size
// A vararg followed by another parameter: only a named argument can reach `tail`, so `head` is omitted at slot 0
// while a LATER slot is supplied — the fill has to land in its own position, not at the end of the vector.
fun varargOmissionBeforeNamed(vararg head: Int, tail: String): String = "${head.size}:$tail"
inline fun varargOmissionInline(vararg xs: String, transform: (Int) -> Int): Int = transform(xs.size)

class VarargOmissionTests {
    // The plain fill, in both physical array forms: a primitive element gives the specialized `IntArray`, a reference
    // element gives `Array<String>`, and the emitter picks between them.
    @TestAttribute
    fun omittedVarargIsAnEmptyArray() {
        assertEquals(0, varargOmissionSumOf())
        assertEquals(6, varargOmissionSumOf(1, 2, 3))
        assertEquals("", varargOmissionNames())
        assertEquals("a,b", varargOmissionNames("a", "b"))
    }

    // The element type is the CALLEE's type parameter, so the fill is only correct if it is rendered in the callee's
    // type frame closed against this call site — `Array<String>`, never the open `T`.
    @TestAttribute
    fun genericVarargFillsAtTheCallSiteTypeArgument() {
        assertEquals(0, varargOmissionCountOf<String>())
        assertEquals(2, varargOmissionCountOf("a", "b"))
    }

    // The fill sits BEFORE a slot this call supplies, so it exercises the positional placement rather than a trailing
    // append.
    @TestAttribute
    fun omittedVarargKeepsItsOwnSlot() {
        assertEquals("0:x", varargOmissionBeforeNamed(tail = "x"))
        assertEquals("2:x", varargOmissionBeforeNamed(1, 2, tail = "x"))
    }

    // The inline splice builds its argument vector in its own emitter and failed loud on the null slot with its own
    // diagnostic, so it can regress while every call above stays green.
    @TestAttribute
    fun inlineSpliceFillsTheOmittedVararg() {
        assertEquals(0, varargOmissionInline { it })
        assertEquals(4, varargOmissionInline("a", "b") { it * 2 })
    }
}
