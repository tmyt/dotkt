// #90 — a BASIC enum (constants only) lowers to a CLR value-type `enum`, which INHERITS ToString/Equals/
// GetHashCode from System.Enum rather than declaring them. Exercise the inherited-member family so ilemit
// must bind to the inherited BCL slot (via an `objMethod` box + Object virtual), not `E.ToString`. The enum
// itself is declared in enum.kt (a separate file) to also cover the module-wide (cross-file) binding.
fun main() {
    println(E.A.toString())          // A  (explicit .toString())
    println(E.B)                     // B  (println(Any?) -> toString)
    println("" + E.C)                // C  (string concat)
    println(E.A == E.B)              // false
    println(E.A.equals(E.A))         // true
    println(E.A.compareTo(E.C))      // -2
}
