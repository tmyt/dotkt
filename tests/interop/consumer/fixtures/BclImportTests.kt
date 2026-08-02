// BCL-interop battery (feature fixture). These fixtures resolve real .NET types from the Interop
// consumer's dll2klib-generated reference KLIBs.
//
// Coverage preserved (old case -> method):
//   il-alias     -> alias_aliasedImport        `import X as Y` resolves the projected type and binds the alias
//   il-dualrep   -> dualrep_twoViewsOneClass    System.Text.StringBuilder (raw) vs kotlin.text.StringBuilder coexist; cast crosses
//   m-i1         -> twoViewsOneClass             raw StringBuilder fluent Append(String/Int), ToString, and Length
//   ktproj-import-> genericFactoryCtorAndStatic  façade-free System.Math import and static Max call
//   il-bclinject -> bclinject_genericFactoryCtorAndStatic  #143 generic value-factory ctor + reference-oblivious Value + static GetHashCode
//   il-tlvalint  -> tlvalint_valueTypeObliviousValue        #8/#11 ThreadLocal<Int>.Value is a bare int32 (default 0), value/ref twin
//
// Top-level names are family-prefixed with `BclImport` (one assembly = one namespace) to avoid clashing with sibling
// batteries and the stdlib.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsTrue as assertTrue
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsFalse as assertFalse
import System.Text.StringBuilder as SB          // il-alias — aliased .NET import
import System.Text.StringBuilder                // il-dualrep — the raw .NET view (bare `StringBuilder` in this file)
import System.Threading.ThreadLocal             // il-bclinject / il-tlvalint
import System.Runtime.CompilerServices.RuntimeHelpers  // il-bclinject
import System.Math                         // ktproj-import — façade-free raw BCL static type

// il-dualrep : the stdlib (kotlin.text) view of the SAME CLR type, in the same program.
fun bclImportUseKt(sb: kotlin.text.StringBuilder): String = sb.toString()

class BclImportTests {
    // il-alias: `import System.Text.StringBuilder as SB` must inject the type AND bind the alias `SB`.
    @TestAttribute
    fun aliasedImport() {
        val sb = SB()
        sb.Append("hello")
        sb.Append(", ")
        sb.Append("alias")
        assertEquals("hello, alias", sb.ToString())   // hello, alias
        assertEquals(12, sb.Length)                    // 12
    }

    // il-dualrep: the raw .NET view (Append/Length/ToString) and the stdlib kotlin.text.StringBuilder view coexist;
    // an explicit cast is the escape hatch across the two frontend types over the one CLR runtime type.
    @TestAttribute
    fun twoViewsOneClass() {
        val net = StringBuilder()                      // the imported .NET view
        net.Append("net").Append(42)                  // fluent chain over String + Int
        assertEquals("net42", net.ToString())          // net42
        assertEquals(5, net.Length)                    // 5
        val s = buildString { append("kt") }           // the stdlib view: buildString over kotlin.text.StringBuilder
        assertEquals("kt", s)                          // kt
        @Suppress("CAST_NEVER_SUCCEEDS")
        val kt = net as kotlin.text.StringBuilder      // escape hatch: both erase to System.Text.StringBuilder
        assertEquals("net42", bclImportUseKt(kt))      // net42
    }

    // il-bclinject: #143 generic value-factory ctor injects; reference-oblivious `Value` is null when unset; static
    // RuntimeHelpers.GetHashCode injects.
    @TestAttribute
    fun genericFactoryCtorAndStatic() {
        assertEquals(40, Math.Max(40, 2))              // static call on an imported raw BCL type
        val tf = ThreadLocal<String>({ "hi" })         // generic value-factory ctor
        assertEquals("hi", tf.Value)                   // hi
        val te = ThreadLocal<String>()
        val v = te.Value                               // Value is a PLATFORM type (String!), null when unset
        assertTrue(v == null)                          // True — the `== null` is legal and true at runtime
        val o: Any = "x"
        assertTrue(RuntimeHelpers.GetHashCode(o) == RuntimeHelpers.GetHashCode(o))  // True — static GetHashCode injects
    }

    // il-tlvalint: #8/#11 a `[MaybeNull]` VALUE-type getter (ThreadLocal<Int>.Value) is an oblivious `Int!` that lowers
    // to a BARE int32 (default 0, not Nullable<Int32>); the ThreadLocal<String> twin proves the reference oblivious keeps
    // a real null check. WRITE side coerces a Nullable<Int32> source down to the bare int32 setter.
    @TestAttribute
    fun valueTypeObliviousValue() {
        val ti = ThreadLocal<Int>()
        val n: Int = ti.Value                          // value-type platform default -> 0
        assertEquals(0, n)                             // 0
        assertFalse(ti.Value == null)                  // False — a bare value type, `== null` is statically false
        val e: Int = ti.Value ?: 99                    // elvis over a non-null bare value -> the value itself
        assertEquals(0, e)                             // 0

        val ts = ThreadLocal<String>()
        assertTrue(ts.Value == null)                   // True — reference oblivious, unset -> null

        ti.Value = 5                                   // bare non-null value write
        assertEquals(5, ti.Value)                      // 5
        val q: Int? = 7                                // a genuine Kotlin Int? = Nullable<Int32>, holding a value
        ti.Value = q                                   // bir2cir unwraps the Nullable<Int32> to the bare int32 slot
        assertEquals(7, ti.Value)                      // 7
        val sq: String? = "hi"                         // reference twin: no value coercion needed
        ts.Value = sq
        assertEquals("hi", ts.Value)                   // hi
    }
}
