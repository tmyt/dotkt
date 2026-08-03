// An OMITTED `vararg` argument — Kotlin's empty array of the element type.
//
// A vararg is omissible without being optional: Kotlin forbids it a default expression, so it reaches neither half of
// the emitter's default fill. Keyed on `defaultValue`, both halves dropped the slot outright and the emitted call
// carried an argument vector one shorter than the declaration it named — `sumOf()` on `fun sumOf(vararg xs: Int)` was
// refused at CIL emission, not at run time.
//
// One case per arm that can fail on its own: the plain fill, the fill whose element type is the callee's open type
// parameter (a different type frame), the fill's POSITION among supplied arguments, the inline splice (a separate
// emitter, which failed with its own diagnostic), and the fill's SINGLE EVALUATION — the array is one value of the
// call, so a later default that names the vararg receives THAT array.
// Every method also asserts the SUPPLIED form of the same call so a fix that swallowed real arguments would not pass.
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
// The fill is ONE value of this call, so a later default that reads it reads THAT array. Two distinct empty arrays
// agree on every observable except IDENTITY, which is why these compare with `===`: a fill re-rendered per reader is
// invisible to `size`, `contentEquals` and everything else.
fun varargOmissionAliased(vararg xs: Int, a: IntArray = xs, b: IntArray = xs): Boolean = a === xs && b === xs
// The same, with the element type OPEN: this is the only shape that puts a generic fill under a plan, so it is the
// only one where the BINDING's declared type — the parameter's `Array<out T>`, not just the `newArray`'s element —
// has to be closed against the call site too. Left open, the binding declares a local in a frame that has no `T`.
fun <T> varargOmissionAliasedGeneric(vararg xs: T, a: Array<out T> = xs): Boolean = a === xs

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

    // ONE array, however many readers. The fill is a value of the call like any other, so it is allocated once and
    // every later reader — here two omitted defaults that name the vararg — reads that allocation. A fill left as a
    // raw expression is re-rendered per reader instead, which allocates a second and a third empty array; only
    // identity can see the difference, and `size`-shaped assertions cannot.
    @TestAttribute
    fun omittedVarargIsEvaluatedOnce() {
        assertEquals(true, varargOmissionAliased())
        assertEquals(true, varargOmissionAliased(1, 2))
    }

    // The same rule where the element type is the callee's own type parameter — the fill and the binding it becomes
    // are both written in the callee's frame, and both have to be closed against this call site.
    @TestAttribute
    fun genericOmittedVarargIsEvaluatedOnce() {
        assertEquals(true, varargOmissionAliasedGeneric<String>())
        assertEquals(true, varargOmissionAliasedGeneric("a", "b"))
    }
}
