import System.Runtime.InteropServices.DllImportAttribute

@DllImportAttribute("missing")
external fun <T> genericPInvoke(value: T): Int
