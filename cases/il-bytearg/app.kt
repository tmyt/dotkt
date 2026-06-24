// Byte/Short (signed) as parameters, locals, fields, and const args. Regression: a `const byte`/`const short`
// passed to a byte/short parameter pushed null (InvalidProgramException); birType/EmitConst/MapType omitted the
// signed Byte/Short (Int/Long/unsigned were present).
fun takeByte(b: Byte): Int = b.toInt()
fun takeShort(s: Short): Int = s.toInt()
class Holder(val b: Byte, val s: Short)
fun main() {
    val i = 5
    println(takeByte(i.toByte()))   // 5   (Int->Byte conv arg)
    println(takeByte(3))            // 3   (Byte const arg)
    val bv: Byte = 7
    println(takeByte(bv))           // 7   (Byte local arg)
    println(takeShort(9))           // 9   (Short const arg)
    val h = Holder(4, 100)
    println(h.b.toInt())            // 4   (Byte field)
    println(h.s.toInt())            // 100 (Short field)
    val neg: Byte = -2              // signed range
    println(takeByte(neg))          // -2
}
