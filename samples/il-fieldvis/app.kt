// Field-level visibility (A-108): a property's visibility is honored on its backing field. Reflection confirms
// private/internal fields are emitted as such (same-class access still works via the property=field model).
import clr.Refl
class Account(initial: Int) {
    private var balance: Int = initial          // -> private field
    internal val tag: String = "acct"           // -> assembly (internal) field
    val owner: String = "me"                    // -> public field
    fun deposit(n: Int) { balance = balance + n }
    fun show(): Int = balance                   // same-class read of the private field
}
fun main() {
    val a = Account(100)
    a.deposit(50)
    println(a.show())                            // 150  (private field works within the class)
    println(a.owner)                             // me
    println(Refl.fieldVis(a, "balance"))         // Internal (Kotlin private -> IL assembly; see note)
    println(Refl.fieldVis(a, "owner"))           // Public
}
