// #86 — a genuine public CLR FIELD.
//
// A field is read through `ldfld`, so it carries no parameter vector and its declared TYPE is the only statement of
// the crossing there is. Resolution used to mark it `member: "field"` and return without stating that type, so
// reading or writing a public `List<int?>` bypassed the refusal entirely — a whole family absent from a check that
// keyed on the stamped declaration.
import fgn.Api

fun main() {
    val api = Api()
    println(api.OrElse(null, 7))     // control: a direct int? parameter still crosses
    println(api.Storage)             // REFUSED: List<Nullable<Int32>> at a public CLR field
}
