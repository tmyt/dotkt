// #73 M2 regression gate: primitive range membership `x in a..b` — bir2cir RangeMembershipLowering fast path.
//  (1) a side-effecting subject `x` renders into BOTH comparison legs, so it must be evaluated exactly ONCE.
//  (2) `..`/`until`/`..<` polarity, `!in`, and LongRange/CharRange subject-temp typing (a mistyped temp = invalid IL).
//  (3) a stable operand splices directly; a variable-held range skips the fast path (real IntRange.contains binding).
var c = 0

fun h(): Int { c++; return 5 }
fun hl(): Long { c++; return 5L }
fun hc(): Char { c++; return 'e' }

fun main() {
    println(h() in 1..10)      // True
    println(c)                 // 1 — not 2 (single evaluation)
    println(h() in 1 until 5)  // False (5 excluded)
    println(c)                 // 2
    val i = 7                  // stable operand: the direct-splice fast path
    println(i in 1..10)        // True
    println(h() in 1..<5)      // rangeUntil (..<): 5 in 1..4 -> False
    println(h() !in 1..10)     // !in: 5 !in 1..10 -> False
    println(hl() in 1L..10L)   // LongRange, side-effecting subject -> Long temp: True
    println(hc() in 'a'..'z')  // CharRange, side-effecting subject -> Char temp: True
    println(c)                 // 6
    val r = 3..8               // variable-held range: NOT the inline-construction fast path
    println(5 in r)            // real IntRange.contains binding: True
}
