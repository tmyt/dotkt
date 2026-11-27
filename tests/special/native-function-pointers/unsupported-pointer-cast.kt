import NativePointers.Api
import kotlin.clr.ClrFunctionPointer
import kotlin.clr.signature.Cdecl

@Suppress("UNCHECKED_CAST")
fun discardWrongConvention() {
    Api.Get() as ClrFunctionPointer<Cdecl, (Int) -> Int>
}
