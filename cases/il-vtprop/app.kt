// BUG-1 repro: setting a MUTABLE property/field on a .NET value-type (struct) receiver via the CLR property
// path. The struct is an addressable local, so the setter must mutate it in place (ldloca), not a copy.
import Probe.Box

fun main() {
    val b = Box(3)      // V=3, F=3
    b.V = 10            // clrPropSet -> set_V on the struct's address
    b.F = 20            // clrPropSet field-store -> stfld on the struct's address
    println(b.V)        // 10  (was 3 before the fix: setter mutated a copy)
    println(b.F)        // 20  (was 3 before the fix)
    println(b.Sum())    // 30
}
