// Nullable / null-safety battery — migrates the nullable-reference / nullable-value / safe-call / elvis / `!!` /
// not-null-assertion / nullable-primitive family of cases/il-* onto the in-process NUnit suite. Each old case's
// `main` + stdout-golden diff becomes one @TestAttribute method whose per-value assertEquals/assertNull is strictly
// stronger than the old string diff (a wrong non-null can't alias to the literal "null"). Every value the old
// il_check asserted is preserved 1:1 (see the `// <expected>` comments). Exception cases (`!!` on null) become the
// proven try/catch-sentinel pattern (the catch clause pins the EXACT exception type; StringsTests uses the same
// shape for NumberFormatException). The side-effecting `println` in the try/finally probe is captured into a log
// list and asserted in order.
//
// EXCLUDED from this family (matched the grep prefix but the real subject is FLOAT / IEEE behavior, not nullability
// — kept in the bash lane for a future float battery):
//   il-nan     -> Double/Float NaN comparisons + infinities (any comparison with NaN is false)  — IEEE-float family
//   il-nancmp  -> float `<=`/`>=` unordered-inverted CIL compares (cgt.un/clt.un)                 — IEEE-float family
//   il-negzero -> Double/Float total order (-0.0 < 0.0, NaN largest & NaN==NaN) boxed vs IEEE     — IEEE-float family
//
// Coverage preserved (old case -> method):
//   il-null                  -> null_elvisSafeCallBang         elvis ?:, safe-call ?., not-null !!, String.length
//   il-nullable-generic-list -> nullable_genericListErasure    #28 List<T?> object-erased interface at every member read
//   il-nullableprim          -> nullableprim_valueTypeUnwrap   C1 value-nullable smart-cast must UNWRAP Nullable<T>.Value
//   il-nullbang              -> nullbang_notNullAssertion       #56/#118 `!!` value-type (Int/Long/Double/Byte/UInt/UByte)
//                               nullbang_unsignedSafeCallElvisCast  #118/#126 unsigned SAFE_CALL/ELVIS/`as?`/if-else join
//                               nullbang_referenceEagerThrow    #115 reference `!!` throws NPE EAGERLY (stored/discarded)
//   il-nullcollarg           -> nullcollarg_nullableInner       #100-H3 nullable-inner collection type-arg collapses V
//   il-nullcs                -> nullcs_stringIntoCharSequence    #156 nullable String into a CharSequence?-receiver slot
//   il-printlnnull           -> printlnnull_nullRendersString   println/print of null renders the string "null"
//   il-reqnn                 -> reqnn_requireCheckNotNull        requireNotNull / checkNotNull (reference + value)
//   il-safecallnv            -> safecallnv_safeCallNullableValue A5 `a?.member` value-type result; receiver once, unwrap
//   il-trynullable           -> trynullable_returnThroughFinally nullable Int? return through try/finally; finally runs
//
// Top-level names are unique within this single battery assembly (one project = one namespace) and prefixed
// (`null`/`nv`/`tryNull`/`nullcs`) to avoid clashing with sibling batteries and stdlib names.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsNull as assertNull

// ---- il-null : elvis / safe-call / not-null ------------------------------------------------------------------
fun nullUp(s: String?): String = s?.uppercase() ?: "none"
fun nullPick(a: String?, b: String): String = a ?: b

// ---- il-nullable-generic-list : a nullable generic element is object-erased at the declaration boundary -------
fun <T> nullBoxes(x: T): List<T?> = listOf(x, null)
fun <T> nullPlainBoxes(x: T): List<T> = listOf(x)

// ---- il-nullableprim : value-type nullable smart-cast / arithmetic / return must UNWRAP Nullable<T>.Value -----
fun nullAddOne(x: Int): Int = x + 1
fun nullFirstOr(n: Int?, d: Int): Int { if (n != null) return n; return d }
fun nullFirstOrExpr(n: Int?, d: Int): Int {
    val x: Int = if (n == null) d else return n // return in expression position must also unwrap Nullable<T>.Value
    return x
}

// ---- il-nullcs : #156 nullable String into a CharSequence?-receiver slot --------------------------------------
fun nullcsPick(n: Int): String? = if (n > 0) "hi" else null

// ---- il-safecallnv : A5 receiver-evaluated-once + nullable-value-type receiver unwrap -------------------------
var nvM = 0
fun nvG(): Char? { nvM++; return 'x' }
fun nvGn(): Char? { nvM++; return if (nvM < 0) 'x' else null }
fun nvS(): String? { nvM++; return "hey" }
fun nvSn(): String? { nvM++; return null }

// ---- il-trynullable : a nullable Int? return through try/finally; finally still runs --------------------------
val tryNullLog = mutableListOf<String>()
fun tryNullF(): Int? {
    try {
        return 1
    } finally {
        tryNullLog.add("fin")
    }
}

// ---- il-reqnn : requireNotNull / checkNotNull (reference + value-nullable) ------------------------------------
fun reqnnFirstChar(s: String?): Char = requireNotNull(s)[0]
fun reqnnMust(n: Int?): Int = checkNotNull(n)

class NullableTests {
    @TestAttribute
    fun null_elvisSafeCallBang() {
        assertEquals("none", nullUp(null))               // none    s?.uppercase() ?: "none" when null
        assertEquals("HI", nullUp("hi"))                 // HI      safe-call chains through, uppercase
        assertEquals("fallback", nullPick(null, "fallback")) // fallback  a ?: b elvis fallback
        val s: String? = "abc"
        assertEquals("ABC", s!!.uppercase())             // ABC     `!!` yields the value, then member call
        assertEquals(5, "hello".length)                  // 5       String.length
    }

    @TestAttribute
    fun nullable_genericListErasure() {
        val strings = nullBoxes("a")
        assertEquals(2, strings.size)                    // 2       IReadOnlyCollection<object>.Count entry point
        assertEquals("a", strings[0])                    // a       get_Item on the erased interface
        val slog = mutableListOf<String>()
        for (value in strings) slog.add(value.toString()) // GetEnumerator over the erased interface
        assertEquals("a|null", slog.joinToString("|"))   // a / null
        val ints = nullBoxes(7)
        assertEquals(2, ints.size)                       // 2
        assertEquals(7, ints[0])                         // 7
        val ilog = mutableListOf<String>()
        for (value in ints) ilog.add(value.toString())
        assertEquals("7|null", ilog.joinToString("|"))   // 7 / null
        assertEquals(1, nullPlainBoxes("b").size)        // 1       plain generic element (no object erasure)
        assertEquals(2, listOf<String?>("c", null).size) // 2       concrete nullable element (no object erasure)
    }

    @TestAttribute
    fun nullableprim_valueTypeUnwrap() {
        val n: Int? = 7
        // n smart-cast to non-null Int inside the guard — every read/op must UNWRAP Nullable<T>.Value.
        if (n != null) {
            val z: Int = n
            assertEquals(7, z)                           // 7       assignment val z: Int = n
            assertEquals(107, z + 100)                   // 107     unwrapped arithmetic
            assertEquals(8, n + 1)                       // 8       arithmetic operand
            assertEquals(8, nullAddOne(n))               // 8       function arg
            assertEquals(14, n * 2)                      // 14
            assertEquals("gt5", if (n > 5) "gt5" else "le5") // gt5  comparison operand
        }
        assertEquals("big", if (n != null && n > 5) "big" else "small") // big  short-circuit && smart-cast
        assertEquals(7, nullFirstOr(7, -1))              // 7       return unwrapped value
        assertEquals(-1, nullFirstOr(null, -1))          // -1
        assertEquals(8, nullFirstOrExpr(8, -2))          // 8       return in expression position
        assertEquals(-2, nullFirstOrExpr(null, -2))      // -2
        val l: Long? = 100L
        if (l != null) {
            assertEquals(101L, l + 1L)                   // 101
            assertEquals(50L, l - 50L)                   // 50
            assertEquals("lgt", if (l > 99L) "lgt" else "lle") // lgt
        }
        val d: Double? = 2.5
        if (d != null) {
            val w: Double = d
            assertEquals(2.5, w)                         // 2.5
            assertEquals(2.75, w + 0.25)                 // 2.75
            assertEquals("dlt", if (d < 3.0) "dlt" else "dge") // dlt
        }
    }

    @TestAttribute
    fun nullbang_notNullAssertion() {
        val n: Int? = 5
        assertEquals(5, n!!)                             // 5       value-type `!!` unwraps
        assertEquals(6, n!! + 1)                         // 6
        assertEquals(5L, n!!.toLong())                   // 5
        val z: Int? = null
        val npe = try { z!!; "no" } catch (e: NullPointerException) { "npe" }
        assertEquals("npe", npe)                          // npe     `!!` on null value-nullable throws NPE
        val l: Long? = 7L
        assertEquals(10L, l!! + 3L)                      // 10
        val d: Double? = 3.5
        assertEquals(3.75, d!! + 0.25)                   // 3.75
        val b: Byte? = 9
        assertEquals(9, b!!.toInt())                     // 9
        val u: UInt? = 5u
        assertEquals(6u, u!! + 1u)                       // 6       unsigned `!!` unwraps Nullable<uint>.Value
        val ub: UByte? = 9u
        assertEquals(9, ub!!.toInt())                    // 9
        val uz: UInt? = null
        val npeU = try { uz!!; "no" } catch (e: NullPointerException) { "npe-u" }
        assertEquals("npe-u", npeU)                       // npe-u
    }

    @TestAttribute
    fun nullbang_unsignedSafeCallElvisCast() {
        val us: UInt? = 5u
        assertEquals(5, us?.toInt())                     // 5       unsigned SAFE_CALL present -> unwrapped
        val un: UInt? = null
        assertNull(un?.toInt())                          // null    SAFE_CALL yields null when receiver is null
        assertEquals(6u, (us ?: 0u) + 1u)                // 6       ELVIS present -> unwrapped value
        assertEquals(9, (un ?: 9u).toInt())              // 9       ELVIS fallback
        val anyU: Any = 5u
        assertEquals(5, (anyU as? UInt)?.toInt())        // 5       unsigned `as?` value present -> unwrapped
        val anyS: Any = "x"
        assertNull(anyS as? UInt)                        // null    unsigned `as?` mismatch -> null
        val cU = true
        val juU: UInt? = if (cU) 5u else null
        assertEquals(5, juU?.toInt())                    // 5       if/else unsigned join present
    }

    @TestAttribute
    fun nullbang_referenceEagerThrow() {
        val ok: String? = "hi"
        assertEquals("hi", ok!!)                         // hi      non-null reference `!!` yields the value
        assertEquals(2, ok!!.length)                     // 2       receiver-position `!!` still yields the value
        val s: String? = null
        val disc = try { s!!; "no" } catch (e: NullPointerException) { "npe-discard" }
        assertEquals("npe-discard", disc)                 // npe-discard  discarded `x!!` still throws EAGERLY
        val s2: String? = null
        val store = try { val y: String = s2!!; y } catch (e: NullPointerException) { "npe-store" }
        assertEquals("npe-store", store)                  // npe-store    stored `val y = x!!` still throws EAGERLY
    }

    @TestAttribute
    fun nullcollarg_nullableInner() {
        // #100-H3: a nullable-inner collection type-arg (Map<String, List<Int>?>) upcast from a MutableMap must
        // still collapse its V and print Kotlin-style — the `?` must not smuggle an un-collapsed IReadOnlyList past
        // the Root-V collapse.
        val mm = mutableMapOf<String, MutableList<Int>>("a" to mutableListOf(1))
        val ro: Map<String, List<Int>?> = mm
        assertEquals("{a=[1]}", ro.toString())           // {a=[1]}
    }

    @TestAttribute
    fun nullcs_stringIntoCharSequence() {
        val z: String? = null
        assertEquals("Z:empty", if (z.isNullOrEmpty()) "Z:empty" else "Z:$z")   // Z:empty  null short-circuits
        val v: String? = nullcsPick(1)
        assertEquals("V:hi", if (v.isNullOrEmpty()) "V:empty" else "V:$v")      // V:hi     adapter wrap, non-empty
        val e: String? = ""
        assertEquals("E:empty", if (e.isNullOrEmpty()) "E:empty" else "E:$e")   // E:empty  adapter, length 0
    }

    @TestAttribute
    fun printlnnull_nullRendersString() {
        assertEquals("null", (null as Any?).toString())  // null    println(null) renders "null"
        val a: Int? = null
        assertEquals("null5x", a.toString() + 5.toString() + "x") // null5x  print(a)+print(5)+println("x")
        val s: String? = null
        assertEquals("null", s.toString())               // null    println(s) on a null String? renders "null"
    }

    @TestAttribute
    fun reqnn_requireCheckNotNull() {
        assertEquals('h', reqnnFirstChar("hello"))       // h       requireNotNull(s)[0]
        assertEquals(7, reqnnMust(7))                    // 7       checkNotNull(n) value-nullable
    }

    @TestAttribute
    fun safecallnv_safeCallNullableValue() {
        nvM = 0
        assertEquals(120, nvG()?.code)                   // 120     nullable VALUE receiver (Char?), unwrapped
        assertNull(nvGn()?.code)                          // null    null path
        assertEquals(3, nvS()?.length)                   // 3       reference receiver, value-type result
        assertNull(nvSn()?.length)                        // null    null path
        assertEquals(4, nvM)                             // 4       every receiver ran exactly once
    }

    @TestAttribute
    fun trynullable_returnThroughFinally() {
        tryNullLog.clear()
        val r: Int? = tryNullF()
        assertEquals("fin", tryNullLog.joinToString("|")) // fin     finally ran (return inside try)
        assertEquals(1, r)                                // 1       nullable Int? return propagates
    }
}
