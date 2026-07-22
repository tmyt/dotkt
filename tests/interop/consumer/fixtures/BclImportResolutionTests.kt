// Ktp BCL-import battery — migrates the single-project cases/ktproj-import .ktproj sample onto the in-process NUnit
// suite. Import-driven .NET resolution: just `import System.X`, no <KotlinClrType>, no façade, no clrgen — the
// facadegen import scan injects the types. The UNIQUE coverage here (vs the StringBuilder-only BclStringBuilderTests and
// the kotlin.math-lowering MathTests) is a façade-free `import System.Math` and a STATIC call on that imported .NET
// type (`Math.Max`) — the raw BCL System.Math, not the kotlin.math.* clrStatic lowering — combined with the fluent
// System.Text.StringBuilder.Append chain. The old case's stdout golden is preserved 1:1 (see `// <expected>`).
//
// Coverage preserved (old case -> method):
//   ktproj-import  -> import_bclStringBuilderAndMath  façade-free import System.Text.StringBuilder (fluent Append) +
//                                                     import System.Math (static Math.Max on the imported .NET type)
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import System.Text.StringBuilder
import System.Math

class BclImportResolutionTests {
    @TestAttribute
    fun bclStringBuilderAndMath() {
        assertEquals(40, Math.Max(40, 2))              // façade-free import System.Math: static call on the imported type
        val sb = StringBuilder()
        sb.Append("dotkt ").Append("imports ").Append("just work: ").Append(Math.Max(40, 2))  // fluent Append chain
        assertEquals("dotkt imports just work: 40", sb.ToString())   // dotkt imports just work: 40
    }
}
