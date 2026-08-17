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
//                    + sideEffectingOperandBeforeHoistedTry (the LEFT operand's spill temp, typed from the operand)
//                    + tryInsideAMintedOperandBlock (the try is a `var` init inside a lowering-minted operand block)
//                    + tryInABranchOfATrySubjectedWhen (recognizing the outer block must not stop the walk inside it)
//                    + tryInConstructionValueLists (#319 array/list/set/map/spread value-list coverage + order)
//
// Added here rather than migrated: nothingReturningCallInValuePosition (#197) — a `fun f(): Nothing` CALL in a
// value position, the in-module twin of the cross-module round-trip case. It belongs to this battery because a
// `Nothing`-typed expression is a control transfer, the same subject as `throw` in expression position.
//
// Top-level names are unique within this single battery assembly (one project = one namespace) and `exc`-prefixed
// to avoid clashing with sibling batteries and stdlib names.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import NUnit.Framework.Legacy.ClassicAssert.IsNull as assertNull

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
// RuntimeException(cause) is an @ClrTypeAlias constructor adapter carried by the stdlib ref DLL. A consuming module
// must expand its declaration-authored `(cause?.toString(), cause)` vector before resolving System.Exception::.ctor.
class ExcCauseErr(cause: Throwable) : RuntimeException(cause)

fun excDirectCause(cause: Throwable): RuntimeException = RuntimeException(cause)
fun excRepeatedDirectCauses(cause: Throwable): List<RuntimeException> = listOf(
    RuntimeException(cause), RuntimeException(cause), RuntimeException(cause), RuntimeException(cause),
    RuntimeException(cause), RuntimeException(cause), RuntimeException(cause), RuntimeException(cause),
)
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

// A SIDE-EFFECTING operand to the LEFT of an operand-position try: the hoist moves the try's statements ahead of
// the whole expression, so the left operand has to be spilled to a temp first to keep it evaluating before them.
// That temp is the one place the hoist declares a type, and it used to copy whichever type slot the node happened
// to carry — a call node carries none, so the spill was declared `kotlin.Any`, the emitted unbox read a boxed
// value that was never boxed, and the process died with an AccessViolationException. It now derives the type the
// way every other spill site does.
val excHoistLog = mutableListOf<String>()
fun excHoistSide(): Int { excHoistLog.add("L"); return 4 }
fun excHoistSum(): Int = excHoistSide() + try { "x".toInt() } catch (e: Exception) { 6 }        // catch arm
fun excHoistConcat(): String = excHoistSide().toString() + try { "7".toInt() } catch (e: Exception) { 0 }  // try arm
fun excHoistMarked(mark: String, value: Int): Int { excHoistLog.add(mark); return value }
fun excHoistVararg(vararg values: Int): Int = values.sum()
class ExcHoistHashKey(val value: Int) {
    override fun hashCode(): Int { excHoistLog.add("hash$value"); return value }
    override fun equals(other: Any?): Boolean = other is ExcHoistHashKey && value == other.value
}
fun excHoistMarkedKey(mark: String, value: Int): ExcHoistHashKey {
    excHoistLog.add(mark)
    return ExcHoistHashKey(value)
}

// Two operands, so the second is evaluated with the first already on the CLR evaluation stack — the position that
// makes an inline protected region illegal.
fun excPair(a: Any, b: Any): String = "$a/$b"
fun excPairInt(a: Int, b: Int): Int = a + b

// ---- #197 : a `fun f(): Nothing` in a VALUE position — the erased `object` must never reach the slot ----------
// `Nothing` has no CLR analog, so a `fun f(): Nothing` returns `object`. A lowering that let the call sit in a
// value slot handed that `object` to whatever read it: the other arm of an if/when merge, the method's `ret`, a
// typed local. `object` is not a `string`, so the verifier rejected a merge the program never performs (ilverify
// StackUnexpected object/string) even though the arm always throws first, so the RUN was green. bir2cir now
// TERMINATES a Nothing-typed value position (`else boom()` -> `else throw boom()`), so nothing merges at all.
// The shapes below are the fault class, not one example: else-arm, then-arm, a `when` arm, an elvis right-hand
// side, a block whose LAST expression is the Nothing call, BOTH arms, a bare `ret`, and a VALUE-typed merge
// (whose branch coercion is the one that lands after the terminator).
class ExcBoom { companion object { fun boom(): Nothing = throw IllegalStateException("boom") } }
fun excFail(msg: String): Nothing = throw IllegalStateException(msg)
val excNothingLog = mutableListOf<String>()
fun excNothingElseArm(n: Int): String { val r: String = if (n >= 0) "kept" else ExcBoom.boom(); return r }
fun excNothingThenArm(n: Int): String = if (n < 0) excFail("then") else "kept2"
fun excNothingWhenArm(n: Int): String = when { n > 5 -> "big"; n > 0 -> excFail("mid"); else -> "small" }
fun excNothingElvis(s: String?): String = s ?: excFail("elvis")
fun excNothingBlockTail(n: Int): String = if (n >= 0) "ok" else { excNothingLog.add("side"); excFail("tail") }
fun excNothingBothArms(n: Int): String = if (n >= 0) excFail("a") else excFail("b")
fun excNothingWholeBody(n: Int): String = excFail("body$n")
fun excNothingValueSlot(n: Int): Int = if (n >= 0) 7 else excFail("int")
fun excNothingSubjectWhen(n: Int): String =
    when (n) { 0 -> "zero"; 1 -> excFail("one"); 2 -> ExcBoom.boom(); else -> "many" }

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
    fun safeDiv() {
        assertEquals(5, excSafeDiv(10, 2))     // safeDiv(10,2) = 5
        assertEquals(-1, excSafeDiv(1, 0))     // safeDiv(1,0) = -1  (ArithmeticException -> -1)
    }

    @TestAttribute
    fun userException() {
        var caught: ExcAppErr? = null
        try { excCustomRisky(-5) } catch (e: ExcAppErr) { caught = e }
        assertEquals("error -5", caught!!.message)   // error -5   (.message -> System.Exception.Message)
        assertEquals(-5, caught.code)                // code=-5
        val msg = try { throw ExcRtErr("boom") } catch (e: Exception) { "caught:" + e.message }
        assertEquals("caught:boom", msg)             // caught:boom  (RuntimeException caught by base Exception)
        val cause = IllegalStateException("inner")
        val wrapped = ExcCauseErr(cause)
        assertEquals("inner", wrapped.cause?.message) // the cause-only adapter selected (String, Exception), not String
        assertTrue(wrapped.message?.contains("inner") == true)
        val direct = excDirectCause(cause)
        assertEquals("inner", direct.cause?.message)  // the same carried adapter applies to an ordinary `new` expression
        assertTrue(direct.message?.contains("inner") == true)
        val repeated = excRepeatedDirectCauses(cause)
        assertEquals(8, repeated.size)
        assertTrue(repeated.all { it.cause?.message == "inner" }) // caller/adapter temp namespaces cannot collide
        assertEquals(42, excCustomRisky(21))         // 42
    }

    @TestAttribute
    fun indexOutOfBounds() {
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
    fun finallyOrder() {
        val log = mutableListOf<String>()
        val r = excNestedF(log)
        assertEquals("inner fin|outer fin", log.joinToString("|"))  // inner fin / outer fin  (in order)
        assertEquals(1, r)                                          // 1  (return threads through both finallys)
    }

    @TestAttribute
    fun runCatching() {
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
    fun throwInExpression() {
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

    // #197: every one of these compiled and ran before — the fault was formal (StackUnexpected object/string at the
    // merge). The value asserts pin the SURVIVING arm; the sentinel catches pin that the Nothing arm still throws
    // its own exception rather than the terminator the lowering adds behind it.
    @TestAttribute
    fun nothingReturningCallInValuePosition() {
        excNothingLog.clear()
        assertEquals("kept", excNothingElseArm(1))      // kept    companion-static Nothing in the else arm
        assertEquals("kept2", excNothingThenArm(1))     // kept2   Nothing in the then arm
        assertEquals("big", excNothingWhenArm(9))       // big     a subject-LESS `when` with a Nothing arm
        assertEquals("small", excNothingWhenArm(-1))    // small
        assertEquals("zero", excNothingSubjectWhen(0))  // zero    a `when` WITH a subject (a different node path)
        assertEquals("many", excNothingSubjectWhen(9))  // many
        assertEquals("e", excNothingElvis("e"))         // e       elvis whose right-hand side is Nothing
        assertEquals("ok", excNothingBlockTail(1))      // ok      block arm whose LAST expression is the Nothing call
        assertEquals(7, excNothingValueSlot(1))         // 7       a VALUE-typed merge (Int), not a reference one
        assertEquals(0, excNothingLog.size)             // the untaken block arm did not run
        // The Nothing arms: each still throws ITS OWN exception, from the call, at the call.
        assertEquals("boom", try { excNothingElseArm(-1) } catch (e: IllegalStateException) { e.message })
        assertEquals("then", try { excNothingThenArm(-1) } catch (e: IllegalStateException) { e.message })
        assertEquals("mid", try { excNothingWhenArm(3) } catch (e: IllegalStateException) { e.message })
        assertEquals("elvis", try { excNothingElvis(null) } catch (e: IllegalStateException) { e.message })
        assertEquals("tail", try { excNothingBlockTail(-1) } catch (e: IllegalStateException) { e.message })
        assertEquals(1, excNothingLog.size)             // ...and the taken block arm ran its statements first
        assertEquals("a", try { excNothingBothArms(1) } catch (e: IllegalStateException) { e.message })
        assertEquals("b", try { excNothingBothArms(-1) } catch (e: IllegalStateException) { e.message })
        assertEquals("body7", try { excNothingWholeBody(7) } catch (e: IllegalStateException) { e.message })
        assertEquals("int", try { excNothingValueSlot(-1).toString() } catch (e: IllegalStateException) { e.message })
        assertEquals("one", try { excNothingSubjectWhen(1) } catch (e: IllegalStateException) { e.message })
        assertEquals("boom", try { excNothingSubjectWhen(2) } catch (e: IllegalStateException) { e.message })
    }

    @TestAttribute
    fun tryAsValue() {
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
    fun tryInOperand() {
        val n = "n=" + try { "5".toInt() } catch (e: NumberFormatException) { -1 }
        assertEquals("n=5", n)                                                    // n=5
        assertEquals(6, 1 + try { excRiskyOp() } catch (e: Exception) { 0 })      // 6
        assertEquals("bad=-1", "bad=" + try { "x".toInt() } catch (e: Exception) { -1 })  // bad=-1
        assertEquals(30, 10 + try { 20 } finally { })                            // 30  (try/finally as operand)
    }

    // The left operand is a CALL, so it is spilled to a temp ahead of the hoisted try (a const/local is left in
    // place). The spill's declared type has to be the operand's own — a `kotlin.Any` slot faulted at runtime.
    @TestAttribute
    fun sideEffectingOperandBeforeHoistedTry() {
        excHoistLog.clear()
        assertEquals(10, excHoistSum())          // 4 + 6 (the try throws, so the catch arm supplies the value)
        assertEquals("47", excHoistConcat())     // "4" + 7 (the try arm supplies it)
        assertEquals(2, excHoistLog.size)        // the left operand ran once per call, in its lexical position
    }

    // The hazard is the BLOCK, not kotc's spelling of it. Several lowerings materialise an operand into a MINTED
    // `valueBlock` whose `var` initializer is then the try-valued expression — a range-membership test's bounds
    // here — so the `try` is no longer a direct statement of the block. It still enters a protected region with the
    // enclosing operands on the stack, and the hoist used to look only at the block's top level: an
    // InvalidProgramException, from source the frontend accepted.
    @TestAttribute
    fun tryInsideAMintedOperandBlock() {
        val membership = excPair("z", (try { 1 } catch (e: Exception) { 2 }) in 1..5)
        assertEquals("z/true", membership.lowercase())
        val nested = excPair("z", (try { 9 } catch (e: Exception) { 0 }) in (try { 1 } catch (e: Exception) { 2 })..10)
        assertEquals("z/true", nested.lowercase())
    }

    // Recognizing a block as hazardous must not stop the walk INSIDE it. A `when` with a try-valued subject is a
    // block whose statements bind that subject, so it answers "yes, this runs a try" — and a try sitting in a
    // later operand slot of one of its branches still has to be hoisted. Classifying the outer block and then
    // returning early left that inner one inline, at a non-empty stack.
    @TestAttribute
    fun tryInABranchOfATrySubjectedWhen() {
        val v = when (try { 1 } catch (e: Exception) { 0 }) {
            1 -> excPairInt(0, try { 2 } catch (e: Exception) { 3 })
            else -> 0
        }
        assertEquals(2, v)
    }

    // #319: ilemit has an array/list/set/map/spread accumulator (and for arrays an index) on the CLR stack before evaluating
    // every construction element, including element 0. Each value-producing try therefore has to move ahead of the
    // construction. Once one moves, every non-neutral value is materialized before the construction consumes any of
    // them; otherwise HashSet/Dictionary hashing can overtake a later source argument. Map entries are one ordered
    // key/value stream rather than opaque entry-schema objects.
    @TestAttribute
    fun tryInConstructionValueLists() {
        excHoistLog.clear()
        val ints = intArrayOf(
            excHoistMarked("array-before", 1),
            try { excHoistMarked("array-try", 2) } catch (e: Exception) { -1 },
            excHoistMarked("array-after", 3),
        )
        val objects = arrayOf(try { "object" } catch (e: Exception) { "bad" })
        val list = listOf(try { 4 } catch (e: Exception) { -1 }, 5)
        val set = setOf(try { 6 } catch (e: Exception) { -1 }, 7)
        val map = mapOf(
            (try { "a" } catch (e: Exception) { "bad" }) to
                (try { excHoistMarked("map-value", 8) } catch (e: Exception) { -1 }),
            "b" to try { excHoistMarked("map-later", 9) } catch (e: Exception) { -1 },
        )
        val nested = arrayOf(
            listOf(excHoistMarked("nested-before", 10)),
            try { listOf(excHoistMarked("nested-try", 11)) }
            catch (e: Exception) { listOf(-1) },
        )
        // The factory rewrite carries the source call's exact result face: `newList` alone cannot distinguish this
        // MutableList from the read-only List above when the first construction has to be spilled.
        val nestedMutable = arrayOf(
            mutableListOf(16),
            try { mutableListOf(17) } catch (e: Exception) { mutableListOf(-1) },
        )
        val spreadSource = intArrayOf(12, 13)
        val spread = excHoistVararg(
            excHoistMarked("spread-before", 14),
            *spreadSource,
            try { excHoistMarked("spread-try", 15) } catch (e: Exception) { -1 },
        )

        assertEquals(6, ints.sum())
        assertEquals("object", objects[0])
        assertEquals(9, list.sum())
        assertEquals(13, set.sum())
        assertEquals(8, map["a"])
        assertEquals(9, map["b"])
        assertEquals(10, nested[0][0])
        assertEquals(11, nested[1][0])
        nestedMutable[0].add(18)
        assertEquals(2, nestedMutable[0].size)
        assertEquals(17, nestedMutable[1][0])
        assertEquals(54, spread)
        assertEquals("array-before", excHoistLog[0])
        assertEquals("array-try", excHoistLog[1])
        assertEquals("array-after", excHoistLog[2])
        assertEquals("map-value", excHoistLog[3])
        assertEquals("map-later", excHoistLog[4])
        assertEquals("nested-before", excHoistLog[5])
        assertEquals("nested-try", excHoistLog[6])
        assertEquals("spread-before", excHoistLog[7])
        assertEquals("spread-try", excHoistLog[8])

        // Factory arguments are all evaluated before the substituted HashSet/Dictionary consumes them. The first
        // hash therefore cannot run before the later key-producing expression just because the first key held a try.
        excHoistLog.clear()
        val orderedSet = setOf(
            try { excHoistMarkedKey("set-try", 20) }
            catch (e: Exception) { ExcHoistHashKey(-1) },
            excHoistMarkedKey("set-later", 21),
        )
        assertEquals(2, orderedSet.size)
        assertEquals("set-try", excHoistLog[0])
        assertEquals("set-later", excHoistLog[1])
        assertEquals("hash20", excHoistLog[2])
        assertEquals("hash21", excHoistLog[3])

        excHoistLog.clear()
        val orderedMap = mapOf(
            (try { excHoistMarkedKey("map-key-try", 22) }
            catch (e: Exception) { ExcHoistHashKey(-1) }) to 1,
            excHoistMarkedKey("map-key-later", 23) to 2,
        )
        assertEquals(2, orderedMap.size)
        assertEquals("map-key-try", excHoistLog[0])
        assertEquals("map-key-later", excHoistLog[1])
        assertEquals("hash22", excHoistLog[2])
        assertEquals("hash23", excHoistLog[3])
    }
}
