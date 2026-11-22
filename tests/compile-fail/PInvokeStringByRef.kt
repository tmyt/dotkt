import System.Runtime.InteropServices.DllImportAttribute
import kotlin.clr.ClrRef

@DllImportAttribute("missing")
external fun unsupportedStringByRef(value: ClrRef<String>): Int
