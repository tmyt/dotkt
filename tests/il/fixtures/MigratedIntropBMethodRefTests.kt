// .NET method-ref / super-call interop battery (batch IntropB) — migrates the two misc BCL-interop cases
// (il-mref, il-supernet) onto the in-process NUnit suite. The tests/il .ktproj facadegen scan-imports pipeline
// injects the .NET types from `import System.*`. Each old case's `main` + stdout-golden becomes one
// @TestAttribute method preserving every asserted value 1:1 (see the `// <expected>` comments).
//
// Coverage preserved (old case -> method):
//   il-mref      -> mref_boundAndUnboundNetMethodRefs  bound (`obj::m`) + unbound (`NetType::m`) .NET method references over StringBuilder
//   il-supernet  -> supernet_superToNetBase            #14 R2: super.<m>() to a facadegen-injected .NET base (System.Random) is a NON-virtual `call` (no re-dispatch → no infinite recursion)
//
// NB: il-mref's old runtime.cs was a no-op (the sample uses only BCL StringBuilder), so it is dropped here.
//
// Top-level names are family-prefixed with `IntropB` (one assembly = one namespace) to avoid clashing with
// sibling batteries and the stdlib.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsTrue as assertTrue
import System.Text.StringBuilder
import System.Random

// il-mref : a higher-order helper the unbound method ref flows through.
fun <T> intropBMrefApply1(f: (StringBuilder) -> T, sb: StringBuilder): T = f(sb)

// il-supernet : super.<m>() to a facadegen-injected .NET base (System.Random) must be a non-virtual base-slot call.
class IntropBSupernetSeededRandom(seed: Int) : Random(seed) {
    override fun Next(): Int = super.Next() + 1000   // super -> System.Random::Next (non-virtual base slot)
}

class MigratedIntropBMethodRefTests {
    // il-mref: bound (`sb::ToString` -> delegate over the instance) and unbound (`StringBuilder::Clear` -> a
    // lifted __mref(self) = self.Clear()) .NET method references.
    @TestAttribute
    fun mref_boundAndUnboundNetMethodRefs() {
        val sb = StringBuilder()
        sb.Append("hello world")
        val g: () -> String = sb::ToString                              // bound .NET method ref
        assertEquals("hello world", g())                               // hello world
        val cleared = intropBMrefApply1(StringBuilder::Clear, sb)      // unbound .NET method ref
        assertEquals(0, cleared.ToString().length)                     // 0
    }

    // il-supernet: super.Next() reaches the System.Random base slot non-virtually — no callvirt re-dispatch to
    // THIS class's override, so no infinite recursion; the same seed stays deterministic across two instances.
    @TestAttribute
    fun supernet_superToNetBase() {
        val r1 = IntropBSupernetSeededRandom(42)
        val r2 = IntropBSupernetSeededRandom(42)
        val v1 = r1.Next()
        val v2 = r2.Next()
        assertTrue(v1 >= 1000)   // True  (super.Next() returned a non-negative base value + the 1000 offset)
        assertTrue(v1 == v2)     // True  (same seed -> deterministic; the override ran on both)
    }
}
