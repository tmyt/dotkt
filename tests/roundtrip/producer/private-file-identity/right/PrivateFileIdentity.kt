package roundtrip.privatefileidentity

private var rightReadCount = 0

@kotlin.clr.ClrName("readRightFilePrivateDll")
@Suppress("UNCHECKED_CAST")
private fun <T> read(raw: Any?): T {
    rightReadCount++
    return raw as T
}

fun rightFilePrivateDefault(value: Int = read<Int>(37)): Int = value
fun rightFilePrivateReads(): Int = rightReadCount
