import System.Runtime.InteropServices.DllImportAttribute
import System.Runtime.InteropServices.MarshalAsAttribute
import System.Runtime.InteropServices.UnmanagedType

@DllImportAttribute("missing")
external fun unsupportedEncoding(@MarshalAsAttribute(UnmanagedType.CustomMarshaler) text: String): Int
