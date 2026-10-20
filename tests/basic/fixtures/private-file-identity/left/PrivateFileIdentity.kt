package privatefileidentity.probe

private var leftReadCount = 0

@kotlin.clr.ClrName("readLeftFilePrivate")
@Suppress("UNCHECKED_CAST")
private fun <T> read(raw: Any?): T {
    leftReadCount++
    return raw as T
}

@get:kotlin.clr.ClrName("getLeftFilePrivateValue")
private val scopedValue: Int get() = 107

fun leftFilePrivateDirect(): Int = read<Int>(17)
fun leftFilePrivateDefault(value: Int = read<Int>(31)): Int = value
fun leftFilePrivateReference(): () -> Int {
    val selected: (Any?) -> Int = ::read
    return { selected(43) }
}
fun leftFilePrivateReads(): Int = leftReadCount
fun leftFilePrivateValue(): Int = scopedValue
