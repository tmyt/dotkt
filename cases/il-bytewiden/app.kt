// #93/#71: narrow numeric operators DECLARE a widened return that a bare bin/unary/inc op drops. Byte/Short
// arithmetic -> Int, UByte/UShort arithmetic -> UInt; inc/dec wrap to the receiver's OWN narrow type (via the
// int-width `+1` desugaring). bir2cir wraps the lowered op in a `conv` to the frontend-declared return type;
// ilemit needs the unsigned conv arms (#71: Conv_U1/U2/U4/U8) for the UByte/UShort -> UInt widening and for the
// explicit .toUByte()/.toUShort()/.toUInt()/.toULong() conversions. Without the fix the value truncates on box.
fun main() {
    // Signed narrow arithmetic widens to Int (println boxes it — must NOT truncate to the narrow left operand).
    val b1: Byte = 100
    val b2: Byte = 100
    println(b1 + b2)                    // 200    (Byte+Byte:Int, not -56)
    val s1: Short = 20000
    val s2: Short = 20000
    println(s1 + s2)                    // 40000  (Short+Short:Int, not -25536)
    // Unsigned narrow arithmetic widens to UInt.
    val ub1: UByte = 200u
    val ub2: UByte = 100u
    println(ub1 + ub2)                  // 300    (UByte+UByte:UInt, not 44)
    val us1: UShort = 40000u
    val us2: UShort = 40000u
    println(us1 + us2)                  // 80000  (UShort+UShort:UInt, not 14464)
    // The common "sum array bytes" pattern (ByteArray element is Byte).
    val arr: ByteArray = byteArrayOf(100, 100)
    println(arr[0] + arr[1])           // 200
    // inc/dec wrap to the narrow type (overflow wraps, like Kotlin/JVM).
    var b: Byte = 127
    b++
    println(b.toInt())                 // -128   (Byte.inc overflow wraps)
    var ub: UByte = 255u
    ub++
    println(ub.toInt())                // 0      (UByte.inc overflow wraps)
    var us: UShort = 65535u
    us++
    println(us.toInt())                // 0      (UShort.inc overflow wraps)
    // Unary minus on Byte widens to Int.
    val mb: Byte = -128
    println(-mb)                       // 128    (Byte.unaryMinus:Int, not -128)
    // Explicit unsigned conversions exercise the #71 conv arms (U1/U2/U4/U8).
    val big = 300
    println(big.toUByte().toInt())     // 44     (300 & 0xFF, Conv_U1)
    println(big.toUShort().toInt())    // 300    (Conv_U2)
    println((-1).toUInt())             // 4294967295          (Conv_U4)
    println((-1).toULong())            // 18446744073709551615 (Conv_U8)
}
