// Exception / try-catch battery — migrates the exception-handling / try-catch / throw / Result / user-exception
// family of cases/il-* onto the in-process NUnit suite. Each old case's `main` + stdout-golden diff becomes one
// @TestAttribute method whose per-value assertEquals/assertTrue/assertNull is strictly stronger than the old
// string diff. Every value the old il_check asserted is preserved 1:1 (see the `// <expected>` comments).
// A thrown-exception scenario becomes the proven try/catch-sentinel (the catch clause pins the EXACT exception
// type — a wrong/absent throw can't alias to the sentinel string; StringsTests/NullableTests use the same shape).
// Ordered side-effecting `println`s (the finally-order probe) are captured into a log list and asserted in order.
//
// EXCLUDED from this family (matched an exc/try grep prefix but the real subject is elsewhere — kept in the bash
// lane): none of the migrated set; sibling `il-suspendcatch`/`il-coexc` (coroutine try/catch) stay out because
// their subject is the suspend state machine, and `il-check-inject`/import interop cases are .NET-interop, not
// exception handling.
//
// Coverage preserved (old case -> method):
//   il-exc        -> exc_safeDiv                 try/catch as a statement; ArithmeticException on integer / by 0
//   il-customexc  -> customexc_userException     user exception : System.Exception; base-ctor chain, .message, .code
//   il-excmap     -> excmap_indexOutOfBounds     Kotlin IndexOutOfBoundsException catches List + array OOR; printStackTrace
//   il-nestedtry  -> nestedtry_finallyOrder      nested try/finally run order + return through both finallys
//   il-result     -> result_runCatching          runCatching -> Result<T>; getOrNull/getOrThrow/getOrDefault/exceptionOrNull
//   il-throwexpr  -> throwexpr_throwInExpression  `throw` in expression position (Nothing) + exact thrown type
//   il-tryexpr    -> tryexpr_tryAsValue           try/catch in value position: expr-body, val init, inside a lambda
//   il-tryexprop  -> tryexprop_tryInOperand       try-expression as a VALUE in an operand slot (empty-stack hoist)
//
// Top-level names are unique within this single battery assembly (one project = one namespace) and `exc`-prefixed
// to avoid clashing with sibling batteries and stdlib names.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsTrue as assertTrue
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsNull as assertNull

// ---- il-exc : try/catch as a statement, ArithmeticException on integer division ------------------------------
fun excSafeDiv(a: Int, b: Int): Int {
    try {
        return a / b
    } catch (e: ArithmeticException) {
        return -1
    }
}

// ---- il-customexc : user exception : System.Exception (base ctor chain, .message -> .Message, catch by base) --
class ExcAppErr(val code: Int) : Exception("error " + code)
class ExcRtErr(m: String) : RuntimeException(m)
fun excCustomRisky(n: Int): Int { if (n < 0) throw ExcAppErr(n); return n * 2 }

// ---- il-result : runCatching -> Result<T>; risky/greet feed the success + failure paths -----------------------
fun excResultRisky(n: Int): Int { if (n < 0) throw IllegalStateException("neg $n"); return n * 2 }
fun excResultGreet(ok: Boolean): String { if (!ok) throw RuntimeException("bad"); return "hi" }

// ---- il-throwexpr : `throw` in expression position (Nothing type) + exception-type mapping --------------------
fun excPick(x: Int): String {
    val s = if (x > 0) "pos" else throw IllegalStateException("neg")
    return s
}
fun excReq(x: Int?): Int = x ?: throw IllegalArgumentException("null")
fun excGuard(x: Int): Int {
    if (x < 0) throw RuntimeException("neg")
    return x
}

// ---- il-tryexpr : try/catch in value position (expression body) ----------------------------------------------
fun excParse(s: String): Int = try { s.toInt() } catch (e: Exception) { -1 }

// ---- il-tryexprop : try-expression as a value in an operand slot ---------------------------------------------
fun excRiskyOp(): Int = "5".toInt()

// ---- il-nestedtry : nested try/finally, captured run order (return threads through both finallys) -------------
fun excNestedF(log: MutableList<String>): Int {
    try {
        try {
            return 1
        } finally {
            log.add("inner fin")
        }
    } finally {
        log.add("outer fin")
    }
}

class ExceptionTests {
    @TestAttribute
    fun exc_safeDiv() {
        assertEquals(5, excSafeDiv(10, 2))     // safeDiv(10,2) = 5
        assertEquals(-1, excSafeDiv(1, 0))     // safeDiv(1,0) = -1  (ArithmeticException -> -1)
    }

    @TestAttribute
    fun customexc_userException() {
        var caught: ExcAppErr? = null
        try { excCustomRisky(-5) } catch (e: ExcAppErr) { caught = e }
        assertEquals("error -5", caught!!.message)   // error -5   (.message -> System.Exception.Message)
        assertEquals(-5, caught.code)                // code=-5
        val msg = try { throw ExcRtErr("boom") } catch (e: Exception) { "caught:" + e.message }
        assertEquals("caught:boom", msg)             // caught:boom  (RuntimeException caught by base Exception)
        assertEquals(42, excCustomRisky(21))         // 42
    }

    @TestAttribute
    fun excmap_indexOutOfBounds() {
        // Kotlin IndexOutOfBoundsException catches BOTH .NET out-of-range types (List -> ArgumentOutOfRangeException,
        // array -> IndexOutOfRangeException); printStackTrace resolves through the override chain (no NRE).
        val list = listOf(1, 2, 3)
        val a = try { list[10]; "no" } catch (e: IndexOutOfBoundsException) { "caught-list" }
        assertEquals("caught-list", a)               // caught-list
        val arr = intArrayOf(1, 2, 3)
        val b = try { arr[10]; "no" } catch (e: IndexOutOfBoundsException) { "caught-arr" }
        assertEquals("caught-arr", b)                // caught-arr
        val c = try { throw RuntimeException("boom") } catch (e: Exception) { (e as Exception).printStackTrace(); "pst-ok" }
        assertEquals("pst-ok", c)                    // pst-ok  (printStackTrace via override chain)
        val d = try { list[99]; "no" } catch (e: RuntimeException) { "caught-super" }
        assertEquals("caught-super", d)              // caught-super  (caught by RuntimeException supertype)
    }

    @TestAttribute
    fun nestedtry_finallyOrder() {
        val log = mutableListOf<String>()
        val r = excNestedF(log)
        assertEquals("inner fin|outer fin", log.joinToString("|"))  // inner fin / outer fin  (in order)
        assertEquals(1, r)                                          // 1  (return threads through both finallys)
    }

    @TestAttribute
    fun result_runCatching() {
        val r = runCatching { excResultRisky(5) }
        assertTrue(r.isSuccess)                                 // True
        assertEquals(10, r.getOrNull())                        // 10
        assertEquals(10, r.getOrThrow())                       // 10
        val r2 = runCatching { excResultRisky(-1) }
        assertTrue(r2.isFailure)                               // True
        assertNull(r2.getOrNull())                             // null  (value-type Result<Int> failure)
        assertEquals(-99, r2.getOrDefault(-99))                // -99
        assertEquals("neg -1", r2.exceptionOrNull()?.message)  // neg -1  (Throwable.message -> Exception.Message)
        val rs = runCatching { excResultGreet(false) }
        assertNull(rs.getOrNull())                             // null  (ref-type Result<String> failure)
        assertEquals("fb", rs.getOrDefault("fb"))              // fb
    }

    @TestAttribute
    fun throwexpr_throwInExpression() {
        assertEquals("pos", excPick(5))     // pos
        assertEquals(42, excReq(42))        // 42
        assertEquals(3, excGuard(3))        // 3
        // The throwing branches ARE the subject (throw in expression position): pin the exact thrown type.
        val a = try { excPick(-1) } catch (e: IllegalStateException) { "ise" }
        assertEquals("ise", a)
        val b = try { excReq(null) } catch (e: IllegalArgumentException) { -9 }
        assertEquals(-9, b)
        val c = try { excGuard(-1) } catch (e: RuntimeException) { -9 }
        assertEquals(-9, c)
    }

    @TestAttribute
    fun tryexpr_tryAsValue() {
        assertEquals(42, excParse("42"))    // 42
        assertEquals(-1, excParse("xx"))    // -1
        val x = try { 10 / 2 } catch (e: Exception) { 0 }
        assertEquals(5, x)                  // 5
        val y = try { 10 / 0 } catch (e: ArithmeticException) { -7 }
        assertEquals(-7, y)                 // -7
        val z = listOf("1", "bad", "3").map { try { it.toInt() } catch (e: Exception) { 0 } }
        assertEquals(4, z.sum())            // 4  (1 + 0 + 3)
    }

    @TestAttribute
    fun tryexprop_tryInOperand() {
        val n = "n=" + try { "5".toInt() } catch (e: NumberFormatException) { -1 }
        assertEquals("n=5", n)                                                    // n=5
        assertEquals(6, 1 + try { excRiskyOp() } catch (e: Exception) { 0 })      // 6
        assertEquals("bad=-1", "bad=" + try { "x".toInt() } catch (e: Exception) { -1 })  // bad=-1
        assertEquals(30, 10 + try { 20 } finally { })                            // 30  (try/finally as operand)
    }
}
