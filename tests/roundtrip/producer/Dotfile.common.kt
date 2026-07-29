// Migrated verify-roundtrip.sh section `roundtrip-dotfile` (#16) — the library half.
// A source file whose stem contains a dot (`Dotfile.common.kt`, the standard MPP common-fragment convention)
// compiles to a file-facade class. kotc must sanitize the stem's non-identifier chars to `_` BEFORE deriving
// the class name (`Dotfile_commonKt`) — else the raw `Dotfile.commonKt` is read by ilemit's DefineType as
// Namespace=roundtrip.dotfile.Dotfile / Name=commonKt, so dll2klib scanning the package never surfaces the
// TOP-LEVEL functions -> a cross-module `unresolved reference` on `commonOnly`. Top-level CLASSES round-trip
// either way (they carry their own type name).
package roundtrip.dotfile

fun commonOnly(x: Int): Int = x + 1     // top-level fun in a DOTTED-name file (the #16 regression surface)
class Box(var v: Int)                    // top-level class in the same file (round-trips either way)
