package privatefileidentity.probe

private var rightReadCount = 0

@kotlin.clr.ClrName("readRightFilePrivate")
@Suppress("UNCHECKED_CAST")
private fun <T> read(raw: Any?): T {
    rightReadCount++
    return raw as T
}

@get:kotlin.clr.ClrName("getRightFilePrivateValue")
private val scopedValue: Int get() = 211

fun rightFilePrivateDirect(): Int = read<Int>(23)
fun rightFilePrivateDefault(value: Int = read<Int>(37)): Int = value
fun rightFilePrivateReference(): () -> Int {
    val selected: (Any?) -> Int = ::read
    return { selected(47) }
}
fun rightFilePrivateReads(): Int = rightReadCount
fun rightFilePrivateValue(): Int = scopedValue
