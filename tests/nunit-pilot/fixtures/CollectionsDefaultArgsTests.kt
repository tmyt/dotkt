// Collections + default-arguments battery — migrates cases/il-defargs. The old case println'd 12 lines and
// diffed the concatenation; here each contract is a discrete value assertion (a slot-shift regression fails
// exactly the one method, self-documenting which contract broke). toString-shaped contracts (data-class copy)
// assert .toString() explicitly — preserving the EXACT textual check the stdout diff encoded.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert

data class P(val x: Int, val y: Int, val z: Int)

fun greet(name: String, greeting: String = "Hello", punct: String = "!"): String = "$greeting, $name$punct"

class CollectionsDefaultArgsTests {
    @TestAttribute
    fun joinToStringDefaults() {
        val xs = listOf(1, 2, 3)
        ClassicAssert.AreEqual("x1-x2-x3", xs.joinToString("-") { "x$it" })
        ClassicAssert.AreEqual("1, 2, 3", xs.joinToString())
        ClassicAssert.AreEqual("[1, 2, 3]", xs.joinToString(prefix = "[", postfix = "]"))
        ClassicAssert.AreEqual("1/2/~", xs.joinToString(separator = "/", limit = 2, truncated = "~"))
    }

    @TestAttribute
    fun substringReceiverReferencingDefault() {
        ClassicAssert.AreEqual("b=c", "a=b=c".substringAfter("="))
        ClassicAssert.AreEqual("a", "a=b=c".substringBefore("="))
        ClassicAssert.AreEqual("nodelim", "nodelim".substringAfter("="))
        ClassicAssert.AreEqual("FALLBACK", "nodelim".substringBefore("=", "FALLBACK"))
    }

    @TestAttribute
    fun dataClassCopyDefaults() {
        val p = P(1, 2, 3)
        ClassicAssert.AreEqual("P(x=1, y=20, z=3)", p.copy(y = 20).toString())
        ClassicAssert.AreEqual("P(x=10, y=2, z=30)", p.copy(x = 10, z = 30).toString())
    }

    @TestAttribute
    fun sameModuleConstantDefaults() {
        ClassicAssert.AreEqual("Hello, Kotlin!", greet("Kotlin"))
        ClassicAssert.AreEqual("Hello, Kotlin?", greet("Kotlin", punct = "?"))
    }
}
