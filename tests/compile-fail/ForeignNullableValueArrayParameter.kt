// #354 — a .NET `Nullable<Int32>[]` parameter has no Kotlin counterpart. `Array<Int?>` is canonically `object[]`,
// so the frontend-visible source type checks while bir2cir must refuse the unrelated physical array types.
import fgn.Api

fun main() {
    println(Api().CountPresentArray(arrayOf<Int?>(1, null)))
}
