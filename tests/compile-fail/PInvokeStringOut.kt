import System.Runtime.InteropServices.DllImportAttribute
import System.Runtime.InteropServices.OutAttribute

@DllImportAttribute("missing")
external fun unsupportedStringOut(@OutAttribute value: String): Int
