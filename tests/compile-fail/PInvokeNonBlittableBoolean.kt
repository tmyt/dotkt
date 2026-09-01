import System.Runtime.InteropServices.DllImportAttribute

@DllImportAttribute("missing")
external fun unsupportedBoolean(value: Boolean): Boolean
