// C14: Double/Float total order (-0.0 < 0.0, NaN largest & NaN==NaN) for BOXED == and .compareTo;
// primitive == / < stay IEEE (-0.0 == 0.0 true, NaN == NaN false).
fun main() {
    println((-0.0 as Any) == (0.0 as Any))       // false
    println((-0.0).compareTo(0.0))                // -1
    println((0.0).compareTo(-0.0))                // 1
    val nan = 0.0 / 0.0
    println((nan as Any) == (nan as Any))         // true
    println(-0.0 == 0.0)                          // true  (primitive IEEE)
    println(Double.NaN == Double.NaN)             // false (primitive IEEE)
    println(Double.NaN.compareTo(1.0))            // 1     (NaN largest)
    println(Double.NaN.compareTo(Double.NaN))     // 0
    println((-0.0f as Any) == (0.0f as Any))      // false (Float)
    println((-0.0f).compareTo(0.0f))              // -1    (Float)
    println((1.0 as Any) == (1.0 as Any))         // true
}
