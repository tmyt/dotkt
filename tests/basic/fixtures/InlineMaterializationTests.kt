// Migrated il batch M3 — inline-splice family. Each old case's `main` + stdout-golden diff becomes one
// @TestAttribute method whose per-value assertEquals is strictly stronger (typed) than the old text diff.
// Every value the old il_check asserted is preserved 1:1 (see the `// <expected>` comments).
//
// Coverage preserved (old case -> method):
//   il-inheritedgenericinline -> inheritedGenericInline  #88 inherited member `inline fun` on a GENERIC owner spliced at a subclass site
//   il-inlcompose             -> transitiveInlineForward  F3/#62 transitive forwarding of an inline PARAM through a user top-level inline + non-local return
//   il-inlnestparamshadow     -> nestedInlineParamShadow  F2/#61 nested inlineLambda param SHADOWING the outer callee's value param must NOT rebind
//   il-inlsiblingdelegate     -> siblingMaterializedCarrier  F4/#63 materialized carrier whose newDelegate targets a __lambda in a SIBLING file class
//   il-memberextinline        -> memberExtInlineNonLocal  #20 inline MEMBER-extension (companion + Long ext) w/ non-local return, extension receiver via __self
//
// All top-level declarations are M3-prefixed (one project = one namespace, shared with sibling batteries + stdlib).
// il-inlsiblingdelegate's b.kt half lives in InlineCrossFileSupport.kt to keep the cross-file-class scenario.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals

// ---- il-inheritedgenericinline : inherited member inline on a GENERIC owner -------------------------------------
abstract class M3Container<E>(val value: E) {
    inline fun transform(block: (E) -> E): E = block(value)
}
class M3IntBox(v: Int) : M3Container<Int>(v)
class M3StrBox(v: String) : M3Container<String>(v)
fun <T : M3Container<Int>> m3ViaBound(t: T): Int = t.transform { it + 12 }

// ---- il-inlcompose : transitive forwarding of an inline PARAM through a user top-level inline -------------------
inline fun m3IcInner(b: () -> Int): Int = b() + 1
inline fun m3IcOuter(b: () -> Int): Int = m3IcInner(b)
fun m3Compute(cond: Boolean): Int {
    val r = m3IcOuter {
        if (cond) return 99
        10
    }
    return r
}

// ---- il-inlnestparamshadow : nested inlineLambda param shadowing the outer callee's value param ----------------
inline fun m3IpsInner(a: Int, g: (Int) -> Int): Int = g(a)
inline fun m3IpsOuter(x: Int, f: (Int) -> Int): Int = f(m3IpsInner(x + 1) { x -> x * 10 })

// ---- il-inlsiblingdelegate (file A half) : materialized carrier over a SIBLING-file newDelegate ----------------
fun m3Sd_callIt(g: () -> Int): Int = g()
inline fun m3SdWrap(x: Int, crossinline t: (Int) -> Int): Int = m3Sd_callIt { t(x) }

// ---- il-memberextinline : inline MEMBER-extension (companion member + Long extension) with non-local return ----
class M3Queue {
    companion object {
        inline fun <T> Long.withState(block: (head: Int, tail: Int) -> T): T {
            val head = (this shr 8).toInt()
            val tail = (this and 0xFF).toInt()
            return block(head, tail)
        }
    }
    fun sum(state: Long): Int = state.withState { h, t -> h + t }
    fun firstNonZero(state: Long): Int {
        state.withState { h, t ->
            if (h != 0) return h
            if (t != 0) return t
        }
        return -1
    }
}

class InlineMaterializationTests {
    @TestAttribute
    fun inheritedGenericInline() {
        assertEquals(42, M3IntBox(20).transform { it + 22 })      // 42
        assertEquals("abcd", M3StrBox("ab").transform { it + "cd" }) // abcd
        assertEquals(42, m3ViaBound(M3IntBox(30)))                // 42
    }

    @TestAttribute
    fun transitiveInlineForward() {
        assertEquals(11, m3Compute(false))  // inner: b()+1 = 10+1 = 11
        assertEquals(99, m3Compute(true))   // non-local return 99 from the escaping lambda
    }

    @TestAttribute
    fun nestedInlineParamShadow() {
        // outer(5){ it+1000 }: inner(6){ x -> x*10 } = 60 ; f(60) = 1060 (pre-fix miscompile gave 1050)
        assertEquals(1060, m3IpsOuter(5) { it + 1000 })  // 1060
    }

    @TestAttribute
    fun siblingMaterializedCarrier() {
        // wrap(20){ m3Sd_bPick(false){5} }: carrier { t(20) } materialized; m3Sd_bPick(false){5} = m3Sd_bSink(7){it+100} = 107
        assertEquals(107, m3SdWrap(20) { m3Sd_bPick(false) { 5 } })  // 107
        assertEquals(5, m3SdWrap(10) { m3Sd_bPick(true) { 5 } })     // 5
    }

    @TestAttribute
    fun memberExtInlineNonLocal() {
        val q = M3Queue()
        assertEquals(3, q.sum(0x0102))          // head=1, tail=2 -> 3
        assertEquals(1, q.firstNonZero(0x0102)) // head=1 != 0 -> 1
        assertEquals(2, q.firstNonZero(0x0002)) // head=0, tail=2 -> 2
        assertEquals(-1, q.firstNonZero(0x0000)) // both 0 -> -1
    }
}
