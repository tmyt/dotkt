// E-0.5: break/continue/labeled-break INSIDE CFG-lowered while loops (the §5.5 path: jumps -> goto loop labels).
fun main() {
    // while + break
    var i = 0
    while (true) {
        if (i == 3) break
        i = i + 1
    }
    println("break at $i")          // 3

    // while + continue (sum of odd 1..5)
    var j = 0
    var sumOdd = 0
    while (j < 6) {
        j = j + 1
        if (j % 2 == 0) continue
        sumOdd = sumOdd + j
    }
    println("sumOdd=$sumOdd")       // 1+3+5 = 9

    // labeled break@outer out of a nested while
    var a = 0
    var hit = "none"
    outer@ while (a < 3) {
        var b = 0
        while (b < 3) {
            if (a + b == 3) { hit = "$a,$b"; break@outer }
            b = b + 1
        }
        a = a + 1
    }
    println("outer break at $hit")  // 1,2
}
