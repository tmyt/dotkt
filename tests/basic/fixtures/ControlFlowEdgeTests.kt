// Control-flow / sliver battery (feature fixture) — the SINGLE non-duplicate scenario salvaged from each of the
// otherwise-redundant cases m-a1 / m-a2 (their other scenarios — extension funs, default
// args, arrays, when-multivalue+ranges, zip, Char.code, destructuring, Pair/to — are already covered by the
// migrated M1-M5 language batteries, so only the unique bit is carried here). Each scenario becomes one
// @TestAttribute method whose per-value assert is strictly stronger (typed) than the old JVM-oracle stdout diff;
// every asserted value it uniquely proved is preserved 1:1 (see `// <expected>`).
//
// Coverage preserved (old case -> method, UNIQUE scenario only):
//   m-a1  -> sealedWhenIsSmartCast   exhaustive `when (is)` over a sealed type + smart cast on the branch
//   m-a2  -> doWhileLabeledBreak     do-while loop + labeled break out of a nested for (break@outer)
// m-a3's Char-range coverage is subsumed by LanguageCoreTests.primitiveMembership (including evaluation order),
// and m-b5's preconditions by ReferencesPropertiesAndContractsTests.requireCheckErrorTodo.
//
// Top-level names are ControlFlow-prefixed (one assembly = one namespace).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals

// ---- m-a1 : sealed type + exhaustive when(is) + smart cast ---------------------------------------------------
sealed class ControlFlowNode
class ControlFlowLeaf(val v: Int) : ControlFlowNode()
class ControlFlowBranch(val n: Int) : ControlFlowNode()

fun controlFlowDescribe(node: ControlFlowNode): Int = when (node) {   // exhaustive when over a sealed type
    is ControlFlowLeaf -> node.v          // smart cast to ControlFlowLeaf: node.v
    is ControlFlowBranch -> node.n * 10   // smart cast to ControlFlowBranch: node.n
}

class ControlFlowEdgeTests {
    @TestAttribute
    fun sealedWhenIsSmartCast() {
        assertEquals(2, controlFlowDescribe(ControlFlowLeaf(2)))     // 2
        assertEquals(50, controlFlowDescribe(ControlFlowBranch(5)))  // 50
    }

    @TestAttribute
    fun doWhileLabeledBreak() {
        var i = 0
        do { i++ } while (i < 3)
        assertEquals(3, i)                             // do-while i=3
        var hit = ""
        outer@ for (a in 1..3) {
            for (b in 1..3) {
                if (a + b == 4) { hit = "$a,$b"; break@outer }   // labeled break out of the nested loop
            }
        }
        assertEquals("1,3", hit)                       // break at 1,3
    }

}
