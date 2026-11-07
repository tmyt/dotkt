import NativePointers.Api
import NativePointers.GenericApi
import NativePointers.CallbackBase
import NativePointers.NullableApi
import NativePointers.AnnotatedApi
import kotlin.clr.ClrFunctionPointer
import kotlin.clr.signature.Managed
import kotlin.clr.signature.Cdecl

fun managed(p: ClrFunctionPointer<Managed, (Int) -> Int>): ClrFunctionPointer<Managed, (Int) -> Int> = Api.Echo(p)
fun cdecl(p: ClrFunctionPointer<Cdecl, (Int) -> Int>): ClrFunctionPointer<Cdecl, (Int) -> Int> = Api.EchoCdecl(p)
fun nullableSignature(): ClrFunctionPointer<Managed, (String?) -> String> = Api.NullableSignature
fun modifiedSignature() = Api.Modified(Api.GetModified())
fun nestedSignature() = Api.NestedEcho(Api.GetNested())
fun unitValueSignature() = Api.UnitValue
fun unitVoidSignature() = Api.UnitVoid
fun rectangularSignature() = Api.Rectangular
fun dataPointerSignature() = Api.DataPointer
fun rectangularPointers() = Api.RectangularPointers
fun wideSignature() = Api.Wide
fun nullableValueSignature() = Api.EchoNullable(Api.NullableValue)
fun nativeNullableGeneric(api: NullableApi<Int>) = api.Value
fun annotatedGeneric(api: AnnotatedApi<Int>) = api.Value

fun choose(p: ClrFunctionPointer<Managed, (Int) -> Int>): Int = Api.Call(p, 21)
fun choose(p: ClrFunctionPointer<Cdecl, (Int) -> Int>): Int = Api.CallCdecl(p, 21)

class CallbackOverride : CallbackBase() {
    override fun Echo(value: ClrFunctionPointer<Managed, (Int) -> Int>): ClrFunctionPointer<Managed, (Int) -> Int> = value
}

fun <T, U> generic(api: GenericApi<T>, p: ClrFunctionPointer<Managed, (T, U) -> U>): ClrFunctionPointer<Managed, (T, U) -> U> =
    api.Echo<U>(p)

fun <T> genericField(api: GenericApi<T>, p: ClrFunctionPointer<Managed, (T) -> T>): ClrFunctionPointer<Managed, (T) -> T> {
    api.Value = p
    api.Property = api.Value
    return api.Property
}

fun main() {
    val pointer = managed(Api.Get())
    Api.Value = pointer
    Api.Property = Api.Value
    val array = Api.EchoArray(Api.GetArray())
    Api.Array = array
    println(Api.Call(Api.Property, 21) + Api.CallCdecl(cdecl(Api.GetCdecl()), 21) + Api.CallArray(array, 21) +
        choose(Api.Get()) + choose(Api.GetCdecl()) + Api.CallOverride(CallbackOverride()))
}
