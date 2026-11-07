import NativePointers.Api
import NativePointers.GenericApi
import kotlin.clr.ClrFunctionPointer
import kotlin.clr.signature.Managed
import kotlin.clr.signature.Cdecl

fun managed(p: ClrFunctionPointer<Managed, (Int) -> Int>): ClrFunctionPointer<Managed, (Int) -> Int> = Api.Echo(p)
fun cdecl(p: ClrFunctionPointer<Cdecl, (Int) -> Int>): ClrFunctionPointer<Cdecl, (Int) -> Int> = Api.EchoCdecl(p)
fun nullableSignature(): ClrFunctionPointer<Managed, (String?) -> String> = Api.NullableSignature

fun <T, U> generic(api: GenericApi<T>, p: ClrFunctionPointer<Managed, (T, U) -> U>): ClrFunctionPointer<Managed, (T, U) -> U> =
    api.Echo<U>(p)

fun main() {
    val pointer = managed(Api.Get())
    Api.Value = pointer
    Api.Property = Api.Value
    println(Api.Call(Api.Property, 21) + Api.CallCdecl(cdecl(Api.GetCdecl()), 21))
}
