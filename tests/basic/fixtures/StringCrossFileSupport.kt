// il-charseqxfile (#149-1): a user class + a top-level fun in a SIBLING .kt of the SAME suite assembly.
// Their String-typed members are the CROSS-FILE receivers that StringsTests.charseqxfile_crossFile() routes
// into a stdlib CharSequence extension (split). Keeping these declarations in their own file preserves the
// original case's cross-file dimension: bir2cir must aggregate all files' declared types (StaticType.GlobalTypes)
// so the cross-file static type resolves and the receiver is adapter-wrapped (else EntryPointNotFound).
class StrXFileCfg {
    val body: String get() = "a\nb\nc"
}

fun strXFileBanner(): String = "x-y-z"
