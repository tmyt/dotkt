import NativePointers.Api
import NativePointers.GenericApi

fun main() {
    val pointer = genericField(GenericApi<Int>(), Api.Get())
    println(choose(pointer) + choose(Api.GetCdecl()))
}
