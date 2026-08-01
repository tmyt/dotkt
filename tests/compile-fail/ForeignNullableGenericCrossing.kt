// #86 — a .NET member whose declaration NO Kotlin expression inhabits.
//
// Carrier-argument erasure makes a nullable value type `System.Object` in every reified argument, so a Kotlin
// `List<Int?>` is an `IReadOnlyList<object>` — and the `System.Collections.Generic.List<Int?>` built below erases
// the same way, to a `List<object>`. `List<Nullable<Int32>>` is therefore a type the language cannot produce, and
// `List<object>` does not inhabit it: two invariant reified generics with no conversion between them. The
// frontend type-checks the call (it sees `List<Int?>` on both sides), so the refusal has to come from the backend.
//
// A silent mis-typing is the one outcome that must not happen, so the crossing is refused at the call, naming the
// member and the slot. The CONTROLS below must keep compiling in the same file, which is what makes this a
// statement about the POSITION rather than about `Nullable` appearing anywhere in a foreign signature: a direct
// `int?` parameter and a `Func<int?, string>` parameter are both inhabited exactly.
import fgn.Api
import System.Collections.Generic.List as NetList

fun main() {
    val api = Api()
    println(api.OrElse(null, 7))                                // control: a direct int? parameter
    println(api.Describe(3) { v -> v?.toString() ?: "none" })    // control: a delegate parameter keeps Nullable<int32>
    // Built explicitly on the .NET type, which is the remedy the refusal must NOT offer: this construction
    // erases its own argument to `List<object>` exactly as a Kotlin `List<Int?>` does, so it reaches the same wall.
    println(api.CountPresent(NetList<Int?>()))                  // REFUSED: List<Nullable<Int32>> at a PARAMETER
}
