// Shared by the direct-CLI policy check and the packaged SDK executable case.
// Deliberately no source-level opt-in: unsigned arrays are ordinary CLR interop.
fun checkUnsignedPlatformOptIn() {
    val bytes: UByteArray = System.Text.Encoding.UTF8.GetBytes("A")
    val shorts = ushortArrayOf(UShort.MAX_VALUE)
    val ints = uintArrayOf(UInt.MAX_VALUE)
    val longs = ulongArrayOf(ULong.MAX_VALUE)
    check(bytes.size == 1 && bytes[0].toInt() == 65)
    check(shorts.size == 1 && shorts[0] == UShort.MAX_VALUE)
    check(ints.size == 1 && ints[0] == UInt.MAX_VALUE)
    check(longs.size == 1 && longs[0] == ULong.MAX_VALUE)
}
