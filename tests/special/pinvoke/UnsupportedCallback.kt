import System.Runtime.InteropServices.DllImportAttribute
import kotlin.clr.ClrFunctionPointer
import kotlin.clr.signature.Array2
import kotlin.clr.signature.Cdecl

@DllImportAttribute("missing")
external fun unsupportedCallback(callback: ClrFunctionPointer<Cdecl, (Array2<Int>) -> Int>): Int
