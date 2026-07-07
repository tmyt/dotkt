// Unsigned specialized array (#53): kotlin.UByteArray -> native System.Byte[] (NOT a value-class wrapper, NOT
// Array<UByte>). ubyteArrayOf/get/size lower to native array ops; toByteArray/toUByteArray reinterpret between the
// signed (SByte[]) and unsigned (Byte[]) native arrays (a value, not a copy — same 8-bit storage).
@OptIn(kotlin.ExperimentalUnsignedTypes::class)
fun main() {
    val a: UByteArray = ubyteArrayOf(1u, 2u, 250u)
    println(a.size)                 // 3
    println(a[2].toInt())           // 250   unsigned read (a signed Byte would be -6)
    val b: ByteArray = a.toByteArray()
    println(b[2].toInt())           // -6     signed reinterpret of 250
    val c: UByteArray = b.toUByteArray()
    println(c[2].toInt())           // 250    reinterpret back to unsigned
    val ub: UByte = 200u
    println(ub.toInt())             // 200
}
