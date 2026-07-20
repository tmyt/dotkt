// Generic .NET-type interop battery (batch IntropB) — migrates the facadegen-driven cases/il-netgen* onto the
// in-process NUnit suite. The old cases were driven by a pre-authored facadegen `.meta` (COLLMETA/GMMETA = a
// scan of System.Collections.ObjectModel.Collection + System.Runtime.CompilerServices.Unsafe/RuntimeHelpers);
// the equivalent in-process form is a plain `import System.*` — the tests/il .ktproj runs the facadegen
// scan-imports pipeline, so the generic .NET types/methods inject at compile. Each old case's `main` +
// stdout-golden becomes one @TestAttribute method preserving every asserted value 1:1 (see `// <expected>`).
//
// Coverage preserved (old case -> method):
//   il-netgen   -> netgen_useGenericNetType        use a generic .NET type (Collection<Int>) façade-free
//   il-netgen2  -> netgen2_inheritGenericNetBase   inherit a GENERIC .NET base class (: Collection<Int>())
//   il-netgen3  -> netgen3_genericMethodsAndIndexer generic .NET static methods (Unsafe.SizeOf<T>/IsReferenceOrContainsReferences<T>) + injected generic this[i] indexer
//
// Top-level names are family-prefixed with `IntropB` (one assembly = one namespace) to avoid clashing with
// sibling batteries and the stdlib.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsTrue as assertTrue
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsFalse as assertFalse
import System.Collections.ObjectModel.Collection
import System.Runtime.CompilerServices.Unsafe
import System.Runtime.CompilerServices.RuntimeHelpers

// il-netgen2 : inherit a GENERIC .NET base class, façade-free.
class IntropBNetGen2IntColl : Collection<Int>() {
    fun addAll(vararg xs: Int) { for (x in xs) Add(x) }
}

class MigratedIntropBNetGenTests {
    // il-netgen: use a generic .NET type (System.Collections.ObjectModel.Collection<Int>) directly.
    @TestAttribute
    fun netgen_useGenericNetType() {
        val c = Collection<Int>()
        c.Add(10)
        c.Add(20)
        c.Add(30)
        assertEquals(3, c.Count)        // 3
        assertTrue(c.Contains(20))      // True
        assertEquals(2, c.IndexOf(30))  // 2
    }

    // il-netgen2: inherit the generic .NET base class; the derived helper drives the inherited Add.
    @TestAttribute
    fun netgen2_inheritGenericNetBase() {
        val c = IntropBNetGen2IntColl()
        c.addAll(5, 7, 9)
        assertEquals(3, c.Count)        // 3
        assertTrue(c.Contains(7))       // True
        assertEquals(2, c.IndexOf(9))   // 2
    }

    // il-netgen3: generic .NET static methods (MakeGenericMethod'd against the call's type arg) + the
    // injected generic `this[i]` indexer (get_Item / set_Item on the constructed type).
    @TestAttribute
    fun netgen3_genericMethodsAndIndexer() {
        assertEquals(4, Unsafe.SizeOf<Int>())                                     // 4
        assertEquals(8, Unsafe.SizeOf<Long>())                                    // 8
        assertEquals(8, Unsafe.SizeOf<Double>())                                  // 8
        assertFalse(RuntimeHelpers.IsReferenceOrContainsReferences<Int>())        // False (a primitive holds no references)
        assertTrue(RuntimeHelpers.IsReferenceOrContainsReferences<String>())      // True  (a reference type)

        val c = Collection<Int>()
        c.Add(10); c.Add(20); c.Add(30)
        assertEquals(20, c[1])       // 20  (get_Item)
        c[1] = 99                    // set_Item
        assertEquals(99, c[1])       // 99
        assertEquals(3, c.Count)     // 3
    }
}
