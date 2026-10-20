package roundtrip.privatefileidentity

private var leftReadCount = 0

@kotlin.clr.ClrName("readLeftFilePrivateDll")
@Suppress("UNCHECKED_CAST")
private fun <T> read(raw: Any?): T {
    leftReadCount++
    return raw as T
}

fun leftFilePrivateDefault(value: Int = read<Int>(31)): Int = value
fun leftFilePrivateReads(): Int = leftReadCount
