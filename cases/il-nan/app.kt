// Double/Float NaN and infinities are not valid JSON number tokens, so the BIR emits them as strings the ilemit const
// handler parses back (surfaced compiling the real stdlib average(), which returns Double.NaN for an empty range).
fun main() {
    println(Double.POSITIVE_INFINITY > 1e300)    // true
    println(Double.NEGATIVE_INFINITY < -1e300)   // true
    println(Float.POSITIVE_INFINITY > 1e30f)     // true
    println(Double.NaN > 0.0)                    // false (any comparison with NaN is false)
    println(Double.NaN < 0.0)                    // false
}
