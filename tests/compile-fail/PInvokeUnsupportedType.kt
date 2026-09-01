import System.Runtime.InteropServices.DllImportAttribute

@DllImportAttribute("missing")
external fun unsupportedPInvoke(value: String): Int
