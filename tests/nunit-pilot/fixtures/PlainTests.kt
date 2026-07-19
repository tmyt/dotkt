// Plain-Kotlin battery — migrates cases/il-arr, il-ext, il-enum, il-pair into ONE fixture (one battery,
// compiled once). Each former stdout-diff case becomes a @TestAttribute method asserting the computed
// VALUE directly (stronger + self-documenting than comparing a println'd string).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert

// ---- il-ext: user-defined extension functions (receiver -> __self first param) ----
fun Int.triple(): Int = this * 3
fun String.shout(): String = this.uppercase()

// ---- il-enum: enum + when ----
enum class Color { RED, GREEN, BLUE }
fun colorName(c: Color): String = when (c) {
    Color.RED -> "red"
    Color.GREEN -> "green"
    else -> "blue"
}

class PlainTests {
    // il-arr: arrays — factory, indexing get/set, .size, indexed + for-in iteration.
    @TestAttribute
    fun arrays() {
        val a = intArrayOf(10, 20, 30)
        ClassicAssert.AreEqual(10, a[0])
        ClassicAssert.AreEqual(30, a[2])
        a[1] = 99
        ClassicAssert.AreEqual(99, a[1])
        ClassicAssert.AreEqual(3, a.size)
        var sum = 0
        var i = 0
        while (i < a.size) { sum += a[i]; i++ }
        ClassicAssert.AreEqual(139, sum)   // 10 + 99 + 30
        var fsum = 0
        for (x in a) fsum += x
        ClassicAssert.AreEqual(139, fsum)
    }

    // il-ext
    @TestAttribute
    fun extensionFunctions() {
        ClassicAssert.AreEqual(21, 7.triple())
        ClassicAssert.AreEqual("HI", "hi".shout())
    }

    // il-enum
    @TestAttribute
    fun enumWhen() {
        ClassicAssert.AreEqual("red", colorName(Color.RED))
        ClassicAssert.AreEqual("green", colorName(Color.GREEN))
        ClassicAssert.AreEqual("blue", colorName(Color.BLUE))
    }

    // il-pair: Pair (a to b) -> ValueTuple, .first/.second, destructuring.
    @TestAttribute
    fun pairAndDestructuring() {
        val p = 3 to 4
        ClassicAssert.AreEqual(3, p.first)
        ClassicAssert.AreEqual(4, p.second)
        val q = "x" to 10
        ClassicAssert.AreEqual("x", q.first)
        ClassicAssert.AreEqual(10, q.second)
        val (a, b) = 5 to 6
        ClassicAssert.AreEqual(11, a + b)
    }
}
