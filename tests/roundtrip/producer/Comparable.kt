// Migrated verify-roundtrip.sh section `roundtrip-comparable` (#179, end-to-end run) — the library half.
// A Kotlin `class C : Comparable<C>` lowers `compareTo` to the CLR `System.IComparable<C>.CompareTo` PascalCase
// slot (bir2cir DeclarationRename) and its supertype to `System.IComparable<C>`. dll2klib renames the self-slot
// `CompareTo` -> `compareTo` + forces the operator flag (so `<`/`>`/`<=`/`>=` resolve to C's own operator) and
// restores the supertype as `kotlin.Comparable<C>` (so `sorted()`'s constraint is satisfied); bir2cir
// NetInteropBinding rebinds compareTo->CompareTo on the external owner so the calls run cross-module.
// `roundtrip-comparable-meta` validates the complete reference-KLIB path in scenarios/run.sh.
package roundtrip.cmp

class Ver(val n: Int) : Comparable<Ver> {
    override fun compareTo(other: Ver): Int = n - other.n
}
