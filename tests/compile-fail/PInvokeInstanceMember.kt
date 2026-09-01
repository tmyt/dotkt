import System.Runtime.InteropServices.DllImportAttribute

class PInvokeInstanceOwner {
    @DllImportAttribute("missing")
    external fun instancePInvoke(value: Int): Int
}
