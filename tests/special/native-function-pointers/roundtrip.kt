import NativePointers.Api
import NativePointers.GenericApi
import kotlin.clr.ClrFunctionPointer
import kotlin.clr.signature.Managed

private interface NamedCallback {
    @kotlin.clr.ClrName("Clash")
    fun selected(p: ClrFunctionPointer<Managed, (Int) -> Int>, f: String.() -> Int): Int = "x".f() + 1
}

private class CallbackCollision : PointerCallbackBase(), NamedCallback

class PointerChild : SuspendPointerBase<Int>() {
    override suspend fun consume(value: Int, pointer: ClrFunctionPointer<Managed, (Int) -> Int>): Int =
        Api.Call(pointer, value)
}

suspend fun main() {
    val pointer = genericField(GenericApi<Int>(), Api.Get())
    val collision = CallbackCollision()
    check((collision as NamedCallback).selected(pointer) { length } == 2)
    check(collision.Clash(pointer) { it.length } == 3)
    println(choose(pointer) + choose(Api.GetCdecl()))
    val base: SuspendPointerBase<Int> = PointerChild()
    check(base.consume(21, pointer) == 42)
}
