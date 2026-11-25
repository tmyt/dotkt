import kotlin.clr.ClrRef
import kotlin.clr.byref

private fun clear(tag: Int, value: ClrRef<Int?>) { value.value = null }
private fun next() = 1

fun incompatibleAddress() {
    var x = System.Threading.ThreadLocal<Int>().Value
    val read = { x }
    if (x != null) clear(value = byref<Int?>(x), tag = next())
    read()
}
