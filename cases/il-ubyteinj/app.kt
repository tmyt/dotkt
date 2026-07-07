import Bt.B
@OptIn(kotlin.ExperimentalUnsignedTypes::class)
fun main() {
    val u: UByte = B.One()
    println(u.toInt())          // 200   System.Byte read as UByte 200 (NOT signed -56)
    val a: UByteArray = B.Arr()
    println(a.size)             // 3     byte[] surfaced as UByteArray
    println(a[2].toInt())       // 250
    println(B.Take(200u))       // 200   pass a UByte to a System.Byte param
    println(B.TakeArr(a))       // 253   pass a UByteArray to a System.Byte[] param (3 + 250)
}
