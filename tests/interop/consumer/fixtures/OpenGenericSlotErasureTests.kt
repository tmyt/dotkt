// #86 D1 — an object-ERASED value handed to an OPEN .NET generic parameter slot.
//
// A `T?` on an unconstrained `T` is `System.Object`, so `intropOgBoxOf<Int>(7)` produces a bare `object`. When that
// value flows into a .NET generic member, the slot it fills is stated OPEN: `Enumerable.Repeat<T>`'s first parameter
// is `!!0` in the call's `memberSig`, and it stays `!!0` because that open form is what the emitter matches the
// member by. The conversion into it must therefore be typed by the CLOSED slot — the call's own type argument,
// `Nullable<int32>` here. Converting to the open node instead emits a cast to whatever `!!0` lowers to at the CALL
// SITE (the caller's own type parameter, or `object`), which pushes an `object` where a `Nullable<int32>` is
// required and fails JIT verification: the whole method dies with InvalidProgramException before printing anything.
//
// The nullable-generic family reaches this arm the moment a call crosses into .NET, and no same-assembly fixture can
// witness it — the callee has to be a real .NET generic whose declared parameter is a type variable.
//
// The CLOSED slot is equally the answer when the callee is NOT generic. A .NET member's parameter is a resolved CLR
// slot whatever its arity, so the erased `object` still has to be narrowed into it — including into a REFERENCE slot
// like `string`, where the narrowing is a `castclass` the CLR insists on even though both sides are references. The
// non-generic cases below are the ones a `memberSig`-presence test missed: a generic .NET call carries `memberSig`
// from the moment it is bound, while a non-generic one carries `argTypes` until member resolution stamps `memberSig`,
// long after this narrowing is decided. They ran (object and string are reference-compatible at run time) but the
// emitted method did not verify — `[found ref 'object'][expected ref 'string']` — so the RUN assertion alone would
// not have caught it; the suite's ILVerify lane is what makes them a witness.
//
// Top-level names are family-prefixed with `IntropOg` (one assembly = one namespace).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import System.Linq.Enumerable
import System.IO.Path
import System.Text.StringBuilder as IntropOgNetStringBuilder

fun <T> intropOgBoxOf(x: T?): T? = x

class OpenGenericSlotErasureTests {
    // The erased argument at a VALUE instantiation: `Enumerable.Repeat<Int?>(<object>, 3)`.
    @TestAttribute
    fun erasedValueIntoOpenGenericSlot() {
        var sum = 0
        for (v in Enumerable.Repeat(intropOgBoxOf<Int>(7), 3)) if (v != null) sum = sum + v
        assertEquals(21, sum)                                  // 21   three boxed 7s narrowed back
    }

    // The absent case through the same slot — the erasure's other state must survive the round trip.
    @TestAttribute
    fun erasedNullIntoOpenGenericSlot() {
        var present = 0
        var absent = 0
        for (v in Enumerable.Repeat(intropOgBoxOf<Int>(null), 2)) if (v == null) absent = absent + 1 else present = present + 1
        assertEquals(0, present)                               // 0
        assertEquals(2, absent)                                // 2
    }

    // The REFERENCE instantiation of the identical shape — the control that makes the value axis the subject.
    @TestAttribute
    fun erasedReferenceIntoOpenGenericSlot() {
        var joined = ""
        for (v in Enumerable.Repeat(intropOgBoxOf<String>("a"), 2)) joined = joined + (v ?: "-")
        assertEquals("aa", joined)                             // aa
    }

    // A NON-GENERIC .NET static whose slot is a reference: `Path.GetExtension(string?)`.
    @TestAttribute
    fun erasedReferenceIntoNonGenericStaticSlot() {
        assertEquals(".txt", Path.GetExtension(intropOgBoxOf<String>("report.txt")))
    }

    // The instance form of the same slot: `StringBuilder.Append(string?)`.
    @TestAttribute
    fun erasedReferenceIntoNonGenericInstanceSlot() {
        val sb = IntropOgNetStringBuilder()
        sb.Append(intropOgBoxOf<String>("ab"))
        assertEquals("ab", sb.ToString())
    }

    // And the CONSTRUCTOR form — a `newClr` slot is a declaration slot like any other.
    @TestAttribute
    fun erasedReferenceIntoNonGenericConstructorSlot() {
        assertEquals("cd", IntropOgNetStringBuilder(intropOgBoxOf<String>("cd")).ToString())
    }
}
