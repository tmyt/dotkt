// Regex battery — migrates the kotlin.text.Regex -> System.Text.RegularExpressions.Regex family of cases/il-*
// onto the in-process NUnit suite. Each old case's `main` + stdout-golden diff becomes one @TestAttribute method
// whose per-value assertEquals/assertTrue/assertFalse/assertNull is strictly stronger (typed, fails the exact
// broken contract) than the old text diff. Every value the old il_check asserted is preserved 1:1 (see the
// `// <expected>` comments). Ordered/iterated `println`s (the .groups walk) are captured into a builder and
// asserted in order — the STRUCTURE (collection surface, iteration order) is unchanged.
//
// This family is the Kotlin Regex surface binding to the BCL: toRegex/containsMatchIn/replace/matches/find
// (il-regex), the TRUE anchored matchEntire/matches (il-regexanchor, #162), the options-taking ctors
// (il-regexopts, #178), replaceFirst/replace/pattern marshaling (il-regexreplace), MatchResult.groups
// (il-regexgroups, ClrMatchGroupCollection), the Sequence-returning findAll/splitToSequence + options getter
// (il-regexseq, #104), and groupValues/destructured (il-groupvalues). All are pure-Kotlin at the source (Regex /
// RegexOption are kotlin.text defaults) — no `import System`, so they belong in this value-assert battery, not
// the interop/round-trip lane.
//
// Coverage preserved (old case -> method):
//   il-regex        -> regex_basic                containsMatchIn / replace / matches / find(-> null) core surface
//   il-regexanchor  -> regexanchor_fullMatch      #162 matchEntire/matches = full anchored match (alternation, lazy, options)
//   il-regexopts    -> regexopts_options          #178 Regex(String, RegexOption) / Regex(String, Set<RegexOption>) ctors -> RegexOptions bitmask
//   il-regexreplace -> regexreplace_marshaling    replaceFirst/replace(String,String) + CharSequence input + toString/pattern
//   il-regexgroups  -> regexgroups_groupCollection MatchResult.groups by-index/by-name/iteration/`in`/containsAll
//   il-regexseq     -> regexseq_sequenceMembers   #104 findAll / splitToSequence (Sequence machinery) + options getter
//   il-groupvalues  -> groupvalues_destructured   MatchResult.groupValues + component destructuring
//
// Every scenario is self-contained inside its method (no shared top-level declarations), so there is nothing to
// name-prefix in this single-battery assembly.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsTrue as assertTrue
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsFalse as assertFalse
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsNull as assertNull

class RegexTests {
    // il-regex: kotlin.text.Regex -> System.Text.RegularExpressions.Regex. toRegex / containsMatchIn / replace /
    // matches / find. A missing find is a real null (assertNull can't alias to the string "null").
    @TestAttribute
    fun regex_basic() {
        val digits = "\\d+".toRegex()
        assertTrue(digits.containsMatchIn("abc123"))     // true
        assertFalse(digits.containsMatchIn("no nums"))   // false
        assertEquals("a#b#c#", digits.replace("a1b22c333", "#"))  // a#b#c#
        val ws = "\\s+".toRegex()
        assertEquals("a_b_c", ws.replace("a  b   c", "_"))        // a_b_c

        val num = "[0-9]+".toRegex()
        assertTrue(num.matches("12345"))                 // true  (whole input)
        assertFalse(num.matches("12a45"))                // false (not whole input)
        assertEquals("42", num.find("abc42def")?.value)  // 42
        assertNull(num.find("nodigits")?.value)          // null
    }

    // il-regexanchor (#162): matchEntire/matches return a FULL anchored match, not a leftmost search filtered by
    // span. A shorter alternation branch or a lazy quantifier must still yield the full-input match; compiled
    // options and existing anchors coexist; a partial match yields null.
    @TestAttribute
    fun regexanchor_fullMatch() {
        assertEquals("ab", Regex("a|ab").matchEntire("ab")?.value)   // ab
        assertTrue(Regex("a|ab").matches("ab"))                      // True
        assertEquals("a", Regex("a|ab").matchEntire("a")?.value)     // a
        assertEquals("aaa", Regex("a+?").matchEntire("aaa")?.value)  // aaa (lazy quantifier forced to fill)
        assertEquals("", Regex("").matchEntire("")?.value)           // "" (empty pattern, empty input)
        assertEquals("12-34,12,34", Regex("(\\d+)-(\\d+)").matchEntire("12-34")?.groupValues?.joinToString(",")) // 12-34,12,34
        assertEquals("ab", Regex("^ab$").matchEntire("ab")?.value)   // ab (existing anchors coexist)
        assertTrue(Regex("(?i)abc").matches("ABC"))                  // True (compiled options honored)
        assertFalse(Regex("[0-9]+").matches("12a45"))                // False (not the whole input)
        assertNull(Regex("a").matchEntire("ab")?.value)              // null (partial, not full)
    }

    // il-regexopts (#178): the options-taking ctors Regex(String, RegexOption) / Regex(String, Set<RegexOption>)
    // convert the RegexOption / Set<RegexOption> arg to the BCL RegexOptions int bitmask at the ctor call site.
    @TestAttribute
    fun regexopts_options() {
        // single RegexOption (compile-time enum constant)
        assertTrue(Regex("a", RegexOption.IGNORE_CASE).matches("A"))                      // True (case-insensitive)
        assertTrue(Regex("a", RegexOption.IGNORE_CASE).matches("a"))                      // True
        assertFalse(Regex("a").matches("A"))                                             // False (control: no option)

        // COMMENTS -> IgnorePatternWhitespace: unescaped whitespace in the pattern is ignored
        assertTrue(Regex("a b", setOf(RegexOption.COMMENTS)).matches("ab"))               // True

        // DOT_MATCHES_ALL -> Singleline: '.' matches a newline
        assertTrue(Regex("a.b", setOf(RegexOption.DOT_MATCHES_ALL)).matches("a\nb"))      // True
        assertFalse(Regex("a.b").matches("a\nb"))                                        // False (control)

        // MULTILINE -> Multiline: '^' matches at a line start (not just string start)
        assertTrue(Regex("^b", setOf(RegexOption.MULTILINE)).containsMatchIn("a\nb"))     // True
        assertFalse(Regex("^b").containsMatchIn("a\nb"))                                 // False (control)

        // multi-element set (OR of two bits)
        assertTrue(Regex("A B", setOf(RegexOption.IGNORE_CASE, RegexOption.COMMENTS)).matches("ab"))  // True

        // runtime-held option (not a compile-time constant) exercises the enumOrdinal path
        val opt = RegexOption.IGNORE_CASE
        assertTrue(Regex("x", opt).matches("X"))                                         // True
    }

    // il-regexreplace: Regex.replaceFirst / replace(String,String) marshaling. replaceFirst must bind the 3-arg
    // String overload (a CharSequence input is materialized to a real String); toString()/pattern read the
    // pattern-string source.
    @TestAttribute
    fun regexreplace_marshaling() {
        val a = "a".toRegex()
        assertEquals("bXnana", a.replaceFirst("banana", "X"))       // bXnana (was: banana, unchanged)
        assertEquals("bXnXnX", a.replace("banana", "X"))            // bXnXnX
        val cs: CharSequence = "banana"
        assertEquals("bXnana", a.replaceFirst(cs, "X"))             // bXnana (was: AccessViolationException)
        assertEquals("a#b34", "[0-9]+".toRegex().replaceFirst("a12b34", "#"))  // a#b34
        assertEquals("a(\\d+)b", Regex("a(\\d+)b").toString())      // a(\d+)b (pattern-string source; method binding)
        assertEquals("c(\\w+)d", Regex("c(\\w+)d").pattern)         // c(\w+)d (rule-3 accessor hoist)
    }

    // il-regexgroups: MatchResult.groups — the ClrMatchGroupCollection surface (by-index/by-name access,
    // iteration, `group in match.groups`, containsAll, named groups).
    @TestAttribute
    fun regexgroups_groupCollection() {
        val re = "(\\d+)-(\\d+)".toRegex()
        val m = re.find("12-34")!!
        val g = m.groups
        assertEquals(3, g.size)          // 3 (whole match + 2 groups)
        assertEquals("12-34", g[0]?.value)  // 12-34
        assertEquals("12", g[1]?.value)     // 12
        assertEquals("34", g[2]?.value)     // 34

        // iteration
        val vals = StringBuilder()
        for (grp in g) { vals.append(grp?.value ?: "?"); vals.append(",") }
        assertEquals("12-34,12,34,", vals.toString())  // 12-34,12,34,

        // `in` -> ClrMatchGroupCollection.contains
        val first = g.iterator().next()
        assertTrue(first in g)                          // true
        assertFalse(null in g)                          // false
        assertTrue(g.containsAll(listOf(g[0], g[1])))   // true

        // named group
        val named = "(?<yr>\\d{4})".toRegex().find("2026")!!
        assertEquals("2026", named.groups["yr"]?.value)  // 2026
    }

    // il-regexseq (#104): Sequence-returning Regex members findAll / splitToSequence over ordinary Sequence
    // machinery, plus the options getter (a default Regex decodes to an empty option set).
    @TestAttribute
    fun regexseq_sequenceMembers() {
        val nums = Regex("\\d+")
        // findAll: every non-overlapping match, left-to-right (Kotlin contract).
        assertEquals("1,22,333", nums.findAll("a1b22c333").map { it.value }.joinToString(","))  // 1,22,333
        assertEquals(0, nums.findAll("no digits here").count())                                 // 0
        // findAll honors startIndex.
        assertEquals("2,3", nums.findAll("1a2a3", 2).map { it.value }.joinToString(","))         // 2,3

        val ws = Regex("\\s+")
        // splitToSequence: identical elements to split(), in order.
        val seq = ws.splitToSequence("a b  c").toList()
        assertEquals("a|b|c", seq.joinToString("|"))    // a|b|c
        assertTrue(seq == ws.split("a b  c"))           // true
        // splitToSequence honors limit.
        assertEquals("a|b c d", ws.splitToSequence("a b c d", 2).toList().joinToString("|"))     // a|b c d

        // options: a default Regex has no options (decodes to an empty set, no longer throws).
        assertTrue(Regex("x").options.isEmpty())        // true
    }

    // il-groupvalues: MatchResult.groupValues (whole match + each group) and component-destructuring of
    // MatchResult.destructured.
    @TestAttribute
    fun groupvalues_destructured() {
        val m = Regex("(a)(b)(c)").find("abc")!!
        assertEquals("abc,a,b,c", m.groupValues.joinToString(","))  // abc,a,b,c
        val m2 = Regex("(\\d+)-(\\d+)").find("12-34")!!
        val (x, y) = m2.destructured
        assertEquals("12 34", "$x $y")                              // 12 34
    }
}
