// #199 regression fixture (part B) — the same-simple-name twin of col199a.pkgFoo, in package col199b, with a
// DIFFERENT return type (String) and different value, so both a declaration-side drop AND an awaited-value
// typing collision would be observable if the keying regressed.
package col199b

suspend fun pkgLeaf(): String = "b"

suspend fun pkgFoo(): String {
    val x = pkgLeaf()   // owner:null same-file suspend call; awaited value typed from `sty` = kotlin.String
    return x + "!"      // "b!"
}
