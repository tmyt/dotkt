import System.Runtime.InteropServices.CallingConvention
import System.Runtime.InteropServices.CharSet
import System.Runtime.InteropServices.DllImportAttribute
import System.Runtime.InteropServices.Marshal
import kotlin.clr.ClrRef

@DllImportAttribute(
    "dotkt_pinvoke_probe",
    EntryPoint = "add_i32",
    CallingConvention = CallingConvention.Cdecl,
    SetLastError = true,
)
external fun add(left: Int, right: Int): Int

@DllImportAttribute("dotkt_pinvoke_probe", EntryPoint = "increment_i32")
external fun increment(value: ClrRef<Int>)

@DllImportAttribute("dotkt_pinvoke_probe", EntryPoint = "none_i32", CharSet = CharSet.None)
external fun none(value: Int): Int

@DllImportAttribute("dotkt_pinvoke_probe", EntryPoint = "ansi_i32", CharSet = CharSet.Ansi)
external fun ansi(value: Int): Int

@DllImportAttribute("dotkt_pinvoke_probe", EntryPoint = "auto_i32", CharSet = CharSet.Auto)
external fun auto(value: Int): Int

@DllImportAttribute(
    "dotkt_pinvoke_probe",
    EntryPoint = "options_i32",
    CallingConvention = CallingConvention.Cdecl,
    CharSet = CharSet.Unicode,
    ExactSpelling = true,
    SetLastError = true,
    PreserveSig = false,
    BestFitMapping = false,
    ThrowOnUnmappableChar = false,
)
external fun options(value: Int): Int

@DllImportAttribute("dotkt_pinvoke_probe", EntryPoint = "set_error_i32", SetLastError = true)
external fun setError(value: Int): Int

fun observedLastError(value: Int): Int {
    check(setError(value) == -1)
    return Marshal.GetLastPInvokeError()
}
