// #25 RESIDUAL APP: consumes the re-imported MPP `genovc` library through a <ProjectReference>. The generic
// `arrOfNulls<String>(3)` factory lives in the lib's COMMON fragment (file class `GenovCommonKt`). kotc emits the
// generic call as `callStatic ... typeArgs=[…] shapeTypes=[…]` with NO resolved `sig`, so bir2cir's owner-
// attribution recovers an EMPTY receiver-key and — because the bare name `arrOfNulls` is present under two file-
// class owners in the ref index — cannot disambiguate -> the generic call was left un-promoted (no `sig`) ->
// ilemit reported `static method not found: arrOfNulls`. bir2cir must promote `shapeTypes`->`sig` for a referenced
// generic top-level call using kotc's facadegen-injected `ownerType`, even when the ref index can't attribute it.
import kotlinx.genovc.arrOfNulls

fun main() {
    val arr = arrOfNulls<String>(3)      // common-fragment generic factory (file class GenovCommonKt)
    println(arr.size)                    // 3
}
