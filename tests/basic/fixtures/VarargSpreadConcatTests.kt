// A vararg argument that MIXES literal elements with a spread — `f(1, *xs, 2)`.
//
// This is the only source shape that mints the `spreadConcat` construction node: a lone spread forwards the existing
// array and an all-literal list builds one directly, so neither reaches it. The node accumulates into a `List<E>` and
// hands over its `ToArray()`, and the four members it builds through — the constructor, `Add`, `AddRange`, `ToArray`
// — plus the list type itself are named by bir2cir; the emitter builds through exactly those and names no BCL type
// of its own (#400). Nothing in the standard library writes a mixed vararg, so without these methods the whole node
// had no gate coverage and its only evidence was a throwaway probe.
//
// One method per element shape the accumulator instantiation can take: a primitive (`List<int>`), a reference type,
// a type this compilation is itself emitting (the emitter cannot reflect on `List<TypeBuilder>` and builds a
// signature view instead), and the callee's own open type parameter. Each asserts ORDER as well as contents, because
// a spread appended rather than spliced still gives the right length.
//
// A NULLABLE VALUE element (`vararg xs: Int?`) is deliberately absent. That shape canonicalizes the vararg's array
// element to `object`, and a mixed spread into it miscompiles today: the literal parts reach `Add` unconverted,
// which is invalid IL (InvalidProgramException on first call), and coercing them to the element slot then exposes a
// second fault in how the values come back out. That is about how the accumulator's element slot is FILLED, not
// about how its members are named, so it is tracked on its own rather than pinned here.
//
// Top-level names are family-prefixed with `varargSpreadConcat` (one assembly = one namespace).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals

class VarargSpreadConcatBox(val v: Int)

fun varargSpreadConcatInts(vararg xs: Int): String = xs.joinToString(",")
fun varargSpreadConcatStrings(vararg xs: String): String = xs.joinToString(",")
fun varargSpreadConcatBoxes(vararg xs: VarargSpreadConcatBox): String = xs.joinToString(",") { it.v.toString() }
fun <T> varargSpreadConcatOpen(vararg xs: T): String = xs.joinToString(",")

class VarargSpreadConcatTests {
    // A primitive element: the accumulator is `List<int>` and its `ToArray()` gives the `int[]` the vararg slot wants.
    @TestAttribute
    fun mixedSpreadOfPrimitiveElements() {
        val middle = intArrayOf(2, 3)
        assertEquals("1,2,3,4", varargSpreadConcatInts(1, *middle, 4))
        assertEquals("1,4", varargSpreadConcatInts(1, *intArrayOf(), 4))
        assertEquals("2,3", varargSpreadConcatInts(*middle))
        // Two spreads either side of a literal: every part goes through the same accumulator, in order.
        assertEquals("2,3,9,2,3", varargSpreadConcatInts(*middle, 9, *middle))
    }

    // A reference element.
    @TestAttribute
    fun mixedSpreadOfReferenceElements() {
        val middle = arrayOf("b", "c")
        assertEquals("a,b,c,d", varargSpreadConcatStrings("a", *middle, "d"))
        assertEquals("a,d", varargSpreadConcatStrings("a", *arrayOf<String>(), "d"))
    }

    // The element type is declared by THIS compilation, so the accumulator instantiation is over a type still being
    // emitted — the case the emitter cannot reflect on and describes with a signature view instead.
    @TestAttribute
    fun mixedSpreadOfLocallyEmittedElements() {
        val middle = arrayOf(VarargSpreadConcatBox(2), VarargSpreadConcatBox(3))
        assertEquals("1,2,3,4",
            varargSpreadConcatBoxes(VarargSpreadConcatBox(1), *middle, VarargSpreadConcatBox(4)))
    }

    // The element type is the CALLEE's own type parameter, closed at this call site.
    @TestAttribute
    fun mixedSpreadOfOpenElements() {
        val middle = arrayOf("b", "c")
        assertEquals("a,b,c,d", varargSpreadConcatOpen("a", *middle, "d"))
        val numbers = arrayOf(2, 3)
        assertEquals("1,2,3,4", varargSpreadConcatOpen(1, *numbers, 4))
    }
}
