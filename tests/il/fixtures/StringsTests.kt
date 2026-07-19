// Strings/text battery — migrates the String/Char/CharSequence/stringify/number-parse family of cases/il-*
// onto the in-process NUnit suite. Each old case's `main` + stdout-golden diff becomes one @TestAttribute method
// whose per-value assertEquals/assertTrue/assertFalse/assertNull is strictly stronger (typed, fails the exact
// broken contract) and self-documenting. Every value the old il_check asserted is preserved 1:1 (see the
// `// <expected>` comments). Side-effecting/ordered `println`s (split loops) are captured into a log list and
// asserted in order.
//
// EXCLUDED from this family (matched the `str` grep prefix but the real subject is elsewhere — kept in the
// bash lane):
//   il-structfloateq      -> STRUCTURAL Double/Float equality (data-class equals/hashCode) — float-equality family
//   il-structfloateqnull  -> STRUCTURAL Double?/Float? equality (nullable field) — float-equality family
//
// Coverage preserved (old case -> method):
//   il-blank         -> blank_isBlankIsNotBlank      isBlank/isNotBlank pure-Kotlin index-loop body
//   il-charminus     -> charminus_charArithmetic     Char-Char -> Int; Char+/-Int -> Char
//   il-charseq       -> charseq_userCharSequence     user class : CharSequence (length/get/subSequence)
//   il-charseqbcl    -> charseqbcl_computedReceiver   #148 computed/BCL-origin String receiver into stdlib ext
//   il-charseqlenref -> charseqlenref_propertyRef    #57 property-ref to length on user CharSequence (direct+inherited)
//   il-charseqmore   -> charseqmore_polymorphic      #149-2/3/4 String branch / StringBuilder / x!!.isNullOrEmpty
//   il-charseqs      -> charseqs_stringLowering      CharSequence lowers to System.String (no user impl)
//   il-charseqx      -> charseqx_stdlibExt           cross-assembly stdlib CharSequence-ext (hasSurrogatePairAt)
//   il-charseqxfile  -> charseqxfile_crossFile       #149-1 cross-file String receiver (decls in StringsCrossFile.kt)
//   il-colstr        -> colstr_collectionStringify   collection/Map prints Kotlin-style in every stringify context
//   il-digittoint    -> digittoint_digitToInt        Char.digitToInt/digitToIntOrNull (Int? return)
//   il-interpnull    -> interpnull_nullInterpolation null interpolated/concatenated operand renders "null"
//   il-nestedstr     -> nestedstr_nestedStringify    nested collection/map stringification recurses
//   il-ntostr        -> ntostr_boxedToString         value-type-nullable/value arg boxed into a referenced object param
//   il-nulltostr     -> nulltostr_nullSafeToString   x.toString() on nullable receiver -> "null" when null
//   il-radix         -> radix_toStringRadix          Int/Long.toString(radix) sign + arbitrary base
//   il-str           -> str_stringOps                uppercase/lowercase/trim/substring/startsWith/contains
//   il-strhash       -> strhash_hashCodeContract     String/Double/Float hashCode CLR-native (behavior, not pinned)
//   il-strnum        -> strnum_numberParsing         toInt/toLong/toByte/toDouble/toFloat + NumberFormatException
//   il-strops        -> strops_stringOps             trim(vararg)/padStart/padEnd/replace pure-Kotlin bodies
//   il-subseq        -> subseq_subSequence           CharSequence.subSequence -> Substring; start evaluated once
//   il-substr        -> substr_substring             String.substring exclusive-end 2-arg conversion
//
// Top-level names are unique within this single battery assembly (one project = one namespace) and `str`-prefixed
// to avoid clashing with sibling batteries and with stdlib names (e.g. a plain `lines`/`word`/`pick`).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsTrue as assertTrue
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsFalse as assertFalse
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsNull as assertNull
import kotlin.reflect.KProperty0
import kotlin.reflect.KProperty1

// ---- il-charseq : user class : CharSequence ------------------------------------------------------------------
class StrSeqS(val s: String) : CharSequence {
    override val length: Int get() = s.length
    override fun get(index: Int): Char = s[index]
    override fun subSequence(startIndex: Int, endIndex: Int): CharSequence = StrSeqS(s.substring(startIndex, endIndex))
}
fun strSeqShow(cs: CharSequence): Int = cs.length     // CharSequence-typed param (polymorphic)

// ---- il-charseqx : user class : CharSequence into a stdlib CharSequence-ext -----------------------------------
class StrExtS(val s: String) : CharSequence {
    override val length: Int get() = s.length
    override fun get(index: Int): Char = s[index]
    override fun subSequence(startIndex: Int, endIndex: Int): CharSequence = StrExtS(s.substring(startIndex, endIndex))
}

// ---- il-charseqlenref : property-ref to length on a user CharSequence (direct + inherited) --------------------
open class StrLenRefA(val s: String) : CharSequence {
    override val length: Int get() = s.length
    override fun get(index: Int): Char = s[index]
    override fun subSequence(startIndex: Int, endIndex: Int): CharSequence = StrLenRefA(s.substring(startIndex, endIndex))
}
class StrLenRefB(s: String) : StrLenRefA(s)   // inherits length INDIRECTLY through StrLenRefA

// ---- il-charseqbcl : computed/BCL-origin String receiver into stdlib CharSequence exts ------------------------
class StrBclCfg(val body: String)
fun strBclLines(): String = "a\nb\nc"
fun strBclWord(): String = "hello"

// ---- il-charseqmore : a String branch of a polymorphic CharSequence if/else ----------------------------------
fun strMorePick(c: Boolean, other: CharSequence): List<String> = (if (c) "a-b-c" else other).split("-")

// ---- il-charseqs : CharSequence lowered to System.String (no user impl) --------------------------------------
fun strSeqLen(cs: CharSequence): Int = cs.length
fun strSeqAt(cs: CharSequence, i: Int): Char = cs[i]
fun strSeqTail(cs: CharSequence, from: Int): CharSequence = cs.subSequence(from, cs.length)
fun strSeqHas(cs: CharSequence, sub: String): Boolean = cs.contains(sub)

// ---- il-ntostr : a value-type-nullable/value arg boxed into a referenced object param ------------------------
fun strNToStrTakeAny(x: Any?): String = x.toString()

// ---- il-subseq : side-effecting start() evaluated exactly ONCE ------------------------------------------------
var strSubSeqCalls = 0
fun strSubSeqStart(): Int { strSubSeqCalls++; return 1 }

class StringsTests {
    @TestAttribute
    fun blank_isBlankIsNotBlank() {
        assertTrue("".isBlank())            // True
        assertTrue("   ".isBlank())         // True
        assertFalse("a b".isBlank())        // False
        assertTrue("  x ".isNotBlank())     // True
        assertTrue("\t\n".isBlank())        // True
    }

    @TestAttribute
    fun charminus_charArithmetic() {
        assertEquals(31, 'a' - 'B')         // Char - Char -> Int: 97 - 66 = 31
        assertEquals(25, 'z' - 'a')         // Char - Char -> Int: 25
        assertEquals('b', 'a' + 1)          // Char + Int  -> Char: 'b'
        assertEquals('b', 'c' - 1)          // Char - Int  -> Char: 'b'
    }

    @TestAttribute
    fun charseq_userCharSequence() {
        val c = StrSeqS("hello")
        assertEquals(5, c.length)                     // 5
        assertEquals('e', c[1])                       // e  (operator get)
        val sub: CharSequence = c.subSequence(1, 4)
        assertEquals(3, sub.length)                   // 3
        assertEquals('e', sub[0])                     // e
        assertEquals(5, strSeqShow(c))                // 5  (passed as CharSequence)
    }

    @TestAttribute
    fun charseqbcl_computedReceiver() {
        val log = mutableListOf<String>()
        for (p in strBclLines().split("\n")) log.add("f:$p")   // app-fun-result receiver
        val c = StrBclCfg("k1\nk2")
        for (p in c.body.split("\n")) log.add("p:$p")          // property-getter receiver
        val m = mapOf("x" to "1\n2")
        for (p in m["x"]!!.split("\n")) log.add("m:$p")        // `!!` + map-indexer receiver
        val sb = StringBuilder(); sb.append("u\nv")
        log.add(sb.toString().replace("\n", "-"))              // BCL-origin (StringBuilder.ToString) receiver
        log.add(strBclWord().substring(1, 4))                  // app-fun-result receiver into substring
        log.add(c.body.replace("\n", "+"))                     // property-getter receiver into replace
        // f:a / f:b / f:c / p:k1 / p:k2 / m:1 / m:2 / u-v / ell / k1+k2
        assertEquals("f:a|f:b|f:c|p:k1|p:k2|m:1|m:2|u-v|ell|k1+k2", log.joinToString("|"))
    }

    @TestAttribute
    fun charseqlenref_propertyRef() {
        val a = StrLenRefA("hello")
        val b = StrLenRefB("worldd")
        val da: KProperty0<Int> = a::length              // DIRECT, bound
        val db: KProperty0<Int> = b::length              // INDIRECT, bound
        val ua: KProperty1<StrLenRefA, Int> = StrLenRefA::length   // DIRECT, unbound
        val ub: KProperty1<StrLenRefB, Int> = StrLenRefB::length   // INDIRECT, unbound
        assertEquals(5, da.get())        // 5
        assertEquals(6, db.get())        // 6
        assertEquals(5, ua.get(a))       // 5
        assertEquals(6, ub.get(b))       // 6
        assertEquals("length", da.name)  // length
    }

    @TestAttribute
    fun charseqmore_polymorphic() {
        val log = mutableListOf<String>()
        // (#149-3) a String BRANCH inside a polymorphic CharSequence-typed if/else
        for (s in strMorePick(true, "z")) log.add("C:$s")
        // (#149-4) x!!.isNullOrEmpty() — nullable CharSequence? slot + a `!!` non-null value
        val nn: String? = "hi"
        log.add(if (nn!!.isNullOrEmpty()) "E:empty" else "E:nonempty")
        // (#149-2) StringBuilder (a non-String CharSequence) -> CharSequence.split
        val sb = StringBuilder("p\nq")
        for (s in sb.split("\n")) log.add("B:$s")
        // C:a / C:b / C:c / E:nonempty / B:p / B:q
        assertEquals("C:a|C:b|C:c|E:nonempty|B:p|B:q", log.joinToString("|"))
    }

    @TestAttribute
    fun charseqs_stringLowering() {
        assertEquals(5, strSeqLen("hello"))            // 5    String -> string param
        assertEquals('e', strSeqAt("hello", 1))        // e    get -> System.String.get_Chars
        assertEquals("llo", strSeqTail("hello", 2).toString())  // llo  subSequence -> Substring
        val sb = StringBuilder()
        sb.append("world")
        assertEquals(5, strSeqLen(sb))                 // 5    StringBuilder -> implicit .toString() snapshot
        val cs: CharSequence = "abc"
        assertEquals(3, strSeqLen(cs))                 // 3
        assertEquals(3, cs.length)                     // 3    member read on a now-string local
        assertTrue(strSeqHas("hello", "ell"))          // True String -> string param -> stdlib ext
        assertTrue(strSeqHas(sb, "orl"))               // True StringBuilder -> toString snapshot -> stdlib ext
    }

    @TestAttribute
    fun charseqx_stdlibExt() {
        assertFalse(StrExtS("hello").hasSurrogatePairAt(0))   // False — user CharSequence -> stdlib ext
        assertFalse("hi".hasSurrogatePairAt(0))               // False — String -> adapter -> stdlib ext
    }

    @TestAttribute
    fun charseqxfile_crossFile() {
        // Cfg / banner are declared in the SIBLING file StringsCrossFile.kt (same assembly) — the #149-1
        // cross-file String receiver into a stdlib CharSequence.split.
        val log = mutableListOf<String>()
        val c = StrXFileCfg()
        for (line in c.body.split("\n")) log.add("L:$line")   // cross-file user-class property receiver
        for (p in strXFileBanner().split("-")) log.add("P:$p") // cross-file top-level fun result receiver
        // L:a / L:b / L:c / P:x / P:y / P:z
        assertEquals("L:a|L:b|L:c|P:x|P:y|P:z", log.joinToString("|"))
    }

    @TestAttribute
    fun colstr_collectionStringify() {
        val m = mapOf("a" to 1, "b" to 2)
        val l = listOf(1, 2, 3)
        assertEquals("m={a=1, b=2}", "m=$m")           // string template, Map
        assertEquals("l=[1, 2, 3]", "l=$l")            // string template, List
        assertEquals("x={a=1, b=2}", "x=" + m)         // string `+` concat, Map
        assertEquals("[1, 2, 3]", "" + l)              // string `+` concat, List
        assertEquals("[1, 2, 3]", l.toString())        // explicit toString(), List
        assertEquals("{a=1, b=2}", m.toString())       // explicit toString(), Map
    }

    @TestAttribute
    fun digittoint_digitToInt() {
        assertEquals(7, '7'.digitToIntOrNull())        // 7
        assertEquals(10, 'a'.digitToIntOrNull(16))     // 10
        assertNull('z'.digitToIntOrNull())             // null
        assertEquals(7, '7'.digitToInt())              // 7
    }

    @TestAttribute
    fun interpnull_nullInterpolation() {
        val x: Any? = null
        assertEquals("[null]", "[$x]")                 // string template, null Any?
        val n: Int? = null
        assertEquals("n=null", "n=$n")                 // string template, null Int?
        assertEquals("null", "" + x)                   // string `+` concat, null Any?
        val s: String? = null
        assertEquals("s=null end", "s=$s end")         // string template, null String?
        val a = 5
        assertEquals("a=5", "a=$a")                    // non-null value, unchanged
        val nn: Int? = 7
        assertEquals("nn=7", "nn=$nn")                 // non-null nullable value
        val m = mapOf("k" to 1)
        assertEquals("m={k=1}", "m=$m")                // Map operand keeps Kotlin-style
    }

    @TestAttribute
    fun nestedstr_nestedStringify() {
        assertEquals("{k=[1, 2]}", mapOf("k" to listOf(1, 2)).toString())               // {k=[1, 2]}
        assertEquals("[[1, 2]]", listOf(listOf(1, 2)).toString())                       // [[1, 2]]
        assertEquals("[[1, 2], [3, 4]]", listOf(listOf(1, 2), listOf(3, 4)).toString()) // [[1, 2], [3, 4]]
        assertEquals("{a={x=1}}", mapOf("a" to mapOf("x" to 1)).toString())             // {a={x=1}}
        assertEquals("[s, t]", listOf("s", "t").toString())                             // [s, t]
        assertEquals("{k=5}", mapOf("k" to 5).toString())                               // {k=5}
    }

    @TestAttribute
    fun ntostr_boxedToString() {
        val n: Int? = 5
        assertEquals("5", n.toString())                // 5
        val m: Int? = null
        assertEquals("null", m.toString())             // null
        val i = 7                                       // a plain value passed to an Any? param -> must box
        assertEquals("7", strNToStrTakeAny(i))         // 7
        assertEquals("5", strNToStrTakeAny(n))         // 5
        assertEquals("null", strNToStrTakeAny(m))      // null
    }

    @TestAttribute
    fun nulltostr_nullSafeToString() {
        val s: String? = null
        assertEquals("null", s.toString())             // null
        val t: String? = "abc"
        assertEquals("abc", t.toString())              // abc
        val sb: StringBuilder? = null
        assertEquals("null", sb.toString())            // null
        assertEquals("v=null", "v=" + s.toString())    // v=null
    }

    @TestAttribute
    fun radix_toStringRadix() {
        assertEquals("-ff", (-255).toString(16))               // -ff
        assertEquals("ff", 255.toString(16))                   // ff
        assertEquals("-80000000", Int.MIN_VALUE.toString(16))  // -80000000
        assertEquals("z", 35.toString(36))                     // z
        assertEquals("ff", 255L.toString(16))                  // ff
        assertEquals("-ff", (-255L).toString(16))              // -ff
        assertEquals("1010", 10.toString(2))                   // 1010
    }

    @TestAttribute
    fun str_stringOps() {
        assertEquals("HELLO", "Hello".uppercase())     // HELLO
        assertEquals("hello", "Hello".lowercase())     // hello
        assertEquals("hi", "  hi  ".trim())            // hi
        assertEquals("ello", "hello".substring(1))     // ello
        assertTrue("hello".startsWith("he"))           // True
        assertTrue("hello".contains("ell"))            // True
    }

    @TestAttribute
    fun strhash_hashCodeContract() {
        // Behavior asserts (equal values hash equal, set membership) — never a pinned hash integer.
        assertTrue("Aa".hashCode() == "Aa".hashCode())                       // True
        assertTrue("hello".hashCode() == ("hel" + "lo").hashCode())          // True
        assertTrue(hashSetOf("a", "b", "c").contains("b"))                   // True
        assertTrue(Double.NaN.hashCode() == Double.NaN.hashCode())          // True
        assertTrue(hashSetOf(1.5, 2.5).contains(1.5))                       // True
        assertTrue((-0.0f).hashCode() == (-0.0f).hashCode())               // True
        // Primitive Int/Long stay on the BCL slot.
        assertEquals("5", 5.toString())                // 5
        assertTrue(5.equals(5))                        // True
        assertEquals(5, 5.hashCode())                  // 5
        assertEquals(-7, (-7).hashCode())              // -7
        assertEquals("2a", 42.toString(16))            // 2a
    }

    @TestAttribute
    fun strnum_numberParsing() {
        assertEquals(42, "42".toInt())                 // 42
        assertEquals(-7L, "-7".toLong())               // -7
        assertEquals(100.toByte(), "100".toByte())     // 100
        val nfe = try { "abc".toInt(); "no" } catch (e: NumberFormatException) { "nfe" }
        assertEquals("nfe", nfe)                        // nfe
        val iae = try { "x".toInt(); "no" } catch (e: IllegalArgumentException) { "iae" }
        assertEquals("iae", iae)                        // iae  (NumberFormatException is-a IllegalArgumentException)
        assertEquals(3.14, "3.14".toDouble())          // 3.14
        assertEquals(2.5f, "2.5".toFloat())            // 2.5
        val comma = try { "3,14".toDouble(); "no" } catch (e: NumberFormatException) { "comma" }
        assertEquals("comma", comma)                    // comma (culture-invariant: comma is not a group sep)
        val nfd = try { "zzz".toDouble(); "no" } catch (e: NumberFormatException) { "nfd" }
        assertEquals("nfd", nfd)                        // nfd
    }

    @TestAttribute
    fun strops_stringOps() {
        assertEquals("hello", "xxhelloxx".trim('x'))   // hello
        assertEquals("hi", "**hi".trimStart('*'))      // hi
        assertEquals("hi", "hi!!".trimEnd('!'))        // hi
        assertEquals("  5", "5".padStart(3))           //   5   (default pad space)
        assertEquals("005", "5".padStart(3, '0'))      // 005
        assertEquals("500", "5".padEnd(3, '0'))        // 500
        assertEquals(">5  <", ">" + "5".padEnd(3) + "<") // >5  <  (default pad space)
        assertEquals("heLLo", "hello".replace("l", "L")) // heLLo
        assertEquals("bbbbbb", "aaa".replace("a", "bb")) // bbbbbb
        assertEquals("aXaX", "abcabc".replace("bc", "X")) // aXaX
        assertEquals("heLLo", "hello".replace('l', 'L')) // heLLo
    }

    @TestAttribute
    fun subseq_subSequence() {
        val cs: CharSequence = "hello"
        strSubSeqCalls = 0
        assertEquals("ell", cs.subSequence(strSubSeqStart(), 4).toString())  // ell
        assertEquals(1, strSubSeqCalls)                // 1  (start() ran exactly once)
        assertEquals("hel", cs.subSequence(0, 3).toString())                 // hel
        assertEquals("llo", cs.subSequence(2, 5).toString())                 // llo
    }

    @TestAttribute
    fun substr_substring() {
        val s = "hello world"
        assertEquals("ell", s.substring(1, 4))         // ell  (end exclusive)
        assertEquals("world", s.substring(6))          // world
        assertEquals("hello", s.substring(0, 5))       // hello
        assertEquals("world", s.substring(6, 11))      // world
    }
}
