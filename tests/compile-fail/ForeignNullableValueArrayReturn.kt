// #354 — the return channel must read the authoritative CLR declaration, not the caller's erased `Array<Int?>`
// stamp, or `Nullable<Int32>[]` reaches a receiving slot typed as the unrelated `object[]`.
import fgn.Api

fun main() {
    println(Api().MakeArray().size)
}
