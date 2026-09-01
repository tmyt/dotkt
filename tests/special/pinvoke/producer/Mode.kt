import System.Runtime.InteropServices.DllImportAttribute

enum class Mode {
    Zero,
    One,
}

enum class PInvokeContainer {
    Only;

    companion {
        @DllImportAttribute("dotkt_pinvoke_probe", EntryPoint = "add_i32")
        external fun companionAdd(left: Int, right: Int): Int
    }
}
