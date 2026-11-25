import kotlin.clr.ClrRef
import kotlin.clr.byref

private inline fun accept(slot: ClrRef<Int>, action: () -> Unit) { action() }

fun test(initial: Int?) {
    var value = initial
    if (value != null) accept(byref(value)) { }
}
