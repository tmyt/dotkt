// Member visibility under the CLR property model: a Kotlin property becomes a real CLR property whose
// ACCESSORS carry its Kotlin visibility (private/internal/public), the backing field being uniformly
// assembly-internal (access routed through get_/set_). A .NET host (Kfc.Refl, imported façade-free via
// facadegen's import scan) reflects the emitted Kotlin type and confirms the honored visibility, driven
// through a plain instance-method call on the injected .NET type.
import Kfc.Refl
class Account(initial: Int) {
    private var balance: Int = initial          // -> private property (private get_/set_)
    internal val tag: String = "acct"           // -> internal (assembly) property
    val owner: String = "me"                    // -> public property
    fun deposit(n: Int) { balance = balance + n }
    fun show(): Int = balance                   // same-class read of the private property
}
fun main() {
    val a = Account(100)
    a.deposit(50)
    val refl = Refl()
    println(a.show())                            // 150  (private property works within the class)
    println(a.owner)                             // me
    println(refl.MemberVis(a, "balance"))        // Private (Kotlin visibility honored on the CLR accessor)
    println(refl.MemberVis(a, "owner"))          // Public
}
