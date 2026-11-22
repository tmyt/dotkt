import System.Runtime.InteropServices.DllImportAttribute

@DllImportAttribute("missing")
external fun unsupportedPInvoke(): String
