package System.Runtime.InteropServices

@Target(AnnotationTarget.VALUE_PARAMETER)
annotation class OutAttribute

@DllImportAttribute("missing")
external fun lookalike(@OutAttribute text: String): Int
