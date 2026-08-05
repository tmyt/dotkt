// .NET method-reference / super-call interop battery (feature fixture). The Interop consumer resolves
// the .NET types through reference KLIBs.
//
// Coverage preserved (old case -> method):
//   il-mref      -> mref_boundAndUnboundNetMethodRefs  bound (`obj::m`) + unbound (`NetType::m`) .NET method references over StringBuilder
//   il-supernet  -> supernet_superToNetBase            #14 R2: super.<m>() to a reference-KLIB-projected .NET base (System.Random) is a NON-virtual `call` (no re-dispatch → no infinite recursion)
//
// NB: il-mref's old runtime.cs was a no-op (the sample uses only BCL StringBuilder), so it is dropped here.
//
// Top-level names are family-prefixed with `BclMethodReference` (one assembly = one namespace) to avoid clashing with
// sibling batteries and the stdlib.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import System.Text.StringBuilder
import System.Random

// il-mref : a higher-order helper the unbound method ref flows through.
fun <T> bclMethodReferenceMrefApply1(f: (StringBuilder) -> T, sb: StringBuilder): T = f(sb)

// il-supernet : super.<m>() to a reference-KLIB-projected .NET base (System.Random) must be a non-virtual base-slot call.
class BclMethodReferenceSupernetSeededRandom(seed: Int) : Random(seed) {
    override fun Next(): Int = super.Next() + 1000   // super -> System.Random::Next (non-virtual base slot)
}

class BclMethodReferenceTests {
    // il-mref: bound (`sb::ToString` -> delegate over the instance) and unbound (`StringBuilder::Clear` -> a
    // lifted __mref(self) = self.Clear()) .NET method references.
    @TestAttribute
    fun boundAndUnboundNetMethodRefs() {
        val sb = StringBuilder()
        sb.Append("hello world")
        val g: () -> String = sb::ToString                              // bound .NET method ref
        assertEquals("hello world", g())                               // hello world
        val cleared = bclMethodReferenceMrefApply1(StringBuilder::Clear, sb)      // unbound .NET method ref
        assertEquals(0, cleared.ToString().length)                     // 0
    }

    // il-supernet: super.Next() reaches the System.Random base slot non-virtually — no callvirt re-dispatch to
    // THIS class's override, so no infinite recursion; the same seed stays deterministic across two instances.
    @TestAttribute
    fun superToNetBase() {
        val r1 = BclMethodReferenceSupernetSeededRandom(42)
        val r2 = BclMethodReferenceSupernetSeededRandom(42)
        val v1 = r1.Next()
        val v2 = r2.Next()
        assertTrue(v1 >= 1000)   // True  (super.Next() returned a non-negative base value + the 1000 offset)
        assertTrue(v1 == v2)     // True  (same seed -> deterministic; the override ran on both)
    }
}
