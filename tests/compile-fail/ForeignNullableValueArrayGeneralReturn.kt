// #354 — the authoritative return keeps CLR general-array identity. Kotlin cannot name `Nullable<Int32>[,]`, and
// its projected element type still has the canonical object image, so the diagnostic must retain rank on both sides.
import fgn.Api

fun main() {
    println(Api().MakeMatrix().size)
}
