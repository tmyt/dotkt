import System.Runtime.InteropServices.DllImportAttribute

@DllImportAttribute("missing")
fun pInvokeWithBody(value: Int): Int = value
