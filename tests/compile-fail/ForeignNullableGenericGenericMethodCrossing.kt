// #86 — a GENERIC .NET method's declared return.
//
// `clrGenericStatic`/`clrGenericInstance` take their parameter descriptor from the frontend and never entered the
// resolution that establishes what the member DECLARES, so the crossing refusal saw no declared return and the whole
// generic-method family went unchecked — the same omission as the non-generic return, one shape further out. The
// build now asserts that every node resolved against a .NET member carries a declared return, so a future omission
// of this kind fails the compiler rather than a review.
import fgn.Api

fun main() {
    val api = Api()
    println(api.OrElse(null, 7))     // control: a direct int? parameter still crosses
    println(api.MakeG<String>())     // REFUSED: List<Nullable<Int32>> at a generic method's RETURN
}
