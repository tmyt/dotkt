// #199 regression fixture (part A) — a top-level suspend fun `pkgFoo` in package `col199a`, sharing its
// SIMPLE name with `col199b.pkgFoo`. Pre-fix these collided in bir2cir's `entries` FunKey(Owner=null, name, sig)
// (top-level Owner is null; the method name is a bare simple name) -> the loser was dropped from the registry
// and left un-lowered (ilemit "reached codegen un-lowered" / EntryPointNotFound). `pkgLeaf` (same-file, awaited
// with owner:null) exercises the `sty`-based awaited-value typing. Returns Int (b returns String) so a typing
// collision would also surface (wrong un/box).
package col199a

suspend fun pkgLeaf(): Int = 10

suspend fun pkgFoo(): Int {
    val x = pkgLeaf()   // same-file suspend call (owner:null); awaited value typed from `sty` = kotlin.Int
    return x + 1        // 11
}
